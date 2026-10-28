import subprocess
import unittest
from unittest.mock import patch

from deploy_seeding_check import SeedingCheckError, build_query, check_seeding, run_az


REVISION = "intochat-test-os--0000012"
CREATED = "2026-10-22T20:00:00+00:00"
REPLICA = REVISION + "-pod-one"
CONTAINER = "123456781234123412341234567890ab"


def replica(name=REPLICA, container=CONTAINER):
    return {"name": name, "containers": [{"name": "app", "containerId": "containerd://" + container,
                                          "ready": True, "started": True}]}


def summary(name=REPLICA, completions="1", failed="0"):
    return {"Replica": name, "Completions": completions, "FailedCompletions": failed,
            "TableName": "PrimaryResult"}


class AzureFixture:
    def __init__(self, rows):
        self.rows = rows
        self.queries = []
        self.replica_reads = 0
        self.replicas = [replica()]
        self.latest_revision = REVISION

    def __call__(self, args, timeout):
        if args[:2] == ["containerapp", "show"]:
            if args[args.index("--query") + 1] == "properties.latestRevisionName":
                return self.latest_revision
            return "/subscriptions/sub/resourceGroups/group/providers/Microsoft.App/managedEnvironments/env"
        if args[:3] == ["containerapp", "env", "show"]:
            return "00000000-0000-0000-0000-000000000001"
        if args[:3] == ["containerapp", "revision", "list"]:
            return [{"name": REVISION, "createdTime": CREATED}]
        if args[:3] == ["containerapp", "replica", "list"]:
            self.replica_reads += 1
            return self.replicas
        if args[:3] == ["monitor", "log-analytics", "query"]:
            self.queries.append(args[args.index("--analytics-query") + 1])
            return self.rows[min(len(self.queries) - 1, len(self.rows) - 1)]
        raise AssertionError(args)


class Clock:
    def __init__(self):
        self.elapsed = 0

    def now(self):
        return self.elapsed

    def sleep(self, seconds):
        self.elapsed += seconds


def check(fixture, timeout=20):
    clock = Clock()
    return check_seeding("rg-intochat-test", "intochat-test-os", az=fixture,
                         timeout=timeout, interval=10, now=clock.now, sleep=clock.sleep)


class DeploySeedingCheckFacts(unittest.TestCase):
    def test_missing_completion_is_polled_then_fails_closed(self):
        fixture = AzureFixture([[]])
        with self.assertRaisesRegex(SeedingCheckError, "missing.*1"):
            check(fixture)
        self.assertEqual(2, len(fixture.queries))

    def test_terminal_failure_stops_without_waiting_for_healthy_logs(self):
        fixture = AzureFixture([[summary(failed="1")], [summary()]])
        with self.assertRaisesRegex(SeedingCheckError, "failed.*1"):
            check(fixture)
        self.assertEqual(1, len(fixture.queries))

    def test_later_healthy_event_cannot_mask_failed_completion(self):
        fixture = AzureFixture([[summary(completions="2", failed="1")]])
        with self.assertRaisesRegex(SeedingCheckError, "failed.*1"):
            check(fixture)

    def test_success_requires_every_current_replica_and_rechecks_identity(self):
        fixture = AzureFixture([[summary()], [summary(), summary(REPLICA + "-two")]])
        fixture.replicas.append(replica(REPLICA + "-two", "a" * 32))
        self.assertEqual(2, check(fixture))
        self.assertEqual(2, len(fixture.queries))
        self.assertGreaterEqual(fixture.replica_reads, 3)

    def test_old_revision_or_replica_cannot_supply_current_proof(self):
        fixture = AzureFixture([[summary("intochat-test-os--old-pod")]])
        with self.assertRaises(SeedingCheckError):
            check(fixture)

    def test_query_limits_creation_revision_replica_and_current_container(self):
        query = build_query("intochat-test-os", REVISION, CREATED, [(REPLICA, CONTAINER)])
        for expected in ["ContainerAppConsoleLogs_CL", "TimeGenerated >= datetime(", CREATED,
                         "ContainerAppName_s == 'intochat-test-os'", "RevisionName_s == '" + REVISION + "'",
                         REPLICA, CONTAINER, "ContainerId_g", "countif(Failures > 0)"]:
            self.assertIn(expected, query)
        self.assertNotIn("arg_max", query)
        self.assertNotIn("take ", query)
        self.assertNotIn("Log_s", query.split("summarize")[-1])

    def test_query_failure_diagnostic_never_contains_cli_output(self):
        private = "secret-canary-do-not-print"
        error = subprocess.CalledProcessError(1, ["az"], output=private, stderr=private)
        with patch("subprocess.run", side_effect=error):
            with self.assertRaises(SeedingCheckError) as raised:
                run_az(["monitor", "log-analytics", "query"], 10)
        self.assertNotIn(private, str(raised.exception))

    def test_cli_failure_identifies_command_and_safe_category(self):
        private = "secret-canary-do-not-print"
        cases = [
            (subprocess.CalledProcessError(1, [private], output=private,
                                          stderr="AADSTS700024: " + private), "authentication failed (AADSTS700024)"),
            (subprocess.CalledProcessError(1, [private], output=private,
                                          stderr="(InsufficientAccessError) " + private), "access denied"),
            (subprocess.CalledProcessError(2, [private], output=private, stderr=private), "exit code 2"),
            (subprocess.TimeoutExpired([private], 10, output=private, stderr=private), "timed out"),
            (OSError(private), "could not start"),
        ]
        for error, category in cases:
            with self.subTest(category=category), patch("subprocess.run", side_effect=error):
                with self.assertRaises(SeedingCheckError) as raised:
                    run_az(["monitor", "log-analytics", "query", "--workspace", private], 10)
                diagnostic = str(raised.exception)
                self.assertIn("monitor log-analytics query", diagnostic)
                self.assertIn(category, diagnostic)
                self.assertNotIn(private, diagnostic)

    def test_invalid_json_identifies_command_without_response_content(self):
        private = "secret-canary-do-not-print"
        response = subprocess.CompletedProcess(["az"], 0, stdout=private, stderr=private)
        with patch("subprocess.run", return_value=response):
            with self.assertRaises(SeedingCheckError) as raised:
                run_az(["containerapp", "env", "show", "--ids", private], 10)
        self.assertIn("containerapp env show", str(raised.exception))
        self.assertIn("invalid JSON", str(raised.exception))
        self.assertNotIn(private, str(raised.exception))

    def test_unknown_command_diagnostic_does_not_echo_arguments(self):
        private = "secret-canary-do-not-print"
        with patch("subprocess.run", side_effect=OSError(private)):
            with self.assertRaises(SeedingCheckError) as raised:
                run_az([private], 10)
        self.assertNotIn(private, str(raised.exception))

    def test_malformed_counts_fail_closed_without_echoing_data(self):
        for invalid in ["secret-canary", -1, None, True]:
            with self.subTest(invalid=invalid):
                fixture = AzureFixture([[summary(completions=invalid)]])
                with self.assertRaises(SeedingCheckError) as raised:
                    check(fixture)
                self.assertNotIn("secret-canary", str(raised.exception))

    def test_no_current_replicas_is_not_success(self):
        fixture = AzureFixture([[]])
        fixture.replicas = []
        with self.assertRaises(SeedingCheckError):
            check(fixture)

    def test_current_container_change_requires_new_completion(self):
        fixture = AzureFixture([[summary()], []])
        original_call = fixture.__call__

        def changing(args, timeout):
            if args[:3] == ["containerapp", "replica", "list"] and fixture.replica_reads == 1:
                fixture.replicas = [replica(container="b" * 32)]
            return original_call(args, timeout)

        with self.assertRaises(SeedingCheckError):
            check(changing)
        self.assertIn("b" * 32, fixture.queries[-1])

    def test_previous_active_revision_cannot_pass_while_new_revision_is_pending(self):
        fixture = AzureFixture([[summary()]])
        fixture.latest_revision = REVISION + "-new"
        with self.assertRaises(SeedingCheckError):
            check(fixture)

    def test_metadata_calls_cannot_extend_the_polling_deadline(self):
        clock = Clock()
        fixture = AzureFixture([[summary()]])

        def slow(args, timeout):
            clock.elapsed += 10
            return fixture(args, timeout)

        with self.assertRaises(SeedingCheckError):
            check_seeding("rg-test", "app", az=slow, timeout=15, now=clock.now, sleep=clock.sleep)
        self.assertEqual(20, clock.elapsed)
