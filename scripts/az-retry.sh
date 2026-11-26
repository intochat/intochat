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
    _deactivated_revs=""
    _active_revs=$(az_retry az containerapp revision list -n "$app" -g "$group" --query "[?properties.active].name" -o tsv) || {
      echo "Failed to list active revisions for $app in $group." >&2
      return 1 2>/dev/null || exit 1
    }
    for revision in $_active_revs; do
      if [ -n "$revision" ]; then
        az_retry az containerapp revision deactivate -n "$app" -g "$group" --revision "$revision" >/dev/null || {
          echo "Failed to deactivate revision $revision of $app in $group." >&2
          return 1 2>/dev/null || exit 1
        }
        _deactivated_revs="${_deactivated_revs}${_deactivated_revs:+ }$revision"
      fi
    done
    i=0
    while :; do
      _nonzero_revs=$(az_retry az containerapp revision list --all -n "$app" -g "$group" --query '[?properties.replicas > `0`].name' -o tsv) || {
        echo "Failed to list revisions for $app in $group while waiting for replicas to drain." >&2
        return 1 2>/dev/null || exit 1
      }
      _nonzero_list=""
      for _rev in $_nonzero_revs; do
        if [ -n "$_rev" ]; then
          _nonzero_list="${_nonzero_list}${_nonzero_list:+ }$_rev"
        fi
      done
      _check_revs=""
      for _rev in $_deactivated_revs $_nonzero_list; do
        if [ -n "$_rev" ]; then
          case " $_check_revs " in
            *" $_rev "*) ;;
            *) _check_revs="${_check_revs}${_check_revs:+ }$_rev" ;;
          esac
        fi
      done
      _live_replicas=""
      for _rev in $_check_revs; do
        _rev_replicas=$(az_retry az containerapp replica list -n "$app" -g "$group" --revision "$_rev" --query '[].name' -o tsv) || {
          echo "Failed to list replicas for revision $_rev of $app in $group while waiting for replicas to drain." >&2
          return 1 2>/dev/null || exit 1
        }
        for _rep in $_rev_replicas; do
          if [ -n "$_rep" ]; then
            _live_replicas="${_live_replicas}${_live_replicas:+ }${_rev}/${_rep}"
          fi
        done
      done
      if [ -z "$_nonzero_list" ] && [ -z "$_live_replicas" ]; then
        break
      fi
      if [ "$i" -ge "$waits" ]; then
        echo "Timed out waiting for replicas of $app in $group to stop (revisions with replicas > 0: ${_nonzero_list:-none}; live replicas: ${_live_replicas:-none})." >&2
        return 1 2>/dev/null || exit 1
      fi
      sleep 5; i=$((i+1))
    done
    # Allow a 10s post-drain buffer so Orleans finishes writing Dead to cluster membership before the new revision starts.
    sleep 10
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
        az_retry az containerapp ingress traffic set -n "$app" -g "$group" --revision-weight "$ready=100" >/dev/null
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
