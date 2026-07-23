#!/usr/bin/env python3
import tempfile
import unittest
from pathlib import Path
import check_docs

class MarkdownLinkTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)
        (self.root / "README.md").write_text("# Public heading\n", encoding="utf-8")
        (self.root / "GUIDE.md").write_text("# Guide\n", encoding="utf-8")
    def tearDown(self): self.temp.cleanup()
    def test_accepts_public_external_email_and_fragment(self):
        (self.root / "README.md").write_text("# Public heading\n\n[guide](GUIDE.md)\n[local](#public-heading)\n[web](https://example.com)\n[email](mailto:a@example.com)\n", encoding="utf-8")
        self.assertEqual([], check_docs.check_markdown_links(self.root, {Path("README.md"), Path("GUIDE.md")}))
    def test_rejects_existing_untracked_docs_and_claude(self):
        (self.root / "docs").mkdir(); (self.root / "docs/private.md").write_text("# Private\n", encoding="utf-8")
        (self.root / "CLAUDE.md").write_text("# Private\n", encoding="utf-8")
        (self.root / "README.md").write_text("[private](docs/private.md)\n[claude](CLAUDE.md)\n", encoding="utf-8")
        errors = check_docs.check_markdown_links(self.root, {Path("README.md"), Path("GUIDE.md")})
        self.assertEqual(2, len(errors))
    def test_rejects_missing_and_bad_fragment(self):
        (self.root / "README.md").write_text("[missing](MISSING.md)\n[bad](#not-present)\n", encoding="utf-8")
        self.assertEqual(2, len(check_docs.check_markdown_links(self.root, {Path("README.md"), Path("GUIDE.md")})))

if __name__ == "__main__": unittest.main()
