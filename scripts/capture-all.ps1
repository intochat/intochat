<#
.SYNOPSIS
  One command that regenerates every pinned contract artifact (RENAME-02), additive-only by default.

.DESCRIPTION
  Runs the Explicit capture facts through `dotnet test --project ... --explicit only` (no exe lookup, so it runs on
  Windows and Linux) and rewrites the committed pins in place. Before writing it snapshots every pinned JSON file;
  afterwards it compares old and new and, unless -AllowBreaking is given, restores the old files and fails when a
  key was removed or a value changed. Added entries are always accepted. With -Out the resulting artifacts are also
  copied to that directory under stable names.

  Brand-free on purpose: projects are found by glob. Never runs StorageFixtureCapture, CaptureSealedSegments or
  CaptureUiTransportBaseline.

.EXAMPLE
  pwsh scripts/capture-all.ps1
  pwsh scripts/capture-all.ps1 -AllowBreaking -Out /tmp/after
  pwsh scripts/capture-all.ps1 -AllowBreaking -KeysOnly

  -KeysOnly runs scripts/keys-only-check.ps1 on the snapshot and the regenerated pins: a rename may change keys,
  never values (D-23). On failure the pins are restored and the script exits 1.
#>
[CmdletBinding()]
param(
    [string]$Out,
    [switch]$AllowBreaking,
    [switch]$KeysOnly,
    [switch]$SkipSync,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (git rev-parse --show-toplevel).Trim()
if (-not $root) { throw 'Not inside a git repository.' }
Set-Location $root

function Find-One([string]$Pattern, [string]$Description) {
    $found = @(git ls-files -- $Pattern)
    if ($found.Count -ne 1) { throw "Expected exactly one $Description matching '$Pattern', found $($found.Count)." }
    return (Join-Path $root $found[0])
}

$archProject = Find-One 'src/**/*Architecture.Tests.Unit.csproj' 'architecture test project'
$flutterProject = Find-One 'src/**/*Modules.Flutter.Tests.Integration.csproj' 'flutter integration test project'
$archDir = Split-Path $archProject
$flutterContracts = Join-Path (Split-Path $flutterProject) 'Contracts'
$fixtureManifest = @(git ls-files -- 'src/**/Storage/Fixtures/*/fixture.json')
if ($fixtureManifest.Count -ne 1) { throw "Expected exactly one storage fixture manifest, found $($fixtureManifest.Count)." }

# Build switches: every *Nightly property the repository defines is turned off; code graph refresh is off.
$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$nightly = @([regex]::Matches($props, '<(\w*Nightly)\b') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
$testArgs = @('-c', $Configuration, '-p:CodeGraphRefresh=false') + @($nightly | ForEach-Object { "-p:$_=false" })

# The pinned JSON baselines, snapshotted so a breaking capture can be rolled back.
$pins = [ordered]@{
    'ContractManifest.json'               = Join-Path $archDir 'ContractManifest.json'
    'GrainFactoryOwners.json'             = Join-Path $archDir 'GrainFactoryOwners.json'
    'OrleansReferenceOwners.json'         = Join-Path $archDir 'OrleansReferenceOwners.json'
    'ConfigurationKeyOwners.json'         = Join-Path $archDir 'ConfigurationKeyOwners.json'
    'TestTierBaseline.json'               = Join-Path $archDir 'TestTierBaseline.json'
    'CollectionCompatibilitySamples.json' = Join-Path $archDir 'CollectionCompatibilitySamples.json'
    'SynapseWordAllowlist.json'           = Join-Path $archDir 'SynapseWordAllowlist.json'
    'FrameworkVendorBaseline.json'        = Join-Path $archDir 'FrameworkVendorBaseline.json'
    'AppsModuleIdBaseline.json'           = Join-Path $archDir 'AppsModuleIdBaseline.json'
    'flutter-wire-names.golden.json'      = Join-Path $flutterContracts 'flutter-wire-names.golden.json'
    'flutter-aliasless-enums.golden.json' = Join-Path $flutterContracts 'flutter-aliasless-enums.golden.json'
    'ui-states.golden.json'               = Join-Path $flutterContracts 'ui-states.golden.json'
}
# Shrinking ratchets: an entry going away (or a count going down) is the point, so removals there are not breaking.
$shrinking = @('FrameworkVendorBaseline.json', 'AppsModuleIdBaseline.json')
$before = @{}
foreach ($name in $pins.Keys) {
    if (-not (Test-Path $pins[$name])) { throw "Pin missing: $($pins[$name])" }
    $before[$name] = [IO.File]::ReadAllBytes($pins[$name])
}

if (-not $SkipSync -and (Test-Path (Join-Path $root "scripts/sync-solution.cs"))) {
    dotnet run (Join-Path $root 'scripts/sync-solution.cs')
    if ($LASTEXITCODE -ne 0) { throw 'sync-solution failed' }
}

function Run-Capture([string]$Project, [string]$Method) {
    Write-Host "capture $Method"
    dotnet test --project $Project @testArgs --filter-method "*$Method" --explicit only --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Capture failed: $Method" }
}

# Only these facts run; StorageFixtureCapture, CaptureSealedSegments and CaptureUiTransportBaseline are never run.
$timer = [Diagnostics.Stopwatch]::StartNew()
foreach ($method in 'CaptureContractManifest', 'CaptureGrainFactoryOwners', 'CaptureOrleansReferenceOwners',
                    'CaptureConfigurationKeyOwners', 'CaptureTestTierBaseline', 'CaptureCollectionCompatibilitySamples',
                    'CaptureSynapseWordAllowlist',
                    'CaptureFrameworkVendorBaseline', 'CaptureAppsModuleIdBaseline') {
    Run-Capture $archProject $method
}
foreach ($method in 'CaptureFlutterWireNames', 'CaptureUiStateGoldens') {
    Run-Capture $flutterProject $method
}
Write-Host ("captures took {0:n0}s" -f $timer.Elapsed.TotalSeconds)

# Leaves of a JSON document as "path<TAB>value" lines: objects recurse, arrays contribute one line per element.
# Raw text is LF-normalised: a CRLF checkout and an LF capture of the same element compare equal.
function Get-Leaves([Text.Json.JsonElement]$Element, [string]$Path, [Collections.Generic.HashSet[string]]$Into) {
    switch ($Element.ValueKind) {
        'Object' { foreach ($p in $Element.EnumerateObject()) { Get-Leaves $p.Value "$Path/$($p.Name)" $Into } }
        'Array'  { foreach ($e in $Element.EnumerateArray()) { [void]$Into.Add("$Path[]`t$($e.GetRawText().Replace("`r`n", "`n"))") } }
        default  { [void]$Into.Add("$Path`t$($Element.GetRawText().Replace("`r`n", "`n"))") }
    }
}
function Get-LeafSet([byte[]]$Bytes) {
    $set = New-Object 'Collections.Generic.HashSet[string]'
    $doc = [Text.Json.JsonDocument]::Parse([IO.MemoryStream]::new($Bytes), [Text.Json.JsonDocumentOptions]::new())
    Get-Leaves $doc.RootElement '' $set
    $doc.Dispose()
    return , $set
}

$breaking = @()
foreach ($name in $pins.Keys) {
    if ($shrinking -contains $name) { continue }
    $old = Get-LeafSet $before[$name]
    $new = Get-LeafSet ([IO.File]::ReadAllBytes($pins[$name]))
    foreach ($line in $old) { if (-not $new.Contains($line)) { $breaking += "${name}: $line" } }
}
if ($breaking.Count -gt 0 -and -not $AllowBreaking) {
    foreach ($name in $pins.Keys) { [IO.File]::WriteAllBytes($pins[$name], $before[$name]) }
    Write-Host 'Capture would remove keys or change values (pins restored); review, then rerun with -AllowBreaking:'
    $breaking | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }
    exit 1
}
if ($breaking.Count -gt 0) { Write-Host "breaking changes accepted: $($breaking.Count)" }

if ($KeysOnly) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("keys-only-" + [Guid]::NewGuid().ToString('N'))
    $was = Join-Path $scratch 'before'; $now = Join-Path $scratch 'after'
    New-Item -ItemType Directory -Force -Path $was, $now | Out-Null
    foreach ($name in $pins.Keys) {
        [IO.File]::WriteAllBytes((Join-Path $was $name), $before[$name])
        if (Test-Path $pins[$name]) { Copy-Item $pins[$name] (Join-Path $now $name) }
    }
    & pwsh -NoProfile -File (Join-Path $root 'scripts/keys-only-check.ps1') -Before $was -After $now
    $keysOnlyExit = $LASTEXITCODE
    Remove-Item -Recurse -Force $scratch
    if ($keysOnlyExit -ne 0) {
        foreach ($name in $pins.Keys) { [IO.File]::WriteAllBytes($pins[$name], $before[$name]) }
        Write-Host 'Pins restored: the capture changed values, not only keys.'
        exit 1
    }
}

if ($Out) {
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $copies = [ordered]@{}
    foreach ($name in $pins.Keys) { $copies[$name] = $pins[$name] }
    $copies['Golden.cs'] = Join-Path $root 'src/IntoChat.OS.Authority/IntoChat.OS.Authority.Tests.Integration/Storage/Golden.cs'
    $copies['fixture.json'] = Join-Path $root $fixtureManifest[0]
    foreach ($name in $copies.Keys) {
        # Normalise line endings so snapshots compare equal on any checkout.
        $text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($copies[$name])).Replace("`r`n", "`n")
        [IO.File]::WriteAllText((Join-Path $Out $name), $text, (New-Object Text.UTF8Encoding($false)))
    }
    Write-Host "wrote $($copies.Count) files to ${Out}"
}
Write-Host 'pins regenerated in place; review git diff'
