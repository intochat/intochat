<#
.SYNOPSIS
    Packages the self-contained IntoChat OS desktop release bundle with SHA-256 manifests.
.DESCRIPTION
    Builds and stages the unified release layout expected by eng/install/install.ps1:
      - Root:     IntoChat.OS.Desktop.exe (Native AOT) + intochat.cmd CLI shim
      - os/:      IntoChat.OS.Kernel.exe (self-contained ReadyToRun)
      - unithost/: IntoChat.OS.Sandbox.exe + IntoChat.OS.UnitHost.cmd (--unit-host worker)
      - shell/:   Flutter Windows desktop bundle (ui/build/windows/x64/runner/Release)
    Produces intochat-<version>-<rid>.zip, VERSION, SHA256SUMS, latest.json, and winget/IntoChat.OS.yaml.
#>
[CmdletBinding()]
param(
    [string]$Rid = '',
    [string]$Configuration = 'Release',
    [string]$Channel = 'stable',
    [string]$PublicDomain = $env:INTOCHAT_PUBLIC_DOMAIN,
    [string]$OutputDir = '',
    [switch]$SkipFlutter,
    [switch]$UpdateManifests,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$DefaultPublicDomain = 'intochat.io'
if ([string]::IsNullOrWhiteSpace($PublicDomain)) {
    $PublicDomain = $DefaultPublicDomain
}

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

function Get-ValidatedArchiveSha256 {
    param([Parameter(Mandatory = $true)][string]$FilePath)
    $hash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -notmatch '^[0-9a-f]{64}$' -or $hash -eq ('0' * 64)) {
        throw "Computed invalid SHA-256 digest '$hash' for '$FilePath'."
    }
    return $hash
}

function Publish-ReleaseManifests {
    param(
        [Parameter(Mandatory = $true)][hashtable]$AssetsByRid
    )
    if ($AssetsByRid.Count -eq 0) {
        throw "Cannot emit release manifests: no packaged archives were supplied."
    }

    [System.IO.File]::WriteAllText($outVersionPath, "$version`n", [System.Text.UTF8Encoding]::new($false))

    $existingSums = @()
    $managedNames = @($AssetsByRid.Keys | ForEach-Object { "intochat-$version-$_.zip" })
    if (Test-Path -LiteralPath $sumsPath) {
        $existingSums = Get-Content -LiteralPath $sumsPath | Where-Object {
            $line = $_
            $line -and -not ($managedNames | Where-Object { $line -match "\s+\*?$([Regex]::Escape($_))$" })
        }
    }
    $newLines = @($AssetsByRid.Keys | Sort-Object | ForEach-Object { "$($AssetsByRid[$_])  intochat-$version-$_.zip" })
    $updatedSums = (@($existingSums) + $newLines) -join "`n"
    [System.IO.File]::WriteAllText($sumsPath, "$updatedSums`n", [System.Text.UTF8Encoding]::new($false))

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
    foreach ($assetRid in ($AssetsByRid.Keys | Sort-Object)) {
        $fileName = "intochat-$version-$assetRid.zip"
        $manifestObj.assets[$assetRid] = [ordered]@{
            url    = "https://releases.$PublicDomain/$Channel/$version/$fileName"
            sha256 = $AssetsByRid[$assetRid]
        }
    }

    $manifestJson = ($manifestObj | ConvertTo-Json -Depth 6) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($latestJsonPath, "$manifestJson`n", [System.Text.UTF8Encoding]::new($false))

    $wingetTemplate = Join-Path $PSScriptRoot 'winget\IntoChat.OS.yaml'
    if (Test-Path -LiteralPath $wingetTemplate) {
        $rendered = Get-Content -LiteralPath $wingetTemplate -Raw
        $rendered = $rendered.Replace('PENDING_RELEASE_VERSION', $version)
        $rendered = $rendered.Replace('PENDING_RELEASE_CHANNEL', $Channel)
        if ($PublicDomain -ne $DefaultPublicDomain) {
            $rendered = $rendered.Replace($DefaultPublicDomain, $PublicDomain)
        }
        if ($manifestObj.assets.Contains('win-x64')) {
            $rendered = $rendered.Replace('PENDING_RELEASE_SHA256_WIN_X64', [string]$manifestObj.assets['win-x64'].sha256)
        }
        if ($manifestObj.assets.Contains('win-arm64')) {
            $rendered = $rendered.Replace('PENDING_RELEASE_SHA256_WIN_ARM64', [string]$manifestObj.assets['win-arm64'].sha256)
        }
        $rendered = $rendered -replace "`r`n", "`n"
        $wingetOutDir = Join-Path $OutputDir 'winget'
        New-Item -ItemType Directory -Path $wingetOutDir -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $wingetOutDir 'IntoChat.OS.yaml'), $rendered, [System.Text.UTF8Encoding]::new($false))
    }
}

Write-Host "IntoChat OS Release Bundler v$version ($Rid)" -ForegroundColor Green

$desktopProj = Join-Path $repoRoot 'src\IntoChat.OS.Desktop\IntoChat.OS.Desktop.csproj'
$kernelProj = Join-Path $repoRoot 'src\IntoChat.OS.Kernel\IntoChat.OS.Kernel\IntoChat.OS.Kernel.csproj'
$sandboxProj = Join-Path $repoRoot 'src\IntoChat.OS.Sandbox\IntoChat.OS.Sandbox.csproj'
$uiDir = Join-Path $repoRoot 'ui'

# Everything a packaging run reads from the repository. The dry run checks the same list, so a moved project
# fails the dry run in CI instead of the next release.
function Assert-ReleaseInputs {
    $inputs = @($desktopProj, $kernelProj, $sandboxProj, (Join-Path $PSScriptRoot 'winget\IntoChat.OS.yaml'))
    if (-not $SkipFlutter) {
        $inputs += Join-Path $uiDir 'pubspec.yaml'
        $inputs += Join-Path $uiDir 'windows\CMakeLists.txt'
    }
    $missing = @($inputs | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missing.Count -gt 0) {
        throw "Release inputs not found: $($missing -join ', ')"
    }
    return $inputs
}

if ($DryRun) {
    foreach ($found in Assert-ReleaseInputs) {
        Write-ReleaseStep "[DryRun] Found $found"
    }
    Write-ReleaseStep "[DryRun] Would publish IntoChat.OS.Desktop (Native AOT), IntoChat.OS.Kernel (R2R), IntoChat.OS.Sandbox (UnitHost), and Flutter shell into $archivePath"
    Write-ReleaseStep "[DryRun] Would emit SHA256SUMS, VERSION, latest.json, and winget/IntoChat.OS.yaml in $OutputDir (domain: $PublicDomain)"
    exit 0
}

if ($UpdateManifests) {
    if (-not (Test-Path -LiteralPath $OutputDir)) {
        throw "Output directory '$OutputDir' does not exist."
    }
    $discovered = @{}
    foreach ($candidateRid in @('win-x64', 'win-arm64')) {
        $candidateZip = Join-Path $OutputDir "intochat-$version-$candidateRid.zip"
        if (Test-Path -LiteralPath $candidateZip -PathType Leaf) {
            $discovered[$candidateRid] = Get-ValidatedArchiveSha256 -FilePath $candidateZip
        }
    }
    if ($discovered.Count -eq 0) {
        throw "No packaged archives matching 'intochat-$version-win-*.zip' found in '$OutputDir'."
    }
    Publish-ReleaseManifests -AssetsByRid $discovered
    Write-ReleaseStep "Updated SHA256SUMS, latest.json, and winget/IntoChat.OS.yaml in $OutputDir."
    exit 0
}

Assert-ReleaseInputs | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("intochat-pkg-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$bundleDir = Join-Path $stageRoot 'bundle'
$osDir = Join-Path $bundleDir 'os'
$unitHostDir = Join-Path $bundleDir 'unithost'
$shellDir = Join-Path $bundleDir 'shell'

New-Item -ItemType Directory -Path $bundleDir, $osDir, $unitHostDir, $shellDir -Force | Out-Null

try {
    Write-ReleaseStep "Publishing IntoChat.OS.Desktop (Native AOT, $Rid)..."
    & dotnet publish $desktopProj -c $Configuration -r $Rid -o $bundleDir -p:PublishAot=true -p:DebugType=none --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for IntoChat.OS.Desktop ($LASTEXITCODE)." }

    $cliShim = "@echo off`r`n`"%~dp0IntoChat.OS.Desktop.exe`" %*`r`n"
    [System.IO.File]::WriteAllText((Join-Path $bundleDir 'intochat.cmd'), $cliShim, [System.Text.Encoding]::ASCII)

    Write-ReleaseStep "Publishing IntoChat.OS.Kernel (Self-Contained ReadyToRun, $Rid)..."
    & dotnet publish $kernelProj -c $Configuration -r $Rid --self-contained -o $osDir -p:PublishReadyToRun=true -p:CodeGraphRefresh=false -p:DebugType=none --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for IntoChat.OS.Kernel ($LASTEXITCODE)." }

    Write-ReleaseStep "Publishing IntoChat.OS.Sandbox / UnitHost ($Rid)..."
    & dotnet publish $sandboxProj -c $Configuration -r $Rid --self-contained -o $unitHostDir -p:PublishReadyToRun=true -p:DebugType=none --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for IntoChat.OS.Sandbox ($LASTEXITCODE)." }

    $unitHostShim = "@echo off`r`n`"%~dp0IntoChat.OS.Sandbox.exe`" --unit-host %*`r`n"
    [System.IO.File]::WriteAllText((Join-Path $unitHostDir 'IntoChat.OS.UnitHost.cmd'), $unitHostShim, [System.Text.Encoding]::ASCII)

    if (-not $SkipFlutter) {
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
        # flutter build windows targets the architecture of the machine it runs on; a bundle without its shell is not a release.
        if (-not (Test-Path -LiteralPath $flutterRunnerOut)) {
            throw "Flutter shell for $Rid not found at '$flutterRunnerOut'; build $Rid on a machine of that architecture."
        }
        Copy-Item -Path (Join-Path $flutterRunnerOut '*') -Destination $shellDir -Recurse -Force
    }

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Write-ReleaseStep "Compressing $archiveName..."
    Compress-Archive -Path (Join-Path $bundleDir '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force

    $sha256 = Get-ValidatedArchiveSha256 -FilePath $archivePath
    Write-ReleaseStep "SHA-256: $sha256"

    Publish-ReleaseManifests -AssetsByRid @{ $Rid = $sha256 }

    Write-ReleaseStep "Packaged $archivePath alongside SHA256SUMS, latest.json, and winget/IntoChat.OS.yaml."
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
