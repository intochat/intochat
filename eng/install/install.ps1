<#
.SYNOPSIS
    IntoChat OS one-command Windows installer (https://intochat.io/install.ps1).
.DESCRIPTION
    Downloads, verifies (SHA-256), unblocks (MotW-free), stages, and activates the self-contained
    IntoChat OS Windows release under ~/.intochat without requiring Administrator privileges, Docker,
    or a pre-installed .NET SDK. Creates a Start Menu shortcut (and optional Desktop shortcut),
    supports clean -Uninstall while preserving user secrets and state, and optionally registers
    local sparse package identity when Microsoft WinApp CLI (`winapp`) is present.
#>
[CmdletBinding()]
param(
    [string]$Channel = $(if ($env:INTOCHAT_CHANNEL) { $env:INTOCHAT_CHANNEL } else { 'stable' }),
    [string]$Version = $env:INTOCHAT_VERSION,
    [string]$PublicDomain = $env:INTOCHAT_PUBLIC_DOMAIN,
    [string]$HomeDir = $(
        if ($env:IntoChatOS__Home) { $env:IntoChatOS__Home }
        elseif ($env:USERPROFILE) { Join-Path $env:USERPROFILE '.intochat' }
        elseif ($env:HOME) { Join-Path $env:HOME '.intochat' }
        else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.intochat' }
    ),
    [string]$LocalArchive = $env:INTOCHAT_ARCHIVE_PATH,
    [string]$ExpectedSha256 = $env:INTOCHAT_ARCHIVE_SHA256,
    [switch]$NoLaunch,
    [switch]$NoPath,
    [switch]$NoShortcut,
    [switch]$DesktopShortcut,
    [switch]$RegisterIdentity,
    [switch]$Uninstall,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

$DefaultPublicDomain = 'intochat.io'
if ([string]::IsNullOrWhiteSpace($PublicDomain)) {
    $PublicDomain = $DefaultPublicDomain
}
$skipPathUpdate = $NoPath -or ($env:INTOCHAT_NO_PATH -eq '1')
$skipShortcuts = $NoShortcut -or ($env:INTOCHAT_NO_SHORTCUT -eq '1')

function Write-Step {
    param([string]$Message)
    Write-Host "  -> $Message" -ForegroundColor Cyan
}

function Test-IsWindowsHost {
    return ($PSVersionTable.PSEdition -eq 'Desktop') -or ([bool]$IsWindows) -or ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)
}

function Resolve-TargetRid {
    $arch = $env:PROCESSOR_ARCHITECTURE
    if ($env:PROCESSOR_ARCHITEW6432) {
        $arch = $env:PROCESSOR_ARCHITEW6432
    }
    if ([string]::IsNullOrWhiteSpace($arch)) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    }
    switch -Regex ($arch) {
        '^ARM64$' { return 'win-arm64' }
        '^(AMD64|x86_64|X64)$' { return 'win-x64' }
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
    if (-not (Test-IsWindowsHost)) { return }
    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if ($null -eq $currentUser) { return }

    $dirInfo = [System.IO.DirectoryInfo]::new($Path)
    $acl = [System.IO.FileSystemAclExtensions]::GetAccessControl($dirInfo, [System.Security.AccessControl.AccessControlSections]::Access)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($existing in @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))) {
        $null = $acl.RemoveAccessRule($existing)
    }
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $currentUser,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow
    )
    $acl.SetAccessRule($rule)
    [System.IO.FileSystemAclExtensions]::SetAccessControl($dirInfo, $acl)
}

function Register-UserPathEntry {
    param([Parameter(Mandatory = $true)][string]$BinDirectory)
    if (Test-IsWindowsHost) {
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
    }

    if (-not (($env:Path -split ';') | Where-Object { [string]::Equals($_.TrimEnd('\'), $BinDirectory.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase) })) {
        $env:Path = "$BinDirectory;$env:Path"
    }
}

function Unregister-UserPathEntry {
    param([Parameter(Mandatory = $true)][string]$BinDirectory)
    $normalizedTarget = $BinDirectory.TrimEnd('\')

    if (Test-IsWindowsHost) {
        $currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not [string]::IsNullOrWhiteSpace($currentUserPath)) {
            $segments = $currentUserPath.Split(';', [System.StringSplitOptions]::RemoveEmptyEntries)
            $remaining = @(
                foreach ($segment in $segments) {
                    if (-not [string]::Equals($segment.TrimEnd('\'), $normalizedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $segment
                    }
                }
            )
            $updatedUserPath = $remaining -join ';'
            if ($updatedUserPath -ne $currentUserPath) {
                [Environment]::SetEnvironmentVariable('Path', $updatedUserPath, 'User')
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:Path)) {
        $separator = [System.IO.Path]::PathSeparator
        $processSegments = $env:Path.Split($separator, [System.StringSplitOptions]::RemoveEmptyEntries)
        $remainingProcess = @(
            foreach ($segment in $processSegments) {
                if (-not [string]::Equals($segment.TrimEnd('\'), $normalizedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $segment
                }
            }
        )
        $env:Path = $remainingProcess -join $separator
    }
}

function Get-StartMenuShortcutPath {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
        return $null
    }
    return Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\IntoChat OS.lnk'
}

function Get-DesktopShortcutPath {
    $desktopDir = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($desktopDir)) {
        return $null
    }
    return Join-Path $desktopDir 'IntoChat OS.lnk'
}

function New-WindowsShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string]$Description = 'IntoChat OS'
    )
    if (-not (Test-IsWindowsHost)) {
        return
    }

    $parentDir = Split-Path -Parent $ShortcutPath
    if (-not [string]::IsNullOrWhiteSpace($parentDir) -and (-not (Test-Path -LiteralPath $parentDir))) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }

    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.Description = $Description
        $shortcut.IconLocation = "$TargetPath,0"
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shortcut) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        if ($null -ne $shell) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

function Remove-WindowsShortcut {
    param(
        [string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$BinDirectory
    )
    if ([string]::IsNullOrWhiteSpace($ShortcutPath) -or (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf))) {
        return $false
    }

    $shouldRemove = $true
    if (Test-IsWindowsHost) {
        $shell = $null
        $shortcut = $null
        try {
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($ShortcutPath)
            $targetPath = [string]$shortcut.TargetPath
            if (-not [string]::IsNullOrWhiteSpace($targetPath)) {
                $normalizedTarget = [System.IO.Path]::GetFullPath($targetPath)
                $normalizedBin = [System.IO.Path]::GetFullPath($BinDirectory).TrimEnd('\') + '\'
                if (-not $normalizedTarget.StartsWith($normalizedBin, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $shouldRemove = $false
                }
            }
        }
        catch {
            Write-Warning "Could not inspect shortcut '$ShortcutPath': $($_.Exception.Message)"
        }
        finally {
            if ($null -ne $shortcut) {
                [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
            }
            if ($null -ne $shell) {
                [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
            }
        }
    }

    if ($shouldRemove) {
        Remove-Item -LiteralPath $ShortcutPath -Force
        return $true
    }
    return $false
}

function Register-IntoChatProtocolHandler {
    param([Parameter(Mandatory = $true)][string]$DesktopExePath)
    if (-not (Test-IsWindowsHost)) {
        return
    }

    $protocolRoot = 'Registry::HKEY_CURRENT_USER\Software\Classes\intochat'
    $iconKey = Join-Path $protocolRoot 'DefaultIcon'
    $commandKey = Join-Path $protocolRoot 'shell\open\command'

    New-Item -Path $protocolRoot -Force | Out-Null
    Set-ItemProperty -Path $protocolRoot -Name '(Default)' -Value 'URL:IntoChat OS Protocol'
    Set-ItemProperty -Path $protocolRoot -Name 'URL Protocol' -Value ''

    New-Item -Path $iconKey -Force | Out-Null
    Set-ItemProperty -Path $iconKey -Name '(Default)' -Value "`"$DesktopExePath`",0"

    New-Item -Path $commandKey -Force | Out-Null
    Set-ItemProperty -Path $commandKey -Name '(Default)' -Value "`"$DesktopExePath`" `"%1`""
}

function Unregister-IntoChatProtocolHandler {
    param([Parameter(Mandatory = $true)][string]$BinDirectory)
    if (-not (Test-IsWindowsHost)) {
        return $false
    }

    $protocolRoot = 'Registry::HKEY_CURRENT_USER\Software\Classes\intochat'
    if (-not (Test-Path -LiteralPath $protocolRoot)) {
        return $false
    }

    $commandKey = Join-Path $protocolRoot 'shell\open\command'
    $shouldRemove = $true
    if (Test-Path -LiteralPath $commandKey) {
        try {
            $cmdValue = (Get-ItemProperty -LiteralPath $commandKey -Name '(Default)' -ErrorAction SilentlyContinue).'(Default)'
            if (-not [string]::IsNullOrWhiteSpace($cmdValue)) {
                $normalizedBin = [System.IO.Path]::GetFullPath($BinDirectory).TrimEnd('\') + '\'
                if (-not $cmdValue.Contains($normalizedBin, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $shouldRemove = $false
                }
            }
        }
        catch {
            $shouldRemove = $false
        }
    }

    if ($shouldRemove) {
        Remove-Item -LiteralPath $protocolRoot -Recurse -Force -ErrorAction SilentlyContinue
        return $true
    }

    return $false
}

function Stop-RunningIntoChat {
    param(
        [Parameter(Mandatory = $true)][string]$BinDirectory,
        [string]$Reason = 'upgrade'
    )
    if (-not (Test-IsWindowsHost)) {
        return
    }

    $existingDesktopExe = Join-Path $BinDirectory 'IntoChat.OS.Desktop.exe'
    if (Test-Path -LiteralPath $existingDesktopExe -PathType Leaf) {
        Write-Step "Stopping running IntoChat OS supervisor prior to $Reason"
        try {
            & $existingDesktopExe --quit 2>$null | Out-Null
        }
        catch {
            Write-Warning "Could not invoke '$existingDesktopExe --quit': $($_.Exception.Message)"
        }
    }

    $normalizedBin = [System.IO.Path]::GetFullPath($BinDirectory).TrimEnd('\') + '\'
    foreach ($procName in @('IntoChat.OS.Desktop', 'IntoChat.OS.Kernel', 'intochat_os_flutter')) {
        $procs = @(Get-Process -Name $procName -ErrorAction SilentlyContinue)
        foreach ($proc in $procs) {
            $procPath = $null
            try { $procPath = $proc.Path } catch { $procPath = $null }
            if (-not [string]::IsNullOrWhiteSpace($procPath) -and $procPath.StartsWith($normalizedBin, [System.StringComparison]::OrdinalIgnoreCase)) {
                try {
                    if (-not $proc.HasExited) {
                        $null = $proc.CloseMainWindow()
                        if (-not $proc.WaitForExit(3000)) {
                            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                        }
                    }
                }
                catch {
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                }
            }
        }
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

if ($Uninstall) {
    if ($DryRun) {
        Write-Step "[DryRun] Would stop running IntoChat OS, remove $binDir, remove User PATH entry, and remove Start Menu and Desktop shortcuts while preserving $secretsDir and $stateDir."
        exit 0
    }

    Stop-RunningIntoChat -BinDirectory $binDir -Reason 'uninstall'

    if (Test-Path -LiteralPath $binDir) {
        Remove-Item -LiteralPath $binDir -Recurse -Force
        Write-Step "Removed binary directory: $binDir"
    } else {
        Write-Step "Binary directory not found (already removed): $binDir"
    }

    if (-not $skipPathUpdate) {
        Unregister-UserPathEntry -BinDirectory $binDir
        Write-Step "Removed $binDir from User PATH."
    } else {
        Write-Step "User PATH cleanup skipped (-NoPath)."
    }

    $startMenuLink = Get-StartMenuShortcutPath
    if (Remove-WindowsShortcut -ShortcutPath $startMenuLink -BinDirectory $binDir) {
        Write-Step "Removed Start Menu shortcut: $startMenuLink"
    }

    $desktopLink = Get-DesktopShortcutPath
    if (Remove-WindowsShortcut -ShortcutPath $desktopLink -BinDirectory $binDir) {
        Write-Step "Removed Desktop shortcut: $desktopLink"
    }

    if (Unregister-IntoChatProtocolHandler -BinDirectory $binDir) {
        Write-Step "Removed HKCU intochat: protocol handler."
    }

    # HARD INVARIANT: Never delete $secretsDir (master.key custody) or $stateDir (user data).
    Write-Step "Uninstall complete. Preserved user secrets ($secretsDir) and state ($stateDir)."
    exit 0
}

$winAppCmd = Get-Command 'winapp' -ErrorAction SilentlyContinue
if ($null -ne $winAppCmd) {
    $winAppLocation = if ($winAppCmd.Source) { $winAppCmd.Source } else { $winAppCmd.Name }
    Write-Step "Detected Microsoft WinApp CLI ($winAppLocation) for optional package identity registration."
}

if ($DryRun) {
    $previewTag = if ($Version) { $Version } else { 'latest' }
    $shortcutNote = if ($skipShortcuts) { 'skip shortcuts' } elseif ($DesktopShortcut) { 'create Start Menu and Desktop shortcuts' } else { 'create Start Menu shortcut' }
    $identityNote = if ($RegisterIdentity) { ', and register local package identity via winapp' } else { '' }
    Write-Step "[DryRun] Would download https://releases.$PublicDomain/$Channel/$previewTag/intochat-$previewTag-$rid.zip, verify SHA-256, stage and unblock into $binDir, register User PATH, $shortcutNote$identityNote."
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

    foreach ($dir in @($resolvedHome, $binDir, $secretsDir, $runDir, $logsDir)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }

    Set-OwnerOnlyDirectoryAcl -Path $secretsDir
    Set-OwnerOnlyDirectoryAcl -Path $runDir

    $extractDir = Join-Path $stagingRoot 'extracted'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir -Force

    # Stop any running IntoChat OS instance cleanly before swapping binaries.
    Stop-RunningIntoChat -BinDirectory $binDir -Reason 'upgrade'

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

    # Clear Mark-of-the-Web (Zone.Identifier ADS) so browser-downloaded or -LocalArchive files
    # never trigger SmartScreen prompts on first launch.
    if (Test-IsWindowsHost) {
        Get-ChildItem -LiteralPath $binDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
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

    if (Test-IsWindowsHost -and (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
        Register-IntoChatProtocolHandler -DesktopExePath $desktopExe
        Write-Step "Registered HKCU intochat: protocol handler -> $desktopExe"
    }

    if (-not $skipShortcuts) {
        if (Test-IsWindowsHost) {
            $startMenuLink = Get-StartMenuShortcutPath
            if (-not [string]::IsNullOrWhiteSpace($startMenuLink)) {
                New-WindowsShortcut -ShortcutPath $startMenuLink -TargetPath $desktopExe -WorkingDirectory $binDir -Description 'IntoChat OS'
                Write-Step "Created Start Menu shortcut: $startMenuLink"
            }
            if ($DesktopShortcut) {
                $desktopLink = Get-DesktopShortcutPath
                if (-not [string]::IsNullOrWhiteSpace($desktopLink)) {
                    New-WindowsShortcut -ShortcutPath $desktopLink -TargetPath $desktopExe -WorkingDirectory $binDir -Description 'IntoChat OS'
                    Write-Step "Created Desktop shortcut: $desktopLink"
                }
            }
        } else {
            Write-Step "Skipping Windows shortcut creation on non-Windows host."
        }
    } else {
        Write-Step "Shortcut creation skipped (-NoShortcut)."
    }

    if ($RegisterIdentity) {
        if (-not (Test-IsWindowsHost)) {
            Write-Warning "Skipping -RegisterIdentity on non-Windows host."
        } elseif ($null -eq $winAppCmd) {
            Write-Warning "Microsoft WinApp CLI ('winapp') was not found on PATH; skipping local package identity registration."
        } elseif (-not (Test-Path -LiteralPath $desktopExe -PathType Leaf)) {
            Write-Warning "Desktop executable '$desktopExe' was not found; skipping local package identity registration."
        } else {
            Write-Step "Registering local sparse package identity via winapp create-debug-identity..."
            try {
                $global:LASTEXITCODE = 0
                & $winAppCmd create-debug-identity $desktopExe
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "winapp create-debug-identity exited with code $LASTEXITCODE; continuing without package identity."
                } else {
                    Write-Step "Registered local debug package identity for $desktopExe."
                }
            }
            catch {
                Write-Warning "winapp create-debug-identity failed: $($_.Exception.Message); continuing without package identity."
            }
        }
    }

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
