<#
.SYNOPSIS
    Packages the self-contained IntoChat OS desktop release bundle with SHA-256 manifests.
.DESCRIPTION
    Builds and stages the unified release layout expected by eng/install/install.ps1:
      - Root:     IntoChat.OS.Desktop.exe (Native AOT) + intochat.cmd CLI shim
      - os/:      IntoChat.OS.Kernel.exe (self-contained ReadyToRun)
      - unithost/: IntoChat.OS.Sandbox.exe + IntoChat.OS.UnitHost.cmd (--unit-host worker)
      - shell/:   Flutter Windows desktop bundle (ui/build/windows/x64/runner/Release)
    Produces intochat-<version>-<rid>.zip, VERSION, SHA256SUMS, and latest.json.
#>
[CmdletBinding()]
param(
    [string]$Rid = '',
    [string]$Configuration = 'Release',
    [string]$Channel = 'stable',
    [string]$PublicDomain = 'intochat.io',
    [string]$OutputDir = '',
    [switch]$SkipFlutter,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$versionFile = Join-Path $repoRoot 'VERSION'
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "VERSION file not found at '$versionFile'."
}
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()

if ([string]::IsNullOrWhiteSpace($Rid)) {
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $Rid = switch -Regex ($arch) {
        '^ARM64$' { 'win-arm64' }
        default   { 'win-x64' }
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'artifacts\release'
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$archiveName = "intochat-$version-$Rid.zip"
$archivePath = Join-Path $OutputDir $archiveName
$sumsPath = Join-Path $OutputDir 'SHA256SUMS'
$latestJsonPath = Join-Path $OutputDir 'latest.json'
$outVersionPath = Join-Path $OutputDir 'VERSION'

function Write-ReleaseStep {
    param([string]$Message)
    Write-Host "  [release] $Message" -ForegroundColor Cyan
}

Write-Host "IntoChat OS Release Bundler v$version ($Rid)" -ForegroundColor Green

if ($DryRun) {
    Write-ReleaseStep "[DryRun] Would publish IntoChat.OS.Desktop (Native AOT), IntoChat.OS.Kernel (R2R), IntoChat.OS.Sandbox (UnitHost), and Flutter shell into $archivePath"
    Write-ReleaseStep "[DryRun] Would emit SHA256SUMS, VERSION, and latest.json in $OutputDir"
    exit 0
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("intochat-pkg-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$bundleDir = Join-Path $stageRoot 'bundle'
$osDir = Join-Path $bundleDir 'os'
$unitHostDir = Join-Path $bundleDir 'unithost'
$shellDir = Join-Path $bundleDir 'shell'

New-Item -ItemType Directory -Path $bundleDir, $osDir, $unitHostDir, $shellDir -Force | Out-Null

try {
    $desktopProj = Join-Path $repoRoot 'src\IntoChat.OS.Desktop\IntoChat.OS.Desktop.csproj'
    Write-ReleaseStep "Publishing IntoChat.OS.Desktop (Native AOT, $Rid)..."
    & dotnet publish $desktopProj -c $Configuration -r $Rid -o $bundleDir -p:PublishAot=true -p:DebugType=none --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for IntoChat.OS.Desktop ($LASTEXITCODE)." }

    $cliShim = "@echo off`r`n`"%~dp0IntoChat.OS.Desktop.exe`" %*`r`n"
    [System.IO.File]::WriteAllText((Join-Path $bundleDir 'intochat.cmd'), $cliShim, [System.Text.Encoding]::ASCII)

    $kernelProj = Join-Path $repoRoot 'src\IntoChat.OS.Kernel\IntoChat.OS.Kernel\IntoChat.OS.Kernel.csproj'
    Write-ReleaseStep "Publishing IntoChat.OS.Kernel (Self-Contained ReadyToRun, $Rid)..."
    & dotnet publish $kernelProj -c $Configuration -r $Rid --self-contained -o $osDir -p:PublishReadyToRun=true -p:CodeGraphRefresh=false -p:DebugType=none --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for IntoChat.OS.Kernel ($LASTEXITCODE)." }

    $sandboxProj = Join-Path $repoRoot 'src\IntoChat.OS.Sandbox\IntoChat.OS.Sandbox.csproj'
    Write-ReleaseStep "Publishing IntoChat.OS.Sandbox / UnitHost ($Rid)..."
    & dotnet publish $sandboxProj -c $Configuration -r $Rid --self-contained -o $unitHostDir -p:PublishReadyToRun=true -p:DebugType=none --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for IntoChat.OS.Sandbox ($LASTEXITCODE)." }

    $unitHostShim = "@echo off`r`n`"%~dp0IntoChat.OS.Sandbox.exe`" --unit-host %*`r`n"
    [System.IO.File]::WriteAllText((Join-Path $unitHostDir 'IntoChat.OS.UnitHost.cmd'), $unitHostShim, [System.Text.Encoding]::ASCII)

    $uiDir = Join-Path $repoRoot 'ui'
    if ((-not $SkipFlutter) -and (Test-Path -LiteralPath (Join-Path $uiDir 'pubspec.yaml'))) {
        $flutterCmd = Get-Command 'flutter' -ErrorAction SilentlyContinue
        if ($null -eq $flutterCmd) {
            throw "Flutter SDK not found on PATH; pass -SkipFlutter for headless kernel-only packaging."
        }
        Write-ReleaseStep "Building Flutter Windows shell..."
        Push-Location $uiDir
        try {
            & flutter build windows --release
            if ($LASTEXITCODE -ne 0) { throw "flutter build windows --release failed ($LASTEXITCODE)." }
        }
        finally {
            Pop-Location
        }

        $winArchSegment = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
        $flutterRunnerOut = Join-Path $uiDir "build\windows\$winArchSegment\runner\Release"
        if (Test-Path -LiteralPath $flutterRunnerOut) {
            Copy-Item -Path (Join-Path $flutterRunnerOut '*') -Destination $shellDir -Recurse -Force
        }
    }

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Write-ReleaseStep "Compressing $archiveName..."
    Compress-Archive -Path (Join-Path $bundleDir '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force

    $sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-ReleaseStep "SHA-256: $sha256"

    [System.IO.File]::WriteAllText($outVersionPath, "$version`n", [System.Text.UTF8Encoding]::new($false))

    $existingSums = @()
    if (Test-Path -LiteralPath $sumsPath) {
        $existingSums = Get-Content -LiteralPath $sumsPath | Where-Object {
            $_ -and ($_ -notmatch "\s+\*?$([Regex]::Escape($archiveName))$")
        }
    }
    $updatedSums = (@($existingSums) + "$sha256  $archiveName") -join "`n"
    [System.IO.File]::WriteAllText($sumsPath, "$updatedSums`n", [System.Text.UTF8Encoding]::new($false))

    $assetUrl = "https://releases.$PublicDomain/$Channel/$version/$archiveName"
    $manifestObj = [ordered]@{
        version = $version
        channel = $Channel
        assets  = [ordered]@{}
    }
    if (Test-Path -LiteralPath $latestJsonPath) {
        $prior = Get-Content -LiteralPath $latestJsonPath -Raw | ConvertFrom-Json
        if ($prior -and $prior.assets) {
            foreach ($prop in $prior.assets.PSObject.Properties) {
                $manifestObj.assets[$prop.Name] = $prop.Value
            }
        }
    }
    $manifestObj.assets[$Rid] = [ordered]@{
        url    = $assetUrl
        sha256 = $sha256
    }

    $manifestJson = ($manifestObj | ConvertTo-Json -Depth 6) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($latestJsonPath, "$manifestJson`n", [System.Text.UTF8Encoding]::new($false))

    Write-ReleaseStep "Packaged $archivePath alongside SHA256SUMS and latest.json."
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
