using System.Text.Json;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// <c>tools/scd_re/scd_opcode_table.json</c> (handler-derived, in the tools submodule) is the single
/// source for DC1 opcode lengths and names. <see cref="DcOpcodes"/> carries a compiled copy; these
/// tests fail the moment the copy and the table differ, in either direction.
/// </summary>
public class DcOpcodesTableDriftTests
{
    private sealed record Row(byte Opcode, int? Length, string? Name);

    private static List<Row> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "scd_re", "scd_opcode_table.json");
        Assert.True(File.Exists(path),
            $"opcode table not found at {path} — is the tools submodule checked out?");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject().Select(p => new Row(
            Convert.ToByte(p.Name, 16),
            p.Value.GetProperty("len").ValueKind == JsonValueKind.Number ? p.Value.GetProperty("len").GetInt32() : null,
            p.Value.TryGetProperty("name", out var n) ? n.GetString() : null)).ToList();
    }

    [Fact]
    public void EveryOpcodeHasTheSameLengthInBothTables()
    {
        var rows = Load();
        Assert.Equal(DcOpcodes.Lengths.Keys.OrderBy(k => k), rows.Select(r => r.Opcode).OrderBy(k => k));

        foreach (var row in rows)
        {
            int compiled = DcOpcodes.Lengths[row.Opcode];
            if (row.Opcode is 0x28 or 0x5a)
                Assert.True(compiled < 0, $"0x{row.Opcode:x2} must be variable-length in DcOpcodes");
            else
                Assert.True(row.Length == compiled,
                    $"0x{row.Opcode:x2}: table says {row.Length}, DcOpcodes says {compiled}");
        }
    }

    [Fact]
    public void EveryOpcodeHasTheSameNameInBothTables()
    {
        var table = Load().Where(r => r.Name is not null).ToDictionary(r => r.Opcode, r => r.Name!);
        Assert.Equal(table.OrderBy(kv => kv.Key), DcOpcodes.Names.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void Name_FallsBackToOpXX_ForAnOpcodeWithoutProof()
    {
        Assert.Equal("ENEMY_ENTRANCE", DcOpcodes.Name(0x5b));
        Assert.Equal("OP_62", DcOpcodes.Name(0x62));
    }
}
