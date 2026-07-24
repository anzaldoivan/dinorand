#!/usr/bin/env python3
import tempfile
import unittest
from pathlib import Path
import release_notices

class PackageLicenseEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.package = Path(self.temp.name) / "example" / "1.2.3"; self.package.mkdir(parents=True)
    def tearDown(self): self.temp.cleanup()
    def write_nuspec(self, license_xml):
        path = self.package / "Example.nuspec"
        path.write_text("<?xml version='1.0'?><package><metadata><id>Example</id><version>1.2.3</version>" + license_xml + "</metadata></package>", encoding="utf-8")
        return path
    def test_expression(self):
        self.assertEqual("MIT", release_notices.read_package_evidence(self.write_nuspec('<license type="expression">MIT</license>')).license_expression)
    def test_missing_and_url_rejected(self):
        for xml in ("", "<licenseUrl>https://example.invalid/license</licenseUrl>"):
            with self.subTest(xml=xml), self.assertRaises(release_notices.NoticeError): release_notices.read_package_evidence(self.write_nuspec(xml))
    def test_file_required_and_captured(self):
        spec = self.write_nuspec('<license type="file">LICENSE.txt</license>')
        with self.assertRaises(release_notices.NoticeError): release_notices.read_package_evidence(spec)
        (self.package / "LICENSE.txt").write_text("license\n", encoding="utf-8")
        self.assertEqual(self.package / "LICENSE.txt", release_notices.read_package_evidence(spec).license_file)
    def test_machine_paths_and_credentials_rejected(self):
        for text in ("cache=/tmp/secret/packages", "token=ghp_not-a-real-token"):
            with self.assertRaises(release_notices.NoticeError): release_notices.assert_safe_text(text)

if __name__ == "__main__": unittest.main()
