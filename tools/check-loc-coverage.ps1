#Requires -Version 7.0
<#
.SYNOPSIS
    Localisation coverage probe: which Chinese UI strings in the source are NOT keys in Loc.cs.

.DESCRIPTION
    Loc (src/Localization/Loc.cs) is a dictionary-style i18n: the Simplified-Chinese source
    string IS the key. This script

      1. parses every `Table(new string[] { ... })` block in Loc.cs and collects the keys
         (strings at even offsets — key, value, key, value …),
      2. scans src/**/*.xaml (attribute values and element text, XML comments stripped) and
         src/**/*.cs (string literals, // and /* */ comments stripped) for literals that
         contain a CJK character — Loc.cs itself is excluded, because its values ARE the
         translations, not consumers of them,
      3. prints the literals that appear in code but have no key in the catalogue, most
         frequent first, and a summary.

    Run it from the repository root:

        pwsh tools/check-loc-coverage.ps1

.PARAMETER Root
    Repository root. Defaults to the parent directory of this script.

.PARAMETER Limit
    How many uncovered strings to list. Default 200.

.PARAMETER XamlOnly
    Only scan .xaml files (the user-visible UI definitions) — the tightest regression metric.

.PARAMETER ExcludeLogs
    Drop .cs literals on lines that look like logging (Logger.*/Log(...)) — those stay Chinese
    on purpose, so counting them as "uncovered UI" overstates the gap.

.PARAMETER FailOnMissing
    Exit with code 3 when anything is uncovered (for CI gating).

.EXAMPLE
    pwsh tools/check-loc-coverage.ps1 -XamlOnly
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [int]$Limit = 200,
    [switch]$XamlOnly,
    [switch]$ExcludeLogs,
    [switch]$FailOnMissing
)

$ErrorActionPreference = 'Stop'
$script:Culture = [System.Globalization.CultureInfo]::InvariantCulture
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# CJK ideographs + CJK punctuation + fullwidth forms. Covers the UI strings used here
# (、。（）：“”… and fullwidth punctuation included).
$script:Cjk = [regex]::new('[\u3000-\u303F\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFE30-\uFE4F\uFF00-\uFFEF]')

function Test-Cjk([string]$value) {
    if ([string]::IsNullOrEmpty($value)) { return $false }
    return $script:Cjk.IsMatch($value)
}

$locPath = Join-Path $Root 'src/Localization/Loc.cs'
if (-not (Test-Path -LiteralPath $locPath)) {
    throw "Loc.cs not found: $locPath (pass -Root <repo root>)"
}

# ---------------------------------------------------------------- catalogue keys
# The catalogue is now one YAML file per language (lang/<code>.yaml), loaded as an embedded
# resource; Loc.cs no longer contains the tables. Keys are the top-level mapping keys, and the
# zh-CN file is the authority for "what ids exist" — the other files are translations of it.
$langDir = Join-Path $Root 'lang'
if (-not (Test-Path -LiteralPath $langDir)) {
    throw "lang directory not found: $langDir (pass -Root <repo root>)"
}

$keys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$catalogueValues = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

foreach ($file in Get-ChildItem -LiteralPath $langDir -Filter '*.yaml' -File) {
    $isSource = $file.BaseName -eq 'zh-CN'
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding utf8) {
        # "id: \"value\"" — ids never contain spaces or colons; indented lines are nested and
        # comments start with #. Values are always double-quoted by the catalogue convention.
        $m = [regex]::Match($line, '^\s*(?<id>[A-Za-z0-9_.-]+)\s*:\s*"(?<value>(?:[^"\\]|\\.)*)"\s*$')
        if (-not $m.Success) { continue }
        [void]$keys.Add($m.Groups['id'].Value)
        if ($isSource) { [void]$catalogueValues.Add($m.Groups['value'].Value) }
    }
}

if ($keys.Count -eq 0) { throw "No catalogue ids found under $langDir" }

# ---------------------------------------------------------------- source scanning
$files = Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Recurse -File |
    Where-Object {
        $_.Extension -in '.xaml', '.cs' -and
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
    }
if ($XamlOnly) { $files = $files | Where-Object { $_.Extension -eq '.xaml' } }

# The catalogue itself is not a consumer: its traditional-Chinese/English VALUES are the
# translations, so counting them as "missing from Loc.cs" is noise that grows with every key
# added. Only sources that CALL the catalogue are scanned.
$catalogueFull = [IO.Path]::GetFullPath($locPath)
$files = $files | Where-Object { [IO.Path]::GetFullPath($_.FullName) -ne $catalogueFull }

$found = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
$scanned = 0

function Add-Hit([string]$text, [string]$file, [int]$line, [string]$kind, [bool]$isLog) {
    $key = $text.Trim()
    if (-not (Test-Cjk $key)) { return }
    # A literal that already appears in the zh-CN catalogue is either routed through an id or
    # is a value the text→id fallback still resolves, so it is not an uncovered string. Ids
    # themselves never carry CJK, so matching the values is the whole test.
    if ($catalogueValues.Contains($key) -or $keys.Contains($key)) { return }
    $script:scanned++
    if ($ExcludeLogs -and $isLog) { return }

    $entry = $null
    if (-not $found.TryGetValue($key, [ref]$entry)) {
        $entry = [pscustomobject]@{ Text = $key; Count = 0; Kind = $kind; Files = [System.Collections.Generic.List[string]]::new() }
        $found[$key] = $entry
    }
    $entry.Count++
    if ($entry.Files.Count -lt 3 -and -not $entry.Files.Contains($file)) { $entry.Files.Add($file) }
}

# .xaml: strip XML comments, then take quoted attribute values and element text.
foreach ($file in ($files | Where-Object { $_.Extension -eq '.xaml' })) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
    $rel = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')

    # Blank out comments but keep the line structure so reported line numbers stay true.
    $stripped = [regex]::Replace($text, '(?s)<!--.*?-->', { param($m) ($m.Value -replace '[^\r\n]', ' ') })

    foreach ($m in [regex]::Matches($stripped, '(?<name>[A-Za-z_][\w:.]*)\s*=\s*"(?<val>[^"]*)"')) {
        $val = $m.Groups['val'].Value
        if (-not (Test-Cjk $val)) { continue }
        $line = ($stripped.Substring(0, $m.Index) -split "`n").Count
        Add-Hit $val $rel $line ('xaml:' + $m.Groups['name'].Value) $false
    }
    foreach ($m in [regex]::Matches($stripped, '>(?<val>[^<>]+)<')) {
        $val = $m.Groups['val'].Value
        if (-not (Test-Cjk $val)) { continue }
        $line = ($stripped.Substring(0, $m.Index) -split "`n").Count
        Add-Hit $val.Trim() $rel $line 'xaml:text' $false
    }
}

# .cs: small lexer so // and /* */ comments are skipped but string literals are kept.
if (-not $XamlOnly) {
    foreach ($file in ($files | Where-Object { $_.Extension -eq '.cs' })) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        $rel = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        $n = $text.Length
        $i = 0
        $line = 1
        $lineStart = 0
        $sb = [System.Text.StringBuilder]::new()

        while ($i -lt $n) {
            $c = $text[$i]
            if ($c -eq "`n") { $line++; $lineStart = $i + 1; $i++; continue }

            # line comment
            if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') {
                while ($i -lt $n -and $text[$i] -ne "`n") { $i++ }
                continue
            }
            # block comment
            if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') {
                $i += 2
                while ($i + 1 -lt $n -and -not ($text[$i] -eq '*' -and $text[$i + 1] -eq '/')) {
                    if ($text[$i] -eq "`n") { $line++; $lineStart = $i + 1 }
                    $i++
                }
                $i += 2
                continue
            }
            # verbatim string @"..."
            if ($c -eq '@' -and $i + 1 -lt $n -and $text[$i + 1] -eq '"') {
                $startLine = $line
                $startIdx = $i
                $i += 2
                [void]$sb.Clear()
                while ($i -lt $n) {
                    if ($text[$i] -eq '"') {
                        if ($i + 1 -lt $n -and $text[$i + 1] -eq '"') { [void]$sb.Append('"'); $i += 2; continue }
                        $i++
                        break
                    }
                    if ($text[$i] -eq "`n") { $line++; $lineStart = $i + 1; [void]$sb.Append("`n") } else { [void]$sb.Append($text[$i]) }
                    $i++
                }
                $lit = $sb.ToString()
                $lineText = ($text.Substring($lineStart, [Math]::Min(200, $text.Length - $lineStart)) -split "`n")[0]
                Add-Hit $lit $rel $startLine 'cs:verbatim' ($lineText -match 'Logger\.|Log\(|Debug\.|Trace\.')
                continue
            }
            # regular string "..."
            if ($c -eq '"') {
                $startLine = $line
                $i++
                [void]$sb.Clear()
                while ($i -lt $n -and $text[$i] -ne '"') {
                    if ($text[$i] -eq '\' -and $i + 1 -lt $n) { [void]$sb.Append($text[$i]).Append($text[$i + 1]); $i += 2; continue }
                    if ($text[$i] -eq "`n") { $line++; $lineStart = $i + 1 }
                    [void]$sb.Append($text[$i])
                    $i++
                }
                $i++
                $lit = [System.Text.RegularExpressions.Regex]::Unescape($sb.ToString())
                $lineText = ($text.Substring($lineStart, [Math]::Min(200, $text.Length - $lineStart)) -split "`n")[0]
                Add-Hit $lit $rel $startLine 'cs:string' ($lineText -match 'Logger\.|Log\(|Debug\.|Trace\.')
                continue
            }
            $i++
        }
    }
}

# ---------------------------------------------------------------- report
$ordered = $found.Values | Sort-Object -Property @{ Expression = 'Count'; Descending = $true }, @{ Expression = 'Text'; Descending = $false }

Write-Host ''
Write-Host ("Loc coverage — root: {0}" -f $Root)
Write-Host ("Catalogue keys: {0}   Files scanned: {1}" -f $keys.Count, $files.Count)
Write-Host ("Uncovered strings: {0}   Occurrences: {1}" -f $found.Count, ($found.Values | Measure-Object -Property Count -Sum).Sum)
Write-Host ''

if ($found.Count -gt 0) {
    $width = [Math]::Min(90, ($ordered | Select-Object -First $Limit | ForEach-Object { $_.Text.Length } | Measure-Object -Maximum).Maximum)
    $fmt = '{0,4}  {1,-28}  {2}'
    Write-Host ($fmt -f 'HIT', 'KIND', 'STRING (missing from Loc.cs)')
    Write-Host ('-' * 96)
    $ordered | Select-Object -First $Limit | ForEach-Object {
        $s = $_.Text -replace "`r?`n", '\n'
        if ($s.Length -gt $width) { $s = $s.Substring(0, $width - 1) + '…' }
        Write-Host ($fmt -f $_.Count, $_.Kind, $s)
    }
    if ($found.Count -gt $Limit) {
        Write-Host ("... {0} more not shown (raise -Limit)" -f ($found.Count - $Limit))
    }
    Write-Host ''
    Write-Host 'Where the uncovered strings live:'
    $ordered | Group-Object -Property Kind | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  {0,-22} {1,4} strings" -f $_.Name, $_.Count)
    }
} else {
    Write-Host 'Every Chinese literal in scope has a catalogue key.'
}

if ($FailOnMissing -and $found.Count -gt 0) { exit 3 }
exit 0
