#!/usr/bin/env bash
# One deploy path for both environments, run by .github/workflows/deploy.yml and usable locally for test.
#   scripts/deploy/deploy.sh --env test [--verify-repeatability] [--check]
#   scripts/deploy/deploy.sh --env prod --image-tag vX.Y.Z   (CI only: the tested images are promoted into the prod registry first)
# Inputs: <values dir>/<env>.json from the owner's private values repo (intochat/os-deployment), checked out by the workflow into
# DEPLOYMENT_VALUES_DIR (default: deployment/ in this repo, gitignored). Secrets: only in the environment's Key Vault
# (09-DEPLOY-OPERATOR.md). The os repo holds no value of any environment.
set +x
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
env_name=""
image_tag=""
verify_repeatability=0
check_only=0
while (($#)); do
  case "$1" in
    --env) env_name="${2:?--env needs test or prod}"; shift 2 ;;
    --image-tag) image_tag="${2:?--image-tag needs a value}"; shift 2 ;;
    --verify-repeatability) verify_repeatability=1; shift ;;
    --check) check_only=1; shift ;;
    *) echo 'deploy: unknown argument; use --env, --image-tag, --verify-repeatability, --check.' >&2; exit 2 ;;
  esac
done
[[ "$env_name" == test || "$env_name" == prod ]] || { echo 'deploy: --env must be test or prod.' >&2; exit 2; }
if [[ "$env_name" == prod && "${GITHUB_ACTIONS:-}" != true ]]; then
  echo 'deploy: production is deployed only by the release workflow.' >&2; exit 2
fi
cd "$root"
values_dir="${DEPLOYMENT_VALUES_DIR:-$root/deployment}"
input="$values_dir/$env_name.json"
command -v jq >/dev/null || { echo 'deploy: jq is required.' >&2; exit 2; }
jq -e --arg env "$env_name" '.env == $env' "$input" >/dev/null || { echo "deploy: $input is not the $env_name input." >&2; exit 2; }

field() { jq -r "$1" "$input"; }
prefix="$(field .prefix)"
group="rg-$prefix-$env_name"
registry="acr$prefix$env_name"
server="$registry.azurecr.io"
# This repo is os: the version is the VERSION file, and the images are built from this checkout (test) or promoted (prod).
os_version="$(tr -d '[:space:]' < VERSION)"
[[ "$os_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$ ]] || { echo 'deploy: VERSION must hold an exact version.' >&2; exit 2; }

if [[ "$env_name" == test ]]; then
  [[ -z "$(git status --porcelain)" ]] || { echo 'deploy: commit changes before deploying immutable image tags.' >&2; exit 2; }
  image_tag="${image_tag:-$(git rev-parse --short HEAD)}"
else
  [[ "$image_tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$ ]] || { echo 'deploy: prod needs a versioned release tag as --image-tag.' >&2; exit 2; }
fi

if [[ "$check_only" == 1 ]]; then echo "deploy: $env_name input validated."; exit 0; fi

for tool in dotnet az curl python3; do command -v "$tool" >/dev/null || { echo "deploy: required command missing: $tool" >&2; exit 2; }; done
warm_azure_tokens() {
  local sp_file="${AZURE_CONFIG_DIR:-$HOME/.azure}/service_principal_entries.json" oidc_token
  if [[ -n "${ACTIONS_ID_TOKEN_REQUEST_URL:-}" && -n "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:-}" && -f "$sp_file" ]]; then
    oidc_token="$(curl -sSf -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" "${ACTIONS_ID_TOKEN_REQUEST_URL}&audience=api://AzureADTokenExchange" | jq -r '.value // empty' || true)"
    if [[ -n "$oidc_token" ]]; then
      jq --arg token "$oidc_token" 'map(if has("client_assertion") then .client_assertion = $token else . end)' "$sp_file" > "$sp_file.tmp" && mv "$sp_file.tmp" "$sp_file"
    fi
  fi
  az account get-access-token --resource https://api.loganalytics.io --output none 2>/dev/null || {
    echo 'deploy: Log Analytics authentication preflight failed.' >&2; exit 1;
  }
  az account get-access-token --resource https://management.azure.com --output none 2>/dev/null || true
  az account get-access-token --resource https://management.azure.com/ --output none 2>/dev/null || true
  az account get-access-token --resource https://management.core.windows.net/ --output none 2>/dev/null || true
  az account get-access-token --resource https://vault.azure.net --output none 2>/dev/null || true
}
# Cache Azure audiences while the workflow's OIDC assertion is fresh.
warm_azure_tokens
restore_args=()
if [[ -n "${NUGET_CONFIG:-}" ]]; then restore_args+=("-p:RestoreConfigFile=$NUGET_CONFIG"); fi
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

location="$(field .location)"
az group create -n "$group" -l "$location" -o none
az acr show -n "$registry" -o none >/dev/null 2>&1 || az acr create -n "$registry" -g "$group" --sku Basic --admin-enabled false -l "$location" -o none

image_exists() { az acr repository show -n "$registry" --image "$1:$image_tag" -o none >/dev/null 2>&1; }

kernel=src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj

build_images() {
  command -v docker >/dev/null || { echo 'deploy: required command missing: docker' >&2; exit 2; }
  az acr login -n "$registry" -o none
  # Published tags are reused: rebuilding the same SHA can change image digests and create needless revisions.
  # The kernel image runs the cloud modules under Topology=cloud; registry and repository are given here because the
  # Container.pubxml profile names the GHCR repository.
  if ! image_exists os; then
    dotnet publish "$kernel" -c Release -r linux-x64 -t:PublishContainer \
      -p:EnableSdkContainerSupport=true \
      -p:ContainerRegistry="$server" -p:ContainerRepository=os -p:ContainerImageTags="$image_tag" "${restore_args[@]}"
  fi
  if ! image_exists os-ui; then
    docker build -f ui/shell/Dockerfile -t "$server/os-ui:$image_tag" ui
    docker push "$server/os-ui:$image_tag"
  fi
  if ! image_exists os-sandbox; then
    local sandbox_dockerfile="src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/Dockerfile"
    local sandbox_context="$root"
    if [[ ! -f "$sandbox_context/$sandbox_dockerfile" ]]; then
      sandbox_context="${MODULES_DIR:-$root/../modules}"
    fi
    docker build --secret id=nuget,env=NUGET_GITHUB_TOKEN -f "$sandbox_context/$sandbox_dockerfile" -t "$server/os-sandbox:$image_tag" "$sandbox_context"
    docker push "$server/os-sandbox:$image_tag"
  fi
}

if [[ "$env_name" == test ]]; then
  build_images
else
  for image in os os-ui os-sandbox; do
    image_exists "$image" || { echo "deploy: $image:$image_tag was not promoted into $registry." >&2; exit 1; }
  done
fi

# RID-less build for the install step below (dotnet run --no-build).
dotnet build "$kernel" -c Release "${restore_args[@]}"

warm_azure_tokens

# The installer reads an install config: the private values file with what only Azure and this run know filled in.
# Nothing secret is rendered here; the file lives in the temporary directory and is removed on exit.
render_config() {
  local subscription principal os_url
  subscription="$(az account show --query id -o tsv)"
  # The object id of whoever is signed in (the OIDC service principal in CI), from the token's oid claim.
  principal="$(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv \
    | python3 -c 'import base64,json,sys; p=sys.stdin.read().strip().split(".")[1]; print(json.loads(base64.urlsafe_b64decode(p+"="*(-len(p)%4))).get("oid",""))')"
  [[ -n "$subscription" && -n "$principal" ]] || { echo 'deploy: could not read the signed-in Azure subscription and principal.' >&2; exit 1; }
  os_url="$(field .osUrl)"
  jq --arg sub "$subscription" --arg principal "$principal" --arg group "$group" --arg registry "$registry" --arg server "$server" --arg tag "$image_tag" --arg sandbox "$server/os-sandbox:$image_tag" --arg role "0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0" --arg os_url "$os_url" \
    '{subscription: $sub, location: .location, prefix: .prefix, env: .env, resourceGroup: $group,
      osImage: ($server + "/os:" + $tag), names: {registry: $registry}, registryServer: $server,
      registry: {enabled: true, sku: "Basic"}, installerPrincipalId: $principal,
      modules: ((.modules // {})
        | .auth = ((.auth // {}) + {Posture: (.auth.Posture // "Secured"), PublicBaseUrl: (.auth.PublicBaseUrl // $os_url)})
        | .csharp = ((.csharp // {}) + {sandboxImage: (.csharp.sandboxImage // $sandbox), sessionExecutorRoleId: (.csharp.sessionExecutorRoleId // $role), edgeUrl: (.csharp.edgeUrl // $os_url)}))}
     + (with_entries(select(.key | IN("cpu", "memory", "storageQuotaGb", "postgres"))))' "$input" > "$work/install.json"
}
render_config

installer() { dotnet run -c Release --no-build --project "$kernel" -- install --env "$env_name" --config "$work/install.json" "$@"; }
update_ui() {
  warm_azure_tokens
  local ui_app="$prefix-$env_name-os-ui"
  local desired="$server/os-ui:$image_tag"
  local current latest rev
  current="$(az containerapp show -g "$group" -n "$ui_app" --query 'properties.template.containers[0].image' -o tsv 2>/dev/null || true)"
  if [[ -n "$current" && "$current" != "$desired" ]]; then
    az containerapp update -g "$group" -n "$ui_app" --image "$desired" -o none
    latest="$(az containerapp show -g "$group" -n "$ui_app" --query 'properties.latestRevisionName' -o tsv)"
    while IFS= read -r rev; do
      [[ -n "$rev" && "$rev" != "$latest" ]] && az containerapp revision deactivate -g "$group" -n "$ui_app" --revision "$rev" -o none || true
    done < <(az containerapp revision list -g "$group" -n "$ui_app" --query '[?properties.active].name' -o tsv)
  fi
}
install() { warm_azure_tokens && installer && update_ui; }
snapshot_revisions() {
  warm_azure_tokens
  local app
  for app in os os-ui; do
    printf '%s\n' "$app"
    # --all: the first install deactivates the previous revision asynchronously, so active-only snapshots race.
    az containerapp revision list --all -g "$group" -n "$prefix-$env_name-$app" --query '[].name' -o tsv | sort
  done
}
install
if [[ "$verify_repeatability" == 1 ]]; then
  snapshot_revisions > "$work/before"
  install
  snapshot_revisions > "$work/after"
  cmp -s "$work/before" "$work/after" || { echo 'deploy: repeat install created or removed a revision.' >&2; exit 1; }
  echo 'deploy: repeat install preserved every revision.'
fi
check_environment() {
  local app="$1" names required
  shift
  names="$(az containerapp show -g "$group" -n "$prefix-$env_name-$app" --query 'properties.template.containers[0].env[].name' -o tsv)"
  for required in "$@"; do
    case $'\n'"$names"$'\n' in
      *$'\n'"$required"$'\n'*) ;;
      *) echo "deploy: $app is missing runtime setting $required." >&2; return 1 ;;
    esac
  done
}
# D-11: the required names are what the installer renders (install --print-env), so the list cannot drift from it.
mapfile -t required_names < <(installer --print-env)
((${#required_names[@]} > 0)) || { echo 'deploy: install --print-env listed no names.' >&2; exit 1; }
check_environment os "${required_names[@]}"

os_url="$(field .osUrl)"
for url in "${os_url%/}/health" "${os_url%/}/"; do
  status=none
  for ((attempt=1; attempt<=${SMOKE_ATTEMPTS:-30}; attempt++)); do
    status="$(curl -sS -o /dev/null -w '%{http_code}' --max-time 10 "$url" || true)"
    [[ "$status" == 200 ]] && break
    if ((attempt < ${SMOKE_ATTEMPTS:-30})); then sleep 10; fi
  done
  [[ "$status" == 200 ]] || { echo "deploy: $url returned $status; expected 200." >&2; exit 1; }
  echo "deploy: $url -> 200"
done
for app in os os-ui; do
  count="$(az containerapp revision list -g "$group" -n "$prefix-$env_name-$app" --query 'length([?properties.active])' -o tsv)"
  [[ "$count" == 1 ]] || { echo "deploy: $app must have exactly one active revision." >&2; exit 1; }
done
warm_azure_tokens
python3 scripts/deploy/deploy_seeding_check.py --resource-group "$group" --app "$prefix-$env_name-os"
echo "deploy: completed $env_name image tag $image_tag"
