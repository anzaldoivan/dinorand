#!/usr/bin/env python3
import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "release.yml"
CI_WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "ci.yml"


class ReleasePublishWorkflowTests(unittest.TestCase):
    def test_release_commands_declare_repository_context(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        commands = re.findall(r"^\s*gh release\s+\S+.*$", workflow, re.MULTILINE)

        self.assertEqual(len(commands), 4)
        for command in commands:
            with self.subTest(command=command.strip()):
                self.assertIn('--repo "$GITHUB_REPOSITORY"', command)

    def test_draft_verification_uses_exact_draft_capable_tag_lookup(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        self.assertNotIn('releases/tags/$RELEASE_TAG', workflow)
        self.assertRegex(
            workflow,
            r'gh release view "\$RELEASE_TAG" --repo "\$GITHUB_REPOSITORY" '
            r'--json tagName,isDraft,assets > draft-release\.json',
        )
        self.assertRegex(workflow, r'release\["tagName"\] != tag')

    def test_release_workflow_contract_runs_in_ci(self):
        ci = CI_WORKFLOW.read_text(encoding="utf-8")

        self.assertRegex(ci, r"(?s)python3 -m unittest.*?scripts/test_release_workflow\.py")


if __name__ == "__main__":
    unittest.main()
