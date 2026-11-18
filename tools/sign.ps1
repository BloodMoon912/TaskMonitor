# 对单个文件做 Authenticode 签名（sha256 摘要 + RFC3161 时间戳），签完用 signtool verify 校验。
# 证书来源按下列顺序查找，只用第一个命中的（密码只从参数/环境变量进，绝不打印、绝不落盘）。
#   1. -PfxPath                     本地 .pfx
#   2. -PfxBase64                   .pfx 的 base64（解码到 $env:TEMP 临时文件，结束即删）
#   3. $env:SIGN_PFX_BASE64         CI 用：仓库 secret 原样塞进环境变量
#   4. $env:SIGN_PFX + SIGN_PFX_PASSWORD
#   5. -Thumbprint                  用户证书存储里已安装的证书
#   6. $env:SIGN_CERT_THUMBPRINT
# 没配置证书时：默认警告并跳过（exit 0）；加 -Require 则抛错（打包/发布链路必须失败）。
[CmdletBinding()]
param(
    # 要签名的文件
    [Parameter(Mandatory)][string]$File,
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    [string]$PfxBase64 = '',
    # 用户证书存储中的证书指纹（Cert:\CurrentUser\My）
    [string]$Thumbprint = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Description = 'TaskMonitor',
    [string]$DescriptionUrl = 'https://github.com/BloodMoon912/TaskMonitor',
    # 缺证书时是否视为错误
    [switch]$Require
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$target = (Resolve-Path -LiteralPath $File -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "not a file: $target" }

# signtool 解析：PATH 优先，否则 Windows SDK 里取版本号最大的 x64 构建
function Resolve-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $root = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path -LiteralPath $root) {
        $best = $null
        $bestVer = [version]'0.0'
        foreach ($dir in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            $v = [version]'0.0'
            [void][version]::TryParse($dir.Name, [ref]$v)
            if ($v -gt $bestVer) { $bestVer = $v; $best = $dir }
        }
        if ($best) {
            $candidate = Join-Path $best.FullName 'x64\signtool.exe'
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    return ''
}

$signtool = Resolve-SignTool
if (-not $signtool) {
    $msg = 'signtool.exe 未找到（PATH 与 Windows SDK 都没有）—— 无法签名'
    if ($Require) { throw $msg }
    Write-Warning "$msg；跳过签名"
    return
}
Write-Host "signtool: $signtool"

# 把 base64 的 pfx 解码到临时文件；返回路径，调用方负责 finally 删除
function New-TempPfx([string]$Base64) {
    $path = Join-Path $env:TEMP ("task_monitor_sign_" + [guid]::NewGuid().ToString('N') + ".pfx")
    $bytes = [Convert]::FromBase64String($Base64)
    [IO.File]::WriteAllBytes($path, $bytes)
    return $path
}

$tempPfx = ''
try {
    # ---------------------------------------------------------------- 证书来源（按优先级）
    $certFile = ''
    $certPassword = ''
    $certThumb = ''

    if ($PfxPath) {
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX 不存在：$PfxPath" }
        $certFile = (Resolve-Path -LiteralPath $PfxPath).ProviderPath
        $certPassword = $PfxPassword
    }
    elseif ($PfxBase64) {
        $tempPfx = New-TempPfx $PfxBase64
        $certFile = $tempPfx
        $certPassword = $PfxPassword
    }
    elseif ($env:SIGN_PFX_BASE64) {
        $tempPfx = New-TempPfx $env:SIGN_PFX_BASE64
        $certFile = $tempPfx
        $certPassword = $PfxPassword
    }
    elseif ($env:SIGN_PFX) {
        if (-not (Test-Path -LiteralPath $env:SIGN_PFX)) { throw "SIGN_PFX 指向的文件不存在：$env:SIGN_PFX" }
        $certFile = (Resolve-Path -LiteralPath $env:SIGN_PFX).ProviderPath
        $certPassword = $env:SIGN_PFX_PASSWORD
    }
    elseif ($Thumbprint) {
        $certThumb = $Thumbprint
    }
    elseif ($env:SIGN_CERT_THUMBPRINT) {
        $certThumb = $env:SIGN_CERT_THUMBPRINT
    }

    if (-not $certFile -and -not $certThumb) {
        $hints = @(
            '未配置签名证书，无法签名：' + $target
            '可用以下任一方式提供：'
            '  -PfxPath <file.pfx> -PfxPassword <password>'
            '  -PfxBase64 <base64 of .pfx>'
            '  -Thumbprint <CurrentUser\My 中的证书指纹>'
            '  环境变量 SIGN_PFX_BASE64（CI 用）'
            '  环境变量 SIGN_PFX + SIGN_PFX_PASSWORD'
            '  环境变量 SIGN_CERT_THUMBPRINT'
        ) -join [Environment]::NewLine
        if ($Require) { throw $hints }
        Write-Warning "$hints`n（未加 -Require，跳过签名）"
        return
    }

    # ---------------------------------------------------------------- 签名
    # 命令行里带密码，但绝不回显：这里不打印参数，signtool 自己也不回显参数
    $signArgs = @('sign', '/fd', 'sha256', '/td', 'sha256', '/tr', $TimestampUrl, '/d', $Description, '/du', $DescriptionUrl)
    if ($certFile) {
        $signArgs += @('/f', $certFile)
        if ($certPassword) { $signArgs += @('/p', $certPassword) }
    } else {
        $signArgs += @('/sha1', $certThumb)
    }
    $signArgs += $target

    Write-Host "==> signing: $target"
    # 原生命令的 stderr 在 PS 5.1 + ErrorActionPreference=Stop 下会被当成终止错误，临时放宽
    $savedEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $signtool @signArgs
        $signExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedEap
    }
    if ($signExit -ne 0) { throw "signtool sign 失败（exit $signExit）：$target" }

    # ---------------------------------------------------------------- 校验
    $ErrorActionPreference = 'Continue'
    try {
        $verifyOut = @(& $signtool verify /pa /v $target 2>&1 | ForEach-Object { "$_" })
        $verifyExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedEap
    }
    if ($verifyExit -ne 0) {
        throw ("signtool verify 失败（exit $verifyExit）：`n" + ($verifyOut -join [Environment]::NewLine))
    }
    if (-not ($verifyOut -match 'Successfully verified|成功验证')) {
        Write-Warning "signtool verify 成功退出，但输出里没有 'Successfully verified'，请人工复核"
    }

    # ---------------------------------------------------------------- 结果
    $auth = Get-AuthenticodeSignature -LiteralPath $target
    if (-not $auth.SignerCertificate) { throw "签名后仍读不到签名证书：$target" }
    $hasTimestamp = $false
    if ($auth.TimeStamperCertificate) { $hasTimestamp = $true }

    Write-Host "==> signed OK: $target"
    Write-Host "    subject:   $($auth.SignerCertificate.Subject)"
    Write-Host "    thumbprint: $($auth.SignerCertificate.Thumbprint)"
    Write-Host "    status:    $($auth.Status)"
    Write-Host "    timestamp: $(if ($hasTimestamp) { 'present' } else { 'missing' })"
}
finally {
    # 临时 pfx 用完即删，不留明文私钥
    if ($tempPfx -and (Test-Path -LiteralPath $tempPfx)) {
        Remove-Item -LiteralPath $tempPfx -Force -ErrorAction SilentlyContinue
    }
}
