import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SCRIPT = Path(__file__).with_name("materialize_task_context.py")


def load_materializer():
    spec = importlib.util.spec_from_file_location("materialize_task_context", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class MaterializeTaskContextTests(unittest.TestCase):
    def setUp(self):
        self.tempdir = tempfile.TemporaryDirectory()
        self.root = Path(self.tempdir.name)
        self.repo = self.root / "repo"
        self.campaign = self.repo / "docs" / "_prompts" / "orchestration" / "demo"
        self.campaign.mkdir(parents=True)
        (self.repo / "docs" / "reference").mkdir(parents=True)
        (self.repo / "docs" / "reference" / "authority.md").write_text(
            "authority body\n", encoding="utf-8"
        )
        (self.campaign / "SPEC.md").write_text(
            "# Spec\n\n## Authority files\n"
            "- `docs/reference/authority.md` | lines 10-12\n"
            "- `docs/reference/empty.md` |\n\n"
            "## Scope\nignored\n",
            encoding="utf-8",
        )
        (self.repo / "docs" / "reference" / "empty.md").write_text(
            "empty authority\n", encoding="utf-8"
        )
        for name in ("ROADMAP.md", "TASKS.md"):
            (self.campaign / name).write_text(f"{name}\n", encoding="utf-8")
        self.worktree = self.root / "worktree"
        self.worktree.mkdir()

    def tearDown(self):
        self.tempdir.cleanup()

    def run_cli(self, campaign=None, worktree=None):
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPT),
                "--campaign",
                str(campaign or self.campaign),
                "--worktree",
                str(worktree or self.worktree),
            ],
            text=True,
            capture_output=True,
        )

    def test_valid_copy_manifest_selectors_and_hashes(self):
        result = self.run_cli()
        self.assertEqual(result.returncode, 0, result.stderr)

        target = self.worktree / ".agent-context" / "demo"
        self.assertEqual(
            (target / "SPEC.md").read_text(encoding="utf-8"),
            (self.campaign / "SPEC.md").read_text(encoding="utf-8"),
        )
        self.assertEqual(
            (target / "docs/reference/authority.md").read_text(encoding="utf-8"),
            "authority body\n",
        )
        manifest = json.loads((target / "MANIFEST.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["campaign_id"], "demo")
        self.assertIsNone(manifest["source_repo_head"])
        self.assertEqual(
            [(entry["path"], entry["selector"]) for entry in manifest["files"]],
            [
                ("SPEC.md", None),
                ("ROADMAP.md", None),
                ("TASKS.md", None),
                ("docs/reference/authority.md", "lines 10-12"),
                ("docs/reference/empty.md", ""),
            ],
        )
        for entry in manifest["files"]:
            expected = hashlib.sha256((target / entry["path"]).read_bytes()).hexdigest()
            self.assertEqual(entry["sha256"], expected)

    def test_template_html_comments_are_ignored(self):
        (self.campaign / "SPEC.md").write_text(
            "## Authority files\n"
            "\n"
            "<!-- Add one line per authority file. -->\n"
            "  <!-- The selector is optional. -->\n"
            "- `docs/reference/authority.md` | lines 10-12\n",
            encoding="utf-8",
        )

        result = self.run_cli()

        self.assertEqual(result.returncode, 0, result.stderr)
        manifest = json.loads(
            (self.worktree / ".agent-context" / "demo" / "MANIFEST.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(
            [entry["path"] for entry in manifest["files"]],
            ["SPEC.md", "ROADMAP.md", "TASKS.md", "docs/reference/authority.md"],
        )

    def test_rejects_malformed_nonempty_authority_line(self):
        (self.campaign / "SPEC.md").write_text(
            "## Authority files\nnot an authority item\n", encoding="utf-8"
        )

        result = self.run_cli()

        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.worktree / ".agent-context" / "demo").exists())

    def test_missing_authority_produces_no_output(self):
        target = self.worktree / ".agent-context" / "demo"
        target.mkdir(parents=True)
        (target / "sentinel").write_text("keep", encoding="utf-8")
        (self.repo / "docs" / "reference" / "empty.md").unlink()

        result = self.run_cli()

        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((target / "sentinel").read_text(encoding="utf-8"), "keep")
        self.assertFalse((target / "MANIFEST.json").exists())

    def test_late_write_failure_preserves_target_and_removes_staging(self):
        target = self.worktree / ".agent-context" / "demo"
        target.mkdir(parents=True)
        (target / "old.txt").write_text("old packet\n", encoding="utf-8")
        (target / "nested").mkdir()
        (target / "nested" / "old.txt").write_text("old nested\n", encoding="utf-8")
        before = {
            path.relative_to(target): path.read_bytes()
            for path in target.rglob("*")
            if path.is_file()
        }
        materializer = load_materializer()
        original_write_bytes = Path.write_bytes

        def fail_authority_write(path, data):
            if path.name == "authority.md" and any(
                part.startswith(".demo.staging-") for part in path.parts
            ):
                raise OSError("forced late copy failure")
            return original_write_bytes(path, data)

        with mock.patch.object(
            Path, "write_bytes", autospec=True, side_effect=fail_authority_write
        ):
            with self.assertRaises(OSError):
                materializer.materialize(self.campaign, self.worktree)

        after = {
            path.relative_to(target): path.read_bytes()
            for path in target.rglob("*")
            if path.is_file()
        }
        self.assertEqual(after, before)
        self.assertEqual(list(target.parent.glob(".demo.staging-*")), [])

    def test_replacement_and_rollback_failure_preserves_recoverable_backup(self):
        target = self.worktree / ".agent-context" / "demo"
        target.mkdir(parents=True)
        (target / "sentinel").write_text("prior packet\n", encoding="utf-8")
        materializer = load_materializer()
        real_replace = os.replace
        calls = []

        def fail_replacement_and_rollback(source, destination):
            calls.append((Path(source), Path(destination)))
            if len(calls) >= 2:
                raise OSError("forced rollback failure")
            return real_replace(source, destination)

        with mock.patch.object(materializer.os, "replace", side_effect=fail_replacement_and_rollback):
            with self.assertRaisesRegex(OSError, r"recoverable backup: .+\.demo\.backup-") as raised:
                materializer.materialize(self.campaign, self.worktree)

        message = str(raised.exception)
        backup_path = Path(message.rsplit("recoverable backup: ", 1)[1])
        self.assertTrue(backup_path.is_dir())
        self.assertEqual((backup_path / "sentinel").read_text(encoding="utf-8"), "prior packet\n")
        self.assertFalse(target.exists())
        self.assertEqual(list(target.parent.glob(".demo.staging-*")), [])

    def test_rejects_traversal_absolute_and_non_md_authorities(self):
        invalid_paths = ("../outside.md", "/tmp/absolute.md", "docs/not-md.txt")
        for invalid_path in invalid_paths:
            with self.subTest(invalid_path=invalid_path):
                campaign = self.repo / "docs" / "_prompts" / "orchestration" / invalid_path.replace(
                    "/", "_"
                ).replace(".", "_")
                campaign.mkdir()
                for name in ("ROADMAP.md", "TASKS.md"):
                    (campaign / name).write_text(name, encoding="utf-8")
                (campaign / "SPEC.md").write_text(
                    f"## Authority files\n- `{invalid_path}` |\n", encoding="utf-8"
                )
                result = self.run_cli(campaign=campaign)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(
                    (self.worktree / ".agent-context" / campaign.name).exists()
                )

    @unittest.skipUnless(hasattr(os, "symlink"), "symlinks unavailable")
    def test_rejects_authority_symlink_escape(self):
        outside = self.root / "outside.md"
        outside.write_text("outside\n", encoding="utf-8")
        link = self.repo / "docs" / "reference" / "escape.md"
        link.symlink_to(outside)
        (self.campaign / "SPEC.md").write_text(
            "## Authority files\n- `docs/reference/escape.md` |\n", encoding="utf-8"
        )

        result = self.run_cli()

        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.worktree / ".agent-context" / "demo").exists())

    def test_source_immutable_and_unrelated_worktree_content_preserved(self):
        unrelated = self.worktree / "keep.txt"
        unrelated.write_text("keep", encoding="utf-8")
        other = self.worktree / ".agent-context" / "other"
        other.mkdir(parents=True)
        (other / "keep.txt").write_text("other", encoding="utf-8")
        source_files = {
            path.relative_to(self.repo): path.read_bytes()
            for path in self.repo.rglob("*")
            if path.is_file()
        }

        result = self.run_cli()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(unrelated.read_text(encoding="utf-8"), "keep")
        self.assertEqual((other / "keep.txt").read_text(encoding="utf-8"), "other")
        self.assertEqual(
            {
                path.relative_to(self.repo): path.read_bytes()
                for path in self.repo.rglob("*")
                if path.is_file()
            },
            source_files,
        )


if __name__ == "__main__":
    unittest.main()
