#!/usr/bin/env python3
import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[1] / ".github" / "workflows" / "release.yml"


class ReleasePublishWorkflowTests(unittest.TestCase):
    def test_release_commands_declare_repository_context(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        commands = [
            (match.group("name"), match.group(0))
            for match in re.finditer(
                r"^\s*gh release (?P<name>create|upload|edit)\b.*$", workflow, re.MULTILINE
            )
        ]

        self.assertEqual([name for name, _ in commands], ["create", "upload", "edit"])
        for name, command in commands:
            with self.subTest(command=name):
                self.assertIn('--repo "$GITHUB_REPOSITORY"', command)


if __name__ == "__main__":
    unittest.main()
