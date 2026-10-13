#!/usr/bin/env python3
"""Materialize an orchestration campaign and its authority packet."""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath, PureWindowsPath


PACKET_FILES = ("SPEC.md", "ROADMAP.md", "TASKS.md")
AUTHORITY_ITEM = re.compile(r"^- `([^`]+)` \|(?: (.*))?$")


class MaterializeError(Exception):
    pass


def inside(path, parent):
    try:
        path.relative_to(parent)
    except ValueError:
        return False
    return True


def regular_file_below(path, parent, label):
    resolved = path.resolve(strict=False)
    if not inside(resolved, parent):
        raise MaterializeError(f"{label} escapes {parent}")
    if not resolved.is_file():
        raise MaterializeError(f"{label} is not a regular file: {path}")
    return resolved


def parse_authorities(spec_path):
    try:
        lines = spec_path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as exc:
        raise MaterializeError(f"cannot read {spec_path}: {exc}") from exc

    start = next(
        (index for index, line in enumerate(lines) if line.strip() == "## Authority files"),
        None,
    )
    if start is None:
        raise MaterializeError("SPEC.md is missing the ## Authority files section")

    authorities = []
    seen = set()
    for line in lines[start + 1 :]:
        if line.startswith("## "):
            break
        stripped = line.strip()
        if not stripped or (stripped.startswith("<!--") and stripped.endswith("-->")):
            continue
        match = AUTHORITY_ITEM.fullmatch(line)
        if not match:
            raise MaterializeError(
                "authority items must use: - `docs/path.md` | <selector>"
            )
        relative_path, selector = match.groups()
        if relative_path in seen:
            raise MaterializeError(f"duplicate authority file: {relative_path}")
        seen.add(relative_path)
        authorities.append((relative_path, (selector or "").strip()))
    return authorities


def validate_authority_path(relative_path, repo, docs_root):
    if (
        not relative_path
        or "\\" in relative_path
        or Path(relative_path).is_absolute()
        or PurePosixPath(relative_path).is_absolute()
        or PureWindowsPath(relative_path).is_absolute()
    ):
        raise MaterializeError(f"invalid authority path: {relative_path}")
    parts = relative_path.split("/")
    if any(part in ("", ".", "..") for part in parts) or parts[0] != "docs":
        raise MaterializeError(f"authority must be a docs-relative path: {relative_path}")
    if not relative_path.endswith(".md"):
        raise MaterializeError(f"authority must be a .md file: {relative_path}")

    source = regular_file_below(repo.joinpath(*parts), docs_root, "authority file")
    return source, Path(*parts)


def source_head(repo):
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=repo,
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError:
        return None
    if result.returncode == 0:
        head = result.stdout.strip()
        if head:
            return head
    return None


def validate_campaign(campaign_arg, worktree_arg):
    campaign = Path(campaign_arg).resolve(strict=False)
    if not campaign.is_dir():
        raise MaterializeError(f"campaign is not a directory: {campaign_arg}")
    if campaign.parent.name != "orchestration" or campaign.parent.parent.name != "_prompts":
        raise MaterializeError(
            "campaign must be directly under docs/_prompts/orchestration"
        )
    docs_root = campaign.parent.parent.parent
    if docs_root.name != "docs":
        raise MaterializeError("campaign must be under a source repository docs directory")
    repo = docs_root.parent

    packet = []
    for name in PACKET_FILES:
        packet.append((name, regular_file_below(campaign / name, campaign, name)))

    authorities = []
    for relative_path, selector in parse_authorities(packet[0][1]):
        source, destination = validate_authority_path(relative_path, repo, docs_root)
        authorities.append((destination, source, selector))

    worktree = Path(worktree_arg).resolve(strict=False)
    if not worktree.is_dir():
        raise MaterializeError(f"worktree is not a directory: {worktree_arg}")
    context = worktree / ".agent-context"
    if context.exists() or context.is_symlink():
        context_resolved = context.resolve(strict=False)
        if not inside(context_resolved, worktree) or not context_resolved.is_dir():
            raise MaterializeError(".agent-context is not a directory under the worktree")
    target = context / campaign.name
    target_resolved = target.resolve(strict=False)
    if not inside(target_resolved, worktree):
        raise MaterializeError("campaign target escapes the worktree")

    return campaign, repo, packet, authorities, worktree, context, target


def remove_path(path):
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.is_dir():
        shutil.rmtree(path)


def new_empty_sibling(context, prefix):
    path = Path(tempfile.mkdtemp(prefix=prefix, dir=context))
    path.rmdir()
    return path


def materialize(campaign_arg, worktree_arg):
    campaign, repo, packet, authorities, worktree, context, target = validate_campaign(
        campaign_arg, worktree_arg
    )

    prepared = []
    for relative_path, source in packet:
        data = source.read_bytes()
        prepared.append(
            (relative_path, data, hashlib.sha256(data).hexdigest(), None)
        )
    for relative_path, source, selector in authorities:
        data = source.read_bytes()
        prepared.append(
            (
                relative_path.as_posix(),
                data,
                hashlib.sha256(data).hexdigest(),
                selector,
            )
        )
    manifest = {
        "campaign_id": campaign.name,
        "files": [
            {"path": path, "sha256": digest, "selector": selector}
            for path, _, digest, selector in prepared
        ],
        "source_repo_head": source_head(repo),
    }

    context.mkdir(parents=True, exist_ok=True)
    staging = None
    backup = None
    preserve_backup = False
    try:
        staging = Path(tempfile.mkdtemp(prefix=f".{campaign.name}.staging-", dir=context))
        for relative_path, data, _, _ in prepared:
            destination = staging / relative_path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        (staging / "MANIFEST.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )

        if target.exists() or target.is_symlink():
            backup = new_empty_sibling(context, f".{campaign.name}.backup-")
            os.replace(target, backup)
        try:
            os.replace(staging, target)
            staging = None
        except BaseException as replacement_error:
            if backup is not None and not target.exists() and not target.is_symlink():
                try:
                    os.replace(backup, target)
                except BaseException as rollback_error:
                    preserve_backup = True
                    raise OSError(
                        f"replacement failed: {replacement_error}; "
                        f"rollback failed: {rollback_error}; "
                        f"recoverable backup: {backup}"
                    ) from rollback_error
                backup = None
            raise
    finally:
        if staging is not None and (staging.exists() or staging.is_symlink()):
            remove_path(staging)
        if (
            backup is not None
            and not preserve_backup
            and (backup.exists() or backup.is_symlink())
        ):
            remove_path(backup)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--campaign", required=True)
    parser.add_argument("--worktree", required=True)
    args = parser.parse_args(argv)
    try:
        materialize(args.campaign, args.worktree)
    except (MaterializeError, OSError, subprocess.SubprocessError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
