using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Emit-time invariant: a Dino Crisis 2 room's Gian container format (32-byte "DC2" entries) must be
/// preserved by whatever pass rewrote it. A DC2 room emitted as a DC1 16-byte-entry container is misread
/// by the engine into an out-of-range GPU-resource index and crashes on room load
/// (docs/decisions/dc2/crash-rcas/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md, docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md). This guard
/// forbids any DC1 16-byte writer (<c>PackageRepacker</c> / <c>GianPackageBuilder.Build</c>) from
/// silently shipping a DC2 file: it is an internal contract, so a violation is a programmer error and
/// fails fast rather than producing an unbootable seed.
/// </summary>
public static class Dc2RoomEmitGuard
{
    /// <summary>Throw if <paramref name="emitted"/> does not preserve the Gian entry stride of
    /// <paramref name="original"/>. No-op when the original is not a Gian container (nothing to preserve).</summary>
    public static void EnsureContainerFormatPreserved(string name, byte[] original, byte[] emitted)
    {
        var originalPkg = GianPackage.TryParse(original);
        if (originalPkg is null) return; // not a Gian container: no invariant to enforce

        var emittedPkg = GianPackage.TryParse(emitted);
        if (emittedPkg is null || emittedPkg.IsDc2 != originalPkg.IsDc2)
            throw new InvalidOperationException(
                $"DC2 room emit for '{name}' changed the Gian container format: the source is a " +
                $"{(originalPkg.IsDc2 ? "DC2 32-byte-entry" : "DC1 16-byte-entry")} package but the emitted " +
                "bytes are not the same stride. A DC2 room must be rewritten with the 32-byte writer " +
                "(BuildDc2 / Dc2ScdBlob.RepackWithBlob), never the DC1 16-byte PackageRepacker/Build — this " +
                "is the container-stride corruption that crashes the game on room load.");
    }
}
