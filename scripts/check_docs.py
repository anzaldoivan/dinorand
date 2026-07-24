#!/usr/bin/env python3
"""Check the small, mechanical part of DinoRand's public documentation contract.

This deliberately does not try to score tone. It catches changelog structure and
the implementation vocabulary that most often leaks from research notes into
player-facing release notes. Human review remains responsible for clarity.
"""

from pathlib import Path
import os, re, subprocess
import sys
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parent.parent
CHANGELOG = ROOT / "CHANGELOG.md"

ALLOWED_SECTIONS = {
    "Added",
    "Changed",
    "Fixed",
    "Removed",
    "Withdrawn",
    "Withdrawn / Research",
    "Deprecated",
    "Security",
}

FORBIDDEN_RELEASE_PATTERNS = (
    (re.compile(r"\bcont\.\s*\d+\b", re.IGNORECASE), "RE continuation id"),
    (re.compile(r"\bK\d+\b"), "technical knowledge id"),
    (re.compile(r"\b0x[0-9a-f]+\b", re.IGNORECASE), "hex/offset literal"),
    (re.compile(r"\b(?:opcode|slot_data|VA)\b", re.IGNORECASE), "implementation field"),
    (re.compile(r"\b(?:EXE-SYMBOLS|STATIC-SCD-RE)\b", re.IGNORECASE), "RE registry"),
    (
        re.compile(r"\b[A-Z][A-Z0-9-]+-(?:PLAN|RCA)(?:\.md)?\b"),
        "technical record identifier",
    ),
    (
        re.compile(
            r"\b(?:PSX-verified|null-deref|code cave|native layout|region cache|pkgutil|RCA|byte-identical)\b",
            re.IGNORECASE,
        ),
        "implementation detail",
    ),
    (re.compile(r"docs/(?:decisions|reference)/", re.IGNORECASE), "internal decision/reference path"),
    (
        re.compile(r"\b[A-Z][A-Za-z0-9]*(?:Patch|Installer|Importer|Planner|Pass)\b"),
        "implementation class",
    ),
)

INLINE_LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
REFERENCE_LINK = re.compile(r"^\s*\[[^\]]+\]:\s*(\S+)", re.MULTILINE)

def markdown_anchors(text: str) -> set[str]:
    anchors, counts = set(), {}
    for line in text.splitlines():
        match = re.match(r"^#{1,6}\s+(.+?)\s*#*\s*$", line)
        if not match: continue
        title = re.sub(r"<[^>]+>", "", match.group(1)); title = re.sub(r"[`*_~]", "", title).strip().lower()
        slug = re.sub(r"[^\w\- ]", "", title, flags=re.UNICODE); slug = re.sub(r"\s+", "-", slug)
        count = counts.get(slug, 0); counts[slug] = count + 1; anchors.add(slug if not count else f"{slug}-{count}")
    return anchors

def tracked_public_files(root: Path) -> set[Path]:
    result = subprocess.run(["git", "-C", str(root), "ls-files", "-z"], check=True, stdout=subprocess.PIPE)
    return {
        Path(raw.decode())
        for raw in result.stdout.split(b"\0")
        if raw and (root / Path(raw.decode())).is_file()
    }

def check_markdown_links(root: Path, tracked: set[Path] | None = None) -> list[str]:
    tracked = {Path(p.as_posix()) for p in (tracked if tracked is not None else tracked_public_files(root))}; errors, cache = [], {}
    for relative in sorted(p for p in tracked if p.suffix.lower() == ".md"):
        source = root / relative
        try: text = source.read_text(encoding="utf-8")
        except OSError as error: errors.append(f"{relative}: tracked Markdown file cannot be read: {error}"); continue
        for match in list(INLINE_LINK.finditer(text)) + list(REFERENCE_LINK.finditer(text)):
            raw = match.group(1).strip(); raw = raw[1:-1] if raw.startswith("<") and raw.endswith(">") else raw; raw = raw.split(maxsplit=1)[0]; parsed = urlsplit(raw)
            if parsed.scheme or parsed.netloc or raw.startswith("/"): continue
            target = relative if not parsed.path else Path((relative.parent / unquote(parsed.path)).as_posix()); target = Path(os.path.normpath(target.as_posix())); line = text[:match.start()].count("\n") + 1
            if target not in tracked: errors.append(f"{relative}:{line}: relative Markdown target is not a tracked public file: {raw}"); continue
            if parsed.fragment:
                if target not in cache: cache[target] = markdown_anchors((root / target).read_text(encoding="utf-8"))
                if unquote(parsed.fragment).lower() not in cache[target]: errors.append(f"{relative}:{line}: Markdown fragment does not exist: {raw}")
    return errors


def section_lines(lines: list[str], section_name: str) -> tuple[int, list[str], list[str]]:
    headings = [(index, line) for index, line in enumerate(lines) if line.startswith("## [")]
    heading_pattern = re.compile(
        rf"^## \[{re.escape(section_name)}\](?:\s|$)"
    )
    matching = [(index, line) for index, line in headings if heading_pattern.match(line)]
    errors: list[str] = []

    if len(matching) != 1:
        errors.append(
            f"expected exactly one '## [{section_name}]' heading, found {len(matching)}"
        )
        return 0, [], errors

    start = matching[0][0]
    following = [index for index, _ in headings if index > start]
    end = following[0] if following else len(lines)
    return start, lines[start + 1 : end], errors


def check_changelog(section_name: str) -> list[str]:
    if not CHANGELOG.exists():
        return ["CHANGELOG.md does not exist"]

    lines = CHANGELOG.read_text(encoding="utf-8").splitlines()
    start, section, errors = section_lines(lines, section_name)
    if errors:
        return errors

    in_fence = False
    active_heading: str | None = None
    for offset, line in enumerate(section, start=start + 2):
        stripped = line.strip()
        if stripped.startswith("```"):
            in_fence = not in_fence
            continue
        if in_fence or not stripped:
            continue

        if line.startswith("### "):
            active_heading = line[4:].strip()
            if active_heading not in ALLOWED_SECTIONS:
                errors.append(
                    f"CHANGELOG.md:{offset}: unsupported '{section_name}' subsection '{active_heading}'"
                )
            continue

        if line.startswith("- "):
            if active_heading is None:
                errors.append(f"CHANGELOG.md:{offset}: bullet appears outside a subsection")
            elif not line.startswith("- **"):
                errors.append(
                    f"CHANGELOG.md:{offset}: top-level release bullets must begin with '- **'"
                )

    section_text = "\n".join(section)
    for pattern, description in FORBIDDEN_RELEASE_PATTERNS:
        for match in pattern.finditer(section_text):
            line_number = start + 2 + section_text[: match.start()].count("\n")
            errors.append(f"CHANGELOG.md:{line_number}: {description} '{match.group(0)}'")

    return errors


def main() -> int:
    section_name = "Unreleased"
    args = sys.argv[1:]
    if args == [] or args == ["--check"]:
        pass
    elif len(args) == 2 and args[0] == "--version":
        section_name = args[1]
    else:
        print(
            "usage: python3 scripts/check_docs.py [--check | --version X.Y.Z]",
            file=sys.stderr,
        )
        return 2

    errors = check_changelog(section_name)
    if section_name == "Unreleased": errors.extend(check_markdown_links(ROOT))
    if errors:
        print("check_docs: public documentation contract failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print(f"check_docs: OK (public changelog contract: {section_name}{', tracked Markdown links' if section_name == 'Unreleased' else ''})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
