#!/usr/bin/env bash
# DX-04 (06-00): build and test a module with the DigitalBrain packages alone, from a folder outside the repository.
# The module folder is copied to <workdir>; every DigitalBrain ProjectReference becomes a PackageReference at the packed
# version, the restore sources are the <packages> folder plus nuget.org, and the copy gets no repository build files.
# An IntoChat.* ProjectReference cannot be satisfied by packages: while any remain the script lists them as warnings and
# exits 0 after the pack (the module still depends on the os, 12-12); once none remain it builds and tests strictly.
#
# Usage: dx04-drive.sh <module-folder> <packages-folder> <workdir>
set -euo pipefail

module=${1:?module folder}
packages=${2:?packages folder}
workdir=${3:?workdir}

nuget_org=https://api.nuget.org/v3/index.json
packages=$(cd "$packages" && pwd -W 2>/dev/null || pwd)
pack=$(ls "$packages"/DigitalBrain.[0-9]*.nupkg 2>/dev/null | head -n 1 || true)
[ -n "$pack" ] || { echo "::error::no DigitalBrain package in $packages"; exit 1; }
version=$(basename "$pack" .nupkg)
version=${version#DigitalBrain.}
echo "DigitalBrain packages: $version"

rm -rf "$workdir"
mkdir -p "$workdir"
cp -R "$module" "$workdir/module"
find "$workdir/module" \( -name bin -o -name obj \) -type d -prune -exec rm -rf {} +

# References between the module's own projects (tests -> module) stay ProjectReferences inside the copy.
local_projects=$(find "$workdir/module" -name '*.csproj' -exec basename {} .csproj \; | sort -u)
blocked=$(grep -ho 'ProjectReference Include="[^"]*IntoChat[^"]*"' $(find "$workdir/module" -name '*.csproj') | sed -E 's/.*\/([^/]*)\.csproj"/\1/' | sort -u \
  | grep -vxF -f <(printf '%s\n' "$local_projects") || true)
if [ -n "$blocked" ]; then
  while IFS= read -r name; do echo "::warning::DX-04 pending: the module still references os project $name"; done <<< "$blocked"
  echo "DX-04 pending: packages packed at $version; the module build from packages runs once no IntoChat.* reference remains"
  exit 0
fi

# Stand-alone build roots: no inheritance from the repository, CPM off, nuget.org plus the packed folder.
cat > "$workdir/Directory.Build.props" <<'PROPS'
<Project>
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <!-- A module author's test projects: xunit v3 on Microsoft.Testing.Platform, the version DigitalBrain.Testing builds against. -->
  <PropertyGroup Condition="$(MSBuildProjectName.Contains('.Tests.'))">
    <OutputType>Exe</OutputType>
    <IsTestProject>true</IsTestProject>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup Condition="$(MSBuildProjectName.Contains('.Tests.'))">
    <PackageReference Include="xunit.v3.mtp-v2" Version="XUNIT_VERSION" />
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
PROPS
xunit=$(sed -nE 's/.*PackageVersion Include="xunit.v3.mtp-v2" Version="([^"]*)".*/\1/p' "$(dirname "$0")/../framework/Directory.Packages.props")
sed -i "s/XUNIT_VERSION/$xunit/" "$workdir/Directory.Build.props"
printf '<Project />\n' > "$workdir/Directory.Build.targets"
printf '<Project />\n' > "$workdir/Directory.Packages.props"
cat > "$workdir/nuget.config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed" value="$packages" />
    <add key="nuget.org" value="$nuget_org" />
  </packageSources>
</configuration>
CONFIG

for project in $(find "$workdir/module" -name '*.csproj'); do
  sed -i -E "s#<ProjectReference Include=\"[^\"]*/(DigitalBrain[A-Za-z.]*)\.csproj\"[^/]*/>#<PackageReference Include=\"\1\" Version=\"$version\" />#g" "$project"
done

export NUGET_PACKAGES="$workdir/.nuget/packages"
for project in $(find "$workdir/module" -name '*.csproj' | sort); do
  dotnet build "$project" -c Release
done
for project in $(find "$workdir/module" -name '*.Tests.Unit.csproj' | sort); do
  dotnet test --project "$project" -c Release --no-build
done
echo "DX-04: the module builds and passes its unit facts from $version packages alone"
