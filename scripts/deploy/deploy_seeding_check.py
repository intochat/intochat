"""Require complete integration seeding evidence for the deployed OS replicas."""
import argparse
from datetime import datetime
import json
import re
import shutil
import subprocess
import sys
import time
import uuid


class SeedingCheckError(Exception):
    pass


def run_az(arguments, timeout):
    command = "Azure CLI command"
    for prefix in [("containerapp", "show"), ("containerapp", "env", "show"),
                   ("containerapp", "revision", "list"), ("containerapp", "replica", "list"),
                   ("monitor", "log-analytics", "query")]:
        if tuple(arguments[:len(prefix)]) == prefix:
            command = "az " + " ".join(prefix)
            break
    try:
        result = subprocess.run([shutil.which("az") or "az", *arguments, "--output", "json", "--only-show-errors"],
                                check=True, capture_output=True, text=True, timeout=timeout)
        return json.loads(result.stdout)
    except subprocess.TimeoutExpired:
        reason = "timed out"
    except subprocess.CalledProcessError as error:
        reason = f"failed with exit code {error.returncode}"
        authentication = re.search(r"\bAADSTS[0-9]{5,8}\b", str(error.stderr or ""))
        if authentication:
            reason = f"authentication failed ({authentication.group(0)})"
        elif re.search(r"\b(AuthorizationFailed|InsufficientAccessError|Forbidden)\b", str(error.stderr or "")):
            reason = "access denied"
    except OSError:
        reason = "could not start"
    except ValueError:
        reason = "returned invalid JSON"
    except subprocess.SubprocessError:
        reason = "process failed"
    # Emit only fixed command names and failure categories, never CLI output or arguments.
    raise SeedingCheckError(f"{command} {reason}; seeding proof unavailable.") from None


def require_name(value):
    if not isinstance(value, str) or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_.-]*", value):
        raise SeedingCheckError("Invalid deployment resource metadata.")
    return value


def require_count(value):
    if isinstance(value, bool) or not re.fullmatch(r"[0-9]+", str(value)):
        raise SeedingCheckError("Invalid seeding count response.")
    return int(value)


def build_query(app, revision, created, replicas):
    require_name(app)
    require_name(revision)
    try:
        timestamp = datetime.fromisoformat(created.replace("Z", "+00:00"))
        if timestamp.tzinfo is None:
            raise ValueError()
    except (AttributeError, TypeError, ValueError):
        raise SeedingCheckError("Missing revision creation timestamp.") from None
    filters = []
    for replica, container in replicas:
        require_name(replica)
        if not re.fullmatch(r"[0-9a-f]{32}|[0-9a-f]{64}", container):
            raise SeedingCheckError("Invalid current container identity.")
        filters.append(f"(Replica == '{replica}' and ContainerId == '{container}')")
    if not filters:
        raise SeedingCheckError("No current replicas to verify.")
    return " ".join([
        "ContainerAppConsoleLogs_CL",
        f"| where TimeGenerated >= datetime({created})",
        f"| where ContainerAppName_s == '{app}' and RevisionName_s == '{revision}'",
        "| where ContainerName_s == 'app'",
        "| extend Replica = ContainerGroupName_s, ContainerId = replace_string(tolower(tostring(ContainerId_g)), '-', '')",
        "| where " + " or ".join(filters),
        r"| extend Failures = tolong(extract(@'Integration seeding completed with ([0-9]+) failures\.', 1, Log_s))",
        "| summarize Completions = countif(isnotnull(Failures)), FailedCompletions = countif(Failures > 0) by Replica",
    ])


def current_replicas(query, group, app):
    revisions = query(["containerapp", "revision", "list", "--name", app, "--resource-group", group,
                       "--all", "--query", "[?properties.active].{name:name,createdTime:properties.createdTime}"])
    if not isinstance(revisions, list) or len(revisions) != 1 or not isinstance(revisions[0], dict):
        raise SeedingCheckError("Expected exactly one active OS revision.")
    revision = require_name(revisions[0].get("name"))
    created = revisions[0].get("createdTime")
    latest = query(["containerapp", "show", "--name", app, "--resource-group", group,
                    "--query", "properties.latestRevisionName"])
    if latest != revision:
        raise SeedingCheckError("The latest OS revision is not the active revision.")
    rows = query(["containerapp", "replica", "list", "--name", app, "--resource-group", group,
                  "--revision", revision, "--query", "[].{name:name,containers:properties.containers[].{name:name,containerId:containerId,ready:ready,started:started}}"])
    if not isinstance(rows, list) or not rows:
        raise SeedingCheckError("No current replicas to verify.")
    replicas = []
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get("containers"), list):
            raise SeedingCheckError("Invalid current replica metadata.")
        name = require_name(row.get("name"))
        containers = [container for container in row["containers"] if isinstance(container, dict) and container.get("name") == "app"]
        if len(containers) != 1 or containers[0].get("ready") is not True or containers[0].get("started") is not True:
            raise SeedingCheckError("Current OS container is not ready.")
        identity = containers[0].get("containerId")
        if not isinstance(identity, str) or "://" not in identity:
            raise SeedingCheckError("Missing current container identity.")
        container_id = identity.split("://", 1)[1].replace("-", "").lower()
        replicas.append((name, container_id))
    if len({name for name, _ in replicas}) != len(replicas):
        raise SeedingCheckError("Duplicate current replica identity.")
    return revision, created, tuple(sorted(replicas))


def missing_completions(rows, replicas):
    expected = {name for name, _ in replicas}
    if not isinstance(rows, list):
        raise SeedingCheckError("Invalid seeding count response.")
    completed = set()
    seen = set()
    failed = 0
    for row in rows:
        if not isinstance(row, dict) or row.get("Replica") not in expected or row["Replica"] in seen:
            raise SeedingCheckError("Seeding counts did not match current replicas.")
        seen.add(row["Replica"])
        count = require_count(row.get("Completions"))
        failures = require_count(row.get("FailedCompletions"))
        if failures > count:
            raise SeedingCheckError("Invalid seeding count response.")
        failed += failures
        if count:
            completed.add(row["Replica"])
    if failed:
        raise SeedingCheckError(f"Integration seeding failed: {failed} completion(s) reported exhausted integrations.")
    return len(expected - completed)


def check_seeding(group, app, *, az=run_az, timeout=300, interval=10, now=time.monotonic, sleep=time.sleep):
    require_name(group)
    require_name(app)
    if timeout <= 0 or interval <= 0:
        raise SeedingCheckError("Seeding wait and polling interval must be positive.")
    deadline = now() + timeout

    def query(arguments):
        remaining = deadline - now()
        if remaining <= 0:
            raise SeedingCheckError("Integration seeding verification timed out; proof unavailable.")
        return az(arguments, min(60, remaining))

    environment = query(["containerapp", "show", "--name", app, "--resource-group", group,
                         "--query", "properties.managedEnvironmentId"])
    if not isinstance(environment, str) or not environment.startswith("/subscriptions/"):
        raise SeedingCheckError("OS environment is unavailable.")
    workspace = query(["containerapp", "env", "show", "--ids", environment,
                       "--query", "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId"])
    try:
        workspace = str(uuid.UUID(workspace))
    except (ValueError, TypeError, AttributeError):
        raise SeedingCheckError("OS environment has no Log Analytics workspace.") from None
    while True:
        snapshot = current_replicas(query, group, app)
        revision, created, replicas = snapshot
        rows = query(["monitor", "log-analytics", "query", "--workspace", workspace,
                      "--analytics-query", build_query(app, revision, created, replicas)])
        missing = missing_completions(rows, replicas)
        if not missing and current_replicas(query, group, app) == snapshot:
            return len(replicas)
        if now() + interval >= deadline:
            raise SeedingCheckError(f"Integration seeding proof missing for {missing} current replica(s), or replicas changed during verification.")
        sleep(interval)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resource-group", required=True)
    parser.add_argument("--app", required=True)
    parser.add_argument("--timeout-seconds", type=int, default=300)
    args = parser.parse_args()
    try:
        count = check_seeding(args.resource_group, args.app, timeout=args.timeout_seconds)
    except SeedingCheckError as error:
        print(f"deploy: {error}", file=sys.stderr)
        return 1
    print(f"deploy: integration seeding verified for {count} current OS replica(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
