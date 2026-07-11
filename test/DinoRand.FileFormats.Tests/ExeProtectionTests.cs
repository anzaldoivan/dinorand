using System.Text;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// DRM/protector detection. The detector is executable-name agnostic — it keys off the embedded
/// "Enigma Protector" signature in the raw PE bytes — so it works for any game's exe (DINO.exe /
/// Dino2.exe). When a path/name is supplied the refusal message names that actual executable.
/// </summary>
public class ExeProtectionTests
{
    private static byte[] WithEnigmaSignature() =>
        Encoding.ASCII.GetBytes("MZ......padding......The Enigma Protector......more padding");

    [Fact]
    public void Enigma_signature_is_detected_regardless_of_exe_name()
    {
        var result = ExeProtection.Inspect(WithEnigmaSignature());
        Assert.True(result.IsProtected);
        Assert.Equal(ExeProtectionKind.EnigmaProtector, result.Kind);
    }

    [Fact]
    public void Clean_bytes_are_not_flagged()
    {
        var result = ExeProtection.Inspect(Encoding.ASCII.GetBytes("a perfectly ordinary file"));
        Assert.False(result.IsProtected);
    }

    [Fact]
    public void Refusal_message_names_the_actual_executable()
    {
        // A DC2 Dino2.exe should be reported as Dino2.exe, not the DC1 default DINO.exe.
        var result = ExeProtection.Inspect(WithEnigmaSignature(), "Dino2.exe");
        Assert.True(result.IsProtected);
        Assert.Contains("Dino2.exe", result.Detail);
        Assert.DoesNotContain("DINO.exe", result.Detail);
    }

    // ---- synthetic mini-PE (structural packer detection + parser guards) ------------------------

    /// <summary>Build a minimal PE32 image: MZ stub, e_lfanew → PE header, given section names with
    /// each section i at vAddr 0x1000*(i+1), vSize 0x1000. Empty name = blanked (packer tell).</summary>
    private static byte[] BuildPe(string[] sectionNames, uint entryRva)
    {
        const int eLfanew = 0x80, optHdr = 0xE0;
        var pe = new byte[eLfanew + 24 + optHdr + sectionNames.Length * 40];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        WriteU32(pe, 0x3C, eLfanew);
        pe[eLfanew] = (byte)'P'; pe[eLfanew + 1] = (byte)'E';
        WriteU16(pe, eLfanew + 6, (ushort)sectionNames.Length);
        WriteU16(pe, eLfanew + 20, optHdr);
        WriteU32(pe, eLfanew + 24 + 16, entryRva);
        int secTable = eLfanew + 24 + optHdr;
        for (int i = 0; i < sectionNames.Length; i++)
        {
            int off = secTable + i * 40;
            Encoding.ASCII.GetBytes(sectionNames[i]).CopyTo(pe, off);
            WriteU32(pe, off + 8, 0x1000);                    // vSize
            WriteU32(pe, off + 12, (uint)(0x1000 * (i + 1))); // vAddr
            WriteU32(pe, off + 16, 0x1000);                   // rawSize
        }
        return pe;
    }

    private static void WriteU16(byte[] b, int off, ushort v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); }
    private static void WriteU32(byte[] b, int off, uint v)
    { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }

    [Fact]
    public void Two_or_more_blank_section_names_flag_as_packed()
    {
        var pe = BuildPe(new[] { ".text", "", "", ".rsrc" }, entryRva: 0x1000);
        var result = ExeProtection.Inspect(pe);
        Assert.Equal(ExeProtectionKind.Packed, result.Kind);
        Assert.True(result.IsProtected);
    }

    [Fact]
    public void One_blank_section_name_is_not_enough_to_flag()
    {
        var pe = BuildPe(new[] { ".text", "", ".data" }, entryRva: 0x1000);
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(pe).Kind);
    }

    [Fact]
    public void Entry_point_in_last_of_many_sections_flags_as_packed()
    {
        // 10 named sections, EP inside section 9 (vAddr 0xA000) — the appended-protector layout.
        var names = Enumerable.Range(0, 10).Select(i => $".s{i}").ToArray();
        var result = ExeProtection.Inspect(BuildPe(names, entryRva: 0xA000));
        Assert.Equal(ExeProtectionKind.Packed, result.Kind);
    }

    [Fact]
    public void Entry_point_in_last_section_of_few_sections_is_clean()
    {
        // Same EP-in-last-section shape but only 4 sections: a normal exe, not a packer.
        var names = new[] { ".text", ".rdata", ".data", ".rsrc" };
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(BuildPe(names, entryRva: 0x4000)).Kind);
    }

    [Fact]
    public void Entry_point_in_code_section_of_many_sections_is_clean()
    {
        var names = Enumerable.Range(0, 10).Select(i => $".s{i}").ToArray();
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(BuildPe(names, entryRva: 0x1000)).Kind);
    }

    [Fact]
    public void Malformed_pe_falls_back_to_clean()
    {
        // Each guard in the header parse: not-MZ, bad e_lfanew, bad PE sig, zero sections,
        // section table overrunning the image. All must classify Clean, never Packed.
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(new byte[0x100]).Kind); // no MZ

        var badLfanew = BuildPe(new[] { "", "" }, 0x1000);
        WriteU32(badLfanew, 0x3C, 0x100000);                                      // e_lfanew past EOF
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(badLfanew).Kind);

        var overflowLfanew = BuildPe(new[] { "", "" }, 0x1000);
        WriteU32(overflowLfanew, 0x3C, 0x7FFFFFF0);           // eLfanew + header would overflow int
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(overflowLfanew).Kind);

        // Header truncated mid-way: PE sig present, but the file ends before the entry-point RVA
        // read at PE+0x28 — must reject, not index past the buffer.
        var truncated = new byte[0x100];
        truncated[0] = (byte)'M'; truncated[1] = (byte)'Z';
        WriteU32(truncated, 0x3C, 0x100 - 0x20);
        truncated[0x100 - 0x20] = (byte)'P'; truncated[0x100 - 0x20 + 1] = (byte)'E';
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(truncated).Kind);

        var badSig = BuildPe(new[] { "", "" }, 0x1000);
        badSig[0x80] = (byte)'X';                                                 // not "PE\0\0"
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(badSig).Kind);

        var zeroSections = BuildPe(new[] { "", "" }, 0x1000);
        WriteU16(zeroSections, 0x80 + 6, 0);                                      // numSections = 0
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(zeroSections).Kind);

        var overrun = BuildPe(new[] { "", "" }, 0x1000);
        WriteU16(overrun, 0x80 + 6, 96);                                          // table past EOF
        Assert.Equal(ExeProtectionKind.None, ExeProtection.Inspect(overrun).Kind);
    }

    [Fact]
    public void Missing_file_inspects_as_clean()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dinorand-no-such-{Guid.NewGuid():N}.exe");
        Assert.False(ExeProtection.Inspect(path).IsProtected);
        Assert.False(ExeProtection.IsProtected(path));
    }

    [Fact]
    public void Protected_file_on_disk_is_detected_via_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dinorand-enigma-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, WithEnigmaSignature());
        try
        {
            var result = ExeProtection.Inspect(path);
            Assert.Equal(ExeProtectionKind.EnigmaProtector, result.Kind);
            Assert.Contains(Path.GetFileName(path), result.Detail); // message names the file
            Assert.True(ExeProtection.IsProtected(path));

            var ex = new DrmProtectedExeException(path, result);
            Assert.Equal(path, ex.ExePath);
            Assert.Equal(result.Detail, ex.Message);
        }
        finally { File.Delete(path); }
    }
}
