<#
.SYNOPSIS
  Proves a pin regeneration changed keys only (D-23): per pin section the multiset of values is unchanged.

.DESCRIPTION
  Compares every JSON pin in -Before with the same-named file in -After. Sectioned pins (ContractManifest,
  TestTierBaseline) compare each top-level member on its own; every other pin is one section. Inside a section the
  entry keys are dropped and the remaining leaves (nested path plus raw value, line endings normalised) form a sorted
  multiset. Keys may change freely; any value change fails. Values on both sides first pass through the namespace
  rename map below, because several pins carry CLR or namespace names as values (Flutter goldens, ContractManifest
  States and method signatures, CollectionCompatibilitySamples, SynapseWordAllowlist): a renamed namespace is the
  same value, an edited alias is not. Exits 1 listing the first
  differences.

.EXAMPLE
  pwsh scripts/keys-only-check.ps1 -Before /tmp/pins-before -After /tmp/pins-after
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    # Pins this change is allowed to delete (shrinking ratchets driven to empty).
    [string[]]$Deleted = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sectioned = @('ContractManifest.json', 'TestTierBaseline.json')

# The namespace rename map (D-14, 02-15): old root -> new root, longest match wins. Module vendor roots move under
# IntoChat.OS.Modules; IntoChat.OS.Framework namespaces declared outside the Framework assembly follow their
# assemblies (Connections, Sdk, Contracts.*). The Framework assembly keeps IntoChat.OS.Framework (R4-03).
$renames = [ordered]@{}
foreach ($vendor in 'AI', 'Apps', 'ArtCraft', 'Circles', 'ClickHouse', 'Flame', 'Flutter', 'Google', 'Microsoft',
                    'Postgres', 'Qdrant', 'Salesforce', 'Supabase', 'Time', 'Tools') {
    $renames["IntoChat.OS.$vendor"] = "IntoChat.OS.Modules.$vendor"
}
# IntoChat.OS.Mcp stays the namespace of the IntoChat.OS.Mcp host; only the module's types move.
foreach ($type in 'IMcpServer', 'McpModule', 'McpServerNeuron', 'McpServerSpec', 'McpServerState', 'McpServerStore', 'McpToolRan') {
    $renames["IntoChat.OS.Mcp.$type"] = "IntoChat.OS.Modules.Mcp.$type"
}
$renames['IntoChat.OS.Framework.Platform.Connections'] = 'IntoChat.OS.Connections.Platform.Connections'
$renames['IntoChat.OS.Framework.Platform.Entries'] = 'IntoChat.OS.Connections.Platform.Entries'
$renames['IntoChat.OS.Framework.Platform.Marketplace'] = 'IntoChat.OS.Connections.Platform.Marketplace'
$renames['IntoChat.OS.Framework.Platform.Secrets'] = 'IntoChat.OS.Connections.Platform.Secrets'
$renames['IntoChat.OS.Framework.Contracts'] = 'IntoChat.OS.Connections.Contracts'
$renames['IntoChat.OS.Framework.Surface'] = 'IntoChat.OS.Connections.Surface'
$renames['IntoChat.OS.Framework.Caching'] = 'IntoChat.OS.Connections.Caching'

$pattern = '(?<![\w.])(' + (($renames.Keys | Sort-Object Length -Descending | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?!\w)'
function Rename([string]$Text) { [regex]::Replace($Text, $pattern, { param($m) $renames[$m.Value] }) }

function Add-Leaves([Text.Json.JsonElement]$Element, [string]$Path, [Collections.Generic.List[string]]$Into) {
    switch ($Element.ValueKind) {
        'Object' { foreach ($p in $Element.EnumerateObject()) { Add-Leaves $p.Value "$Path/$($p.Name)" $Into } }
        'Array'  { foreach ($e in $Element.EnumerateArray()) { Add-Leaves $e "$Path[]" $Into } }
        default  { $Into.Add("$Path`t$($Element.GetRawText().Replace("`r`n", "`n"))") }
    }
}

# Section name -> leaves of its entries, entry keys dropped.
function Get-Sections([string]$File, [bool]$Split) {
    $doc = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($File))
    $sections = [ordered]@{}
    $parts = New-Object 'Collections.Generic.List[object]'
    if ($Split -and $doc.RootElement.ValueKind -eq 'Object') {
        foreach ($p in $doc.RootElement.EnumerateObject()) { $parts.Add(@($p.Name, $p.Value)) }
    } else { $parts.Add(@('(root)', $doc.RootElement)) }
    foreach ($part in $parts) {
        $leaves = New-Object 'Collections.Generic.List[string]'
        $value = [Text.Json.JsonElement]$part[1]
        switch ($value.ValueKind) {
            'Object' { foreach ($entry in $value.EnumerateObject()) { Add-Leaves $entry.Value '' $leaves } }
            'Array'  { foreach ($entry in $value.EnumerateArray()) { Add-Leaves $entry '' $leaves } }
            default  { Add-Leaves $value '' $leaves }
        }
        $sections[[string]$part[0]] = $leaves
    }
    $doc.Dispose()
    return $sections
}

$failures = New-Object 'Collections.Generic.List[string]'
foreach ($old in Get-ChildItem -Path $Before -Filter '*.json' -File) {
    $name = $old.Name
    $new = Join-Path $After $name
    if (-not (Test-Path $new)) {
        if ($Deleted -notcontains $name) { $failures.Add("${name}: pin missing after capture") }
        continue
    }
    $split = $sectioned -contains $name
    $was = Get-Sections $old.FullName $split
    $now = Get-Sections $new $split
    $names = @($was.Keys) + @($now.Keys | Where-Object { -not $was.Contains($_) })
    foreach ($section in $names) {
        $a = [string[]]@(if ($was.Contains($section)) { $was[$section] | ForEach-Object { Rename $_ } })
        $b = [string[]]@(if ($now.Contains($section)) { $now[$section] | ForEach-Object { Rename $_ } })
        [Array]::Sort($a, [StringComparer]::Ordinal); [Array]::Sort($b, [StringComparer]::Ordinal)
        # Multiset difference by counting.
        $count = New-Object 'Collections.Generic.Dictionary[string,int]' ([StringComparer]::Ordinal)
        foreach ($x in $a) { $n = 0; [void]$count.TryGetValue($x, [ref]$n); $count[$x] = $n + 1 }
        foreach ($x in $b) { $n = 0; [void]$count.TryGetValue($x, [ref]$n); $count[$x] = $n - 1 }
        $shown = 0
        foreach ($kv in $count.GetEnumerator()) {
            if ($kv.Value -eq 0) { continue }
            if ($shown++ -ge 10) { break }
            $failures.Add("${name} [$section] $(if ($kv.Value -gt 0) { 'lost' } else { 'gained' }) x$([Math]::Abs($kv.Value)): $($kv.Key)")
        }
    }
}
if ($failures.Count -gt 0) {
    Write-Host "keys-only check failed: values changed ($($failures.Count) differences shown, up to 10 per section):"
    $failures | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host 'keys-only check passed: every pin section kept its values; only keys changed'
