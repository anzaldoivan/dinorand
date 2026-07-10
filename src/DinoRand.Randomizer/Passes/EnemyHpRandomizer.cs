using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// DC1 per-placement enemy maxHP override (gated off by default). Writes a seeded,
/// <see cref="RandomizerConfig.EnemyDifficulty"/>-scaled HP into each eligible <c>0x20</c> spawn
/// record's <c>+6</c> word; the handler copies it into entity <c>+0x11A</c>, bypassing the engine's
/// <c>{750,850,1000}</c> birth roll (DC1-G2, live-confirmed 2026-07-08 — EXE-SYMBOLS <c>0x42656A</c>;
/// docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED"). A plain SCD file edit; no EXE/CE.
///
/// <para><b>Eligibility</b> starts from <see cref="EnemyRandomizer"/>'s filter (positively-decoded
/// dinosaurs via <see cref="EnemyRecord.IsRandomizableDino"/>; scripted T-Rex + cutscene rooms skipped;
/// <b><c>0x20</c> records only</b> — a <c>0x59</c> record's <c>+6</c> is model-pointer bytes) and then
/// requires <see cref="EnemyRecord.IsHpPresettable"/>: only cat-2 RaptorHeavy and cat-7 Pteranodon births
/// keep a nonzero preset — cat-1 Velociraptor and cat-5 Swarm births overwrite maxHP with 1000
/// unconditionally on the PC build, so editing them would be a silent no-op (STATIC-SCD-RE cont.48).
/// HP gates no progression (key/door logic ignores it), so beatability is unaffected; the drawn value is
/// clamped to a ushort-safe, never-zero band.</para>
/// </summary>
public sealed class EnemyHpRandomizer : IRandomizationPass
{
    public string Name => "enemy-hp";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeEnemyHp;

    // The vanilla roll's modal value (birth-init rolls {750,850,1000}); the difficulty band anchors here.
    // ponytail: these band constants are the tuning knob — HP *feel* needs a playtest/CE pass a static
    // model can't see. Widen/narrow HERE, not in the pass logic below.
    private const int VanillaModalHp = 850;
    private const int MinHp = 200, MaxHp = 4000; // ushort-safe; never 0 (0 would re-trigger the roll)

    public void Apply(RandomizationContext context)
    {
        var rng = context.Seed.RngFor(Name);
        var scripted = context.Game.ScriptedEnemyRoomCodes;
        var cutscene = context.Game.CutsceneRoomCodes;
        double difficulty = Math.Clamp(context.Config.EnemyDifficulty, 0, 1);

        var spoiler = context.Spoiler.Section("Enemy HP (DC1 per-placement)", "Room", "Change");

        // Difficulty widens the HP band: at 0 it hovers below vanilla, at 1 it reaches ~4× the modal.
        int lo = (int)(VanillaModalHp * (0.4 + 0.8 * difficulty)); // diff 0.5 → 680
        int hi = (int)(VanillaModalHp * (0.8 + 3.2 * difficulty)); // diff 0.5 → 2040
        if (hi <= lo) hi = lo + 1;

        int changed = 0, roomsTouched = 0;
        foreach (var room in context.AllRooms())
        {
            if (room.Enemies.Count == 0) continue;
            int code = room.Stage * 0x100 + room.Room;
            if (scripted.Contains(code) || cutscene.Contains(code)) continue;

            int roomEdits = 0, roomLo = int.MaxValue, roomHi = 0;
            foreach (var e in room.Enemies)
            {
                if (e.Opcode != DcOpcodes.Enemy || !e.IsRandomizableDino || !e.IsHpPresettable) continue;
                int hp = Math.Clamp(rng.Next(lo, hi + 1), MinHp, MaxHp);
                e.MaxHp = (ushort)hp;
                if (e.IsEdited)
                {
                    changed++; roomEdits++;
                    roomLo = Math.Min(roomLo, hp); roomHi = Math.Max(roomHi, hp);
                }
            }
            if (roomEdits > 0)
            {
                roomsTouched++;
                spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(code),
                               $"{roomEdits} enemy HP set ({roomLo}..{roomHi})");
            }
        }

        context.Log($"[enemy-hp] set HP on {changed} spawn(s) across {roomsTouched} rooms (difficulty {difficulty:0.##})");
        spoiler.AddNote($"set HP on {changed} spawn(s) across {roomsTouched} room(s) — per-placement +6 maxHP "
            + $"override, difficulty {difficulty:0.##} (band {lo}..{hi})");
    }
}
