#!/usr/bin/env python3
import tempfile
import unittest
import zipfile
from pathlib import Path

import package_release


class ReleaseArchiveInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.source = Path(self.temp.name) / "osx-arm64"
        self.source.mkdir()
        for name in (
            "dinorand",
            "DinoRand.Avalonia",
            "LICENSE",
            "LEGAL.md",
            "THIRD-PARTY-NOTICES.md",
            "notices/DEPENDENCY-INVENTORY.md",
            "notices/DOTNET-RUNTIME-LICENSE.txt",
            "notices/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
        ):
            path = self.source / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode("ascii"))
        self.native_libraries = {
            "libAvaloniaNative.dylib",
            "libHarfBuzzSharp.dylib",
            "libSkiaSharp.dylib",
        }
        for name in self.native_libraries:
            (self.source / name).write_bytes(name.encode("ascii"))

    def tearDown(self):
        self.temp.cleanup()

    def test_osx_native_runtime_libraries_are_archived(self):
        destination = Path(self.temp.name) / "release.zip"
        package_release.archive_tree(self.source, destination, "osx-arm64")
        with zipfile.ZipFile(destination) as archive:
            names = set(archive.namelist())
        self.assertTrue(self.native_libraries <= names)

    def test_unexpected_top_level_library_fails_closed(self):
        (self.source / "unexpected.dylib").write_bytes(b"unexpected")
        with self.assertRaises(package_release.PackageError):
            package_release.archive_tree(
                self.source, Path(self.temp.name) / "release.zip", "osx-arm64"
            )


if __name__ == "__main__":
    unittest.main()
