#!/usr/bin/env python3
"""Generate / verify the numeric rollup in docs/reference/cross/progress/RE-PROGRESS.md.

The rollup counts ROT if hand-maintained, so — like scripts/gen_docs_index.py — they
are GENERATED. This script recomputes them straight from the source registries and
rewrites ONLY the block between the GENERATED:rollup markers. The prose and the
per-subsystem status matrices in the doc are hand-authored and left untouched.

    --apply   recompute the counts and rewrite the rollup block in place.
    --check   exit 1 if the block is stale (used to catch drift).

Every number here is reproducible by hand from the command shown in the doc's
footnotes; this script just runs the same greps in Python. It never invents a
number a grep can't reproduce.
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOC = os.path.join(ROOT, "docs/reference/cross/progress/RE-PROGRESS.md")
BEGIN = "<!-- GENERATED:rollup by scripts/gen_re_progress.py — do not hand-edit; run `python3 scripts/gen_re_progress.py --apply`. -->"
END = "<!-- /GENERATED:rollup -->"

# One reproducible rule per number. The grep equivalents live in the doc footnotes.
SYMBOL_ROW = re.compile(r'^\| `(0x|\[|scene|slot)')          # EXE-SYMBOLS address rows
K_ROW = re.compile(r'^\| K[0-9]+ ')                          # K## KNOWN rows


def count_lines(path, pattern):
    with open(path, encoding="utf-8") as fh:
        return sum(1 for line in fh if pattern.match(line))


def counts():
    d1_sym = count_lines(os.path.join(ROOT, "docs/reference/dc1/_registries/EXE-SYMBOLS.md"), SYMBOL_ROW)
    d2_sym = count_lines(os.path.join(ROOT, "docs/reference/dc2/_registries/EXE-SYMBOLS.md"), SYMBOL_ROW)
    k_known = count_lines(os.path.join(ROOT, "docs/reference/dc2/_registries/KNOWLEDGE-AND-QUESTIONS.md"), K_ROW)
    with open(os.path.join(ROOT, "tools/scd_re/scd_opcode_table.json"), encoding="utf-8") as fh:
        ops = json.load(fh)
    op_total = len(ops)
    op_semantic = sum(1 for v in ops.values() if v.get("kind") != "fixed")
    keys = sorted(int(k, 16) for k in ops)
    op_span = "0x%x–" % keys[0] + "0x%x" % keys[-1]
    return dict(d1_sym=d1_sym, d2_sym=d2_sym, k_known=k_known,
                op_total=op_total, op_semantic=op_semantic, op_span=op_span)


def render(c):
    """The rollup block. Subsystem tallies are NOT generated (judgment calls) — the
    per-game matrices below the block are their reproducible-by-reading source."""
    return "\n".join([
        BEGIN,
        "",
        "| Metric | DC1 (`DINO.exe`) | DC2 (`Dino2.exe`) |",
        "|---|---|---|",
        f"| **EXE symbols decoded** (address rows) [^sym] | **{c['d1_sym']}** | **{c['d2_sym']}** |",
        f"| **SCD opcode coverage** [^ops] | **{c['op_total']}/{c['op_total']}** census'd "
        f"({c['op_span']}); **{c['op_semantic']}** with in-table semantics | n/a — slot-5 VM ops "
        "tracked as EXE-SYMBOLS rows + K40/K41/K65 |",
        f"| **K## KNOWN** (byte-cited) [^k] | n/a — DC1 uses `cont.N` narrative | **{c['k_known']}** |",
        "",
        END,
    ])


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "--check"
    with open(DOC, encoding="utf-8") as fh:
        text = fh.read()
    if BEGIN not in text or END not in text:
        print(f"gen_re_progress: markers not found in {os.path.relpath(DOC, ROOT)}", file=sys.stderr)
        return 2
    block = render(counts())
    pre, rest = text.split(BEGIN, 1)
    _, post = rest.split(END, 1)
    new = pre + block + post
    if mode == "--apply":
        with open(DOC, "w", encoding="utf-8") as fh:
            fh.write(new)
        print("gen_re_progress: wrote rollup —", " ".join(f"{k}={v}" for k, v in counts().items()))
        return 0
    if new != text:
        print("gen_re_progress: rollup is stale — run "
              "'python3 scripts/gen_re_progress.py --apply'", file=sys.stderr)
        return 1
    print("gen_re_progress: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
