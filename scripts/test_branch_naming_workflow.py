#!/usr/bin/env python3
import os
from pathlib import Path
import re
import subprocess
import textwrap
import unittest


WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "branch-naming.yml"
CONTRIBUTING = Path(__file__).parents[1] / "CONTRIBUTING.md"


def branch_check():
    workflow = WORKFLOW.read_text(encoding="utf-8")
    match = re.search(
        r"(?m)^        run: \|\n(?P<body>(?:^          .*(?:\n|$))+)",
        workflow,
    )
    if match is None:
        raise AssertionError("branch-name workflow must contain one literal run block")
    return textwrap.dedent(match.group("body"))


def run_check(head_ref: str, head_login: str):
    environment = os.environ.copy()
    environment.update(HEAD_REF=head_ref, HEAD_LOGIN=head_login)
    return subprocess.run(
        ["bash", "-c", branch_check()],
        env=environment,
        capture_output=True,
        text=True,
        check=False,
    )


class BranchNamingWorkflowTests(unittest.TestCase):
    def test_ordinary_feature_branch_is_allowed_for_a_human_author(self):
        result = run_check("feature/enemy-randomizer", "maintainer")

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_stable_version_branch_is_allowed(self):
        result = run_check("version/v0.6.1", "maintainer")

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_prerelease_version_branch_is_allowed(self):
        result = run_check("version/v1.2.3-rc.1", "maintainer")

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_dependabot_branch_is_allowed_for_dependabot(self):
        result = run_check(
            "dependabot/nuget/src/example/production-dependencies",
            "dependabot[bot]",
        )

        self.assertEqual(result.returncode, 0, result.stderr)

    def test_human_cannot_claim_the_dependabot_namespace(self):
        result = run_check(
            "dependabot/nuget/src/example/production-dependencies",
            "maintainer",
        )

        self.assertNotEqual(result.returncode, 0)

    def test_obsolete_release_branch_names_are_rejected(self):
        for head_ref in ("release/v0.6.1", "feature/release-v0.6.1"):
            with self.subTest(head_ref=head_ref):
                result = run_check(head_ref, "maintainer")

                self.assertNotEqual(result.returncode, 0)

    def test_malformed_version_branch_names_are_rejected(self):
        for head_ref in (
            "version/V0.6.1",
            "version/v01.6.1",
            "version/v0.6",
            "version/v0.6.1+build",
        ):
            with self.subTest(head_ref=head_ref):
                result = run_check(head_ref, "maintainer")

                self.assertNotEqual(result.returncode, 0)

    def test_contributor_policy_names_the_version_branch_contract(self):
        policy = CONTRIBUTING.read_text(encoding="utf-8")

        self.assertIn("`version/vX.Y.Z`", policy)


if __name__ == "__main__":
    unittest.main()
