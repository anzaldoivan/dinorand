using E = DinoRand.FileFormats.Tests.SyntheticRoom.Enemy;
using I = DinoRand.FileFormats.Tests.SyntheticRoom.Item;
using D = DinoRand.FileFormats.Tests.SyntheticRoom.Door;
using DinoRand.FileFormats.Stage;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The <b>synthetic mock room corpus</b> the room round-trip gates fall back to when no real Dino Crisis
/// install is configured (i.e. on CI, and on a fresh clone). Materialises DMCA-safe rooms built by
/// <see cref="SyntheticRoom"/> into a per-process temp directory once, and hands back its path so the
/// existing enumerators can <c>Directory.EnumerateFiles</c> it exactly as they do a real <c>Data</c> dir.
/// No game bytes ever touch the repo — the files live only in the temp dir, generated at test time.
///
/// <para>Local dev prefers real files (point <c>DINORAND_DC1_DIR</c>/<c>DINORAND_DC2_DIR</c> at your install,
/// or drop a <c>.env</c> — see <see cref="TestEnvBootstrap"/>); these mocks are the CI/no-install fallback.</para>
/// </summary>
internal static class MockRooms
{
    private static readonly object Gate = new();
    private static string? _dc1;
    private static string? _dc2;

    /// <summary>Temp dir holding the DC1 <c>st*.dat</c> mock corpus (generated once per process).</summary>
    public static string Dc1DataDir() { lock (Gate) { return _dc1 ??= GenerateDc1(); } }

    /// <summary>Temp dir holding the DC2 <c>ST*.DAT</c> mock corpus (generated once per process).</summary>
    public static string Dc2DataDir() { lock (Gate) { return _dc2 ??= GenerateDc2(); } }

    // 12 DC1 rooms spanning every species (cat 1/2/3/4/5/7 via 0x20, cat 8 via 0x59), items (incl. an empty
    // 0xFF slot), locked + unlocked doors, and single/multi-enemy rooms — so every edit-path assertion runs.
    private static string GenerateDc1()
    {
        var dir = FreshDir("dc1");
        var raptor = new E(DcOpcodes.Enemy, 1, 15);
        var heavy = new E(DcOpcodes.Enemy, 2, 21);
        var trexBoss = new E(DcOpcodes.Enemy, 3, 20);
        var trexChief = new E(DcOpcodes.Enemy, 4, 10);
        var swarm = new E(DcOpcodes.Enemy, 5, 7);
        var ptero = new E(DcOpcodes.Enemy, 7, 18);
        var theri = new E(DcOpcodes.Enemy2, 8, 22); // 0x59-placed Therizinosaurus

        var rooms = new (string name, I[] items, D[] doors, E[] enemies)[]
        {
            ("st101.dat", new[] { new I(0x16, 1), new I(0x2b, 5) }, Array.Empty<D>(), Array.Empty<E>()),
            ("st102.dat", Array.Empty<I>(), new[] { new D(2, 0x0d, 0, 0), new D(1, 0x03, 0x12, 1) }, Array.Empty<E>()),
            ("st103.dat", Array.Empty<I>(), Array.Empty<D>(), new[] { raptor, swarm }),
            ("st104.dat", new[] { new I(0x21, 1) }, new[] { new D(3, 0x05, 0, 0) }, new[] { heavy }),
            ("st105.dat", Array.Empty<I>(), Array.Empty<D>(), new[] { ptero, raptor }),
            ("st106.dat", Array.Empty<I>(), Array.Empty<D>(), new[] { trexBoss }),
            ("st107.dat", Array.Empty<I>(), Array.Empty<D>(), new[] { trexChief }),
            ("st108.dat", Array.Empty<I>(), Array.Empty<D>(), new[] { theri }),
            ("st109.dat", new[] { new I(0xFF, 1), new I(0x16, 2) }, new[] { new D(1, 0x02, 0, 0) }, Array.Empty<E>()),
            ("st10a.dat", Array.Empty<I>(), Array.Empty<D>(), new[] { raptor, heavy, swarm }),
            ("st10b.dat", new[] { new I(0x1a, 2) }, Array.Empty<D>(), new[] { ptero, theri }),
            ("st10c.dat", new[] { new I(0x16, 1) }, new[] { new D(4, 0x00, 0, 0), new D(5, 0x01, 0x2e, 2), new D(1, 0x0d, 0, 0) }, Array.Empty<E>()),
        };

        foreach (var (name, items, doors, enemies) in rooms)
            File.WriteAllBytes(Path.Combine(dir, name), SyntheticRoom.Dc1Room(items, doors, enemies));
        return dir;
    }

    private static string GenerateDc2()
    {
        var dir = FreshDir("dc2");
        // 12 DC2 rooms, each an LZSS0 SCD blob (varying size) + raw tail, container tiling exactly.
        for (int v = 0; v < 12; v++)
            File.WriteAllBytes(Path.Combine(dir, $"ST1{v:X2}.DAT"), SyntheticRoom.Dc2Room(v));
        return dir;
    }

    private static string FreshDir(string leaf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dinorand-mockrooms", leaf);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
