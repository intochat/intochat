#!/usr/bin/env bash
# Instantiate the intochat-module template from the packed IntoChat.OS.Templates, then build and test each instance.
# Usage: template-smoke.sh <workdir> <packagesDir> [feed]
#   workdir     empty directory outside the repository; instances are created under it
#   packagesDir folder holding IntoChat.OS.Templates.*.nupkg and the IntoChat.OS* packages it restores
#   feed        optional literal "feed": keep the template's own nuget.config (GitHub Packages feed,
#               credentials from NUGET_GITHUB_USERNAME / NUGET_GITHUB_TOKEN) instead of mapping to packagesDir
# The script never prints credentials.
set -euo pipefail

workdir=${1:?workdir}
packages=${2:?packagesDir}
mode=${3:-local}

mkdir -p "$workdir"
workdir=$(cd "$workdir" && pwd -W 2>/dev/null || pwd)
packages=$(cd "$packages" && pwd -W 2>/dev/null || pwd)

pack=$(ls "$packages"/IntoChat.OS.Templates.*.nupkg | head -n 1)
[ -n "$pack" ] || { echo "no IntoChat.OS.Templates package in $packages" >&2; exit 1; }

cleanup() { dotnet new uninstall IntoChat.OS.Templates >/dev/null 2>&1 || true; }
trap cleanup EXIT

dotnet new uninstall IntoChat.OS.Templates >/dev/null 2>&1 || true
dotnet new install "$pack" --force >/dev/null

write_local_config() {
  local file=$1
  cat > "$file" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="IntoChat.OS*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF
}

for entry in Weather:weather Gmail:gmail MyModule:my-module Foo.Bar:foo-bar; do
  name=${entry%%:*}
  id=${entry##*:}
  dir="$workdir/$name"
  started=$(date +%s)
  dotnet new intochat-module -n "$name" --ModuleId "$id" -o "$dir" >/dev/null
  [ "$mode" = "feed" ] || write_local_config "$dir/nuget.config"
  (cd "$dir" && dotnet build "$name.Tests.Integration/$name.Tests.Integration.csproj" -c Release >/dev/null)
  (cd "$dir" && dotnet test --project "$name.Tests.Integration/$name.Tests.Integration.csproj" -c Release --no-build)
  echo "template-smoke: $name ($id) ok in $(( $(date +%s) - started ))s"
done

# The template engine has no parameter validation; an invalid module id is rejected by the SDK generator (IOS0002)
# when the instance builds. Accept either a refused instantiation or a failing build.
bad="$workdir/Bad"
if dotnet new intochat-module -n Bad --ModuleId "Bad Id" -o "$bad" >/dev/null 2>&1; then
  [ "$mode" = "feed" ] || write_local_config "$bad/nuget.config"
  if (cd "$bad" && dotnet build "Bad/Bad.csproj" -c Release >/dev/null 2>&1); then
    echo "template-smoke: --ModuleId 'Bad Id' was accepted and built" >&2
    exit 1
  fi
fi
echo "template-smoke: 'Bad Id' rejected"
