#!/bin/sh
# Packages the self-contained IntoChat OS POSIX release bundle (macOS / Linux) with SHA-256 manifests.
# Produces intochat-<version>-<rid>.tar.gz, VERSION, SHA256SUMS, latest.json, and homebrew/intochat.rb matching eng/install/install.sh.
set -eu

DEFAULT_PUBLIC_DOMAIN="intochat.ai"
RID=""
CONFIGURATION="Release"
CHANNEL="stable"
PUBLIC_DOMAIN="${INTOCHAT_PUBLIC_DOMAIN:-$DEFAULT_PUBLIC_DOMAIN}"
OUTPUT_DIR=""
SKIP_FLUTTER=0
UPDATE_MANIFESTS=0
DRY_RUN=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        --rid)
            [ "$#" -ge 2 ] || { echo "Missing value for --rid" >&2; exit 2; }
            RID="$2"
            shift 2
            ;;
        --configuration)
            [ "$#" -ge 2 ] || { echo "Missing value for --configuration" >&2; exit 2; }
            CONFIGURATION="$2"
            shift 2
            ;;
        --channel)
            [ "$#" -ge 2 ] || { echo "Missing value for --channel" >&2; exit 2; }
            CHANNEL="$2"
            shift 2
            ;;
        --domain)
            [ "$#" -ge 2 ] || { echo "Missing value for --domain" >&2; exit 2; }
            PUBLIC_DOMAIN="$2"
            shift 2
            ;;
        --output)
            [ "$#" -ge 2 ] || { echo "Missing value for --output" >&2; exit 2; }
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --skip-flutter)
            SKIP_FLUTTER=1
            shift
            ;;
        --update-manifests)
            UPDATE_MANIFESTS=1
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

validate_sha256() {
    digest="$1"
    target="$2"
    if ! printf '%s\n' "$digest" | grep -Eq '^[0-9a-f]{64}$'; then
        echo "Computed invalid SHA-256 digest '$digest' for '$target'." >&2
        exit 1
    fi
    if [ -z "$(printf '%s' "$digest" | tr -d '0')" ]; then
        echo "Refusing placeholder all-zero SHA-256 digest for '$target'." >&2
        exit 1
    fi
}

upsert_sum_entry() {
    sum_sha="$1"
    sum_name="$2"
    tmp_dir="$3"
    if [ -f "$SUMS_PATH" ]; then
        awk -v target="$sum_name" '{ sub(/\r$/, ""); if ($2 != target && $2 != ("*" target)) print }' "$SUMS_PATH" > "$tmp_dir/SHA256SUMS.tmp" || true
        mv "$tmp_dir/SHA256SUMS.tmp" "$SUMS_PATH"
    fi
    printf '%s  %s\n' "$sum_sha" "$sum_name" >> "$SUMS_PATH"
}

lookup_sum_entry() {
    target_name="$1"
    if [ ! -f "$SUMS_PATH" ]; then
        return 0
    fi
    awk -v target="$target_name" '{ sub(/\r$/, ""); if ($2 == target || $2 == ("*" target)) { print tolower($1); exit } }' "$SUMS_PATH"
}

render_homebrew_manifest() {
    brew_template="$SCRIPT_DIR/homebrew/intochat.rb"
    if [ ! -f "$brew_template" ]; then
        return 0
    fi
    osx_arm_sha="$(lookup_sum_entry "intochat-${VERSION}-osx-arm64.tar.gz")"
    osx_x64_sha="$(lookup_sum_entry "intochat-${VERSION}-osx-x64.tar.gz")"
    if [ -z "$osx_arm_sha" ] && [ -z "$osx_x64_sha" ]; then
        return 0
    fi

    mkdir -p "${OUTPUT_DIR}/homebrew"
    brew_out="${OUTPUT_DIR}/homebrew/intochat.rb"
    awk \
        -v ver="$VERSION" \
        -v chan="$CHANNEL" \
        -v dom="$PUBLIC_DOMAIN" \
        -v defdom="$DEFAULT_PUBLIC_DOMAIN" \
        -v arm="$osx_arm_sha" \
        -v x64="$osx_x64_sha" '
        {
            gsub(/PENDING_RELEASE_VERSION/, ver)
            gsub(/PENDING_RELEASE_CHANNEL/, chan)
            if (dom != defdom) {
                gsub(defdom, dom)
            }
            if (arm != "") {
                gsub(/PENDING_RELEASE_SHA256_OSX_ARM64/, arm)
            }
            if (x64 != "") {
                gsub(/PENDING_RELEASE_SHA256_OSX_X64/, x64)
            }
            print
        }
    ' "$brew_template" > "$brew_out"
}

write_latest_json() {
    tmp_dir="$1"
    printf '{\n  "version": "%s",\n  "channel": "%s",\n  "assets": {\n' "$VERSION" "$CHANNEL" > "$tmp_dir/latest.json.tmp"
    first=1
    for cand_rid in osx-arm64 osx-x64 linux-x64 linux-arm64; do
        cand_name="intochat-${VERSION}-${cand_rid}.tar.gz"
        cand_sha="$(lookup_sum_entry "$cand_name")"
        if [ -n "$cand_sha" ]; then
            cand_url="https://releases.${PUBLIC_DOMAIN}/${CHANNEL}/${VERSION}/${cand_name}"
            if [ "$first" -eq 0 ]; then
                printf ',\n' >> "$tmp_dir/latest.json.tmp"
            fi
            first=0
            printf '    "%s": {\n      "url": "%s",\n      "sha256": "%s"\n    }' "$cand_rid" "$cand_url" "$cand_sha" >> "$tmp_dir/latest.json.tmp"
        fi
    done
    printf '\n  }\n}\n' >> "$tmp_dir/latest.json.tmp"
    mv "$tmp_dir/latest.json.tmp" "$LATEST_JSON"
}

printf 'IntoChat OS Release Bundler v%s (%s)\n' "$VERSION" "$RID"

if [ "$DRY_RUN" -eq 1 ]; then
    step "[DryRun] Would publish IntoChat.OS.Desktop (Native AOT), IntoChat.OS.Kernel (R2R), IntoChat.OS.Sandbox (UnitHost), and Flutter shell into $ARCHIVE_PATH"
    step "[DryRun] Would emit SHA256SUMS, VERSION, latest.json, and homebrew/intochat.rb in $OUTPUT_DIR (domain: $PUBLIC_DOMAIN)"
    exit 0
fi

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/intochat-pkg.XXXXXXXXXX")"
cleanup() {
    rm -rf "$STAGE_DIR"
}
trap cleanup EXIT INT TERM

if [ "$UPDATE_MANIFESTS" -eq 1 ]; then
    if [ ! -d "$OUTPUT_DIR" ]; then
        echo "Output directory '$OUTPUT_DIR' does not exist." >&2
        exit 1
    fi
    found_any=0
    printf '%s\n' "$VERSION" > "$VERSION_OUT"
    for cand_rid in osx-arm64 osx-x64 linux-x64 linux-arm64; do
        cand_name="intochat-${VERSION}-${cand_rid}.tar.gz"
        cand_path="${OUTPUT_DIR}/${cand_name}"
        if [ -f "$cand_path" ]; then
            cand_sha="$(compute_sha256 "$cand_path")"
            validate_sha256 "$cand_sha" "$cand_path"
            upsert_sum_entry "$cand_sha" "$cand_name" "$STAGE_DIR"
            found_any=1
        fi
    done
    if [ "$found_any" -eq 0 ]; then
        echo "No packaged archives matching 'intochat-${VERSION}-*.tar.gz' found in '$OUTPUT_DIR'." >&2
        exit 1
    fi
    write_latest_json "$STAGE_DIR"
    render_homebrew_manifest
    step "Updated SHA256SUMS, latest.json, and manifests in $OUTPUT_DIR."
    exit 0
fi

mkdir -p "$OUTPUT_DIR"
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
validate_sha256 "$SHA256" "$ARCHIVE_PATH"
step "SHA-256: $SHA256"

printf '%s\n' "$VERSION" > "$VERSION_OUT"
upsert_sum_entry "$SHA256" "$ARCHIVE_NAME" "$STAGE_DIR"
write_latest_json "$STAGE_DIR"
render_homebrew_manifest

step "Packaged $ARCHIVE_PATH alongside SHA256SUMS, latest.json, and manifests."
