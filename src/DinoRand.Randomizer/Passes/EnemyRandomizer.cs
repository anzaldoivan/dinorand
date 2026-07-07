using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Phase 2. In Dino Crisis the enemy species is bound to the loaded EMD <b>model resource</b>,
/// not to an id byte (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.10/11), so this pass does not substitute from a
/// global pool. Instead it <b>permutes the (model, motion) pointer pairs already loaded in a
/// room</b> among that room's enemy records, keeping each swap within one AI
/// <see cref="EnemyRecord.Category"/>:
/// <list type="bullet">
///   <item>only resources the room already loads are ever used → always valid / round-trips;</item>
///   <item>same-category only → a grounded-raptor model never lands in a flyer/boss slot;</item>
///   <item>only entities that positively decode as a dinosaur
///   (<see cref="EnemyRecord.IsRandomizableDino"/>) are eligible → a <c>0x20</c> entity whose
///   model is not a known species (a rig-sharing humanoid corpse, walk-desync garbage) is never
///   edited;</item>
///   <item>scripted T-Rex rooms (<see cref="Definitions.GameDefinition.ScriptedEnemyRoomCodes"/>)
///   <i>and</i> choreographed cutscene rooms
///   (<see cref="Definitions.GameDefinition.CutsceneRoomCodes"/>) are skipped entirely.</item>
/// </list>
///
/// <para><b>NPCs are out of scope (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.15).</b> <c>0x20</c> is the general
/// entity opcode, but the game's live NPC characters (Gail, Rick, Kirk) are <i>not</i> placed by it —
/// they load via the cutscene system. The only non-dinosaur <c>0x20</c> entity in DC1 is the
/// <c>st50c</c> humanoid corpse, which shares the raptor rig (so bone count / category cannot flag it)
/// but is a singleton, so the ≥2-records rule already leaves it alone. The cutscene-room exclusion
/// covers the remaining risk: scenes that choreograph their (dinosaur) entities by slot.</para>
/// The permutation is a bijection on the room's <i>distinct</i> pairs, then applied per record by
/// original pair, so duplicate spawn records of one logical enemy (the same slot scripted under
/// several conditions) all receive the same new species and stay consistent.
///
/// <para><b>Species decode (cont.14) — permutations are same-species.</b> The model skeleton
/// decode (<see cref="EnemySkeleton"/>/<see cref="DinoSpecies"/>) shows the enemy skeleton topology
/// is 1:1 with the AI <see cref="EnemyRecord.Category"/> over the whole corpus. Because this pass
/// only ever permutes <i>within</i> a category, every swap stays within one species class — the
/// distinct (model, motion) pairs in a category are pose/mesh variants of the same dinosaur, not
/// different species. So an in-room permutation reshuffles same-species variants; it never produces
/// a cross-species swap. Cross-room species placement would require importing a model+motion
/// resource a room does not load (its pointer would dangle), so it is out of scope here.</para>
/// </summary>
public sealed class EnemyRandomizer : IRandomizationPass
{
    public string Name => "enemies";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeEnemies;

    public void Apply(RandomizationContext context)
    {
        var rng = context.Seed.RngFor(Name);
        var scripted = context.Game.ScriptedEnemyRoomCodes;
        var cutscene = context.Game.CutsceneRoomCodes;

        // Spoiler section (docs/decisions/cross/SPOILER-LOG-PLAN.md §4). DC1 permutes same-species (model, motion)
        // variants within a room, so the meaningful diff is per-room counts, not species names.
        var spoiler = context.Spoiler.Section("Enemies (DC1 in-room permute)", "Room", "Change");

        int changed = 0, roomsTouched = 0;
        foreach (var room in context.AllRooms())
        {
            if (room.Enemies.Count == 0) continue;
            int code = room.Stage * 0x100 + room.Room;
            // Scripted T-Rex set-pieces and choreographed cutscenes are both off-limits: the former
            // are hand-placed, the latter bind a dinosaur's (model, motion) to a scripted animation.
            if (scripted.Contains(code) || cutscene.Contains(code)) continue;

            bool any = false;
            int roomEdits = 0, roomVariants = 0;
            // Only permute records that positively decode as a dinosaur (cont.15): excludes any
            // 0x20 entity whose model is not a known species — a defence against rig-sharing
            // non-dinosaurs (e.g. a humanoid corpse) and walk-desync garbage ever being edited.
            foreach (var group in room.Enemies.Where(e => e.IsRandomizableDino).GroupBy(e => e.Category))
            {
                var records = group.ToList();
                if (records.Count < 2) continue;

                // Permute the distinct (model, motion) pairs, then remap every record by its
                // original pair so identical enemies stay identical.
                var distinct = records
                    .Select(e => (e.OriginalModelPtr, e.OriginalMotionPtr))
                    .Distinct()
                    .ToList();
                if (distinct.Count < 2) continue; // homogeneous group → permute is a no-op

                var shuffled = NonIdentityPermutation(distinct, rng);
                var map = new Dictionary<(uint, uint), (uint, uint)>();
                for (int i = 0; i < distinct.Count; i++) map[distinct[i]] = shuffled[i];

                foreach (var e in records)
                {
                    var (m, mo) = map[(e.OriginalModelPtr, e.OriginalMotionPtr)];
                    e.ModelPtr = m;
                    e.MotionPtr = mo;
                    if (e.IsEdited) { changed++; any = true; roomEdits++; }
                }
                roomVariants += distinct.Count;
            }
            if (any)
            {
                roomsTouched++;
                spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(code),
                               $"{roomEdits} spawn(s) permuted among {roomVariants} same-species variant(s)");
            }
        }

        context.Log($"[enemies] permuted {changed} spawns across {roomsTouched} rooms");
        spoiler.AddNote($"permuted {changed} spawn(s) across {roomsTouched} room(s) — in-room, "
            + "same-species variant permutation (species never change in DC1)");
    }

    /// <summary>
    /// Fisher–Yates shuffle that never returns the input order (the list has ≥2 distinct
    /// elements, so a single rotation when the shuffle lands on identity guarantees every
    /// position changes — i.e. a real swap).
    /// </summary>
    private static List<T> NonIdentityPermutation<T>(List<T> items, Random rng)
    {
        var result = new List<T>(items);
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }
        if (result.SequenceEqual(items))
        {
            var first = result[0];
            result.RemoveAt(0);
            result.Add(first);
        }
        return result;
    }
}
