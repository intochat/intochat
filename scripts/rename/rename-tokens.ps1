#requires -Version 7
<#
Ordered token map for the DigitalBrain -> IntoChat.OS rename.

  pwsh scripts/rename/rename-tokens.ps1 -Stage rename -DryRun -Report <file>   # writes a report, changes nothing
  pwsh scripts/rename/rename-tokens.ps1 -Stage rename                           # git mv + content edits
  pwsh scripts/rename/rename-tokens.ps1 -Stage config                           # config prefix (Plan 05)

Stage rename leaves untouched (Plans 04 / 05): config prefixes (DigitalBrain: / DigitalBrain__), config-root literals,
digitalbrain-master-key / digitalbrain-bootstrap-password, Dart pub names and barrel files (digitalbrain_*), the proto package,
generated neurons.pb*.dart, pubspec.lock, the Windows runner, and the frozen crypto strings.
The brand tokens in this file are assembled from fragments so the file never matches the grep gate it supports.
#>
param(
    [ValidateSet('rename', 'config')][string]$Stage = 'rename',
    [switch]$DryRun,
    [string]$Report
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$UpB = 'Digital' + 'Brain'          # DigitalBrain
$LoB = 'digital' + 'brain'          # digitalbrain
$CamB = 'digital' + 'Brain'          # digitalBrain
$AllB = 'DIGITAL' + 'BRAIN'          # DIGITALBRAIN
$root = (git rev-parse --show-toplevel).Trim()
Set-Location $root

function NewRule([string]$id, [string]$pattern, [string]$replacement) {
    [pscustomobject]@{ Id = $id; Rx = [regex]::new($pattern); Rep = $replacement }
}
$L = '(?<![A-Za-z0-9_])'   # not preceded by an identifier character
$N = '(?![A-Za-z0-9_])'    # not followed by an identifier character

# Protected fragments (restored verbatim): frozen crypto strings, entity name, external names, Plan 04/05 scopes.
$protectLiterals = @(
    "${UpB}.OwnerKeys.v3", "${UpB} deployment key binding v1", 'Digital Brain Tech', "${LoB}.docs", "E:/intochat/${LoB}/", "${LoB}.tech",
    "${LoB}.Neurons", "package ${LoB};", "ui.${LoB}.io", "${LoB}-master-key", "${LoB}-bootstrap-password"
)
# Config prefixes stay for the config stage (Plan 05).
$protectConfig = @(
    [regex]::new("${UpB}(?=__)"), [regex]::new("${UpB}(?=:[A-Za-z])")
)

$renameRules = @(
    (NewRule 'R1 slnx'            ([regex]::Escape("${UpB}.slnx")) 'Intochat.OS.slnx'),
    (NewRule 'R2 env family'      ([regex]::Escape("${AllB}_")) 'INTOCHATOS_'),
    (NewRule 'H1 X- header'       "X-${UpB}-" 'X-IntoChatOS-'),
    (NewRule 'H2 x- header'       "x-${LoB}-" 'x-intochatos-'),
    (NewRule 'UA user agent'      "(?<![A-Za-z0-9_./])${UpB}/(?=\d)" 'IntoChatOS/'),
    (NewRule 'K1 kernel host'     "${L}${UpB}\.OS$N" 'IntoChat.OS.Kernel'),
    (NewRule 'K1b kernel aspire'  "${L}${UpB}_OS(?![A-Za-z0-9])" 'IntoChat_OS_Kernel'),
    (NewRule 'R5 aspire name'     "(?<![A-Za-z0-9])${UpB}_" 'IntoChat_OS_'),
    (NewRule 'P1a plural I'       "I${UpB}s" 'IIntoChatOSBrains'),
    (NewRule 'P1b plural'         "${UpB}s" 'IntoChatOSBrains'),
    (NewRule 'R6 dotted'          "${L}${UpB}\.(?=[A-Za-z])" 'IntoChat.OS.'),
    (NewRule 'R7 identifier'      "(?<=[A-Za-z0-9_])${UpB}|${UpB}(?=[A-Za-z0-9_])" 'IntoChatOS'),
    (NewRule 'R8 bare word'       "${L}${UpB}$N" 'IntoChat.OS'),
    (NewRule 'L1 state container' "${LoB}-v2-state" 'intochat-os-state'),
    (NewRule 'L2 launcher'        "${LoB}\.cs" 'intochat-os.cs'),
    (NewRule 'L3a db name'        "(?<=(?:DatabaseName = |PostgresDatabase = )`")${LoB}" 'intochat_os'),
    (NewRule 'L3b db name'        "(?<=Database=)${LoB}" 'intochat_os'),
    (NewRule 'L4 state dir'       "\.${LoB}" '.intochat-os'),
    (NewRule 'L5 dash'            "${LoB}-" 'intochat-os-'),
    (NewRule 'L6 dotted'          "${LoB}\.(?=[A-Za-z])" 'intochatos.'),
    (NewRule 'L7 bare lower'      "${L}${LoB}$N" 'intochat-os'),
    (NewRule 'L8 camel'           ${CamB} 'intoChatOS')
)
$configRules = @(
    (NewRule 'C3 env prefix'      "${UpB}__" 'IntoChatOS__'),
    (NewRule 'C4 config key'      "${UpB}(?=:[A-Za-z])" 'IntoChatOS'),
    (NewRule 'C5 master key'      "${LoB}-master-key" 'intochat-os-master-key'),
    (NewRule 'C6 bootstrap pw'    "${LoB}-bootstrap-password" 'intochat-os-bootstrap-password')
)
$pathRules = @(
    $renameRules | Where-Object { $_.Id -in @('R1 slnx', 'K1 kernel host', 'P1a plural I', 'P1b plural', 'R6 dotted', 'R7 identifier', 'R8 bare word', 'L2 launcher') }
)

# --- exceptions table ---------------------------------------------------------------------------
$exceptions = @()
foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'rename-exceptions.tsv') -Encoding UTF8) {
    if ($line.StartsWith('#') -or [string]::IsNullOrWhiteSpace($line)) { continue }
    $f = $line -split "`t"
    if ($f.Count -lt 5) { throw "bad exception row: $line" }
    $exceptions += [pscustomobject]@{ Path = $f[0]; Literal = $f[1]; Replacement = $f[2]; Stage = $f[3]; Reason = $f[4]; Hits = 0 }
}

$counts = [ordered]@{}
function Count([string]$id, [int]$n) { if ($n -gt 0) { $counts[$id] = ($counts[$id] ?? 0) + $n } }

# --- file selection -------------------------------------------------------------------------------
$binaryExt = '\.(png|jpe?g|gif|ico|webp|woff2?|ttf|otf|eot|zip|gz|nupkg|dll|exe|pdb|snk|pfx|bin|mp3|mp4|wav|jar|so|dylib|pdf|svgz|xlsx|docx)$'
function SkipContent([string]$p) {
    if ($p -match $binaryExt) { return $true }
    if ($p -like 'scripts/rename/*') { return $true }
    if ($p -match '/Storage/Fixtures/bdb79fc21/' -and $p -notmatch 'fixture\.json$') { return $true }
    if ($p -match 'neurons\.pb[a-z]*\.dart$') { return $true }
    if ($p -match '(^|/)pubspec\.lock$') { return $true }
    if ($p -match '/app/shell/windows/') { return $true }
    return $false
}

$allFiles = @(git ls-files) | Where-Object { $_ }

# --- path plan ---------------------------------------------------------------------------------------
function MapSegment([string]$s) {
    foreach ($r in $pathRules) { $s = $r.Rx.Replace($s, $r.Rep) }
    $s
}
$pathMap = [ordered]@{}   # old -> new
foreach ($p in $allFiles) {
    $segs = $p -split '/'
    $new = ($segs | ForEach-Object { MapSegment $_ }) -join '/'
    $pathMap[$p] = $new
}
$moved = @($pathMap.GetEnumerator() | Where-Object { $_.Key -ne $_.Value })

# targets must not collide (case-insensitive) with each other or with an unmoved path
$seen = @{}
foreach ($kv in $pathMap.GetEnumerator()) {
    $k = $kv.Value.ToLowerInvariant()
    if ($seen.ContainsKey($k)) { throw "path collision: $($kv.Key) and $($seen[$k]) -> $($kv.Value)" }
    $seen[$k] = $kv.Key
}
# directory renames (deepest first) and file renames
$dirMoves = @{}
$fileMoves = @()
foreach ($kv in $moved) {
    $segs = $kv.Key -split '/'; $nsegs = $kv.Value -split '/'
    for ($i = 0; $i -lt $segs.Count - 1; $i++) {
        if ($segs[$i] -ne $nsegs[$i]) {
            $oldDir = ($segs[0..$i]) -join '/'
            $parentOld = if ($i -gt 0) { ($segs[0..($i - 1)] -join '/') + '/' } else { '' }
            $newDir = $parentOld + $nsegs[$i]
            $dirMoves[$oldDir] = @{ Old = $oldDir; New = $newDir; Depth = $i }
        }
    }
    if ($segs[-1] -ne $nsegs[-1]) {
        $parent = if ($segs.Count -gt 1) { ($segs[0..($segs.Count - 2)] -join '/') + '/' } else { '' }
        $fileMoves += [pscustomobject]@{ Old = $kv.Key; New = $parent + $nsegs[-1] }
    }
}
$dirList = @($dirMoves.Values | Sort-Object { - $_.Depth }, { $_.Old })

# --- content transform ---------------------------------------------------------------------------------
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
$phStart = [char]0xE000; $phEnd = [char]0xE001
$phRx = [regex]::new("$phStart(\d+)$phEnd")

function Transform([string]$text, [string]$oldPath, [string[]]$extraLog) {
    $store = [System.Collections.Generic.List[string]]::new()
    $ph = { param([string]$original) $store.Add($original); "$phStart$($store.Count - 1)$phEnd" }
    $t = $text
    # exception rows
    foreach ($e in $exceptions) {
        if (-not ($oldPath -eq $e.Path -or $oldPath.EndsWith('/' + $e.Path))) { continue }
        if (-not $t.Contains($e.Literal)) { continue }
        $n = ($t.Length - $t.Replace($e.Literal, '').Length) / $e.Literal.Length
        $e.Hits += $n
        $keep = if (($Stage -eq 'rename' -and $e.Stage -eq 'rename') -or ($Stage -eq 'config' -and $e.Stage -eq 'config')) { $e.Replacement } else { $e.Literal }
        if ($Stage -eq 'rename' -and $e.Stage -eq 'config') { $keep = $e.Literal }
        if ($Stage -eq 'config' -and $e.Stage -eq 'rename') { continue }
        $t = $t.Replace($e.Literal, (& $ph $keep))
    }
    if ($Stage -eq 'rename') {
        foreach ($lit in $protectLiterals) {
            if ($t.Contains($lit)) { $t = $t.Replace($lit, (& $ph $lit)) }
        }
        foreach ($rx in $protectConfig) {
            $t = $rx.Replace($t, { param($m) & $ph $m.Value })
        }
        if ($oldPath -eq '.gitignore') {
            $t = [regex]::Replace($t, "(?m)^\.${LoB}/(?=\r?$)", { param($m) & $ph $m.Value })
        }
    }
    $rules = if ($Stage -eq 'rename') { $renameRules } else { $configRules }
    foreach ($r in $rules) {
        $m = $r.Rx.Matches($t).Count
        if ($m -gt 0) { Count $r.Id $m; $t = $r.Rx.Replace($t, $r.Rep) }
    }
    $t = $phRx.Replace($t, { param($m) $store[[int]$m.Groups[1].Value] })
    if ($Stage -eq 'rename' -and $oldPath -eq '.gitignore' -and -not $t.Contains('.intochat-os/')) {
        $eol = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
        $t = $t -replace "(?m)^\.${LoB}/(\r?)$", ".${LoB}/$eol.intochat-os/`$1"
    }
    $t
}

# --- run -------------------------------------------------------------------------------------------------
$edited = [System.Collections.Generic.List[string]]::new()
$skippedNonUtf8 = [System.Collections.Generic.List[string]]::new()
$residual = [System.Collections.Generic.List[string]]::new()
$brandRx = [regex]::new('digital[ _-]?brain', 'IgnoreCase')

if (-not $DryRun -and $Stage -eq 'rename') {
    foreach ($f in ($fileMoves | Sort-Object { - ($_.Old.Split('/').Count) })) { $o = git mv -- $f.Old $f.New 2>&1; if ($LASTEXITCODE -ne 0) { throw "git mv failed: $($f.Old) -> $($f.New): $o" } }
    foreach ($d in $dirList) { $o = git mv -- $d.Old $d.New 2>&1; if ($LASTEXITCODE -ne 0) { throw "git mv failed: $($d.Old) -> $($d.New): $o" } }
}

foreach ($old in $allFiles) {
    if (SkipContent $old) { continue }
    $new = if ($Stage -eq 'rename') { $pathMap[$old] } else { $old }
    $read = if ($DryRun -or $Stage -eq 'config') { $old } else { $new }
    if ($Stage -eq 'config') { $read = $old }
    if (-not (Test-Path -LiteralPath $read)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($read)
    if ($bytes.Length -eq 0) { continue }
    if ([Array]::IndexOf($bytes, [byte]0) -ge 0) { continue }
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    try { $text = $utf8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 })) }
    catch { $skippedNonUtf8.Add($old); continue }
    if ($text.IndexOf(${LoB}, [StringComparison]::OrdinalIgnoreCase) -lt 0 -and $text.IndexOf('Digital Brain', [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
    $out = Transform $text $old @()
    if ($out -ne $text) {
        $edited.Add($old)
        if (-not $DryRun) {
            $enc = [System.Text.UTF8Encoding]::new($bom)
            $body = $enc.GetBytes($out)
            $final = if ($bom) { [byte[]](0xEF, 0xBB, 0xBF) + $body } else { $body }
            [System.IO.File]::WriteAllBytes($read, $final)
        }
    }
    $left = $brandRx.Matches($out)
    if ($left.Count -gt 0) {
        $tokens = ($left | ForEach-Object { $_.Value } | Sort-Object -Unique) -join ','
        $other = foreach ($m in $left) {
            $after = $out.Substring($m.Index + $m.Length, [Math]::Min(6, $out.Length - $m.Index - $m.Length))
            $isConfig = $m.Value -ceq $UpB -and ($after -match '^(__|:[A-Za-z]|"\s*:)')
            $isLowerKnown = $m.Value -ceq $LoB -and ($after -match '^(-master-key|-bootstrap-password|\.docs|\.tech|\.Neurons|;|\.io|_)')
            if (-not $isConfig -and -not $isLowerKnown) {
                $a = [Math]::Max(0, $m.Index - 25); $len = [Math]::Min(60, $out.Length - $a)
                ($out.Substring($a, $len) -replace '[
]', ' ')
            }
        }
        $residual.Add("$old`t$($left.Count)`t$tokens`t$(@($other).Count)`t$(@($other | Select-Object -First 3) -join ' || ')")
    }
}

if ($Report) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# rename-tokens report: stage=$Stage dryRun=$($DryRun.IsPresent)")
    $lines.Add("tracked files: $($allFiles.Count)")
    $lines.Add("files with content edits: $($edited.Count)")
    $lines.Add("paths changed: $($moved.Count)  directory moves: $($dirList.Count)  file moves: $($fileMoves.Count)")
    $lines.Add('')
    $lines.Add('## rule hit counts')
    foreach ($k in $counts.Keys) { $lines.Add(("{0,8}  {1}" -f $counts[$k], $k)) }
    $lines.Add('')
    $lines.Add('## exception-table rows (hits)')
    foreach ($e in $exceptions) { $lines.Add(("{0,4}  [{1}] {2} :: {3}" -f $e.Hits, $e.Stage, $e.Path, $e.Literal)) }
    $lines.Add('')
    $lines.Add("## directory moves (deepest first)")
    foreach ($d in $dirList) { $lines.Add("$($d.Old) => $($d.New)") }
    $lines.Add('')
    $lines.Add('## file moves (basename changes)')
    foreach ($f in $fileMoves) { $lines.Add("$($f.Old) => $($f.New)") }
    $lines.Add('')
    $lines.Add('## non-UTF8 files skipped')
    foreach ($s in $skippedNonUtf8) { $lines.Add($s) }
    $lines.Add('')
    $lines.Add('## path residuals (target path still contains the brand)')
    foreach ($kv in $moved) { if ($brandRx.IsMatch($kv.Value)) { $lines.Add("$($kv.Key) => $($kv.Value)") } }
    foreach ($kv in $pathMap.GetEnumerator()) { if ($kv.Key -eq $kv.Value -and $brandRx.IsMatch($kv.Key)) { $lines.Add("(unchanged) $($kv.Key)") } }
    $lines.Add('')
    $lines.Add('## content residuals: path <TAB> count <TAB> distinct tokens')
    foreach ($r in $residual) { $lines.Add($r) }
    $lines.Add('')
    $lines.Add('## files edited')
    foreach ($e in $edited) { $lines.Add($e) }
    [System.IO.File]::WriteAllLines($Report, $lines)
}
Write-Host "stage=$Stage dryRun=$($DryRun.IsPresent) edited=$($edited.Count) moved=$($moved.Count) dirs=$($dirList.Count) residualFiles=$($residual.Count)"
