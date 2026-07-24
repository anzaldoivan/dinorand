#!/usr/bin/env python3
"""Validate an existing DinoRand release tag and curated release inputs."""
from __future__ import annotations
import argparse
from dataclasses import dataclass
from pathlib import Path
import re, subprocess, sys
import xml.etree.ElementTree as ET

SEMVER_TAG = re.compile(r"^v(?P<core>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:-(?P<pre>(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*))*))?$")
class ValidationError(RuntimeError): pass
@dataclass(frozen=True)
class ReleaseRef:
    tag: str; version: str; core_version: str; prerelease: bool

def parse_tag(tag: str) -> ReleaseRef:
    match = SEMVER_TAG.fullmatch(tag)
    if not match:
        raise ValidationError("tag must match vMAJOR.MINOR.PATCH or vMAJOR.MINOR.PATCH-PRERELEASE; leading zeroes and build metadata are unsupported")
    return ReleaseRef(tag, tag[1:], match.group("core"), match.group("pre") is not None)

def validate_project_version(root: Path, release: ReleaseRef) -> None:
    try: values = [(e.text or "").strip() for e in ET.parse(root / "Directory.Build.props").findall(".//VersionPrefix") if (e.text or "").strip()]
    except (OSError, ET.ParseError) as error: raise ValidationError(f"cannot read Directory.Build.props: {error}") from error
    if values != [release.core_version]: raise ValidationError(f"Directory.Build.props VersionPrefix must be {release.core_version}; found {values or ['missing']}")

def extract_changelog(root: Path, release: ReleaseRef) -> str:
    try: lines = (root / "CHANGELOG.md").read_text(encoding="utf-8").splitlines()
    except OSError as error: raise ValidationError(f"cannot read CHANGELOG.md: {error}") from error
    pattern = re.compile(rf"^## \[{re.escape(release.version)}\](?:\s+(?:-|—)\s+\d{{4}}-\d{{2}}-\d{{2}})?\s*$")
    starts = [i for i, line in enumerate(lines) if pattern.fullmatch(line)]
    if len(starts) != 1: raise ValidationError(f"CHANGELOG.md must contain exactly one curated ## [{release.version}] section")
    start = starts[0]; end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines)); section = lines[start + 1:end]
    if not any(re.match(r"^- \*\*\S", line) for line in section): raise ValidationError(f"CHANGELOG.md [{release.version}] section must contain a non-empty curated release bullet")
    return "\n".join(section).strip() + "\n"

def run_git(root: Path, *args: str, check: bool = True):
    return subprocess.run(["git", "-C", str(root), *args], check=check, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
def resolve_tag_commit(root: Path, tag: str) -> str:
    parse_tag(tag)
    try: commit = run_git(root, "rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}").stdout.strip()
    except subprocess.CalledProcessError as error: raise ValidationError(f"existing tag refs/tags/{tag} was not found") from error
    if not re.fullmatch(r"[0-9a-f]{40}", commit): raise ValidationError(f"tag {tag} did not resolve to a full commit SHA")
    return commit
def require_origin_main_ancestor(root: Path, commit: str) -> None:
    result = run_git(root, "merge-base", "--is-ancestor", commit, "refs/remotes/origin/main", check=False)
    if result.returncode == 1: raise ValidationError(f"tag commit {commit} is not contained in freshly fetched origin/main")
    if result.returncode != 0: raise ValidationError(f"could not verify origin/main ancestry: {result.stderr.strip() or result.returncode}")
def validate_release(root: Path, tag: str):
    release = parse_tag(tag); validate_project_version(root, release); notes = extract_changelog(root, release); commit = resolve_tag_commit(root, tag); require_origin_main_ancestor(root, commit); return release, commit, notes
def main(argv=None):
    parser = argparse.ArgumentParser(); parser.add_argument("--tag", required=True); parser.add_argument("--repository", type=Path, default=Path.cwd()); parser.add_argument("--notes-output", type=Path, required=True); parser.add_argument("--github-output", type=Path); args = parser.parse_args(argv)
    try: release, commit, notes = validate_release(args.repository.resolve(), args.tag)
    except ValidationError as error: print(f"release validation failed: {error}", file=sys.stderr); return 1
    args.notes_output.write_text(notes, encoding="utf-8", newline="\n")
    output = f"tag={release.tag}\nversion={release.version}\ncore_version={release.core_version}\nprerelease={'true' if release.prerelease else 'false'}\ncommit={commit}\n"
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as stream: stream.write(output)
    else: sys.stdout.write(output)
    return 0
if __name__ == "__main__": raise SystemExit(main())
