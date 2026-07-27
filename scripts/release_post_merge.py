#!/usr/bin/env python3
"""Validate a merged version PR without executing code from the merge commit."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path
import re
import subprocess
import sys

from release_validate import (
    ValidationError,
    extract_changelog,
    parse_tag,
    validate_project_version,
)


class PostMergeValidationError(RuntimeError):
    pass


@dataclass(frozen=True)
class PostMergeDecision:
    tag: str


def branch_to_tag(head_ref: str) -> str:
    if not head_ref.startswith("version/"):
        raise PostMergeValidationError(
            "release branch must use version/vMAJOR.MINOR.PATCH"
        )
    tag = head_ref.removeprefix("version/")
    try:
        parse_tag(tag)
    except ValidationError as error:
        raise PostMergeValidationError(
            f"malformed release branch {head_ref!r}: {error}"
        ) from error
    return tag


def classify_pull_request(event: dict) -> PostMergeDecision | None:
    """Return release metadata only for an eligible same-repository merge."""
    try:
        pull_request = event["pull_request"]
        repository = event["repository"]["full_name"]
        action = event["action"]
        merged = pull_request["merged"]
        base_ref = pull_request["base"]["ref"]
        head_ref = pull_request["head"]["ref"]
        head_repository = pull_request["head"]["repo"]["full_name"]
    except (KeyError, TypeError) as error:
        raise PostMergeValidationError(
            "pull request event is missing required release metadata"
        ) from error

    if (
        action != "closed"
        or merged is not True
        or base_ref != "main"
        or head_repository != repository
    ):
        return None
    if not head_ref.startswith("version/"):
        return None
    return PostMergeDecision(tag=branch_to_tag(head_ref))


def run_git(root: Path, *args: str, check: bool = True):
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def require_merge_commit(root: Path, merge_commit: str) -> None:
    if not re.fullmatch(r"[0-9a-f]{40}", merge_commit):
        raise PostMergeValidationError(
            "merge commit must be a full lowercase commit SHA"
        )
    try:
        checked_out = run_git(root, "rev-parse", "--verify", "HEAD").stdout.strip()
    except subprocess.CalledProcessError as error:
        raise PostMergeValidationError(
            f"could not resolve candidate HEAD: {error.stderr.strip()}"
        ) from error
    if checked_out != merge_commit:
        raise PostMergeValidationError(
            f"candidate HEAD {checked_out} is not merge commit {merge_commit}"
        )

    ancestry = run_git(
        root,
        "merge-base",
        "--is-ancestor",
        merge_commit,
        "refs/remotes/origin/main",
        check=False,
    )
    if ancestry.returncode == 1:
        raise PostMergeValidationError(
            f"merge commit {merge_commit} is not contained in current origin/main"
        )
    if ancestry.returncode != 0:
        raise PostMergeValidationError(
            "could not verify main ancestry: "
            f"{ancestry.stderr.strip() or ancestry.returncode}"
        )


def validate_candidate(root: Path, tag: str, merge_commit: str) -> None:
    try:
        release = parse_tag(tag)
        validate_project_version(root, release)
        extract_changelog(root, release)
    except ValidationError as error:
        raise PostMergeValidationError(str(error)) from error
    require_merge_commit(root, merge_commit)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--event", type=Path, required=True)
    parser.add_argument("--repository", type=Path, required=True)
    parser.add_argument("--merge-commit", required=True)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args(argv)

    try:
        event = json.loads(args.event.read_text(encoding="utf-8"))
        decision = classify_pull_request(event)
        if decision is None:
            raise PostMergeValidationError(
                "workflow invoked for an ineligible pull request"
            )
        event_merge_commit = event["pull_request"].get("merge_commit_sha")
        if event_merge_commit != args.merge_commit:
            raise PostMergeValidationError(
                "event merge commit does not match the checked-out candidate"
            )
        validate_candidate(
            args.repository.resolve(), decision.tag, args.merge_commit
        )
    except (OSError, json.JSONDecodeError, PostMergeValidationError) as error:
        print(f"post-merge release validation failed: {error}", file=sys.stderr)
        return 1

    output = f"tag={decision.tag}\ncommit={args.merge_commit}\n"
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as stream:
            stream.write(output)
    else:
        sys.stdout.write(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
