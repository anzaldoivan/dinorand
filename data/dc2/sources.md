# Dino Crisis 2 dataset — external sources

Every external source consulted while building `data/dc2/`, with what it yielded and
whether it was usable. Byte-confirmed facts are NOT listed here (they come from the game
files, documented in [`README.md`](README.md) and `docs/dc2/`).

| Source | URL | Yielded | Status |
|---|---|---|---|
| GameFAQs walkthrough (JL Lee, v1.0, 29/11/2000) | cached locally at `docs/_reference-dumps/DC2-ROOMS-FAQ.md` (originally GameFAQs PS Dino Crisis 2) | Weapon list + stats, enemy roster (combo + Dino Colosseum tables), ~72 named areas in play order, key items, 10 Dino File locations | **used — primary guide source** |
| Wikipedia — Dino Crisis 2 | https://en.wikipedia.org/wiki/Dino_Crisis_2 | Cross-confirmed 10-creature roster; 6-zone area progression (Jungle/Military → Research → Offshore/Underwater → Volcano/City → Edward City → Missile Silo); "Extinct Points" currency | **used — cross-confirmation** |
| Dino Crisis Wiki (Fandom) — DC2 creatures category (rendered page) | https://dinocrisis.fandom.com/wiki/Category:Dino_Crisis_2_creatures | (intended: canonical creature list) | **blocked — HTTP 403** (Cloudflare bot gate on the human-facing page) |
| Dino Crisis Wiki (Fandom) — MediaWiki API (category list) | https://dinocrisis.fandom.com/api.php?action=query&list=categorymembers&cmtitle=Category:Dino_Crisis_2_creatures&cmlimit=500&format=json | Canonical DC2 creature list incl. four raptor colour variants (Brown/Blue/Green/Red Raptor) not in the FAQ; upgraded most roster entries to `confirmed` | **used — the `api.php` endpoint is NOT gated like the rendered page** |
| Dino Crisis Wiki — per-creature pages (`action=parse&prop=wikitext`) | https://dinocrisis.fandom.com/api.php?action=parse&page=<Creature>&prop=wikitext&format=json | Sizes/body-types/roles/HP for all 11 creatures + 4 raptor variants. Pinned: Velociraptor = one creature in colour variants (firms E30/E31/E32 grouping); Compsognathus ~0.5 m (firms EA0); **Blue Raptor = the FAQ's "Super Raptor"** — optional 20+ no-damage-combo spawn, pairs, not on Easy, 7,000 EP/kill (cross-matches the FAQ combo table) | **used — fed enemies.json roster/mapping/raptorVariants** |
| GameFAQs — Weapons/Tools guide (Nemesis) | https://gamefaqs.gamespot.com/ps/913995-dino-crisis-2/faqs/8878 | (intended: full weapon/item catalog) | **blocked — HTTP 403**; superseded by the locally cached FAQ |
| GameFAQs — DC2 FAQ index (PS) | https://gamefaqs.gamespot.com/ps/913995-dino-crisis-2/faqs | List of additional guides (Ghidrah, Minesweeper_, RIrawan, DesertEagle9753) | reference only — not fetched (host blocks WebFetch) |
| GameFAQs — DC2 FAQ index (PC) | https://gamefaqs.gamespot.com/pc/582190-dino-crisis-2/faqs | PC-version guide list | reference only |
| Capcom Database (Fandom) | https://capcom.fandom.com/wiki/Dino_Crisis_2 | (general overview) | not fetched |
| IMFDB — Dino Crisis 2 | https://www.imfdb.org/wiki/Dino_Crisis_2 | (real-world weapon identifications) | reference only |
| DC2-Mod-SDK (Gemini-Loboto3 / "Classic REbirth") | https://github.com/Gemini-Loboto3/DC2-Mod-SDK | Repo is a stub ("nothing really here for now") — only extractor C++ source; **no** ST-id→area mapping data | checked — no room-name data |
| Dino Crisis Wiki — Research Facility / 3rd Energy Reactor pages (api.php) | https://dinocrisis.fandom.com/api.php?action=parse&page=Research%20Facility&prop=wikitext | Confirm zone existence/spelling (Research Facility → "Third Energy Research and Development Facility"); do **not** enumerate rooms per area | used — zone-level only |
| GameFAQs walkthrough index (area-progression cross-check) | https://gamefaqs.gamespot.com/ps/913995-dino-crisis-2/faqs | Canonical area order: Jungle → Military Facility → Research Facility → Patrol Ship → 3rd Energy Facility/Reactor → Edward City (Triceratops chase) → Missile Silo | used — confirms zone order |
| **XGameMania — ディノクライシス2攻略 MAP** (Japanese) | https://xgamemania.com/dino/2/map/1.html … /10.html | **Per-zone ROOM NAMES in Japanese** (e.g. 基地・エントランス) for all 10 zones — the source for `map.json` room lists. Also item/dino placement per room. | **used — primary room-name source** |
| XGameMania — area chart / item / dino-file pages | https://xgamemania.com/dino/2/index.html | Zone progression, weapon/tool/file lists, DINO FILE contents | reference / cross-check |

## Byte source for zone names (not a URL)

`Dino2.exe` (rebirth build, `4249140_DinoCrisis2/rebirth/Dino2.exe`) contains an **area-name
string table** at file offset `0x118e90` / VA `0x00733e90`: `JUNGLE`, `MILITARY FACILITY`,
`RESEARCH FACILITY`, `PATROL SHIP`, `3RD ENERGY FACILITY`, `3RD ENERGY REACTOR`,
`AREAWAY TO CITY`, `EDWARD CITY`, `HABITAT SUPPORT FACILITY`, `MISSILE SILO` — the **10
canonical zone names** (exact spelling), which is why `map.json` zone names are tagged
`confirmed`. The table is referenced as a stack-built array in code (`mov [esp+off], <ptr>`),
not a clean stage-indexed data table, so it confirms the **names** but not a stage→zone index.

**Japanese room names are NOT in the game data.** Searched every EXE (english/japanese/rebirth)
and all 2071 `.DAT` files for the Shift-JIS bytes of area kanji (基地, 施設, 研究,
エントランス, ジャングル, 通路, ホール, エネルギー, ミサイル, …): **zero** multi-character
hits (only coincidental single-kanji bytes inside model/geometry data). DC2's on-screen area
name (e.g. 基地・エントランス) is baked into the pre-rendered `.DBS` background graphics, not
stored as an engine string — hence the room names come from XGameMania (a guide), not bytes.

## Notes on reliability

- **GameFAQs and Fandom block `WebFetch` (403) on their rendered pages**, but Fandom's
  **MediaWiki `api.php` endpoint is not gated** — `action=query&list=categorymembers` returned
  the creatures category directly. Lesson: for any MediaWiki/Fandom wiki, hit `api.php` rather
  than the `/wiki/` page. The walkthrough was also cached locally from a prior session
  (`docs/_reference-dumps/DC2-ROOMS-FAQ.md`). With the API working, the roster is now backed by three
  independent sources (FAQ + Wikipedia + Dino Crisis Wiki) for most creatures.
- **No second independent walkthrough was fetchable**, so all per-area placement and
  weapon-stat data is effectively **single-source** (one FAQ). Re-verify against a second
  guide (e.g. Ghidrah's) before treating placements as ground truth.
- The forum source that seeded the byte-level format work (residentevil123 / tapatalk) is
  preserved at `docs/_reference-dumps/DINOCRISIS2.md`; it documents the package/LZSS format, not
  gameplay content, so it is not re-listed here.
