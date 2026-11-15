#!/bin/sh
# IntoChat OS one-command POSIX installer for macOS and Linux (https://intochat.ai/install.sh).
# Installs a self-contained IntoChat OS release under ~/.intochat without root, Docker, or a pre-installed .NET SDK.
set -eu

DEFAULT_PUBLIC_DOMAIN="intochat.ai"
CHANNEL="${INTOCHAT_CHANNEL:-stable}"
VERSION="${INTOCHAT_VERSION:-}"
PUBLIC_DOMAIN="${INTOCHAT_PUBLIC_DOMAIN:-$DEFAULT_PUBLIC_DOMAIN}"
HOME_DIR="${IntoChatOS__Home:-$HOME/.intochat}"
LOCAL_ARCHIVE="${INTOCHAT_ARCHIVE_PATH:-}"
EXPECTED_SHA256="${INTOCHAT_ARCHIVE_SHA256:-}"
NO_LAUNCH=0
NO_PATH="${INTOCHAT_NO_PATH:-0}"
DRY_RUN=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        --channel)
            [ "$#" -ge 2 ] || { echo "Missing value for --channel" >&2; exit 2; }
            CHANNEL="$2"
            shift 2
            ;;
        --version)
            [ "$#" -ge 2 ] || { echo "Missing value for --version" >&2; exit 2; }
            VERSION="$2"
            shift 2
            ;;
        --home)
            [ "$#" -ge 2 ] || { echo "Missing value for --home" >&2; exit 2; }
            HOME_DIR="$2"
            shift 2
            ;;
        --no-launch)
            NO_LAUNCH=1
            shift
            ;;
        --no-path)
            NO_PATH=1
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

step() {
    printf '  -> %s\n' "$1"
}

resolve_rid() {
    os_name="$(uname -s)"
    arch_name="$(uname -m)"

    case "$os_name" in
        Darwin)
            os_part="osx"
            ;;
        Linux)
            os_part="linux"
            ;;
        *)
            echo "Unsupported operating system: $os_name" >&2
            exit 1
            ;;
    esac

    case "$arch_name" in
        x86_64|amd64)
            arch_part="x64"
            ;;
        arm64|aarch64)
            arch_part="arm64"
            ;;
        *)
            echo "Unsupported CPU architecture: $arch_name" >&2
            exit 1
            ;;
    esac

    printf '%s-%s' "$os_part" "$arch_part"
}

normalize_sha256() {
    printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -d ' \t\r\n'
}

require_valid_sha256() {
    candidate="$1"
    source_desc="$2"

    if [ -z "$candidate" ]; then
        echo "Missing required SHA-256 digest ($source_desc). Installation aborted." >&2
        exit 1
    fi
    if ! printf '%s\n' "$candidate" | grep -Eq '^[0-9a-f]{64}$'; then
        echo "Invalid SHA-256 digest '$candidate' ($source_desc). Expected 64 hexadecimal characters." >&2
        exit 1
    fi
    if [ -z "$(printf '%s' "$candidate" | tr -d '0')" ]; then
        echo "Refusing placeholder all-zero SHA-256 digest ($source_desc). Installation aborted." >&2
        exit 1
    fi
}

compute_sha256() {
    target_file="$1"
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$target_file" | awk '{print tolower($1)}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$target_file" | awk '{print tolower($1)}'
    elif command -v openssl >/dev/null 2>&1; then
        openssl dgst -sha256 "$target_file" | awk '{print tolower($NF)}'
    else
        echo "No SHA-256 verification tool found (sha256sum, shasum, or openssl required)." >&2
        exit 1
    fi
}

download_file() {
    url="$1"
    dest="$2"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --proto '=https' --tlsv1.2 "$url" -o "$dest"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$dest" "$url"
    else
        echo "Neither curl nor wget is installed." >&2
        exit 1
    fi
}

ensure_path_profile() {
    bin_path="$1"
    export_line="export PATH=\"$bin_path:\$PATH\""

    for rc in "$HOME/.zshrc" "$HOME/.bashrc" "$HOME/.profile"; do
        if [ -f "$rc" ]; then
            if ! grep -Fq "$bin_path" "$rc"; then
                printf '\n# IntoChat OS\n%s\n' "$export_line" >> "$rc"
            fi
        fi
    done

    if [ ! -f "$HOME/.zshrc" ] && [ ! -f "$HOME/.bashrc" ] && [ ! -f "$HOME/.profile" ]; then
        printf '# IntoChat OS\n%s\n' "$export_line" >> "$HOME/.profile"
    fi
}

RID="$(resolve_rid)"
BIN_DIR="$HOME_DIR/bin"
SECRETS_DIR="$HOME_DIR/secrets"
RUN_DIR="$HOME_DIR/run"
LOGS_DIR="$HOME_DIR/logs"
STATE_DIR="$HOME_DIR/state"

printf '\nIntoChat OS Installer (%s, channel: %s)\n' "$RID" "$CHANNEL"
step "Home directory: $HOME_DIR"

if [ "$(uname -s)" = "Linux" ] && ! command -v bwrap >/dev/null 2>&1; then
    step "Note: 'bwrap' (bubblewrap) was not found on PATH; install bubblewrap for full namespace sandbox isolation."
fi

if [ "$DRY_RUN" -eq 1 ]; then
    preview_ver="${VERSION:-latest}"
    step "[DryRun] Would download https://releases.$PUBLIC_DOMAIN/$CHANNEL/$preview_ver/intochat-$preview_ver-$RID.tar.gz, verify SHA-256, and stage into $BIN_DIR."
    exit 0
fi

STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/intochat-stage.XXXXXXXXXX")"
cleanup() {
    rm -rf "$STAGING_DIR"
}
trap cleanup EXIT INT TERM

ARCHIVE_PATH=""
SHA_TO_VERIFY="$(normalize_sha256 "$EXPECTED_SHA256")"

if [ -n "$LOCAL_ARCHIVE" ]; then
    if [ ! -f "$LOCAL_ARCHIVE" ]; then
        echo "Local archive '$LOCAL_ARCHIVE' does not exist." >&2
        exit 1
    fi
    require_valid_sha256 "$SHA_TO_VERIFY" "INTOCHAT_ARCHIVE_SHA256"
    ARCHIVE_PATH="$LOCAL_ARCHIVE"
    step "Using local release archive: $ARCHIVE_PATH"
else
    BASE_URL="https://releases.$PUBLIC_DOMAIN/$CHANNEL"
    if [ -z "$VERSION" ]; then
        VERSION_FILE="$STAGING_DIR/VERSION"
        download_file "$BASE_URL/VERSION" "$VERSION_FILE"
        VERSION="$(tr -d ' \t\r\n' < "$VERSION_FILE")"
        if [ -z "$VERSION" ]; then
            echo "Release VERSION file at '$BASE_URL/VERSION' was empty." >&2
            exit 1
        fi
    fi

    ASSET_NAME="intochat-$VERSION-$RID.tar.gz"
    ASSET_URL="$BASE_URL/$VERSION/$ASSET_NAME"
    ARCHIVE_PATH="$STAGING_DIR/$ASSET_NAME"

    if [ -z "$SHA_TO_VERIFY" ]; then
        SUMS_FILE="$STAGING_DIR/SHA256SUMS"
        download_file "$BASE_URL/$VERSION/SHA256SUMS" "$SUMS_FILE"
        SHA_TO_VERIFY="$(awk -v target="$ASSET_NAME" '{ sub(/\r$/, ""); if ($2 == target || $2 == ("*" target)) { print tolower($1); exit } }' "$SUMS_FILE")"
        SHA_TO_VERIFY="$(normalize_sha256 "$SHA_TO_VERIFY")"
        require_valid_sha256 "$SHA_TO_VERIFY" "SHA256SUMS"
    else
        require_valid_sha256 "$SHA_TO_VERIFY" "INTOCHAT_ARCHIVE_SHA256"
    fi

    step "Downloading $ASSET_URL"
    download_file "$ASSET_URL" "$ARCHIVE_PATH"
fi

SHA_TO_VERIFY="$(normalize_sha256 "$SHA_TO_VERIFY")"
require_valid_sha256 "$SHA_TO_VERIFY" "archive verification"

step "Verifying SHA-256 digest"
ACTUAL_SHA="$(normalize_sha256 "$(compute_sha256 "$ARCHIVE_PATH")")"
require_valid_sha256 "$ACTUAL_SHA" "computed archive digest"
if [ "$ACTUAL_SHA" != "$SHA_TO_VERIFY" ]; then
    echo "SHA-256 mismatch for '$ARCHIVE_PATH': expected '$SHA_TO_VERIFY', computed '$ACTUAL_SHA'." >&2
    exit 1
fi

mkdir -p "$HOME_DIR" "$BIN_DIR" "$SECRETS_DIR" "$RUN_DIR" "$LOGS_DIR" "$STATE_DIR"
chmod 700 "$SECRETS_DIR" "$RUN_DIR"

EXTRACT_DIR="$STAGING_DIR/extracted"
mkdir -p "$EXTRACT_DIR"
tar -xzf "$ARCHIVE_PATH" -C "$EXTRACT_DIR"

if [ -x "$BIN_DIR/IntoChat.OS.Desktop" ]; then
    step "Stopping running IntoChat OS supervisor prior to upgrade"
    "$BIN_DIR/IntoChat.OS.Desktop" --quit >/dev/null 2>&1 || true
fi

rm -rf "$BIN_DIR.prev"
if [ -d "$BIN_DIR" ]; then
    mv "$BIN_DIR" "$BIN_DIR.prev"
fi
mv "$EXTRACT_DIR" "$BIN_DIR"
rm -rf "$BIN_DIR.prev"

if [ ! -f "$BIN_DIR/intochat" ] && [ -f "$BIN_DIR/IntoChat.OS.Desktop" ]; then
    cat > "$BIN_DIR/intochat" <<'EOF'
#!/bin/sh
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$SCRIPT_DIR/IntoChat.OS.Desktop" "$@"
EOF
    chmod 755 "$BIN_DIR/intochat"
fi

if [ "$(uname -s)" = "Darwin" ] && command -v xattr >/dev/null 2>&1; then
    xattr -dr com.apple.quarantine "$BIN_DIR" >/dev/null 2>&1 || true
fi

if [ "$NO_PATH" -eq 0 ]; then
    ensure_path_profile "$BIN_DIR"
    export PATH="$BIN_DIR:$PATH"
    step "Installed IntoChat OS into $BIN_DIR."
else
    step "Installed IntoChat OS into $BIN_DIR (PATH registration skipped)."
fi

if [ "$NO_LAUNCH" -eq 0 ] && [ "${SPIKE_WINDOWS:-1}" != "0" ] && [ -x "$BIN_DIR/intochat" ]; then
    if [ "$(uname -s)" = "Darwin" ] || [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; then
        step "Starting IntoChat OS..."
        "$BIN_DIR/intochat" &
    else
        step "Headless session detected; starting IntoChat OS kernel in web mode..."
        "$BIN_DIR/intochat" --kernel-only
    fi
else
    step "Run 'intochat' to start IntoChat OS."
fi
