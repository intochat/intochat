"""Fail closed unless GitHub verified the exact master commit being deployed."""
import json
import os
import re
import subprocess
import sys
import time


def require_success(runs, sha, event):
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("Deployment requires a full commit SHA.")
    allowed = {"push"} if event == "push" else {"push", "workflow_dispatch"}
    matching = [run for run in runs if run.get("headSha") == sha
                and run.get("headBranch") == "master" and run.get("event") in allowed]
    # gh returns newest first. A failed rerun must not be hidden by an older success.
    if not matching or matching[0].get("status") != "completed" or matching[0].get("conclusion") != "success":
        raise ValueError("No successful latest master workflow run for the exact deployment commit.")
    return matching[0]


if __name__ == "__main__":
    workflow, sha, event = sys.argv[1:4]
    deadline = time.monotonic() + int(os.environ.get("GATE_WAIT_SECONDS", "0"))
    while True:
        result = subprocess.run(["gh", "run", "list", "--repo", os.environ["GITHUB_REPOSITORY"],
                                 "--workflow", workflow, "--commit", sha, "--branch", "master", "--limit", "30",
                                 "--json", "databaseId,headSha,headBranch,event,status,conclusion"], check=True, capture_output=True, text=True)
        runs = json.loads(result.stdout)
        try:
            verified = require_success(runs, sha, event)
            if os.environ.get("GITHUB_OUTPUT"):
                with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                    output.write(f"run_id={verified['databaseId']}\n")
            print(f"Verified {workflow} success for {sha}.")
            break
        except ValueError as error:
            if time.monotonic() >= deadline or (runs and runs[0].get("status") == "completed"):
                sys.exit(str(error))
            time.sleep(15)
