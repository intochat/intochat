<#
.SYNOPSIS
  One command that regenerates every pinned contract artifact into a directory (RENAME-02).

.DESCRIPTION
  Builds the projects that own the Explicit capture facts, runs the captures, copies the eight pinned
  artifacts plus Golden.cs and the storage fixture manifest into -Out under stable names, and (unless
  -InPlace) restores the tracked baselines so the working tree stays clean.

  Brand-free on purpose: projects and exes are found by glob, so the script survives the rename unchanged.
  Never runs StorageFixtureCapture, CaptureSealedSegments or CaptureUiTransportBaseline.

.EXAMPLE
  pwsh scripts/capture-all.ps1 -Out C:/tmp/before
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [switch]$InPlace,
    [switch]$SkipBuild,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (git rev-parse --show-toplevel).Trim()
if (-not $root) { throw 'Not inside a git repository.' }
Set-Location $root
$config = $Configuration.ToLowerInvariant()

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

if (-not $InPlace) {
    $dirty = @(git status --porcelain)
    if ($dirty.Count -gt 0) { throw "Working tree must be clean unless -InPlace is given:`n$($dirty -join "`n")" }
}

# Build switches: every *Nightly property the repository defines is turned off; code graph refresh is off.
$props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$nightly = @([regex]::Matches($props, '<(\w*Nightly)\b') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
$buildArgs = @('-c', $Configuration, '-p:CodeGraphRefresh=false') + @($nightly | ForEach-Object { "-p:$_=false" })

if (-not $SkipBuild) {
    foreach ($project in @($archProject, $flutterProject)) {
        Write-Host "build $project"
        dotnet build $project @buildArgs --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
}

function Exe-Of([string]$Project) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Project)
    $exe = Join-Path $root "artifacts/bin/$name/$config/$name.exe"
    if (-not (Test-Path $exe)) { throw "Test exe not found: $exe" }
    return $exe
}

function Run-Capture([string]$Exe, [string]$Method) {
    Write-Host "capture $Method"
    & $Exe --filter-method "*$Method" --explicit only | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Capture failed: $Method" }
}

$archExe = Exe-Of $archProject
$flutterExe = Exe-Of $flutterProject

# Only these facts run; StorageFixtureCapture, CaptureSealedSegments and CaptureUiTransportBaseline are never run.
foreach ($method in 'CaptureContractManifest', 'CaptureGrainFactoryOwners', 'CaptureOrleansReferenceOwners',
                    'CaptureConfigurationKeyOwners', 'CaptureTestTierBaseline', 'CaptureCollectionCompatibilitySamples') {
    Run-Capture $archExe $method
}
foreach ($method in 'CaptureFlutterWireNames', 'CaptureUiStateGoldens') {
    Run-Capture $flutterExe $method
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$artifacts = [ordered]@{
    'ContractManifest.json'              = Join-Path $archDir 'ContractManifest.json'
    'GrainFactoryOwners.json'            = Join-Path $archDir 'GrainFactoryOwners.json'
    'OrleansReferenceOwners.json'        = Join-Path $archDir 'OrleansReferenceOwners.json'
    'ConfigurationKeyOwners.json'        = Join-Path $archDir 'ConfigurationKeyOwners.json'
    'TestTierBaseline.json'              = Join-Path $archDir 'TestTierBaseline.json'
    'CollectionCompatibilitySamples.json' = Join-Path $archDir 'CollectionCompatibilitySamples.json'
    'flutter-wire-names.golden.json'     = Join-Path $flutterContracts 'flutter-wire-names.golden.json'
    'ui-states.golden.json'              = Join-Path $flutterContracts 'ui-states.golden.json'
    'Golden.cs'                          = Join-Path $archDir 'Golden.cs'
    'fixture.json'                       = Join-Path $root $fixtureManifest[0]
}
$written = @()
foreach ($name in $artifacts.Keys) {
    $source = $artifacts[$name]
    if (-not (Test-Path $source)) { throw "Artifact missing: $source" }
    # Normalise line endings so snapshots compare equal on any checkout.
    $bytes = [IO.File]::ReadAllBytes($source)
    $text = [Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n")
    $target = Join-Path $Out $name
    [IO.File]::WriteAllText($target, $text, (New-Object Text.UTF8Encoding($false)))
    $written += $target
}

if (-not $InPlace) {
    # The captures wrote into the tree; put every touched tracked file back.
    $touched = @(git status --porcelain | ForEach-Object { $_.Substring(3).Trim('"') })
    if ($touched.Count -gt 0) { git checkout -- @touched }
    $left = @(git status --porcelain)
    if ($left.Count -gt 0) { throw "Tree not clean after restore:`n$($left -join "`n")" }
}

Write-Host "wrote $($written.Count) files to ${Out}:"
$written | ForEach-Object { Write-Host "  $_" }
