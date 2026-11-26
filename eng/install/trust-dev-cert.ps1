<#
.SYNOPSIS
    Trust or remove the IntoChat Dev public signing certificate for Windows MSIX sideloading.
.DESCRIPTION
    Resolves IntoChat-DevCert.cer locally (adjacent eng/release/windows/IntoChat-DevCert.cer
    or -CertPath) or downloads it over HTTPS from GitHub Releases, verifies its pinned SHA-1
    thumbprint (3365BF08BBE6D23778CE1B61715C1AD6DDDDAD7E) and subject (CN=IntoChat Dev) before
    trusting anything, and installs the public certificate into Cert:\LocalMachine\TrustedPeople
    (elevating via UAC if required, or using Microsoft WinApp CLI when available).
    Pass -Uninstall to remove the pinned certificate from TrustedPeople and Root stores.
#>
[CmdletBinding()]
param(
    [string]$CertPath = $env:INTOCHAT_DEV_CERT_PATH,
    [string]$Url = $(if ($env:INTOCHAT_DEV_CERT_URL) { $env:INTOCHAT_DEV_CERT_URL } else { 'https://github.com/intochat/intochat/releases/latest/download/IntoChat-DevCert.cer' }),
    [switch]$DryRun,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

$ExpectedThumbprint = '3365BF08BBE6D23778CE1B61715C1AD6DDDDAD7E'
$ExpectedSubject = 'CN=IntoChat Dev'

function Write-Step {
    param([string]$Message)
    Write-Host "  -> $Message" -ForegroundColor Cyan
}

function Test-IsWindowsHost {
    return ($PSVersionTable.PSEdition -eq 'Desktop') -or ([bool]$IsWindows) -or ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT)
}

function Test-IsAdministrator {
    if (-not (Test-IsWindowsHost)) {
        return $false
    }
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-PinnedCertificate {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Certificate file '$Path' does not exist."
    }

    $fullPath = (Resolve-Path -LiteralPath $Path).Path
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($fullPath)
    try {
        if ($cert.HasPrivateKey) {
            throw "Refusing certificate '$fullPath': public .cer must never contain private key material."
        }
        $actualThumbprint = $cert.Thumbprint.ToUpperInvariant()
        if ($actualThumbprint -ne $ExpectedThumbprint) {
            throw "Certificate thumbprint mismatch for '$fullPath'. Expected '$ExpectedThumbprint', got '$actualThumbprint'. Refusing to trust."
        }
        if ($cert.Subject -ne $ExpectedSubject) {
            throw "Certificate subject mismatch for '$fullPath'. Expected '$ExpectedSubject', got '$($cert.Subject)'. Refusing to trust."
        }
        return [PSCustomObject]@{
            Path       = $fullPath
            Subject    = $cert.Subject
            Thumbprint = $actualThumbprint
            NotAfter   = $cert.NotAfter
        }
    }
    finally {
        $cert.Dispose()
    }
}

function Resolve-LocalCertPath {
    param(
        [string]$ExplicitPath,
        [bool]$PreferUrl = $false
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Specified -CertPath '$ExplicitPath' does not exist."
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    if ($PreferUrl) {
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $engRoot = Split-Path -Parent $PSScriptRoot
        if (-not [string]::IsNullOrWhiteSpace($engRoot)) {
            $adjacent = Join-Path (Join-Path (Join-Path $engRoot 'release') 'windows') 'IntoChat-DevCert.cer'
            if (Test-Path -LiteralPath $adjacent -PathType Leaf) {
                return (Resolve-Path -LiteralPath $adjacent).Path
            }
        }
    }

    return $null
}

function Invoke-ElevatedPowerShell {
    param([Parameter(Mandatory = $true)][string[]]$ArgumentList)

    $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList $ArgumentList
    if ($null -eq $proc -or $proc.ExitCode -ne 0) {
        $code = if ($null -eq $proc) { 'unknown' } else { $proc.ExitCode }
        throw "Elevated certificate operation failed (exit code $code)."
    }
}

function Remove-PinnedCertFromStores {
    $removed = 0
    foreach ($storeName in @('TrustedPeople', 'Root')) {
        $certStorePath = "Cert:\LocalMachine\$storeName\$ExpectedThumbprint"
        if (Test-Path -LiteralPath $certStorePath) {
            Remove-Item -LiteralPath $certStorePath -Force
            Write-Step "Removed $ExpectedThumbprint from Cert:\LocalMachine\$storeName."
            $removed++
        }
    }
    return $removed
}

Write-Host ""
Write-Host "IntoChat OS Dev Certificate Helper ($ExpectedSubject)" -ForegroundColor Green
Write-Step "Pinned thumbprint: $ExpectedThumbprint"

if ($Uninstall) {
    if ($DryRun) {
        Write-Step "[DryRun] Would remove certificate $ExpectedThumbprint from Cert:\LocalMachine\TrustedPeople and Cert:\LocalMachine\Root."
        exit 0
    }

    if (-not (Test-IsWindowsHost)) {
        throw "Windows certificate store operations require Windows (pass -DryRun on non-Windows hosts)."
    }

    $presentInTrustedPeople = Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$ExpectedThumbprint"
    $presentInRoot = Test-Path -LiteralPath "Cert:\LocalMachine\Root\$ExpectedThumbprint"
    if (-not $presentInTrustedPeople -and -not $presentInRoot) {
        Write-Step "Certificate $ExpectedThumbprint is not installed in LocalMachine TrustedPeople or Root."
        exit 0
    }

    if (-not (Test-IsAdministrator)) {
        Write-Step "Requesting Administrator elevation to remove $ExpectedThumbprint from LocalMachine certificate stores..."
        if (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
            Invoke-ElevatedPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Uninstall')
        }
        else {
            $inlineUninstall = @"
`$ErrorActionPreference = 'Stop'
foreach (`$s in @('TrustedPeople', 'Root')) {
    `$p = "Cert:\LocalMachine\`$s\$ExpectedThumbprint"
    if (Test-Path -LiteralPath `$p) { Remove-Item -LiteralPath `$p -Force }
}
"@
            $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($inlineUninstall))
            Invoke-ElevatedPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        }
        Write-Step "Removed $ExpectedThumbprint from LocalMachine certificate stores."
        exit 0
    }

    $null = Remove-PinnedCertFromStores
    Write-Step "Uninstall complete."
    exit 0
}

$preferExplicitUrl = $PSBoundParameters.ContainsKey('Url') -and [string]::IsNullOrWhiteSpace($CertPath)
$localCertPath = Resolve-LocalCertPath -ExplicitPath $CertPath -PreferUrl $preferExplicitUrl

if ($DryRun) {
    if ($null -ne $localCertPath) {
        $verified = Assert-PinnedCertificate -Path $localCertPath
        Write-Step "[DryRun] Verified local certificate '$($verified.Path)' (Subject: $($verified.Subject), Thumbprint: $($verified.Thumbprint)); would install into Cert:\LocalMachine\TrustedPeople."
    }
    else {
        if (-not $Url.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing insecure certificate URL '$Url'. HTTPS is required."
        }
        Write-Step "[DryRun] Would download $Url, verify thumbprint $ExpectedThumbprint, and install into Cert:\LocalMachine\TrustedPeople."
    }
    exit 0
}

if (-not (Test-IsWindowsHost)) {
    throw "Windows certificate store operations require Windows (pass -DryRun on non-Windows hosts)."
}

$stagingRoot = $null
try {
    $resolvedCertPath = $localCertPath
    if ($null -eq $resolvedCertPath) {
        if (-not $Url.StartsWith('https://', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing insecure certificate URL '$Url'. HTTPS is required."
        }
        $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("intochat-devcert-" + [Guid]::NewGuid().ToString('N').Substring(0, 12))
        New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
        $resolvedCertPath = Join-Path $stagingRoot 'IntoChat-DevCert.cer'
        Write-Step "Downloading $Url"
        Invoke-WebRequest -Uri $Url -OutFile $resolvedCertPath -UseBasicParsing
    }
    else {
        Write-Step "Using local certificate: $resolvedCertPath"
    }

    $verified = Assert-PinnedCertificate -Path $resolvedCertPath
    Write-Step "Verified certificate thumbprint $($verified.Thumbprint) ($($verified.Subject), valid through $($verified.NotAfter.ToString('yyyy-MM-dd')))."

    if (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$ExpectedThumbprint") {
        Write-Step "Certificate $ExpectedThumbprint is already trusted in Cert:\LocalMachine\TrustedPeople."
        exit 0
    }

    if (-not (Test-IsAdministrator)) {
        Write-Step "Requesting Administrator elevation to trust $ExpectedThumbprint in Cert:\LocalMachine\TrustedPeople..."
        if (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
            Invoke-ElevatedPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-CertPath', $verified.Path)
        }
        else {
            $pathB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($verified.Path))
            $inlineInstall = @"
`$ErrorActionPreference = 'Stop'
`$p = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$pathB64'))
`$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(`$p)
try {
    if (`$c.HasPrivateKey -or `$c.Thumbprint.ToUpperInvariant() -ne '$ExpectedThumbprint' -or `$c.Subject -ne '$ExpectedSubject') {
        throw 'Pinned certificate verification failed in elevated helper.'
    }
} finally { `$c.Dispose() }
Import-Certificate -FilePath `$p -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
"@
            $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($inlineInstall))
            Invoke-ElevatedPowerShell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        }
        if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$ExpectedThumbprint")) {
            throw "Certificate $ExpectedThumbprint was not found in Cert:\LocalMachine\TrustedPeople after elevation."
        }
        Write-Step "Trusted $ExpectedThumbprint in Cert:\LocalMachine\TrustedPeople. You can now install IntoChat-win-x64.msix or IntoChat.OS.appinstaller."
        exit 0
    }

    $winAppCmd = Get-Command 'winapp' -ErrorAction SilentlyContinue
    if ($null -ne $winAppCmd) {
        Write-Step "Installing verified certificate via winapp cert install..."
        $global:LASTEXITCODE = 0
        & $winAppCmd.Source cert install $verified.Path
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "winapp cert install exited with code $LASTEXITCODE; falling back to Import-Certificate."
        }
    }

    if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$ExpectedThumbprint")) {
        Write-Step "Importing verified certificate into Cert:\LocalMachine\TrustedPeople..."
        Import-Certificate -FilePath $verified.Path -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    }

    if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\TrustedPeople\$ExpectedThumbprint")) {
        throw "Failed to confirm $ExpectedThumbprint in Cert:\LocalMachine\TrustedPeople."
    }

    Write-Step "Trusted $ExpectedThumbprint in Cert:\LocalMachine\TrustedPeople. You can now install IntoChat-win-x64.msix or IntoChat.OS.appinstaller."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($stagingRoot) -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
