import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
spec = importlib.util.spec_from_file_location("validate_tools", Path(__file__).with_name("validate-tools.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class ToolsGateTests(unittest.TestCase):
    def test_real_gitlink_accepts_clean_child_and_rejects_changed_revision(self):
        with tempfile.TemporaryDirectory(prefix="dino-tools-gate-") as tmp:
            root = Path(tmp)
            child = root / "tools"
            child.mkdir()

            def git(directory, *args):
                return subprocess.check_output(
                    ["git", "-C", str(directory), *args], text=True, stderr=subprocess.DEVNULL
                ).strip()

            git(root, "init", "-q")
            git(child, "init", "-q")
            git(child, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--allow-empty", "-qm", "Synthetic fixture")
            sha = git(child, "rev-parse", "HEAD")
            (root / ".gitmodules").write_text(
                f'[submodule "tools"]\n\tpath = tools\n\turl = {module.URL}\n'
            )
            git(root, "add", ".gitmodules")
            git(root, "update-index", "--add", "--cacheinfo", f"160000,{sha},tools")
            self.assertEqual(child, module.validate(root))
            # Git hooks export the parent index; child Git must use its own index.
            with patch.dict(os.environ, {"GIT_INDEX_FILE": str(root / ".git/index")}):
                self.assertEqual(child, module.validate(root))
            git(child, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--allow-empty", "-qm", "Changed fixture revision")
            with self.assertRaisesRegex(ValueError, "commit mismatch"):
                module.validate(root)

    def test_missing_gitlink_rejected(self):
        with patch.object(module, "run", return_value=""):
            with self.assertRaisesRegex(ValueError, "gitlink"):
                module.validate(Path("/unused"))

    def test_missing_submodule_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            with patch.object(module, "run", side_effect=["160000 " + "a" * 40 + " 0 tools", "tools", module.URL]):
                with self.assertRaisesRegex(ValueError, "missing"):
                    module.validate(Path(tmp))

    def test_mismatched_pin_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "tools/.git").mkdir(parents=True)
            replies = ["160000 " + "a" * 40 + " 0 tools", "tools", module.URL, str(root / "tools"), "b" * 40]
            with patch.object(module, "run", side_effect=replies):
                with self.assertRaisesRegex(ValueError, "commit mismatch"):
                    module.validate(root)

    def test_wrong_url_rejected(self):
        with patch.object(module, "run", side_effect=["160000 " + "a" * 40 + " 0 tools", "tools", "https://example.invalid/repo"]):
            with self.assertRaisesRegex(ValueError, "URL mismatch"):
                module.validate(Path("/unused"))
