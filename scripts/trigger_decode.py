"""Render human-readable trigger summaries over the decoded spawn-control layer.

DOWNSTREAM of spawn_catalog.py. Reads ONLY data/dc1/room-data.json (the already-decoded
`spawn_control.records[]`) + data/dc1/flag-labels.json (authored event names) — it does NOT
touch the game .dat files, so `--check` runs in CI without the install.

For every spawn_control record it DERIVES three fields and writes them at the end of the record:
  - logic       : a deterministic debug trace (opcodes/offsets/handlers/flags)
  - summary      : a structured {trigger, condition, condition_flag, effect, oneShot, plain} object
  - confidence   : confirmed | inferred | unknown  (per the rule table in derive_confidence)
and one top-level `_flag_glossary` mapping each gate flag (group:index) to its authored label
plus its DERIVED set_by list.

THE CONTRACT (enforced by --check; wire into the test suite):
  C1 every gate.flag referenced by a record is present in _flag_glossary
  C2 every glossary flag has an authored label in flag-labels.json
  C3 glossary[k].set_by == union of gate.set_by across all records referencing k
  C4 summary.condition_flag (when set) is a glossary key
  C5 confidence matches derive_confidence()
  C7 IDEMPOTENCE: re-running --apply would reproduce the file byte-for-byte
     (so summary/logic/glossary cannot be hand-edited without --check failing)

Usage:
  python scripts/trigger_decode.py            # dry-run: print the derived blocks
  python scripts/trigger_decode.py --apply    # write summary/logic/confidence + glossary
  python scripts/trigger_decode.py --check     # CI gate: assert file == regenerated + C1..C7
"""
import sys, os, json

_ROOT = os.path.join(os.path.dirname(__file__), "..", "data", "dc1")
ROOMDATA_FP = os.path.join(_ROOT, "room-data.json")
LABELS_FP = os.path.join(_ROOT, "flag-labels.json")

CONTROL_BLOCKS = ("spawn_control", "item_control")  # enemy + item placement layers
DERIVED_REC_KEYS = ("summary", "logic", "confidence")
# record-level fields a prior pass (incl. the hand POC) may have left behind; popped before rebuild
LEGACY_REC_KEYS = ("humanLogic",)


def _flag_key(flag):
    return f"{flag['group']}:{flag['index']}" if flag else None


def load_labels():
    return json.load(open(LABELS_FP, encoding="utf-8")).get("labels", {})


# ---- derivations -----------------------------------------------------------

def self_latch_subs(records, room_id):
    """Subs of THIS room that set a gate flag (the room self-latches its own encounter)."""
    subs = set()
    for r in records:
        for sb in (r.get("gate") or {}).get("set_by", []) or []:
            if sb.get("room") == room_id:
                subs.add(sb.get("sub"))
    return subs


def is_room_native(records):
    """Native staging: all records are unconditional 0x59 relocs with no 0x20 placement
    (mirrors spawn_catalog.build_block's `native`). The functioning enemy is engine-installed."""
    return bool(records) and all(r["opcode"] == "0x59" and r["dominated"] for r in records)


def derive_confidence(rec, room_native):
    """unresolved trigger -> unknown; pure native reloc -> inferred; else confirmed."""
    if (rec.get("trigger") or {}).get("kind") == "unresolved":
        return "unknown"
    if room_native:
        return "inferred"
    return "confirmed"


def derive_oneshot(rec, latch_subs):
    """True iff this record's spawn is governed by a flag THIS room sets itself:
    a self-latched gated init record, or the event sub that does the latching."""
    gate = rec.get("gate") or {}
    if gate.get("type") == "test_branch" and gate.get("flag"):
        if any(sb.get("room") == _self_room for sb in gate.get("set_by", []) or []):
            return True
    if rec["sub"] in latch_subs and rec["sub"] != 0:
        return True
    return False


def _is_item(rec):
    return "item_id" in rec


def render_logic(rec):
    op = rec["opcode"]
    if _is_item(rec):
        parts = [f"0x28 item @{rec['rec_offset']} in sub{rec['sub']} ({rec['placement_path']}); "
                 f"id={rec['item_id']} ({rec['item_name']}) count={rec['count']}; "
                 f"reachable={rec['reachable']} dominated={rec['dominated']}"]
    else:
        kind = "reloc" if op == "0x59" else "placement"
        parts = [f"{op} {kind} @{rec['rec_offset']} in sub{rec['sub']} ({rec['spawn_path']}); "
                 f"reachable={rec['reachable']} dominated={rec['dominated']}"]
    gate = rec.get("gate") or {}
    if gate.get("type") == "test_branch":
        fl = gate.get("flag")
        flag_s = f"{fl['group']}:{fl['index']}" if fl else "?"
        sb = gate.get("set_by", []) or []
        set_s = ", ".join(f"{x['room']}/sub{x['sub']}={x['value']}" for x in sb) or "(none)"
        parts.append(f"gate: {gate.get('test_op')} {gate.get('predicate')} @{gate.get('test_offset')}"
                     f" -> 0x0e {gate.get('branch_sense')} @{gate.get('branch_offset')};"
                     f" flag {flag_s} set_by [{set_s}]")
    elif gate.get("type") == "branch_only":
        parts.append(f"gate: branch_only @{gate.get('branch_offset')} ({gate.get('predicate')})")
    elif gate.get("type") == "unconditional":
        parts.append("gate: unconditional (post-dominates sub entry)")
    trig = rec.get("trigger") or {}
    if trig.get("kind") == "aot_zone":
        aoffs = ", ".join(a["aot_offset"] for a in trig.get("aots", []))
        parts.append(f"trigger: 0x28 subtype-3 enter-zone @[{aoffs}] -> run sub{rec['sub']}")
    elif trig.get("kind") in ("init_gosub", "gosub"):
        parts.append(f"trigger: gosub from sub{trig.get('called_from_subs')}")
    elif trig.get("kind") == "unresolved":
        parts.append("trigger: no static invoker (vestigial/cutscene?)")
    if _is_item(rec):
        cf = rec.get("collected_flag") or {}
        if cf.get("bit") is not None:
            parts.append(f"collected_flag GetFlag(8,{cf['bit']})")
    else:
        kf = rec.get("kill_flag") or {}
        if kf.get("bit") is not None:
            parts.append(f"kill_flag GetFlag(4,{kf['bit']})")
    return ". ".join(parts) + "."


def render_summary(rec, room_id, room_name, labels, latch_subs, room_native):
    species = rec.get("species") or "enemy"
    gate = rec.get("gate") or {}
    trig = rec.get("trigger") or {}
    one_shot = derive_oneshot(rec, latch_subs)

    # trigger phrase
    tk = trig.get("kind")
    if tk in ("init", "init_gosub"):
        trigger = "room entry"
    elif tk == "aot_zone":
        trigger = f"entering the {room_name} trigger zone"
    elif tk == "gosub":
        trigger = "an in-room event"
    elif tk == "unresolved":
        trigger = "no known trigger"
    else:
        trigger = "room entry"

    # condition phrase (+ machine join key)
    flag = gate.get("flag")
    condition_flag = _flag_key(flag)
    if gate.get("type") == "test_branch" and flag:
        label = labels.get(condition_flag, {}).get("label", f"flag {condition_flag}")
        sense = gate.get("branch_sense")
        if sense == "skip-if-true":
            condition = f"only until {label} has happened"
        elif sense == "skip-if-false":
            condition = f"only after {label}"
        else:
            condition = f"gated on {label} ({sense})"
    elif gate.get("type") == "branch_only":
        condition = "gated by an unresolved predicate"
    else:
        condition = "none"

    is_item = _is_item(rec)
    label = labels.get(condition_flag, {}).get("label", f"flag {condition_flag}") if flag else None
    sense = gate.get("branch_sense")

    if is_item:
        empty = rec.get("item_id") == "0xff"
        item = rec.get("item_name") or "item"
        cnt = rec.get("count") or 1
        qty = f" (x{cnt})" if cnt and cnt > 1 else ""
        if empty:
            effect = "reserve a runtime-armed item slot (id assigned at runtime)"
            if gate.get("type") == "test_branch" and flag:
                when = (f"while {label} has not happened" if sense == "skip-if-true"
                        else f"after {label}" if sense == "skip-if-false"
                        else f"gated on {label}")
                plain = (f"A runtime-armed item slot here (its item id is filled at runtime, not "
                         f"fixed in the file), present only {when}.")
            else:
                plain = ("A runtime-armed item slot here — its item id is filled at runtime, "
                         "not fixed in the file.")
        else:
            effect = f"place the {item}{qty}"
            if tk == "unresolved":
                plain = (f"This {item} placement sits in a subroutine with no statically-resolved "
                         f"invoker, so whether or when it appears is unknown (possibly vestigial, or "
                         f"placed by a path the static decoder does not model).")
            elif gate.get("type") == "test_branch" and flag:
                if sense == "skip-if-true":
                    plain = f"Place the {item} here only while {label} has not happened."
                elif sense == "skip-if-false":
                    plain = f"Place the {item} here only after {label}."
                else:
                    plain = f"Place the {item} here, gated on {label}."
            elif tk == "aot_zone":
                plain = f"When you enter the {room_name} trigger zone, place the {item}."
            else:  # unconditional / no gate
                plain = f"The {item} is here on every visit (until you pick it up)."
        return {
            "trigger": trigger, "condition": condition, "condition_flag": condition_flag,
            "effect": effect, "oneShot": one_shot, "plain": plain,
        }

    # ---- enemy ----
    if room_native:
        effect = f"stage the {species} model (functioning enemy is engine-spawned)"
    else:
        effect = f"spawn a {species}"

    if room_native:
        plain = (f"On load, {room_name} relocates the {species} model, but the functioning "
                 f"{species} is spawned natively by the engine, not by this record.")
    elif tk == "unresolved":
        plain = (f"This {species} placement sits in a subroutine with no statically-resolved "
                 f"invoker, so whether or when it spawns is unknown (possibly vestigial, or "
                 f"triggered by a path the static decoder does not model).")
    elif gate.get("type") == "test_branch" and flag:
        if sense == "skip-if-true":
            plain = (f"Spawn a {species} on entry only until {label} has happened; "
                     f"afterwards it never spawns here again.")
        elif sense == "skip-if-false":
            plain = (f"Spawn a {species} only after {label}; otherwise this spot stays empty.")
        else:
            plain = f"Spawn a {species} gated on {label}."
    elif tk == "aot_zone":
        plain = f"When you enter the {room_name} trigger zone, {effect}."
        if one_shot:
            plain += " This is a one-shot encounter."
    else:  # unconditional init
        plain = f"Every time you enter {room_name}, {effect} (until it has been killed)."

    return {
        "trigger": trigger,
        "condition": condition,
        "condition_flag": condition_flag,
        "effect": effect,
        "oneShot": one_shot,
        "plain": plain,
    }


# ---- glossary --------------------------------------------------------------

def build_glossary(doc, labels):
    """Top-level _flag_glossary: per referenced gate flag, authored label + DERIVED set_by."""
    refs = {}  # key -> list of set_by entries (deduped)
    for room in doc["rooms"].values():
        for block in CONTROL_BLOCKS:
            for rec in (room.get(block) or {}).get("records", []):
                gate = rec.get("gate") or {}
                fl = gate.get("flag")
                if not fl:
                    continue
                key = _flag_key(fl)
                bucket = refs.setdefault(key, [])
                for sb in gate.get("set_by", []) or []:
                    if sb not in bucket:
                        bucket.append(sb)
    gloss = {}
    for key in sorted(refs, key=lambda k: (int(k.split(":")[0]), int(k.split(":")[1]))):
        lab = labels.get(key)
        gloss[key] = {
            "label": lab["label"] if lab else None,
            "event_room": (lab or {}).get("event_room"),
            "set_by": refs[key],
        }
    return gloss


# ---- apply / check ---------------------------------------------------------

_self_room = None  # set per-room during rendering (used by derive_oneshot)


def regenerate(doc, labels):
    """Return a NEW doc with summary/logic/confidence on every record and a top-level
    _flag_glossary. Pure function of (doc, labels) -> doc."""
    global _self_room
    out = json.loads(json.dumps(doc))  # deep copy
    # glossary first (independent of per-record rendering)
    glossary = build_glossary(out, labels)
    # insert _flag_glossary as the first key after _source-style preamble, before "rooms"
    rebuilt = {}
    for k, v in out.items():
        if k == "rooms":
            rebuilt["_flag_glossary"] = glossary
        if k == "_flag_glossary":
            continue  # drop any prior copy; re-inserted above
        rebuilt[k] = v
    if "_flag_glossary" not in rebuilt:  # no "rooms"? (shouldn't happen) — append
        rebuilt["_flag_glossary"] = glossary
    out = rebuilt

    for room_id, room in out["rooms"].items():
        room_name = room.get("name", room_id)
        _self_room = room_id
        for block in CONTROL_BLOCKS:
            blk = room.get(block)
            if not blk:
                continue
            records = blk.get("records", [])
            latch = self_latch_subs(records, room_id)
            # native staging only applies to the enemy block
            room_native = is_room_native(records) if block == "spawn_control" else False
            new_records = []
            for rec in records:
                base = {k: v for k, v in rec.items()
                        if k not in DERIVED_REC_KEYS and k not in LEGACY_REC_KEYS}
                base["summary"] = render_summary(rec, room_id, room_name, labels, latch, room_native)
                base["logic"] = render_logic(rec)
                base["confidence"] = derive_confidence(rec, room_native)
                new_records.append(base)
            blk["records"] = new_records
    _self_room = None
    return out


def serialize(doc):
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def integrity_violations(doc, labels):
    """C1..C5 referential rules (C7 idempotence is checked separately by byte-compare)."""
    v = []
    gloss = doc.get("_flag_glossary", {})
    derived_gloss = build_glossary(doc, labels)
    # C1 + C3: every referenced flag in glossary, with matching set_by
    for key, exp in derived_gloss.items():
        if key not in gloss:
            v.append(f"C1: gate flag {key} referenced by a record is missing from _flag_glossary")
            continue
        if gloss[key].get("set_by") != exp["set_by"]:
            v.append(f"C3: _flag_glossary[{key}].set_by != union of gate.set_by")
    # C2: every glossary flag has an authored label
    for key, g in gloss.items():
        if not g.get("label"):
            v.append(f"C2: glossary flag {key} has no authored label in flag-labels.json (add one)")
    # C4 + C5 per record (both blocks)
    for room_id, room in doc["rooms"].items():
        for block in CONTROL_BLOCKS:
            blk = room.get(block)
            if not blk:
                continue
            records = blk.get("records", [])
            room_native = is_room_native(records) if block == "spawn_control" else False
            for rec in records:
                cf = (rec.get("summary") or {}).get("condition_flag")
                if cf is not None and cf not in gloss:
                    v.append(f"C4: {room_id} {rec['rec_offset']} condition_flag {cf} not in glossary")
                exp_conf = derive_confidence(rec, room_native)
                if rec.get("confidence") != exp_conf:
                    v.append(f"C5: {room_id} {rec['rec_offset']} confidence "
                             f"{rec.get('confidence')!r} != derived {exp_conf!r}")
    return v


def cmd_apply():
    doc = json.load(open(ROOMDATA_FP, encoding="utf-8"))
    labels = load_labels()
    new = regenerate(doc, labels)
    text = serialize(new)
    json.loads(text)  # validate
    open(ROOMDATA_FP, "w", encoding="utf-8", newline="\n").write(text)
    n = sum(len((r.get(b) or {}).get("records", []))
            for r in new["rooms"].values() for b in CONTROL_BLOCKS)
    print(f"applied: {n} records summarized; {len(new['_flag_glossary'])} glossary flags; JSON valid")


def cmd_check():
    on_disk_text = open(ROOMDATA_FP, encoding="utf-8").read()
    doc = json.loads(on_disk_text)
    labels = load_labels()
    fails = []
    # C7 idempotence: regenerating must reproduce the file byte-for-byte
    regen_text = serialize(regenerate(doc, labels))
    if regen_text != on_disk_text:
        fails.append("C7: room-data.json is not in sync with the generator "
                     "(run `trigger_decode.py --apply`). summary/logic/glossary must not be hand-edited.")
    # C1..C5 on the regenerated model (so integrity is checked even pre-apply)
    fails.extend(integrity_violations(json.loads(regen_text), labels))
    if fails:
        print("CONTRACT VIOLATIONS:")
        for f in fails:
            print("  -", f)
        sys.exit(1)
    print("trigger-decode contract OK (C1..C7)")


def cmd_emit():
    doc = json.load(open(ROOMDATA_FP, encoding="utf-8"))
    labels = load_labels()
    new = regenerate(doc, labels)
    preview = {"_flag_glossary": new["_flag_glossary"],
               "rooms": {rid: {"spawn_control": r["spawn_control"]}
                         for rid, r in new["rooms"].items() if r.get("spawn_control")}}
    print(json.dumps(preview, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    arg = sys.argv[1] if len(sys.argv) > 1 else "--emit"
    if arg == "--apply":
        cmd_apply()
    elif arg == "--check":
        cmd_check()
    else:
        cmd_emit()
