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
    [string]$PublicDomain = $env:INTOCHAT_PUBLIC_DOMAIN,
    [string]$HomeDir = $(if ($env:IntoChatOS__Home) { $env:IntoChatOS__Home } else { Join-Path $env:USERPROFILE '.intochat' }),
    [string]$LocalArchive = $env:INTOCHAT_ARCHIVE_PATH,
    [string]$ExpectedSha256 = $env:INTOCHAT_ARCHIVE_SHA256,
    [switch]$NoLaunch,
    [switch]$NoPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

$DefaultPublicDomain = 'intochat.ai'
if ([string]::IsNullOrWhiteSpace($PublicDomain)) {
    $PublicDomain = $DefaultPublicDomain
}
$skipPathUpdate = $NoPath -or ($env:INTOCHAT_NO_PATH -eq '1')

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

function Resolve-ValidatedSha256 {
    param(
        [string]$Value,
        [Parameter(Mandatory = $true)][string]$SourceDescription
    )
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Missing required SHA-256 digest ($SourceDescription). Installation aborted."
    }
    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized -notmatch '^[0-9a-f]{64}$') {
        throw "Invalid SHA-256 digest '$normalized' ($SourceDescription). Expected 64 hexadecimal characters."
    }
    if ($normalized -eq ('0' * 64)) {
        throw "Refusing placeholder all-zero SHA-256 digest ($SourceDescription). Installation aborted."
    }
    return $normalized
}

function Read-Sha256FromSumsText {
    param(
        [string]$SumsText,
        [Parameter(Mandatory = $true)][string]$ArchiveFileName
    )
    if ([string]::IsNullOrWhiteSpace($SumsText)) {
        return $null
    }
    $escaped = [Regex]::Escape($ArchiveFileName)
    foreach ($line in ($SumsText -split "`r?`n")) {
        if ($line -match "^\s*([0-9a-fA-F]{64})\s+\*?$escaped\s*$") {
            return $Matches[1]
        }
    }
    return $null
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

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("intochat-stage-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

try {
    $archivePath = $null
    $sha256ToVerify = $null

    if (-not [string]::IsNullOrWhiteSpace($LocalArchive)) {
        if (-not (Test-Path -LiteralPath $LocalArchive -PathType Leaf)) {
            throw "Local archive '$LocalArchive' does not exist."
        }
        $sha256ToVerify = Resolve-ValidatedSha256 -Value $ExpectedSha256 -SourceDescription 'ExpectedSha256 / INTOCHAT_ARCHIVE_SHA256'
        $archivePath = (Resolve-Path -LiteralPath $LocalArchive).Path
        Write-Step "Using local release archive: $archivePath"
    } else {
        $baseUrl = "https://releases.$PublicDomain/$Channel"
        $callerSha = $null
        if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
            $callerSha = Resolve-ValidatedSha256 -Value $ExpectedSha256 -SourceDescription 'ExpectedSha256 / INTOCHAT_ARCHIVE_SHA256'
        }

        if ([string]::IsNullOrWhiteSpace($Version)) {
            $manifestUrl = "$baseUrl/latest.json"
            Write-Step "Fetching release manifest from $manifestUrl"
            $manifest = Invoke-RestMethod -Uri $manifestUrl -Method Get -UseBasicParsing
            $Version = ([string]$manifest.version).Trim()
            if ([string]::IsNullOrWhiteSpace($Version)) {
                throw "Release manifest '$manifestUrl' did not specify a version."
            }

            $expectedFile = "intochat-$Version-$rid.zip"
            $manifestSha = $null
            if ($manifest.assets -and $manifest.assets.$rid) {
                $assetUrl = [string]$manifest.assets.$rid.url
                $manifestSha = [string]$manifest.assets.$rid.sha256
            }
            if ([string]::IsNullOrWhiteSpace($assetUrl)) {
                $assetUrl = "$baseUrl/$Version/$expectedFile"
            }

            if ($null -ne $callerSha) {
                if (-not [string]::IsNullOrWhiteSpace($manifestSha)) {
                    $normalizedManifestSha = Resolve-ValidatedSha256 -Value $manifestSha -SourceDescription 'release manifest'
                    if ($normalizedManifestSha -ne $callerSha) {
                        throw "SHA-256 mismatch between caller digest '$callerSha' and release manifest '$normalizedManifestSha'."
                    }
                }
                $sha256ToVerify = $callerSha
            } else {
                $sha256ToVerify = $manifestSha
                if ([string]::IsNullOrWhiteSpace($sha256ToVerify)) {
                    $sumsUrl = "$baseUrl/$Version/SHA256SUMS"
                    $sumsText = Invoke-RestMethod -Uri $sumsUrl -Method Get -UseBasicParsing
                    $sha256ToVerify = Read-Sha256FromSumsText -SumsText ([string]$sumsText) -ArchiveFileName $expectedFile
                }
            }
        } else {
            $expectedFile = "intochat-$Version-$rid.zip"
            $assetUrl = "$baseUrl/$Version/$expectedFile"
            if ($null -ne $callerSha) {
                $sha256ToVerify = $callerSha
            } else {
                $sumsUrl = "$baseUrl/$Version/SHA256SUMS"
                $sumsText = Invoke-RestMethod -Uri $sumsUrl -Method Get -UseBasicParsing
                $sha256ToVerify = Read-Sha256FromSumsText -SumsText ([string]$sumsText) -ArchiveFileName $expectedFile
            }
        }

        if (-not $assetUrl.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing insecure release asset URL '$assetUrl'. HTTPS is required."
        }
        $sha256ToVerify = Resolve-ValidatedSha256 -Value $sha256ToVerify -SourceDescription 'release manifest / SHA256SUMS'

        $archivePath = Join-Path $stagingRoot "intochat-$rid.zip"
        Write-Step "Downloading $assetUrl"
        Invoke-WebRequest -Uri $assetUrl -OutFile $archivePath -UseBasicParsing
    }

    $expectedNormalized = Resolve-ValidatedSha256 -Value $sha256ToVerify -SourceDescription 'archive verification'
    Write-Step "Verifying SHA-256 digest"
    $computedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($computedHash -ne $expectedNormalized) {
        throw "SHA-256 mismatch for '$archivePath'. Expected '$expectedNormalized', computed '$computedHash'."
    }

    foreach ($dir in @($resolvedHome, $binDir, $secretsDir, $runDir, $logsDir, $stateDir)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }

    Set-OwnerOnlyDirectoryAcl -Path $secretsDir
    Set-OwnerOnlyDirectoryAcl -Path $runDir

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

    if (-not $skipPathUpdate) {
        Register-UserPathEntry -BinDirectory $binDir
        Write-Step "Installed IntoChat OS into $binDir and added to User PATH."
    } else {
        Write-Step "Installed IntoChat OS into $binDir (PATH registration skipped)."
    }

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
