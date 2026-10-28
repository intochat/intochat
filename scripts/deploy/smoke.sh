#!/usr/bin/env bash
# Smoke tests after a deploy (W5 section 6, steps 1, 3 and 7): os /health, one active revision.
# Usage: scripts/smoke.sh <test|prod>. Needs a logged-in az CLI. Hosts come from the apps' FQDNs.
set -euo pipefail

env_name="${1:?usage: smoke.sh <test|prod>}"
prefix="${PREFIX:-intochat}"
group="rg-${prefix}-${env_name}"
timeout_seconds="${SMOKE_TIMEOUT_SECONDS:-300}"

fqdn() {
  az containerapp show -g "$group" -n "${prefix}-${env_name}-$1" --query properties.configuration.ingress.fqdn -o tsv
}

wait_for_health() {
  local name="$1" host deadline status
  host="$(fqdn "$name")"
  [[ -n "$host" ]] || { echo "smoke: $name has no ingress FQDN" >&2; return 1; }
  deadline=$((SECONDS + timeout_seconds))
  while true; do
    status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "https://${host}/health" || true)"
    if [[ "$status" == "200" ]]; then
      echo "smoke: $name https://${host}/health -> 200"
      return 0
    fi
    if (( SECONDS >= deadline )); then
      echo "smoke: $name https://${host}/health still ${status:-none} after ${timeout_seconds}s" >&2
      return 1
    fi
    sleep 10
  done
}

active_revisions() {
  local name="$1" count
  count="$(az containerapp revision list -g "$group" -n "${prefix}-${env_name}-${name}" --query "length([?properties.active])" -o tsv)"
  if [[ "$count" != "1" ]]; then
    echo "smoke: $name has $count active revisions, expected exactly 1" >&2
    return 1
  fi
  echo "smoke: $name has exactly one active revision"
}

failed=0
wait_for_health os || failed=1
for app in os os-ui; do active_revisions "$app" || failed=1; done
exit "$failed"
