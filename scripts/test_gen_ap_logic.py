"""Focused contract tests for the generated Dino Crisis 2 AP logic."""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gen_ap_logic as ap


class Dc2LogicV2Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.data = ap.build_dc2()
        cls.sources = json.loads(
            (ap.DC2 / "item-sources.json").read_text(encoding="utf-8")
        )["sources"]
        cls.guards = json.loads(
            (ap.DC2 / "door-guards.json").read_text(encoding="utf-8")
        )

    def test_uses_physical_campaign_contract(self) -> None:
        data = self.data
        self.assertEqual(2, data["version"])
        self.assertEqual("physical", data["topology"])
        self.assertEqual("101", data["startRoom"])
        self.assertEqual("504", data["goalRoom"])
        self.assertEqual({"608", "707", "905", "906"}, set(data["excludedRooms"]))
        self.assertTrue(set(data["excludedRooms"]).isdisjoint(data["regions"]))

    def test_locations_consume_ap_availability_not_writer_eligibility(self) -> None:
        available = {
            source["source_id"]
            for source in self.sources
            if source.get("eligible_item_rewrite")
            and source["ap_availability"]["disposition"] == "unconditional"
        }
        # Of the two fixture-modelable sources, only flag(6,59) has a published producer
        # (ST701 r3). flag(6,109) has none and must remain explicitly excluded.
        available.add("ST705:r0:op35@0x5892")
        locations = self.data["locations"]
        self.assertEqual(available, {loc["sourceId"] for loc in locations})
        self.assertEqual(42, len(locations))
        self.assertFalse(any("op31" in loc["sourceId"] for loc in locations))
        self.assertEqual(len(locations), len({loc["apId"] for loc in locations}))
        self.assertEqual(len(locations), len({loc["name"] for loc in locations}))

        gated = next(loc for loc in locations if loc["sourceId"] == "ST705:r0:op35@0x5892")
        self.assertEqual(["701"], gated["requiresRooms"])
        self.assertNotIn(
            "ST304:r0:op35@0x7d7c",
            {loc["sourceId"] for loc in locations},
        )
        unresolved = next(
            row for row in self.data["exclusions"]
            if row["sourceId"] == "ST304:r0:op35@0x7d7c"
        )
        self.assertEqual("unresolved_story_flag_producer", unresolved["reason"])

    def test_pool_conserves_location_catalog_multiset(self) -> None:
        placed = sorted(loc["itemId"] for loc in self.data["locations"])
        pooled = sorted(self.data["items"]["poolItemIds"])
        self.assertEqual(placed, pooled)
        self.assertEqual([0x2E], self.data["items"]["progressionItemIds"])
        self.assertEqual([0x33, 0x34], self.data["items"]["optionalFixedItemIds"])
        self.assertNotIn(0x33, pooled)
        self.assertNotIn(0x34, pooled)

    def test_locations_and_items_publish_writer_compatibility_classes(self) -> None:
        item_classes = {
            int(item_id): rewrite_class
            for item_id, rewrite_class in self.data["items"]["rewriteClasses"].items()
        }
        self.assertEqual("health", item_classes[0x1A])
        self.assertNotIn(0x1E, item_classes)
        self.assertEqual("generic_key", item_classes[0x2E])
        self.assertEqual("special_key_2f", item_classes[0x2F])
        for location in self.data["locations"]:
            self.assertEqual(
                item_classes[location["itemId"]],
                location["rewriteClass"],
                location["sourceId"],
            )

    def test_nonprogression_temporary_ownership_items_have_fixed_placement_classes(self) -> None:
        self.assertEqual(
            [0x22, 0x23, 0x2B],
            self.data["items"]["fixedLifecycleItemIds"],
        )
        in_pool = {
            location["itemId"]: location
            for location in self.data["locations"]
            if location["itemId"] in {0x22, 0x2B}
        }
        self.assertEqual({0x22, 0x2B}, set(in_pool))
        self.assertEqual("fixed_lifecycle_22", in_pool[0x22]["placementClass"])
        self.assertEqual("fixed_lifecycle_2b", in_pool[0x2B]["placementClass"])
        gas = next(location for location in self.data["locations"] if location["itemId"] == 0x2E)
        self.assertEqual("generic_key", gas["placementClass"])

    def test_all_conditional_commits_have_one_disposition(self) -> None:
        rows = self.data["conditionalCommitDispositions"]
        self.assertEqual(91, len(rows))
        self.assertEqual(91, len({row["id"] for row in rows}))
        self.assertEqual(27, sum(row["kind"] == "door" for row in rows))
        item_rows = [row for row in rows if row["kind"] == "item"]
        self.assertEqual(64, len(item_rows))
        self.assertTrue(all(
            row["disposition"] == "excluded_sat1_physical_trigger"
            for row in item_rows
        ))

    def test_door_dispositions_preserve_resolved_native_predicates(self) -> None:
        dispositions = {
            row["id"]: row
            for row in self.data["conditionalCommitDispositions"]
            if row["kind"] == "door"
        }
        for guard in self.guards["door_guards"]:
            identity = (
                f"ST{guard['room']}:door@{guard['commit_off']}->ST{guard['dest_id']}"
            )
            self.assertEqual(
                [clause["expr"] for clause in guard["guards"]],
                dispositions[identity]["nativeConditions"],
                identity,
            )
        item_dispositions = {
            row["id"]: row
            for row in self.data["conditionalCommitDispositions"]
            if row["kind"] == "item"
        }
        for guard in self.guards["item_guards"]:
            identity = f"ST{guard['room']}:item@{guard['commit_off']}"
            self.assertEqual(
                [clause["expr"] for clause in guard["guards"]],
                item_dispositions[identity]["nativeConditions"],
                identity,
            )

    def test_gas_mask_gate_occurs_exactly_once(self) -> None:
        gas_edges = [
            edge for edge in self.data["edges"]
            if 0x2E in edge["requiresItems"]
        ]
        self.assertEqual(1, len(gas_edges))
        self.assertEqual(("10D", "10F"), (gas_edges[0]["from"], gas_edges[0]["to"]))

        dispositions = [
            row for row in self.data["conditionalCommitDispositions"]
            if row["disposition"] == "modeled_item_gate"
        ]
        self.assertEqual(1, len(dispositions))
        self.assertEqual("ST10D:door@0xe4c0->ST10F", dispositions[0]["id"])

    def test_guarded_finale_commit_is_the_victory_contract(self) -> None:
        self.assertEqual(
            {
                "kind": "guarded_door_commit",
                "from": "503",
                "to": "504",
                "commitOff": "0x15f56",
            },
            self.data["victory"],
        )
        finale = [
            edge for edge in self.data["edges"]
            if (edge["from"], edge["to"]) == ("503", "504")
        ]
        self.assertEqual(1, len(finale))

    def test_exclusions_cover_every_non_location_source(self) -> None:
        included = {loc["sourceId"] for loc in self.data["locations"]}
        excluded = {row["sourceId"] for row in self.data["exclusions"]}
        self.assertEqual(
            {source["source_id"] for source in self.sources},
            included | excluded,
        )
        self.assertFalse(included & excluded)

    def test_build_is_deterministic_and_beatable(self) -> None:
        self.assertEqual(self.data, ap.build_dc2())
        held = set(self.data["items"]["progressionItemIds"])
        reachable = ap._reachable_rooms(self.data, held)
        self.assertIn(self.data["goalRoom"], reachable)
        self.assertTrue({loc["room"] for loc in self.data["locations"]} <= reachable)
        self.assertTrue(ap._forward_fill_beatable(self.data))


if __name__ == "__main__":
    unittest.main()
