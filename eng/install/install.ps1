<#
.SYNOPSIS
    IntoChat OS one-command Windows installer (https://intochat.ai/install.ps1).
.DESCRIPTION
    Downloads, verifies (SHA-256), stages, and activates the self-contained IntoChat OS
    Windows release under ~/.intochat without requiring Administrator privileges, Docker,
    or a pre-installed .NET SDK. Also detects Microsoft WinApp CLI (`winapp`) if present.
#>
[CmdletBinding()]
param(
    [string]$Channel = $(if ($env:INTOCHAT_CHANNEL) { $env:INTOCHAT_CHANNEL } else { 'stable' }),
    [string]$Version = $env:INTOCHAT_VERSION,
    [string]$PublicDomain = $(if ($env:INTOCHAT_PUBLIC_DOMAIN) { $env:INTOCHAT_PUBLIC_DOMAIN } else { 'intochat.ai' }),
    [string]$HomeDir = $(if ($env:IntoChatOS__Home) { $env:IntoChatOS__Home } else { Join-Path $env:USERPROFILE '.intochat' }),
    [string]$LocalArchive = $env:INTOCHAT_ARCHIVE_PATH,
    [string]$ExpectedSha256 = $env:INTOCHAT_ARCHIVE_SHA256,
    [switch]$NoLaunch,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

function Write-Step {
    param([string]$Message)
    Write-Host "  -> $Message" -ForegroundColor Cyan
}

function Resolve-TargetRid {
    $arch = $env:PROCESSOR_ARCHITECTURE
    if ($env:PROCESSOR_ARCHITEW6432) {
        $arch = $env:PROCESSOR_ARCHITEW6432
    }
    switch -Regex ($arch) {
        '^ARM64$' { return 'win-arm64' }
        '^(AMD64|x86_64)$' { return 'win-x64' }
        default { throw "Unsupported Windows processor architecture '$arch'. IntoChat OS supports win-x64 and win-arm64." }
    }
}

function Set-OwnerOnlyDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentUser) { return }

    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $currentUser,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow
    )
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Register-UserPathEntry {
    param([Parameter(Mandatory = $true)][string]$BinDirectory)
    $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $segments = @()
    if (-not [string]::IsNullOrWhiteSpace($currentUserPath)) {
        $segments = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
    }

    $alreadyPresent = $false
    foreach ($segment in $segments) {
        if ([string]::Equals($segment.TrimEnd('\'), $BinDirectory.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
            $alreadyPresent = $true
            break
        }
    }

    if (-not $alreadyPresent) {
        $updatedUserPath = if ([string]::IsNullOrWhiteSpace($currentUserPath)) {
            $BinDirectory
        } else {
            "$BinDirectory;$currentUserPath"
        }
        [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, 'User')
    }

    if (-not (($env:Path -split ';') | Where-Object { [string]::Equals($_.TrimEnd('\'), $BinDirectory.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase) })) {
        $env:Path = "$BinDirectory;$env:Path"
    }
}

$rid = Resolve-TargetRid
$resolvedHome = [System.IO.Path]::GetFullPath($HomeDir)
$binDir = Join-Path $resolvedHome 'bin'
$secretsDir = Join-Path $resolvedHome 'secrets'
$runDir = Join-Path $resolvedHome 'run'
$logsDir = Join-Path $resolvedHome 'logs'
$stateDir = Join-Path $resolvedHome 'state'

Write-Host ""
Write-Host "IntoChat OS Installer ($rid, channel: $Channel)" -ForegroundColor Green
Write-Step "Home directory: $resolvedHome"

$winAppCmd = Get-Command 'winapp' -ErrorAction SilentlyContinue
if ($null -ne $winAppCmd) {
    Write-Step "Detected Microsoft WinApp CLI ($($winAppCmd.Source)) for optional package identity registration."
}

if ($DryRun) {
    $previewTag = if ($Version) { $Version } else { 'latest' }
    Write-Step "[DryRun] Would download https://releases.$PublicDomain/$Channel/$previewTag/intochat-$previewTag-$rid.zip, verify SHA-256, stage into $binDir, and register User PATH."
    exit 0
}

foreach ($dir in @($resolvedHome, $binDir, $secretsDir, $runDir, $logsDir, $stateDir)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

Set-OwnerOnlyDirectoryAcl -Path $secretsDir
Set-OwnerOnlyDirectoryAcl -Path $runDir

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("intochat-stage-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

try {
    $archivePath = $null
    $sha256ToVerify = $ExpectedSha256

    if (-not [string]::IsNullOrWhiteSpace($LocalArchive)) {
        if (-not (Test-Path -LiteralPath $LocalArchive)) {
            throw "Local archive '$LocalArchive' does not exist."
        }
        $archivePath = (Resolve-Path -LiteralPath $LocalArchive).Path
        Write-Step "Using local release archive: $archivePath"
    } else {
        $baseUrl = "https://releases.$PublicDomain/$Channel"
        if ([string]::IsNullOrWhiteSpace($Version)) {
            $manifestUrl = "$baseUrl/latest.json"
            Write-Step "Fetching release manifest from $manifestUrl"
            $manifest = Invoke-RestMethod -Uri $manifestUrl -Method Get -UseBasicParsing
            $Version = $manifest.version
            if ($manifest.assets -and $manifest.assets.$rid) {
                $assetUrl = $manifest.assets.$rid.url
                $sha256ToVerify = $manifest.assets.$rid.sha256
            } else {
                $assetUrl = "$baseUrl/$Version/intochat-$Version-$rid.zip"
            }
        } else {
            $assetUrl = "$baseUrl/$Version/intochat-$Version-$rid.zip"
            if ([string]::IsNullOrWhiteSpace($sha256ToVerify)) {
                $sumsUrl = "$baseUrl/$Version/SHA256SUMS"
                $sumsText = Invoke-RestMethod -Uri $sumsUrl -Method Get -UseBasicParsing
                $expectedFile = "intochat-$Version-$rid.zip"
                foreach ($line in ($sumsText -split "`r?`n")) {
                    if ($line -match "^([0-9a-fA-F]{64})\s+\*?$([Regex]::Escape($expectedFile))$") {
                        $sha256ToVerify = $Matches[1]
                        break
                    }
                }
            }
        }

        $archivePath = Join-Path $stagingRoot "intochat-$rid.zip"
        Write-Step "Downloading $assetUrl"
        Invoke-WebRequest -Uri $assetUrl -OutFile $archivePath -UseBasicParsing
    }

    if (-not [string]::IsNullOrWhiteSpace($sha256ToVerify)) {
        Write-Step "Verifying SHA-256 digest"
        $computedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($computedHash -ne $sha256ToVerify.Trim().ToLowerInvariant()) {
            throw "SHA-256 mismatch for '$archivePath'. Expected '$sha256ToVerify', computed '$computedHash'."
        }
    }

    $extractDir = Join-Path $stagingRoot 'extracted'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force

    # Stop any running IntoChat OS instance cleanly before swapping binaries.
    $existingDesktopExe = Join-Path $binDir 'IntoChat.OS.Desktop.exe'
    if (Test-Path -LiteralPath $existingDesktopExe) {
        Write-Step "Stopping running IntoChat OS supervisor prior to upgrade"
        & $existingDesktopExe --quit | Out-Null
    }

    $backupDir = "$binDir.prev"
    if (Test-Path -LiteralPath $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
    }
    if (Test-Path -LiteralPath $binDir) {
        Move-Item -LiteralPath $binDir -Destination $backupDir
    }
    Move-Item -LiteralPath $extractDir -Destination $binDir
    if (Test-Path -LiteralPath $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
    }

    # Create thin CLI shim `intochat.cmd` if not bundled in the archive.
    $cliCmdPath = Join-Path $binDir 'intochat.cmd'
    if (-not (Test-Path -LiteralPath $cliCmdPath)) {
        $shimContent = "@echo off`r`n`"%~dp0IntoChat.OS.Desktop.exe`" %*`r`n"
        [System.IO.File]::WriteAllText($cliCmdPath, $shimContent, [System.Text.Encoding]::ASCII)
    }

    Register-UserPathEntry -BinDirectory $binDir
    Write-Step "Installed IntoChat OS into $binDir and added to User PATH."

    $desktopExe = Join-Path $binDir 'IntoChat.OS.Desktop.exe'
    if ((-not $NoLaunch) -and ($env:SPIKE_WINDOWS -ne '0') -and (Test-Path -LiteralPath $desktopExe)) {
        Write-Step "Starting IntoChat OS..."
        Start-Process -FilePath $desktopExe -WorkingDirectory $binDir | Out-Null
    } else {
        Write-Step "Run 'intochat' in a terminal to launch IntoChat OS."
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
