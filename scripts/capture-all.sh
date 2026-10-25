#!/usr/bin/env bash
# Cross-platform twin of capture-all.ps1: regenerates every pinned contract artifact, additive-only by default.
# Usage: scripts/capture-all.sh [--allow-breaking] [--keys-only] [--skip-sync] [--out DIR] [--help]
# Delegates to pwsh when it is installed (flags are forwarded as -AllowBreaking/-KeysOnly/-SkipSync/-Out); otherwise runs the
# same `dotnet test --explicit only` captures and compares old/new pins with jq.
set -euo pipefail

allow=0; keys=0; sync=1; out=""
while [ $# -gt 0 ]; do
  case "$1" in
    --help|-h) sed -n '2,5p' "$0"; exit 0 ;;
    --allow-breaking) allow=1 ;;
    --keys-only) keys=1 ;;
    --skip-sync) sync=0 ;;
    --out) out="$2"; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

root=$(git rev-parse --show-toplevel)
cd "$root"

if command -v pwsh >/dev/null 2>&1; then
  args=()
  [ "$allow" = 1 ] && args+=(-AllowBreaking)
  [ "$keys" = 1 ] && args+=(-KeysOnly)
  [ "$sync" = 0 ] && args+=(-SkipSync)
  [ -n "$out" ] && args+=(-Out "$out")
  exec pwsh -NoProfile -File scripts/capture-all.ps1 "${args[@]}"
fi

command -v jq >/dev/null 2>&1 || { echo "capture-all.sh needs pwsh or jq" >&2; exit 1; }
# The keys-only check (D-23) is PowerShell; without pwsh refuse rather than skip it.
if [ "$keys" = 1 ]; then echo "--keys-only needs pwsh (scripts/keys-only-check.ps1)" >&2; exit 1; fi

arch=$(git ls-files -- 'src/**/*Architecture.Tests.Unit.csproj')
flutter=$(git ls-files -- 'src/**/*Modules.Flutter.Tests.Integration.csproj')
archdir=$(dirname "$arch"); contracts="$(dirname "$flutter")/Contracts"
nightly=$(grep -oE '<[A-Za-z0-9]*Nightly\b' Directory.Build.props | tr -d '<' | sort -u | sed 's/.*/-p:&=false/' | tr '\n' ' ')

pins=(
  "$archdir/ContractManifest.json" "$archdir/GrainFactoryOwners.json" "$archdir/OrleansReferenceOwners.json"
  "$archdir/ConfigurationKeyOwners.json" "$archdir/TestTierBaseline.json" "$archdir/CollectionCompatibilitySamples.json" "$archdir/SynapseWordAllowlist.json"
  "$archdir/FrameworkVendorBaseline.json" "$archdir/AppsModuleIdBaseline.json"
  "$archdir/NamespaceSchemeBaseline.json"
  "$contracts/flutter-wire-names.golden.json" "$contracts/flutter-aliasless-enums.golden.json" "$contracts/ui-states.golden.json"
)
# Shrinking ratchets: removals there are the point, so they are not breaking.
shrinking=" FrameworkVendorBaseline.json AppsModuleIdBaseline.json NamespaceSchemeBaseline.json "
snap=$(mktemp -d); trap 'rm -rf "$snap"' EXIT
for pin in "${pins[@]}"; do mkdir -p "$snap/$(dirname "$pin")"; cp "$pin" "$snap/$pin"; done

if [ "$sync" = 1 ] && [ -f scripts/sync-solution.cs ]; then dotnet run scripts/sync-solution.cs; fi

capture() {
  echo "capture $2"
  # shellcheck disable=SC2086
  dotnet test --project "$1" -c Release -p:CodeGraphRefresh=false $nightly --filter-method "*$2" --explicit only --nologo
}
for m in CaptureContractManifest CaptureGrainFactoryOwners CaptureOrleansReferenceOwners CaptureConfigurationKeyOwners CaptureTestTierBaseline CaptureCollectionCompatibilitySamples CaptureSynapseWordAllowlist \
         CaptureFrameworkVendorBaseline CaptureAppsModuleIdBaseline CaptureNamespaceSchemeBaseline; do capture "$arch" "$m"; done
for m in CaptureFlutterWireNames CaptureUiStateGoldens; do capture "$flutter" "$m"; done

# Leaves as "path<TAB>value": objects recurse, arrays contribute one line per element (same rule as the ps1).
leaves() {
  jq -r 'def flat($p): if type=="object" then to_entries[] | .key as $k | .value | flat($p + "/" + $k)
    elif type=="array" then .[] | "\($p)[]\t\(tojson)" else "\($p)\t\(tojson)" end; flat("")' "$1" | sort -u
}
breaking=0
for pin in "${pins[@]}"; do
  case "$shrinking" in *" $(basename "$pin") "*) continue ;; esac
  removed=$(comm -23 <(leaves "$snap/$pin") <(leaves "$pin") || true)
  if [ -n "$removed" ]; then breaking=1; echo "$(basename "$pin"):"; echo "$removed" | head -20 | sed 's/^/  /'; fi
done
if [ "$breaking" = 1 ] && [ "$allow" = 0 ]; then
  for pin in "${pins[@]}"; do cp "$snap/$pin" "$pin"; done
  echo "Capture would remove keys or change values (pins restored); rerun with --allow-breaking after review." >&2
  exit 1
fi
if [ -n "$out" ]; then mkdir -p "$out"; for pin in "${pins[@]}"; do tr -d '\r' < "$pin" > "$out/$(basename "$pin")"; done; fi
echo "pins regenerated in place; review git diff"
