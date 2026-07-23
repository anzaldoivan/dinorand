#!/usr/bin/env python3
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock
import release_validate

class ReleaseTagTests(unittest.TestCase):
    def test_accepts_stable_and_prerelease(self):
        stable = release_validate.parse_tag("v1.2.3")
        pre = release_validate.parse_tag("v1.2.3-rc.1")
        self.assertEqual((stable.version, stable.core_version, stable.prerelease), ("1.2.3", "1.2.3", False))
        self.assertEqual((pre.version, pre.core_version, pre.prerelease), ("1.2.3-rc.1", "1.2.3", True))
    def test_rejects_malformed_or_unsupported(self):
        for tag in ("1.2.3", "v1.2", "v01.2.3", "v1.2.3+build.1", "v1.2.3-rc..1", "v1.2.3-01"):
            with self.subTest(tag=tag), self.assertRaises(release_validate.ValidationError):
                release_validate.parse_tag(tag)

class RepositoryInputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)
        (self.root / "Directory.Build.props").write_text("<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>\n", encoding="utf-8")
        (self.root / "CHANGELOG.md").write_text("# Changelog\n\n## [1.2.3] - 2026-07-23\n\n### Changed\n\n- **A curated release note.**\n\n## [1.2.2]\n", encoding="utf-8")
    def tearDown(self): self.temp.cleanup()
    def test_project_version_mismatch(self):
        with self.assertRaisesRegex(release_validate.ValidationError, "VersionPrefix"):
            release_validate.validate_project_version(self.root, release_validate.parse_tag("v1.2.4"))
    def test_changelog_mismatch_and_empty(self):
        release = release_validate.parse_tag("v1.2.3-rc.1")
        with self.assertRaisesRegex(release_validate.ValidationError, "CHANGELOG"):
            release_validate.extract_changelog(self.root, release)
        (self.root / "CHANGELOG.md").write_text("## [1.2.3-rc.1]\n\n### Changed\n\n## [1.2.3]\n", encoding="utf-8")
        with self.assertRaisesRegex(release_validate.ValidationError, "non-empty"):
            release_validate.extract_changelog(self.root, release)
    @mock.patch("release_validate.run_git")
    def test_missing_tag(self, run_git):
        run_git.side_effect = subprocess.CalledProcessError(128, ["git", "rev-parse"])
        with self.assertRaisesRegex(release_validate.ValidationError, "tag"):
            release_validate.resolve_tag_commit(self.root, "v1.2.3")
    @mock.patch("release_validate.run_git")
    def test_tag_not_on_origin_main(self, run_git):
        run_git.return_value = subprocess.CompletedProcess(["git", "merge-base"], 1, "", "")
        with self.assertRaisesRegex(release_validate.ValidationError, "origin/main"):
            release_validate.require_origin_main_ancestor(self.root, "a" * 40)

if __name__ == "__main__": unittest.main()
