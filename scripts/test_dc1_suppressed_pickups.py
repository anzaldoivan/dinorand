"""Focused DC1 pickup-suppression contract tests."""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gen_ap_logic as ap
import gen_item_map as item_map


ROOT = Path(__file__).resolve().parent.parent
DC1 = ROOT / "data" / "dc1"
AID = "0303:0x4fa68"
PANEL = "0103:0x1292c"


def physical_ids(doc: dict) -> set[str]:
    return {
        physical_id
        for location in doc["locations"]
        for physical_id in location["physicalIds"]
    }


class Dc1SuppressedPickupTests(unittest.TestCase):
    def test_standalone_policy_pins_aid_without_changing_panel_key(self) -> None:
        room_data = json.loads((DC1 / "room-data.json").read_text(encoding="utf-8"))
        map_data = json.loads((DC1 / "map.json").read_text(encoding="utf-8"))
        generated, _ = item_map.regenerate_map(room_data, map_data)

        room = generated["rooms"]["0303"]
        aid_pins = [entry for entry in room.get("itemPriorities", [])
                    if AID in {f"0303:{offset}" for offset in entry["records"]}]
        self.assertEqual("Fixed", aid_pins[0]["priority"])
        self.assertNotIn(AID, {
            f"0303:{offset}"
            for entry in room.get("scatterTargets", [])
            for offset in entry["records"]
        })

        panel_room = generated["rooms"]["0103"]
        self.assertNotIn(PANEL, {
            f"0103:{offset}"
            for entry in panel_room.get("itemPriorities", [])
            for offset in entry["records"]
        })

    def test_ap_logic_and_client_checks_omit_aid_but_retain_panel_key(self) -> None:
        logic = ap.build_dc1()
        checks = json.loads((DC1 / "ap-client-checks.json").read_text(encoding="utf-8"))

        self.assertNotIn(AID, physical_ids(logic))
        self.assertIn(PANEL, physical_ids(logic))
        check_ids = {
            f"{record['room']}:{record['rec']}"
            for location in checks["locations"]
            for record in location["records"]
        }
        self.assertNotIn(AID, check_ids)
        self.assertIn(PANEL, check_ids)

        registry = json.loads((DC1 / "ap-id-registry.json").read_text(encoding="utf-8"))
        self.assertEqual(
            230817833,
            next(row["id"] for row in registry["locations"]
                 if row["key"] == "0303:21:512,-2048"),
        )


if __name__ == "__main__":
    unittest.main()
