# 把本机正式版装到 D:\App\TaskMonitor\ 并做本机签名（自签证书，仅本机信任）。
# 为什么自签：公开信任的代码签名证书必须由 CA 签发给法人实体，个人开源项目拿不到；
# 自签 + 本机信任能让 Get-AuthenticodeSignature 显示 Valid 与发布者名称，但这台机器以外仍然是「不明的发行者」。
# 证书私钥导出到 certs\（已被 .gitignore 忽略），绝不进版本库。
[CmdletBinding()]
param(
    # 要安装的 exe；默认取 dist\ 里最新的打包产物
    [string]$SourceExe = '',
    [string]$InstallDir = 'D:\App\TaskMonitor',
    [string]$Subject = 'CN=BloodMoon912',
    [string]$CertDir = 'certs',
    # 已存在同名证书就复用（指纹稳定，重装不会换发布者）
    [switch]$ReuseExisting
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repo = Split-Path -Parent $PSScriptRoot

function Resolve-SourceExe {
    param([string]$Explicit)
    if ($Explicit) {
        $p = (Resolve-Path -LiteralPath $Explicit -ErrorAction Stop).ProviderPath
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "not a file: $p" }
        return $p
    }
    $dist = Join-Path $repo 'dist'
    if (-not (Test-Path -LiteralPath $dist)) { throw "no dist folder — run tools\pack.ps1 first" }
    $cand = Get-ChildItem -LiteralPath $dist -Filter 'task_monitor-*-x64.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $cand) { throw "no packaged exe in $dist — run tools\pack.ps1 first" }
    return $cand.FullName
}

# ---------- 1. 证书 ----------
$cert = $null
if ($ReuseExisting) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}

if (-not $cert) {
    Write-Host "creating self-signed code signing certificate for '$Subject'"
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -FriendlyName 'TaskMonitor local code signing (self-signed)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}

$thumb = $cert.Thumbprint
Write-Host "certificate thumbprint: $thumb"

# 导出 pfx（重装/换机器用）与 cer（信任链用）。密码写在旁边的 .txt，仍然是本机文件。
$certPath = Join-Path $repo $CertDir
if (-not (Test-Path -LiteralPath $certPath)) { New-Item -ItemType Directory -Path $certPath | Out-Null }
$pfxPath = Join-Path $certPath 'task_monitor-local.pfx'
$cerPath = Join-Path $certPath 'task_monitor-local.cer'
$pwdPath = Join-Path $certPath 'task_monitor-local.pfx.password.txt'
if (-not (Test-Path -LiteralPath $pfxPath)) {
    $password = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
    Set-Content -LiteralPath $pwdPath -Value $password -NoNewline -Encoding ascii
    Write-Host "exported $pfxPath"
}
if (-not (Test-Path -LiteralPath $cerPath)) {
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Write-Host "exported $cerPath"
}

# 让本机把这张证书当成受信任的根 + 受信任的发布者，签名才会显示 Valid。
foreach ($store in @('Root', 'TrustedPublisher')) {
    $path = "Cert:\CurrentUser\$store"
    $existing = Get-ChildItem $path -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $thumb }
    if ($existing) {
        Write-Host "already trusted in CurrentUser\$store"
    }
    else {
        Import-Certificate -FilePath $cerPath -CertStoreLocation $path | Out-Null
        Write-Host "trusted in CurrentUser\$store"
    }
}

# ---------- 2. 安装到 D:\App ----------
$source = Resolve-SourceExe -Explicit $SourceExe
if (-not (Test-Path -LiteralPath $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }
$target = Join-Path $InstallDir 'task_monitor.exe'

# 目标正在运行就别覆盖（exe 被锁）
$running = Get-Process task_monitor -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $target }
if ($running) {
    throw "task_monitor is running from $target — stop it first (shutdown.sentinel) and rerun"
}

Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "installed $(Split-Path -Leaf $source) -> $target"

# ---------- 3. 签名 ----------
& (Join-Path $PSScriptRoot 'sign.ps1') -File $target -Thumbprint $thumb -Require
if ($LASTEXITCODE -ne 0) { throw "sign.ps1 failed with exit $LASTEXITCODE" }

# ---------- 4. 校验与清单 ----------
$sig = Get-AuthenticodeSignature -LiteralPath $target
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$version = (Get-Item -LiteralPath $target).VersionInfo

$manifest = @(
    "path=$target"
    "size=$((Get-Item -LiteralPath $target).Length)"
    "sha256=$hash"
    "product=$($version.ProductName)"
    "fileVersion=$($version.FileVersion)"
    "productVersion=$($version.ProductVersion)"
    "signatureStatus=$($sig.Status)"
    "signer=$($sig.SignerCertificate.Subject)"
    "thumbprint=$thumb"
) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $InstallDir 'build-info.txt') -Value $manifest -Encoding UTF8
# sha256sum 兼容格式，方便跟 Release 上的 .sha256 对拍
Set-Content -LiteralPath "$target.sha256" -Value ("{0}  {1}" -f $hash, 'task_monitor.exe') -Encoding ascii

Write-Host ''
Write-Host "installed : $target"
Write-Host "version   : $($version.FileVersion) / $($version.ProductVersion)"
Write-Host "sha256    : $hash"
Write-Host "signature : $($sig.Status)  signer=$($sig.SignerCertificate.Subject)"
if ($sig.Status -ne 'Valid') {
    throw "signature did not validate (status=$($sig.Status)) — check the trust stores"
}
