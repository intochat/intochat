#!/usr/bin/env bash
# Restore and build intochat/platform against the os packages packed in this run.
# Usage: downstream-build.sh <platformDir> <packagesDir>
#   platformDir checkout of intochat/platform
#   packagesDir folder holding the freshly packed IntoChat.OS*.nupkg files
# IntoChat.* packages restore only from packagesDir, never from a remote feed.
# The script never prints credentials.
set -euo pipefail

platform=${1:?platform_dir}
packages=${2:?packages_dir}

platform=$(cd "$platform" && pwd -W 2>/dev/null || pwd)
packages=$(cd "$packages" && pwd -W 2>/dev/null || pwd)
root=$(cd "$(dirname "$0")/../.." && pwd)
V=$(tr -d '[:space:]' < "$root/VERSION")

config=$(mktemp -d)/nuget.config
cat > "$config" <<CFG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="IntoChat.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
CFG

dotnet restore "$platform/IntoChat.slnx" --configfile "$config" -p:IntoChatOSVersion="$V"
dotnet build "$platform/IntoChat.slnx" -c Release --no-restore -p:IntoChatOSVersion="$V"
echo "downstream-build: platform builds against os $V"
