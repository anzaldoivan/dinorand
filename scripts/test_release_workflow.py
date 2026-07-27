#!/usr/bin/env python3
import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "release.yml"
CI_WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "ci.yml"
CUT_RELEASE_SKILL = (
    Path(__file__).parents[1] / ".agents" / "skills" / "cut-release" / "SKILL.md"
)
TEST_ENV_BOOTSTRAP = (
    Path(__file__).parents[1]
    / "test"
    / "DinoRand.FileFormats.Tests"
    / "TestEnvBootstrap.cs"
)


class ReleasePublishWorkflowTests(unittest.TestCase):
    def test_publish_uses_repository_owned_publisher_not_runner_gh(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertNotRegex(
            workflow,
            re.compile(r"^\s*gh release\s", re.MULTILINE),
        )
        self.assertIn("python3 scripts/release_publish.py", workflow)

    def test_publish_has_pinned_python_and_a_hard_job_timeout(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        publish = workflow.split("\n  publish:\n", 1)[1]
        self.assertRegex(publish, r"timeout-minutes:\s+30")
        self.assertRegex(
            publish,
            r"uses: actions/setup-python@[0-9a-f]{40} # v",
        )
        self.assertRegex(publish, r'python-version:\s+"3\.12\.13"')
        self.assertIn("--attempts 3", publish)
        self.assertIn("--request-timeout 120", publish)
        self.assertIn("--upload-timeout 120", publish)

    def test_every_release_job_has_a_hard_timeout(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertEqual(
            re.findall(
                r"^\s{4}timeout-minutes:\s+(\d+)$",
                workflow,
                re.MULTILINE,
            ),
            ["10", "30", "10", "30"],
        )

    def test_every_python_job_uses_the_same_pinned_runtime(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertEqual(workflow.count("python-version: \"3.12.13\""), 3)
        self.assertEqual(
            len(re.findall(r"uses: actions/setup-python@[0-9a-f]{40} # v", workflow)),
            3,
        )

    def test_manual_recovery_dispatches_the_same_validated_tag(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertRegex(
            workflow,
            r"(?s)workflow_dispatch:\s+inputs:\s+tag:\s+"
            r"description:.*?required:\s+true",
        )
        self.assertIn("inputs.tag || github.ref_name", workflow)
        self.assertEqual(
            workflow.count("ref: ${{ needs.validate.outputs.tag }}"),
            1,
        )
        self.assertIn("ref: ${{ github.workflow_sha }}", workflow)

    def test_cut_release_skill_uses_pr_then_tag_and_nondestructive_recovery(self):
        skill = CUT_RELEASE_SKILL.read_text(encoding="utf-8")

        self.assertIn("version/vX.Y.Z", skill)
        self.assertNotIn("feature/release-vX.Y.Z", skill)
        self.assertIn("post-merge release-tag workflow", skill)
        self.assertIn("RELEASE_TAG_TOKEN", skill)
        self.assertNotIn("git tag -a", skill)
        self.assertNotIn("git push origin refs/tags/", skill)
        self.assertIn(
            "gh workflow run release.yml --ref main -f tag=vX.Y.Z",
            skill,
        )
        self.assertIn("gh run rerun RUN_ID", skill)
        self.assertNotIn("git push origin :refs/tags/", skill)

    def test_ordinary_release_tests_explicitly_disable_real_install_fixtures(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        skill = CUT_RELEASE_SKILL.read_text(encoding="utf-8")
        bootstrap = TEST_ENV_BOOTSTRAP.read_text(encoding="utf-8")

        self.assertIn('DINORAND_DISABLE_REAL_INSTALL: "1"', workflow)
        self.assertIn("DINORAND_DISABLE_REAL_INSTALL=1", skill)
        self.assertIn('GetEnvironmentVariable("DINORAND_DISABLE_REAL_INSTALL")', bootstrap)

    def test_release_workflow_contract_runs_in_ci(self):
        ci = CI_WORKFLOW.read_text(encoding="utf-8")

        self.assertRegex(
            ci,
            r"(?s)python3 -m unittest.*?scripts/test_release_post_merge\.py.*?"
            r"scripts/test_release_publish\.py.*?"
            r"scripts/test_release_workflow\.py",
        )


if __name__ == "__main__":
    unittest.main()
