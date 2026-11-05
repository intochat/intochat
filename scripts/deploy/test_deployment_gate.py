from pathlib import Path
import unittest
from deployment_gate import require_success


class DeploymentGateFacts(unittest.TestCase):
    def test_only_successful_master_push_of_exact_commit_is_accepted(self):
        sha = "a" * 40
        run = dict(headSha=sha, headBranch="master", event="push", status="completed", conclusion="success")
        require_success([run], sha, "push")
        for field, wrong in [("headSha", "b" * 40), ("headBranch", "feature"),
                             ("event", "pull_request"), ("status", "in_progress"), ("conclusion", "failure")]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                require_success([{**run, field: wrong}], sha, "push")

    def test_missing_or_malformed_commit_fails_closed(self):
        for sha in ["master", "abc1234", "a" * 40]:
            with self.subTest(sha=sha), self.assertRaises(ValueError):
                require_success([], sha, "push")

    def test_deployment_accepts_only_successful_test_run_for_same_commit(self):
        sha = "a" * 40
        run = dict(headSha=sha, headBranch="master", event="workflow_dispatch", status="completed", conclusion="success")
        require_success([run], sha, "deployment")
        with self.assertRaises(ValueError):
            require_success([{**run, "headSha": "b" * 40}], sha, "deployment")


ROOT = Path(__file__).resolve().parents[2]


class DeployWorkflowFacts(unittest.TestCase):
    """D-16, 09-10: the deploy workflow is started only by a published release and starts nothing until the owner enables it."""

    @classmethod
    def setUpClass(cls):
        import yaml
        cls.workflow = yaml.safe_load((ROOT / ".github" / "workflows" / "deploy.yml").read_text(encoding="utf-8"))

    def test_the_only_trigger_is_a_published_release(self):
        # Owner rule (2026-11-05): a deployment starts only by publishing a release; a push deploys nothing.
        triggers = self.workflow.get("on", self.workflow.get(True))
        self.assertEqual({"release"}, set(triggers))
        self.assertEqual({"types": ["published"]}, triggers["release"])

    def test_prod_runs_after_test_in_the_same_run_and_promotes_its_artifact(self):
        jobs = self.workflow["jobs"]
        self.assertEqual(["test"], jobs["prod"]["needs"])
        uses = [step.get("uses", "") for step in jobs["prod"]["steps"]]
        self.assertTrue(any(use.startswith("actions/download-artifact@") for use in uses))
        self.assertFalse(any("deployment_gate.py deploy.yml" in step.get("run", "") for step in jobs["prod"]["steps"]))

    def test_no_manual_trigger_and_no_dispatch_clause_anywhere(self):
        self.assertNotIn("workflow_dispatch", (ROOT / ".github" / "workflows" / "deploy.yml").read_text(encoding="utf-8"))

    def test_every_deploy_job_is_gated_on_the_owner_variable(self):
        for name, job in self.workflow["jobs"].items():
            with self.subTest(job=name):
                self.assertIn("vars.DEPLOY_ENABLED == 'true'", job["if"])

    def test_environments_are_test_and_production_and_values_come_from_the_private_repo(self):
        jobs = self.workflow["jobs"]
        self.assertEqual("test", jobs["test"]["environment"])
        self.assertEqual("production", jobs["prod"]["environment"])
        for name, job in jobs.items():
            with self.subTest(job=name):
                checkouts = [step for step in job["steps"] if step.get("with", {}).get("repository") == "intochat/os-deployment"]
                self.assertEqual(1, len(checkouts))
                self.assertEqual("${{ secrets.DEPLOYMENT_VALUES_TOKEN }}", checkouts[0]["with"]["token"])
                self.assertIs(False, checkouts[0]["with"]["persist-credentials"])

    def test_the_os_repo_names_no_deployment_values_and_the_script_reads_its_names_from_the_installer(self):
        script = (ROOT / "scripts" / "deploy" / "deploy.sh").read_text(encoding="utf-8")
        self.assertIn("--print-env", script)
        self.assertNotIn("check_environment os IntoChatOS__", script)
        self.assertIn("deployment/*.json", (ROOT / ".gitignore").read_text(encoding="utf-8"))
        self.assertEqual([], [path.name for path in (ROOT / "deployment").glob("*.json")] if (ROOT / "deployment").exists() else [])
