# 一键打包：Release 构建 → 解析版本号 → 在 dist\ 产出单文件 exe + sha256（可选 zip、Authenticode 签名）。
# 人类与 CI 共用；CI 的签名凭证全部经环境变量交给 tools/sign.ps1（见其顶部的证书来源列表）。
# 兼容 Windows PowerShell 5.1 与 PowerShell 7：不使用三元运算符、??、-AsHashtable 等 PS7 专有语法。
[CmdletBinding()]
param(
    # 构建配置，默认 Release（Debug 产物不会被发布）
    [string]$Configuration = 'Release',
    # 产物目录，默认 <repo>\dist
    [string]$OutputDir = '',
    # 覆盖版本号；默认先读 csproj 的 <InformationalVersion>，没有再读 <Version>
    [string]$Version = '',
    # 额外生成 zip（内含 exe + sha256）
    [switch]$Zip,
    # 经 tools/sign.ps1 -Require 签名；请求了签名却没配置证书时直接失败，绝不悄悄发未签名二进制
    [switch]$Sign,
    # 跳过 dotnet build（复用已有构建产物）
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'dist' }
if (-not [IO.Path]::IsPathRooted($OutputDir)) { $OutputDir = Join-Path $repo $OutputDir }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# 只读 csproj 文本取属性；版本号唯一来源仍是 csproj，本脚本不改它。
function Read-CsprojProperty([string]$Path, [string]$Name) {
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    $m = [regex]::Match($text, "<$Name>\s*([^<]+?)\s*</$Name>")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ''
}

# 清单里记仓库相对路径（用 / 分隔），CI 的后续步骤直接消费，不再自己拼产物名。
# 不能用 [IO.Path]::GetRelativePath —— 那是 .NET Core 才有的 API，PS 5.1 跑不了。
function Get-RepoRelative([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root)
    $sep = [string][IO.Path]::DirectorySeparatorChar
    if (-not $rootFull.EndsWith($sep)) { $rootFull = $rootFull + $sep }
    if ($full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($rootFull.Length).Replace('\', '/')
    }
    return $full
}

try {
    # 1. 构建（stdout 直接流到控制台，失败按退出码判定）
    if (-not $SkipBuild) {
        Write-Host "==> dotnet build -c $Configuration"
        Push-Location -LiteralPath $repo
        try {
            & dotnet build -c $Configuration
            $buildExit = $LASTEXITCODE
        } finally {
            Pop-Location
        }
        if ($buildExit -ne 0) { throw "dotnet build 失败（exit $buildExit）" }
    }

    # Costura.Fody 已把依赖全部织进 exe，构建输出的单文件就是发布件
    $exe = Join-Path $repo "bin\$Configuration\net48\task_monitor.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "构建产物不存在：$exe（先 dotnet build -c $Configuration，或去掉 -SkipBuild）"
    }

    # 2. 解析版本号：-Version > <InformationalVersion> > <Version>
    $ver = $Version.Trim()
    $csproj = Join-Path $repo 'task_monitor.csproj'
    if (-not $ver) {
        foreach ($prop in @('InformationalVersion', 'Version')) {
            $value = Read-CsprojProperty $csproj $prop
            if ($value) { $ver = $value; break }
        }
    }
    if (-not $ver) { throw "无法确定版本号：$csproj 缺少 <InformationalVersion>/<Version>，请用 -Version 指定" }
    if ($ver -match '[\\/:*?"<>|]') { throw "版本号含非法路径字符：$ver" }

    # 3. 复制成带版本的产物名
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $baseName = "task_monitor-$ver-x64"
    $exeOut = Join-Path $OutputDir "$baseName.exe"
    $shaOut = "$exeOut.sha256"
    $zipOut = Join-Path $OutputDir "$baseName.zip"
    $signingOut = Join-Path $OutputDir "$baseName.signing.txt"
    $manifestOut = Join-Path $OutputDir 'pack-manifest.txt'

    Copy-Item -LiteralPath $exe -Destination $exeOut -Force
    Write-Host "==> copied $exeOut"

    # 4. 签名（必须在算 sha256 之前：发布出去的校验和必须对应发布出去的二进制）
    $signed = $false
    if ($Sign) {
        $signParams = @{ File = $exeOut; Require = $true }
        # CI 把 base64 的 pfx 放进 SIGN_PFX_BASE64；sign.ps1 自己解码到 $env:TEMP 临时文件并在 finally 删除
        if ($env:SIGN_PFX_BASE64) { $signParams['PfxBase64'] = $env:SIGN_PFX_BASE64 }
        & (Join-Path $PSScriptRoot 'sign.ps1') @signParams

        $sig = Get-AuthenticodeSignature -LiteralPath $exeOut
        if (-not $sig.SignerCertificate) { throw "签名后仍无签名证书：$exeOut（sign.ps1 应已抛错）" }
        if ($sig.Status -eq 'HashMismatch') { throw "签名后哈希不匹配：$exeOut" }
        $signed = $true

        $timestamped = $false
        if ($sig.TimeStamperCertificate) { $timestamped = $true }
        $signingLines = @(
            "File: $(Split-Path -Leaf $exeOut)"
            "Subject: $($sig.SignerCertificate.Subject)"
            "Thumbprint: $($sig.SignerCertificate.Thumbprint)"
            "Status: $($sig.Status)"
            "Timestamped: $(if ($timestamped) { 'yes' } else { 'no' })"
        )
        [IO.File]::WriteAllLines($signingOut, [string[]]$signingLines, $utf8NoBom)
        Write-Host "==> signed: $($sig.SignerCertificate.Subject) (timestamp: $(if ($timestamped) { 'yes' } else { 'no' }))"
    }

    # 5. sha256（sha256sum 格式：大写哈希 + 两个空格 + 文件名）
    $hash = (Get-FileHash -LiteralPath $exeOut -Algorithm SHA256).Hash.ToUpperInvariant()
    [IO.File]::WriteAllText($shaOut, "$hash  $baseName.exe`n", $utf8NoBom)
    Write-Host "==> sha256: $hash"

    # 6. 可选 zip（exe + sha256）
    if ($Zip) {
        if (Test-Path -LiteralPath $zipOut) { Remove-Item -LiteralPath $zipOut -Force }
        Compress-Archive -LiteralPath @($exeOut, $shaOut) -DestinationPath $zipOut -CompressionLevel Optimal
        Write-Host "==> zipped $zipOut"
    }

    # 7. 机器可读清单：CI 唯一消费入口（产物名、校验和文件、签名状态）
    $manifest = [ordered]@{
        VERSION     = $ver
        EXE         = (Get-RepoRelative $exeOut $repo)
        SHA256      = $hash
        SHA256_FILE = (Get-RepoRelative $shaOut $repo)
        ZIP         = ''
        SIGNED      = $(if ($signed) { 'yes' } else { 'no' })
    }
    if ($Zip) { $manifest['ZIP'] = (Get-RepoRelative $zipOut $repo) }
    if ($signed) { $manifest['SIGNING_FILE'] = (Get-RepoRelative $signingOut $repo) }

    $manifestLines = foreach ($key in $manifest.Keys) { "$key=$($manifest[$key])" }
    [IO.File]::WriteAllLines($manifestOut, [string[]]$manifestLines, $utf8NoBom)

    # 8. 摘要表
    $rows = @(
        [pscustomobject]@{
            Path   = (Get-RepoRelative $exeOut $repo)
            SizeMB = [math]::Round((Get-Item -LiteralPath $exeOut).Length / 1MB, 2)
            SHA256 = $hash
            Signed = $manifest['SIGNED']
        }
    )
    if ($Zip) {
        $rows += [pscustomobject]@{
            Path   = (Get-RepoRelative $zipOut $repo)
            SizeMB = [math]::Round((Get-Item -LiteralPath $zipOut).Length / 1MB, 2)
            SHA256 = ''
            Signed = $manifest['SIGNED']
        }
    }
    Write-Host ''
    Write-Host "==> packed version $ver ($Configuration)"
    $rows | Format-Table -AutoSize | Out-String -Width 4096 | Write-Host
    Write-Host "manifest: $manifestOut"
}
catch {
    Write-Host "PACK FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
