using System;
using System.IO;
using System.Linq;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Room-file enumeration must be case-INSENSITIVE. In a real GOG DC1 install
/// <c>english/Data</c> ships room 0502 as <c>St502.dat</c> (capital <c>S</c>) — the only story-room
/// file with a case anomaly. A case-sensitive <c>st*.dat</c> glob (the default for
/// <see cref="Directory.EnumerateFiles(string,string)"/> on a case-sensitive filesystem such as
/// Linux/WSL/CI) silently drops it, so room 0502 vanishes from the door graph and becomes a
/// no-outbound sink. These tests pin the case-insensitive contract (STATIC-SCD-RE cont.42).
/// </summary>
public class RoomEnumerationCaseTests
{
    private static string MakeInstall(params string[] roomFileNames)
    {
        var root = Path.Combine(Path.GetTempPath(), "dinorand_case_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        foreach (var name in roomFileNames)
            File.WriteAllBytes(Path.Combine(data, name), new byte[16]);
        return root;
    }

    [Fact]
    public void EnumerateRooms_includes_capital_case_room_files()
    {
        // St502.dat (capital S) is room 0502; stA02.dat is a demo-stage room (stage 0xA).
        var root = MakeInstall("st100.dat", "St502.dat", "stA02.dat");
        try
        {
            var rooms = new DinoCrisis1().EnumerateRooms(root);
            var codes = rooms.Select(r => (r.Stage << 8) | r.Room).ToHashSet();

            Assert.Contains(0x0100, codes);   // st100.dat — always worked
            Assert.Contains(0x0502, codes);   // St502.dat — the fix
            Assert.Contains(0x0A02, codes);   // stA02.dat — demo stage still included
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnumerateRooms_still_excludes_non_room_dat_files()
    {
        // Regression guard: the fix must not start matching stage files (single hex digit) or
        // unrelated .dat files that merely contain a room-like number.
        var root = MakeInstall("st100.dat", "st1.dat", "Wire502.dat", "ast502.dat", "St1.DAT.bak");
        try
        {
            var rooms = new DinoCrisis1().EnumerateRooms(root);
            var codes = rooms.Select(r => (r.Stage << 8) | r.Room).ToHashSet();

            Assert.Contains(0x0100, codes);         // the only real room here
            Assert.Single(codes);                    // st1 / Wire502 / ast502 / .bak all excluded
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GetDataDir_recognizes_a_dir_holding_only_capital_case_room_files()
    {
        // The data-dir validator (HasRoomFiles) must also be case-insensitive: a Data folder whose
        // only room file is capital-cased must still resolve as the install's data dir.
        var root = MakeInstall("St502.dat");
        try
        {
            var dataDir = new DinoCrisis1().GetDataDir(root);
            Assert.Equal(Path.Combine(root, "Data"), dataDir);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Dc2_EnumerateRooms_is_case_insensitive_too()
    {
        // Same root-cause fix on the DC2 glob ("ST*.DAT"): a lower/mixed-case room file must still
        // enumerate (rebirth/Data is clean today, but the enumeration must not be casing-fragile).
        var root = MakeInstall("st100.dat");   // lower-case; DC2 pattern is ST(\d)(\d\d)\.DAT IgnoreCase
        try
        {
            var rooms = new DinoCrisis2().EnumerateRooms(root);
            var codes = rooms.Select(r => (r.Stage << 8) | r.Room).ToHashSet();
            Assert.Contains(0x0100, codes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
