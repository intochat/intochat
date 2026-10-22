#!/usr/bin/env bash
set -euo pipefail

image="${1:?Usage: check-sandbox-image.sh <local-image>}"
container=""
cleanup() {
  if [[ -n "$container" ]]; then docker rm --force "$container" >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

container="$(docker run --detach --pull never --network none --read-only "$image")"
for ((attempt = 0; attempt < 60; attempt++)); do
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then
    echo "sandbox: process exited before startup completed (exit $(docker inspect --format '{{.State.ExitCode}}' "$container"))." >&2
    exit 1
  fi
  if docker exec "$container" timeout 2 bash -c \
    'exec 3<>/dev/tcp/127.0.0.1/8080; printf "GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3; IFS= read -r response <&3; [[ "$response" == "HTTP/1.1 200 "* ]]' >/dev/null 2>&1; then
    echo "sandbox: startup and contract fingerprint completed; /health returned 200."
    exit 0
  fi
  sleep 1
done
echo "sandbox: /health did not return 200 within the startup checks." >&2
exit 1
