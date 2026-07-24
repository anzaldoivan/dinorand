#!/usr/bin/env python3
"""Package apworld/<world>/ into dist/<world>.apworld (a zip with the world dir at root).

Shape is the one live-verified against AP 0.6.7 on 2026-07-19: archive root holds exactly
one folder named identically to the .apworld file, __pycache__/*.pyc excluded, test/ kept.
Run by the release workflow and locally for verification:

    python3 scripts/package_apworlds.py
"""
import stat
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DIST = REPO / "dist"


def package(world_dir: Path) -> Path:
    name = world_dir.name
    out = DIST / f"{name}.apworld"
    files = sorted(
        p for p in world_dir.rglob("*")
        if p.is_file() and p.suffix != ".pyc" and "__pycache__" not in p.parts
    )
    DIST.mkdir(exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for p in files:
            archive_name = (Path(name) / p.relative_to(world_dir)).as_posix()
            info = zipfile.ZipInfo(archive_name, (2020, 1, 1, 0, 0, 0))
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, p.read_bytes(), compresslevel=9)

    # Fail the release rather than ship a zip AP can't load.
    names = zipfile.ZipFile(out).namelist()
    roots = {n.split("/")[0] for n in names}
    assert roots == {name}, f"{out.name}: archive root must be only {name}/, got {roots}"
    assert f"{name}/archipelago.json" in names, f"{out.name}: missing archipelago.json"
    assert not [n for n in names if "__pycache__" in n], f"{out.name}: __pycache__ leaked in"
    print(f"{out.relative_to(REPO)}  ({len(names)} files)")
    return out


if __name__ == "__main__":
    worlds = sorted(p.parent for p in (REPO / "apworld").glob("*/archipelago.json"))
    if not worlds:
        sys.exit("no apworld/*/archipelago.json found")
    for w in worlds:
        package(w)
