#requires -Version 7
# Builds one full-trust MSIX holding the self-contained OS host (IntoChat.OS.Kernel), the Flutter Windows shell
# (which carries the Windows connector through the Kernel's Windows edge) and the desktop launcher, plus an
# App Installer file for updates. Version comes from VERSION at the repository root.
#
#   pwsh scripts/msix/build-msix.ps1 [-Build 0] [-Publisher "CN=..."] [-BaseUri https://.../windows]
#                                    [-CertificatePath cert.pfx -CertificatePassword (Read-Host -AsSecureString)]
#                                    [-SkipOs] [-SkipShell]
#
# Output: artifacts/msix/IntoChat.OS_<version>_x64.msix and artifacts/msix/IntoChat.OS.appinstaller.
# Without -CertificatePath the package is left unsigned (Windows installs only signed packages).
[CmdletBinding()]
param(
    [int] $Build = 0,
    [string] $Name = 'IntoChat.OS',
    [string] $Publisher = 'CN=IntoChat Dev',
    [string] $PublisherDisplayName = 'IntoChat',
    [string] $BaseUri = 'https://github.com/intochat/os/releases/latest/download',
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [switch] $SkipOs,
    [switch] $SkipShell
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$out = Join-Path $root 'artifacts' 'msix'
$layout = Join-Path $out 'layout'
$semver = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($semver -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "VERSION '$semver' is not major.minor.patch." }
$version = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$Build"
$packageFile = "${Name}_${version}_x64.msix"

function Find-SdkTool([string] $tool) {
    $onPath = Get-Command $tool -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits' '10' 'bin'
    $found = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Where-Object Name -Match '^10\.' |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64' $tool } |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw "$tool not found. Install the Windows SDK (App packaging tools)." }
    return $found
}

function Invoke-Checked([string] $file, [string[]] $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file exited with $LASTEXITCODE." }
}

New-Item -ItemType Directory -Force $layout | Out-Null

if (-not $SkipOs) {
    Remove-Item -Recurse -Force (Join-Path $layout 'os') -ErrorAction SilentlyContinue
    Invoke-Checked 'dotnet' @('publish', (Join-Path $root 'src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $layout 'os'), '--nologo')
}

if (-not $SkipShell) {
    $shell = Join-Path $root 'src/IntoChat.OS.Modules/Google/Flutter/app/shell'
    Push-Location $shell
    try { Invoke-Checked 'flutter' @('build', 'windows', '--release', "--build-name=$semver", "--build-number=$Build") }
    finally { Pop-Location }
    Remove-Item -Recurse -Force (Join-Path $layout 'shell') -ErrorAction SilentlyContinue
    Copy-Item -Recurse (Join-Path $shell 'build/windows/x64/runner/Release') (Join-Path $layout 'shell')
}

$launcher = Join-Path $out 'launcher'
Invoke-Checked 'dotnet' @('publish', (Join-Path $PSScriptRoot 'launcher/IntoChat.OS.Desktop.cs'), '-c', 'Release', '-o', $launcher, '--nologo')
Copy-Item (Join-Path $launcher 'IntoChat.OS.Desktop.exe') $layout -Force

# The package logos reuse the shell's icons; Windows scales them.
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$icons = Join-Path $root 'src/IntoChat.OS.Modules/Google/Flutter/app/shell/web/icons'
Copy-Item (Join-Path $icons 'Icon-192.png') (Join-Path $assets 'Square44x44Logo.png') -Force
Copy-Item (Join-Path $icons 'Icon-192.png') (Join-Path $assets 'Square150x150Logo.png') -Force
Copy-Item (Join-Path $icons 'Icon-192.png') (Join-Path $assets 'StoreLogo.png') -Force

foreach ($required in 'os/IntoChat.OS.Kernel.exe', 'os/features', 'shell/intochat_os_flutter.exe', 'IntoChat.OS.Desktop.exe') {
    if (-not (Test-Path (Join-Path $layout $required))) { throw "The package layout is missing $required." }
}

function Expand-Template([string] $template, [string] $target) {
    $text = Get-Content (Join-Path $PSScriptRoot $template) -Raw
    foreach ($token in @{ name = $Name; publisher = $Publisher; publisherDisplayName = $PublisherDisplayName; version = $version; baseUri = $BaseUri.TrimEnd('/'); package = $packageFile }.GetEnumerator()) {
        $text = $text.Replace("`$$($token.Key)`$", [System.Security.SecurityElement]::Escape($token.Value))
    }
    Set-Content -Path $target -Value $text -NoNewline -Encoding utf8
}
Expand-Template 'AppxManifest.xml' (Join-Path $layout 'AppxManifest.xml')

$msix = Join-Path $out $packageFile
Invoke-Checked (Find-SdkTool 'makeappx.exe') @('pack', '/d', $layout, '/p', $msix, '/o')

if ($CertificatePath) {
    $signArguments = @('sign', '/fd', 'SHA256', '/f', $CertificatePath)
    if ($CertificatePassword) { $signArguments += @('/p', (ConvertFrom-SecureString $CertificatePassword -AsPlainText)) }
    Invoke-Checked (Find-SdkTool 'signtool.exe') ($signArguments + $msix)
} else {
    Write-Warning 'No -CertificatePath: the package is unsigned. Sign it with a certificate whose subject equals -Publisher before installing.'
}

Expand-Template 'IntoChat.OS.appinstaller' (Join-Path $out 'IntoChat.OS.appinstaller')
Write-Host "Built $msix (version $version)."
