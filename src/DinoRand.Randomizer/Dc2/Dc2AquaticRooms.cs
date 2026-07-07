namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// DC2 underwater rooms whose intended enemy is <b>aquatic but delivered through the GENERIC TYPE-0x10
/// spawn path</b> (its model comes from a runtime global, not a hardcoded ctor TYPE). The habitat-driven
/// skip (<see cref="Dc2CrossSpeciesPlanner"/> / <see cref="Dc2RoomEnemySwap.IsAquaticNativeRoom(System.Collections.Generic.IReadOnlyList{Dc2SpawnRecord})"/>)
/// only sees hardcoded aquatic ctor TYPEs (0x05/0x0a/0x0b/0x0c), so it cannot detect these rooms — they
/// must be protected explicitly by <c>st_id</c> so the randomizer never converts their aquatic enemy to a
/// land donor.
///
/// <para>Defense-in-depth: the generic TYPE-0x10 spawn is <b>not edited today</b> (its model base is a
/// non-literal global — K60), so these rooms are already left unchanged in practice. Listing them here
/// keeps that protection explicit and guarantees it holds if a future <c>Dc2ExeEnemyPatcher</c> ever
/// teaches the randomizer to edit generic-model bases. Mirrors the <see cref="Dc2RoomExclusions"/> pattern
/// (a standalone st_id set consulted by both the bulk pass and the single-room CLI op).</para>
/// </summary>
public static class Dc2AquaticRooms
{
    /// <summary>Underwater room <c>st_id</c>s whose aquatic enemy is generic-delivered (invisible to the
    /// habitat skip). Grows as more are identified via the per-room E-file census / live capture.</summary>
    public static IReadOnlySet<string> Explicit { get; } = new HashSet<string>
    {
        "704", // ST704 — underwater; its enemy is a Mosasaurus (E30, aquatic) spawned via the generic
               // TYPE-0x10 path (live map: E30=Mosasaurus ST704). No hardcoded aquatic ctor TYPE in its
               // spawns, so IsAquaticNativeRoom's habitat check can't see it — protect it by st_id.
    };

    /// <summary>True iff <paramref name="roomKey"/> (an <c>st_id</c> like "704") is an explicitly-listed
    /// aquatic room whose enemy the habitat skip cannot detect.</summary>
    public static bool Contains(string roomKey) => Explicit.Contains(roomKey);
}
