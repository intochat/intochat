#!/bin/sh
# Reusable POSIX /bin/sh retry and Container App revision-handover helper for Azure CLI calls.
# - Retries transient ARM errors (including 503 Service Unavailable HTML responses) up to 5 times with linear backoff.
# - Treats RevisionAlreadyInRequestedState as idempotent success.
# - Fails fast on ResourceNotFound / ResourceGroupNotFound so existence checks do not stall.
# - Guards empty revision names before deactivate/activate calls.
#
# Usage:
#   . scripts/az-retry.sh                                # source functions into a shell
#   sh scripts/az-retry.sh az <args...>                  # run a single Azure CLI command with retries
#   sh scripts/az-retry.sh stop-revision <app> <group> [waits]
#   sh scripts/az-retry.sh start-revision <app> <group>

# BEGIN az_retry
az_retry() {
  _az_attempt=1
  while [ "$_az_attempt" -le 5 ]; do
    _az_err_file=$(mktemp)
    _az_rc=0
    _az_out=$("$@" 2>"$_az_err_file") || _az_rc=$?
    if [ "$_az_rc" -eq 0 ]; then
      rm -f "$_az_err_file"
      printf '%s\n' "$_az_out"
      return 0
    fi
    _az_err=$(cat "$_az_err_file")
    rm -f "$_az_err_file"
    case "$_az_err" in
      *RevisionAlreadyInRequestedState*) return 0 ;;
      *ResourceNotFound*|*ResourceGroupNotFound*) printf '%s\n' "$_az_err" >&2; return "$_az_rc" ;;
    esac
    if [ "$_az_attempt" -eq 5 ]; then
      printf '%s\n' "$_az_err" >&2
      return "$_az_rc"
    fi
    sleep $((_az_attempt * 2))
    _az_attempt=$((_az_attempt + 1))
  done
  return 1
}
# END az_retry

az_stop_revisions() {
  app="$1"
  group="$2"
  waits="${3:-24}"
  # BEGIN stop_revisions_body
  if az_retry az containerapp show -n "$app" -g "$group" --query name -o tsv >/dev/null 2>&1; then
    for revision in $(az_retry az containerapp revision list -n "$app" -g "$group" --query "[?properties.active].name" -o tsv); do
      if [ -n "$revision" ]; then
        az_retry az containerapp revision deactivate -n "$app" -g "$group" --revision "$revision" >/dev/null
      fi
    done
    i=0
    while [ $i -lt "$waits" ] && [ -n "$(az_retry az containerapp revision list -n "$app" -g "$group" --query '[?properties.replicas > `0`].name' -o tsv)" ]; do
      sleep 5; i=$((i+1))
    done
  fi
  # END stop_revisions_body
}

az_start_revision() {
  app="$1"
  group="$2"
  # BEGIN start_revision_body
  if az_retry az containerapp show -n "$app" -g "$group" --query name -o tsv >/dev/null 2>&1; then
    latest=$(az_retry az containerapp show -n "$app" -g "$group" --query properties.latestRevisionName -o tsv)
    if [ -z "$latest" ]; then
      echo "Failed to resolve latestRevisionName for $app in $group." >&2
      exit 1
    fi
    if [ "$(az_retry az containerapp revision show -n "$app" -g "$group" --revision "$latest" --query properties.active -o tsv)" != "true" ]; then
      az_retry az containerapp revision activate -n "$app" -g "$group" --revision "$latest" >/dev/null
    fi
    i=0
    while [ $i -lt 120 ] && [ "$(az_retry az containerapp show -n "$app" -g "$group" --query properties.latestReadyRevisionName -o tsv)" != "$latest" ]; do
      sleep 5; i=$((i+1))
    done
    ready=$(az_retry az containerapp show -n "$app" -g "$group" --query properties.latestReadyRevisionName -o tsv)
    if [ "$ready" != "$latest" ]; then
      if [ -n "$ready" ]; then
        az_retry az containerapp revision deactivate -n "$app" -g "$group" --revision "$latest" >/dev/null
        az_retry az containerapp revision activate -n "$app" -g "$group" --revision "$ready" >/dev/null
      fi
      echo "Revision $latest of $app did not become ready; $ready runs again." >&2
      exit 1
    fi
  fi
  # END start_revision_body
}

case "${0##*/}" in
  az-retry.sh)
    case "${1:-}" in
      stop-revision) shift; az_stop_revisions "$@" ;;
      start-revision) shift; az_start_revision "$@" ;;
      "") ;;
      *) az_retry "$@" ;;
    esac
    ;;
esac
