# 生成本机自签名代码签名证书（仅限本地自测/开发，绝不是可信发布者）。
# 产物：<OutDir>\task_monitor-dev-<日期>.pfx（私钥，口令保护）+ 同名 .cer（公钥）。
# 本脚本不碰 git；certs/ 已在 .gitignore 里，切勿提交任何 .pfx。
[CmdletBinding()]
param(
    [string]$Subject = 'CN=TaskMonitor Dev (BloodMoon912)',
    # 默认 <repo>\certs
    [string]$OutDir = '',
    [int]$Years = 2,
    # 不传则交互输入（Read-Host -AsSecureString）
    [string]$Password = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'certs' }
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $repo $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $Password) {
    $secure = Read-Host -AsSecureString "為憑證 '$Subject' 設定 PFX 密碼"
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
if (-not $Password) { throw '密碼不能為空：Export-PfxCertificate 需要非空密碼保護私鑰' }

# 1. 建自簽名代碼簽名憑證，私鑰可導出
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($Years) `
    -CertStoreLocation 'Cert:\CurrentUser\My'

$stamp = Get-Date -Format 'yyyyMMdd'
$pfx = Join-Path $OutDir "task_monitor-dev-$stamp.pfx"
$cer = Join-Path $OutDir "task_monitor-dev-$stamp.cer"

# 2. 导出 .pfx（含私钥）与 .cer（公钥，用于信任导入）
$securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $securePassword | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

$thumbprint = $cert.Thumbprint

Write-Host ''
Write-Host "==> 自簽名憑證已建立（有效期至 $($cert.NotAfter.ToString('yyyy-MM-dd'))）"
Write-Host "    Subject:    $($cert.Subject)"
Write-Host "    Thumbprint: $thumbprint"
Write-Host "    PFX:        $pfx"
Write-Host "    CER:        $cer"
Write-Host ''
Write-Host '---- 簽名一次建置 ----'
Write-Host "pwsh tools/sign.ps1 -File dist\task_monitor-<version>-x64.exe -Thumbprint $thumbprint"
Write-Host "`$env:SIGN_CERT_THUMBPRINT = '$thumbprint'; pwsh tools/pack.ps1 -Sign -Zip"
Write-Host ''
Write-Host '---- 本機信任此憑證（只影響當前使用者） ----'
Write-Host "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\CurrentUser\TrustedPeople"
Write-Host '# 若 signtool verify /pa 仍報「鏈終止於不受信任的根」，另需匯入 CurrentUser\Root：'
Write-Host "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\CurrentUser\Root"
Write-Host ''
Write-Warning '這是自簽名憑證：只對本機/已手動信任它的機器有效。其他機器與 Windows SmartScreen 仍會警告「未知的發行者」，不能當作正式發佈簽名。'
Write-Warning "私鑰在 $pfx，口令只有你知道；不要把 certs/ 或任何 .pfx 提交進倉庫（.gitignore 已忽略 /certs/）。"
