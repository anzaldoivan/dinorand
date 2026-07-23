#!/usr/bin/env python3
"""Generate data/dc1/ap-client-checks.json — the AP runtime client's per-location check
predicates (AP-CLIENT-PLAN.md §2).

DC1's engine keeps a per-pickup "taken" flag: the SCD 0x28-subtype-4 registration (DINO.exe
0x426C9C) suppresses a spawned pickup via GetFlag(7, word[rec+0x20]) for real item ids
(GetFlag(8, ·) for 0xff runtime-armed slots), and the AOT take handler (0x44A411, SetFlag sites
0x44A6A1/0x44A6B9) sets the same flag on pickup. word[rec+0x20] is the record's *take index* —
per-record, NOT the item id (the room-data `collected_flag: {group 8, bit=id}` guess predates
this decode; see EXE-SYMBOLS cont.80).

The vanilla take-index space is imperfect for AP checks: some records carry index 0 (never
suppressed — the item respawns and every take sets flag 7:0) and some indices are shared between
distinct AP locations. Because the AP install step rewrites these exact records anyway (id @+0x1c),
it can also REKEY +0x20 — so this generator classifies every AP location and emits:

  * predicate      — {kind:"flag", group:7, anyOf:[idx,…]} (any set bit == taken),
  * records        — [{room, rec, vanillaTake, take}] where `take` is the FINAL index the
                     installer writes at rec+0x20 (== vanillaTake when no rekey is needed),
  * excluded       — true for locations whose shared index is pinned by room-script references
                     (op-0x25/0x26 group-7 reads/writes; rekeying would break the script's
                     choreography). The distiller copies this into dc1_logic.json so the apworld
                     marks them LocationProgressType.EXCLUDED (filler only), and the client fires
                     every member of the shared group on the flip.
  * sharedWith     — the other member locations of an unsplit shared-flag group.

Classification rules (in order):
  1. poisoned  — a script WRITES one of the location's indices (op-0x25 g7): the flag can
     set/clear without a pickup → excluded, no rekey.
  2. clean     — all indices nonzero and shared with no other location: keep vanilla
     (script READS are fine — they don't fire the flag).
  3. rekeyable — every record's index is 0 or wholly unreferenced by scripts: assign one fresh
     index per location from the free pool (deterministic: sorted location name order,
     ascending free indices).
  4. pinned-shared — shared with another location on a script-READ index: excluded, no rekey.

Requires the DC1 install (english/Data room corpus) — run locally after a room-data/census
change, never on CI:

    python3 scripts/gen_ap_client_checks.py --apply   # rewrite data/dc1/ap-client-checks.json
    python3 scripts/gen_ap_client_checks.py --check   # verify the committed file is current

`scripts/gen_ap_logic.py --check` (CI) independently name-validates the committed file against
dc1_logic.json without needing the install.
"""
from __future__ import annotations

import json
import os
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "data" / "dc1" / "ap-client-checks.json"

sys.path.insert(0, str(REPO / "scripts"))
import gen_ap_logic as gal  # noqa: E402  (location set + names must match the distiller exactly)

sys.path.insert(0, str(REPO / "tools" / "scd_re"))

TAKE_OFF = 0x20          # word[rec+0x20] = take index (EXE-SYMBOLS 0x426C9C, cont.80)
ID_OFF = 0x1C
OP28_ITEM_SUBTYPE = 4
GROUP = 7                # real-id pickups check/set flag group 7 (0xff slots use 8 — not AP locations)
TRANSITION_RESERVED = {44}  # 7:44 = native stage-6 transition latch (EXE-SYMBOLS 0x44A411) — never allocate
# Indices the EXE sets NATIVELY from weapon OWNERSHIP, not from a pickup (0x441DFA/0x441E1B:
# owns Shotgun 0x01 → SetFlag(7,212), owns Grenade Gun 0x09 → SetFlag(7,138) — the vanilla
# "despawn the floor copy of an owned weapon" de-dup). A location left on one of these indices
# would FALSE-FIRE the moment AP grants that weapon → its records must always be rekeyed.
NATIVE_AUTOSET = {212, 138}


def _data_dir() -> Path:
    value = os.environ.get("DINORAND_DC1_DIR")
    if not value:
        env = REPO / ".env"
        if env.is_file():
            for raw in env.read_text(encoding="utf-8").splitlines():
                if raw.strip().startswith("DINORAND_DC1_DIR="):
                    value = raw.split("=", 1)[1].strip().strip('"').strip("'")
                    break
    if not value:
        raise SystemExit("gen_ap_client_checks: DINORAND_DC1_DIR is not configured — "
                         "this generator is install-gated and never runs on CI.")
    if os.name == "nt" and value.startswith("/mnt/") and len(value) > 6:
        value = f"{value[5].upper()}:/{value[7:]}"
    root = Path(value)
    for d in (root, root / "Data", root / "english" / "Data"):
        if d.is_dir() and d.name.lower() == "data":
            return d
    raise SystemExit("gen_ap_client_checks: DINORAND_DC1_DIR has no DC1 Data folder")


ROOM_FILE_RE = __import__("re").compile(r"^st([0-9a-c])([0-9a-f]{2})\.dat$")


def _room_files(data_dir: Path) -> dict[str, Path]:
    """room code (lowercase 4-hex) -> PRISTINE .dat path, case-insensitively
    (dc1-st502-case-glob-bug). A live install may be randomized (and passes that inject script
    records shift every rec_offset), so files in Data/.dinorand_backup take precedence."""
    out: dict[str, Path] = {}
    for d in (data_dir, data_dir / ".dinorand_backup"):  # backup LAST → wins
        if not d.is_dir():
            continue
        for p in d.iterdir():
            m = ROOM_FILE_RE.match(p.name.lower())
            if m:
                out[f"{int(m.group(1), 16):02x}{m.group(2)}"] = p
    return out


def _rdt(path: Path) -> bytes:
    import scd
    buf, _ = scd.get_rdt(str(path))
    if buf is None:
        raise SystemExit(f"gen_ap_client_checks: {path.name}: no RDT payload")
    return buf


def _script_refs() -> tuple[set[int], set[int]]:
    """(read-or-written, written) group-7 indices across the room-script corpus.
    op 0x26 = flag test, op 0x25 = flag set (group=b[1], index=b[2]); op 0x48 never targets g7."""
    from extract_logic import walk_ops
    read: set[int] = set()
    written: set[int] = set()
    for p in sorted(_room_files(_data_dir()).values()):
        try:
            buf = _rdt(p)
        except SystemExit:
            continue
        for _sub, _off, op, bs in walk_ops(buf):
            if op in (0x25, 0x26) and len(bs) >= 3 and bs[1] == GROUP:
                read.add(bs[2])
                if op == 0x25:
                    written.add(bs[2])
    return read, written


def _location_records() -> list[dict]:
    """The distiller's exact AP location set, each with its room-data record offsets + take words."""
    items = gal.load_items()
    m = gal.load_map()
    raw = json.loads((gal.DC1 / "room-data.json").read_text(encoding="utf-8"))
    files = _room_files(_data_dir())
    rdt_cache: dict[str, bytes] = {}

    # Index physical records by stable room+offset. Logical membership comes only from map.itemGroups.
    recs_by_physical: dict[tuple[str, int], dict] = {}
    for code, room in raw["rooms"].items():
        ic = room.get("item_control")
        if not ic:
            continue
        c = gal._code(code)
        for rec in ic["records"]:
            iid = gal._item_id(rec["item_id"])
            offset = int(rec["rec_offset"], 16)
            recs_by_physical[(c, offset)] = {"room": c, "rec": rec, "iid": iid}

    out = []
    for loc in gal.load_locations(items["names"], m["itemGroups"]):
        if loc["itemId"] == gal.EMPTY_SLOT_ID or loc["room"] not in m["regions"]:
            continue  # same filter as build_dc1
        rows = []
        for offset in loc["recordOffsets"]:
            r = recs_by_physical[(loc["room"], offset)]
            room = r["room"]
            if room not in rdt_cache:
                rdt_cache[room] = _rdt(files[room])
            buf = rdt_cache[room]
            off = int(r["rec"]["rec_offset"], 16)
            count = struct.unpack_from("<H", buf, off + 0x1E)[0]
            if not (buf[off] == 0x28 and buf[off + 2] == OP28_ITEM_SUBTYPE
                    and buf[off + ID_OFF] == r["iid"] and count == int(r["rec"]["count"])):
                raise SystemExit(f"gen_ap_client_checks: {room} rec {hex(off)} does not decode as "
                                 f"item id/count 0x{r['iid']:02x}/{r['rec']['count']} — "
                                 "room-data is stale vs the install")
            rows.append({"room": room, "rec": hex(off),
                         "opcode": buf[off], "subtype": buf[off + 2], "length": 44,
                         "itemId": r["iid"], "amount": count,
                         "geometry": list(struct.unpack_from("<8h", buf, off + 4)),
                         "take": struct.unpack_from("<H", buf, off + TAKE_OFF)[0]})
        out.append({"loc": loc, "records": rows})
    return out


def build() -> dict:
    read_refs, written_refs = _script_refs()
    locs = _location_records()

    takes_of = {L["loc"]["name"]: sorted({r["take"] for r in L["records"]}) for L in locs}
    # index -> location names using it (nonzero only)
    users: dict[int, list[str]] = {}
    for L in locs:
        for t in takes_of[L["loc"]["name"]]:
            if t:
                users.setdefault(t, []).append(L["loc"]["name"])

    all_vanilla = {r["take"] for L in locs for r in L["records"]}
    reserved = all_vanilla | read_refs | written_refs | TRANSITION_RESERVED | {0}
    free = [i for i in range(1, 256) if i not in reserved]

    # AP location ids come from the explicit append-only registry; sorting or renaming can no
    # longer renumber a published location.
    _item_ids, location_ids = gal._registered_ap_ids(
        set(gal.load_items()["names"].values()),
        [L["loc"] for L in locs],
        gal.load_ap_id_registry())
    ap_id = {L["loc"]["name"]: location_ids[L["loc"]["key"]] for L in locs}

    entries = []
    for L in sorted(locs, key=lambda L: L["loc"]["name"]):
        name = L["loc"]["name"]
        T = takes_of[name]
        shared = sorted({u for t in T if t for u in users[t] if u != name})
        pinned = [t for t in T if t in read_refs]
        poisoned = [t for t in T if t in written_refs]

        autoset = [t for t in T if t in NATIVE_AUTOSET]
        if poisoned:
            cls, final = "poisoned", None
        elif not shared and 0 not in T and not autoset:
            cls, final = "clean", None
        elif not pinned:
            cls, final = "rekeyed", free.pop(0)  # every record is 0 or unreferenced → safe to rekey
        else:
            cls, final = "pinned-shared", None

        records = [{"room": r["room"], "rec": r["rec"],
                    "expectedOpcode": r["opcode"], "expectedSubtype": r["subtype"],
                    "expectedLength": r["length"], "vanillaItemId": r["itemId"],
                    "vanillaAmount": r["amount"], "vanillaTake": r["take"],
                    "geometry": r["geometry"],
                    "take": final if final is not None else r["take"]}
                   for r in L["records"]]
        flags = sorted({r["take"] for r in records if r["take"]})
        entry = {
            "name": name,
            "apId": ap_id[name],
            "key": L["loc"]["key"],
            "logicalId": L["loc"]["key"],
            "room": L["loc"]["room"],
            "predicate": {"kind": "flag", "group": GROUP, "anyOf": flags},
            "records": records,
            "class": cls,
        }
        if cls in ("poisoned", "pinned-shared"):
            entry["excluded"] = True
            if shared:
                entry["sharedWith"] = shared
        entries.append(entry)

    return {
        "_generated_by": "scripts/gen_ap_client_checks.py",
        "_source": "DINO.exe 0x426C9C/0x44A411 take-flag decode (EXE-SYMBOLS cont.80) + "
                   "data/dc1/room-data.json + the english/Data room corpus",
        "version": 2,
        "game": "dc1",
        "flagGroup": GROUP,
        "locations": entries,
    }


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "--check"
    doc = build()
    text = json.dumps(doc, indent=1, ensure_ascii=False) + "\n"
    if mode == "--apply":
        OUT.write_text(text, encoding="utf-8", newline="\n")
        by_cls: dict[str, int] = {}
        for e in doc["locations"]:
            by_cls[e["class"]] = by_cls.get(e["class"], 0) + 1
        print(f"wrote {OUT.relative_to(REPO)}: {len(doc['locations'])} locations {by_cls}")
        return 0
    if not OUT.exists():
        print(f"STALE: {OUT.relative_to(REPO)} missing — run --apply", file=sys.stderr)
        return 1
    if OUT.read_text(encoding="utf-8") != text:
        print(f"STALE: {OUT.relative_to(REPO)} differs from regeneration — run --apply", file=sys.stderr)
        return 1
    print("ap-client-checks.json is current")
    return 0


if __name__ == "__main__":
    sys.exit(main())
