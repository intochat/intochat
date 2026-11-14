#!/bin/sh
# Packages the self-contained IntoChat OS POSIX release bundle (macOS / Linux) with SHA-256 manifests.
# Produces intochat-<version>-<rid>.tar.gz, VERSION, SHA256SUMS, and latest.json matching eng/install/install.sh.
set -eu

RID=""
CONFIGURATION="Release"
CHANNEL="stable"
PUBLIC_DOMAIN="intochat.io"
OUTPUT_DIR=""
SKIP_FLUTTER=0
DRY_RUN=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        --rid)
            RID="$2"
            shift 2
            ;;
        --configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --channel)
            CHANNEL="$2"
            shift 2
            ;;
        --domain)
            PUBLIC_DOMAIN="$2"
            shift 2
            ;;
        --output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --skip-flutter)
            SKIP_FLUTTER=1
            shift
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        *)
            echo "Unknown option: $1" >&2
            exit 2
            ;;
    esac
done

SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd)"
VERSION="$(tr -d ' \t\r\n' < "$REPO_ROOT/VERSION")"

if [ -z "$RID" ]; then
    os_name="$(uname -s)"
    arch_name="$(uname -m)"
    case "$os_name" in
        Darwin) os_part="osx" ;;
        Linux)  os_part="linux" ;;
        *) echo "Unsupported OS: $os_name" >&2; exit 1 ;;
    esac
    case "$arch_name" in
        x86_64|amd64)  arch_part="x64" ;;
        arm64|aarch64) arch_part="arm64" ;;
        *) echo "Unsupported CPU architecture: $arch_name" >&2; exit 1 ;;
    esac
    RID="${os_part}-${arch_part}"
fi

if [ -z "$OUTPUT_DIR" ]; then
    OUTPUT_DIR="$REPO_ROOT/artifacts/release"
fi

ARCHIVE_NAME="intochat-${VERSION}-${RID}.tar.gz"
ARCHIVE_PATH="${OUTPUT_DIR}/${ARCHIVE_NAME}"
SUMS_PATH="${OUTPUT_DIR}/SHA256SUMS"
VERSION_OUT="${OUTPUT_DIR}/VERSION"
LATEST_JSON="${OUTPUT_DIR}/latest.json"

step() {
    printf '  [release] %s\n' "$1"
}

compute_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print tolower($1)}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print tolower($1)}'
    else
        openssl dgst -sha256 "$1" | awk '{print tolower($NF)}'
    fi
}

printf 'IntoChat OS Release Bundler v%s (%s)\n' "$VERSION" "$RID"

if [ "$DRY_RUN" -eq 1 ]; then
    step "[DryRun] Would publish IntoChat.OS.Desktop (Native AOT), IntoChat.OS.Kernel (R2R), IntoChat.OS.Sandbox (UnitHost), and Flutter shell into $ARCHIVE_PATH"
    step "[DryRun] Would emit SHA256SUMS, VERSION, and latest.json in $OUTPUT_DIR"
    exit 0
fi

mkdir -p "$OUTPUT_DIR"
STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/intochat-pkg.XXXXXXXXXX")"
cleanup() {
    rm -rf "$STAGE_DIR"
}
trap cleanup EXIT INT TERM

BUNDLE_DIR="$STAGE_DIR/bundle"
OS_DIR="$BUNDLE_DIR/os"
UNITHOST_DIR="$BUNDLE_DIR/unithost"
SHELL_DIR="$BUNDLE_DIR/shell"
mkdir -p "$BUNDLE_DIR" "$OS_DIR" "$UNITHOST_DIR" "$SHELL_DIR"

step "Publishing IntoChat.OS.Desktop (Native AOT, $RID)..."
dotnet publish "$REPO_ROOT/src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.csproj" \
    -c "$CONFIGURATION" -r "$RID" -o "$BUNDLE_DIR" \
    -p:PublishAot=true -p:DebugType=none --nologo

cat > "$BUNDLE_DIR/intochat" <<'EOF'
#!/bin/sh
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$SCRIPT_DIR/IntoChat.OS.Desktop" "$@"
EOF
chmod 755 "$BUNDLE_DIR/IntoChat.OS.Desktop" "$BUNDLE_DIR/intochat"

step "Publishing IntoChat.OS.Kernel (Self-Contained ReadyToRun, $RID)..."
dotnet publish "$REPO_ROOT/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj" \
    -c "$CONFIGURATION" -r "$RID" --self-contained -o "$OS_DIR" \
    -p:PublishReadyToRun=true -p:CodeGraphRefresh=false -p:DebugType=none --nologo
chmod 755 "$OS_DIR/IntoChat.OS.Kernel"

step "Publishing IntoChat.OS.Sandbox / UnitHost ($RID)..."
dotnet publish "$REPO_ROOT/src/IntoChat.OS.Sandbox/IntoChat.OS.Sandbox.csproj" \
    -c "$CONFIGURATION" -r "$RID" --self-contained -o "$UNITHOST_DIR" \
    -p:PublishReadyToRun=true -p:DebugType=none --nologo
chmod 755 "$UNITHOST_DIR/IntoChat.OS.Sandbox"

cat > "$UNITHOST_DIR/IntoChat.OS.UnitHost" <<'EOF'
#!/bin/sh
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$SCRIPT_DIR/IntoChat.OS.Sandbox" --unit-host "$@"
EOF
chmod 755 "$UNITHOST_DIR/IntoChat.OS.UnitHost"

if [ "$SKIP_FLUTTER" -eq 0 ] && [ -f "$REPO_ROOT/ui/pubspec.yaml" ]; then
    if ! command -v flutter >/dev/null 2>&1; then
        echo "Flutter SDK not found on PATH; pass --skip-flutter for headless kernel-only packaging." >&2
        exit 1
    fi
    case "$RID" in
        osx-*)
            step "Building Flutter macOS shell..."
            (cd "$REPO_ROOT/ui" && flutter build macos --release)
            cp -R "$REPO_ROOT/ui/build/macos/Build/Products/Release/"* "$SHELL_DIR/" 2>/dev/null || true
            ;;
        linux-*)
            step "Building Flutter Linux shell..."
            (cd "$REPO_ROOT/ui" && flutter build linux --release)
            cp -R "$REPO_ROOT/ui/build/linux/"*"/release/bundle/"* "$SHELL_DIR/" 2>/dev/null || true
            ;;
    esac
fi

step "Compressing $ARCHIVE_NAME..."
tar -czf "$ARCHIVE_PATH" -C "$BUNDLE_DIR" .

SHA256="$(compute_sha256 "$ARCHIVE_PATH")"
step "SHA-256: $SHA256"

printf '%s\n' "$VERSION" > "$VERSION_OUT"

if [ -f "$SUMS_PATH" ]; then
    awk -v target="$ARCHIVE_NAME" '$2 != target && $2 != ("*" target)' "$SUMS_PATH" > "$STAGE_DIR/SHA256SUMS.tmp" || true
    mv "$STAGE_DIR/SHA256SUMS.tmp" "$SUMS_PATH"
fi
printf '%s  %s\n' "$SHA256" "$ARCHIVE_NAME" >> "$SUMS_PATH"

ASSET_URL="https://releases.${PUBLIC_DOMAIN}/${CHANNEL}/${VERSION}/${ARCHIVE_NAME}"
cat > "$LATEST_JSON" <<EOF
{
  "version": "${VERSION}",
  "channel": "${CHANNEL}",
  "assets": {
    "${RID}": {
      "url": "${ASSET_URL}",
      "sha256": "${SHA256}"
    }
  }
}
EOF

step "Packaged $ARCHIVE_PATH alongside SHA256SUMS and latest.json."
