using System.Buffers.Binary;
using System.Text;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Classifies a <c>DINO.exe</c> as a clean (DRM-free) executable or one wrapped by a commercial
/// software protector — specifically <b>The Enigma Protector</b>, which guards the Steam release.
///
/// <para><b>Why this matters.</b> Every <see cref="ExePatcher"/> edit assumes the unprotected
/// Classic REbirth / GOG layout, where the <c>.text</c>/<c>.rdata</c>/<c>.data</c> sections are raw
/// == virtual and <c>file_offset = VA - 0x400000</c> (see <see cref="ExePatcher.VaToFileOffset"/>).
/// On the Enigma-wrapped Steam build those file offsets fall inside encrypted, compressed sections,
/// so patching would silently corrupt the executable. Refusing it is therefore both a Terms-of-Service
/// matter (we do not modify a DRM-protected game) and a correctness safeguard.</para>
///
/// <para><b>How the Steam build was identified</b> (read-only static PE comparison, Steam vs the
/// DRM-free Rebirth build): 12 sections with <b>9 of the section names zeroed</b>, the entry point
/// living in the final high-entropy section rather than <c>.text</c>, a stub import table
/// (~15 funcs vs ~165), and the literal ASCII string <c>"Enigma Protector"</c> embedded 7× in the
/// appended protector section. The detector below keys off the two cheapest, false-positive-proof of
/// those signals: the embedded Enigma signature and the zeroed section-name table.</para>
/// </summary>
public static class ExeProtection
{
    /// <summary>The "Enigma Protector" marker string the wrapper bakes into the appended section.
    /// Definitive: a normal MSVC-linked PE never contains it.</summary>
    private static readonly byte[] EnigmaSignature = Encoding.ASCII.GetBytes("Enigma Protector");

    /// <summary>
    /// Inspect the executable at <paramref name="exePath"/>. Returns <see cref="ExeProtectionResult.Clean"/>
    /// for an unreadable / non-PE file (so this never masks a more specific error downstream) rather than
    /// false-flagging it as protected.
    /// </summary>
    public static ExeProtectionResult Inspect(string exePath)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(exePath); }
        catch { return ExeProtectionResult.Clean; }
        return Inspect(bytes, Path.GetFileName(exePath));
    }

    /// <summary>Inspect the executable image in <paramref name="exe"/>. Messages use a generic
    /// "the game executable" label; prefer the overload that takes the filename where one is known.</summary>
    public static ExeProtectionResult Inspect(ReadOnlySpan<byte> exe) => Inspect(exe, "The game executable");

    /// <summary>Inspect the executable image in <paramref name="exe"/>, naming <paramref name="exeName"/>
    /// (e.g. <c>DINO.exe</c> / <c>Dino2.exe</c>) in any refusal message so the warning matches the actual
    /// game. The detector is name-agnostic — only the wording reflects <paramref name="exeName"/>.</summary>
    public static ExeProtectionResult Inspect(ReadOnlySpan<byte> exe, string exeName)
    {
        // Definitive signature first — a clean game executable never carries it.
        if (IndexOf(exe, EnigmaSignature) >= 0)
            return new ExeProtectionResult(
                ExeProtectionKind.EnigmaProtector,
                $"{exeName} carries 'The Enigma Protector' DRM (the Steam release). DinoRand only supports the " +
                "DRM-free executable (Classic REbirth / GOG build) and will not modify a protected game.");

        // Structural fallback: a packer blanks the original section-name table. A normal compiler-linked
        // PE names every section, so two or more all-zero names is a reliable packed-image tell.
        if (TryParse(exe, out var pe))
        {
            if (pe.BlankSectionNames >= 2)
                return new ExeProtectionResult(
                    ExeProtectionKind.Packed,
                    $"{exeName} looks packed/protected: {pe.BlankSectionNames} of {pe.SectionCount} section names " +
                    "are blank and the original sections are not in their normal layout. DinoRand only supports the " +
                    "DRM-free executable and will not modify a protected game.");

            // Entry point sitting in the last of many sections (not the code section) is the other packer tell.
            if (pe.SectionCount >= 10 && pe.EntryPointSectionIndex == pe.SectionCount - 1)
                return new ExeProtectionResult(
                    ExeProtectionKind.Packed,
                    $"{exeName} looks packed/protected: its entry point lies in the last of {pe.SectionCount} " +
                    "sections rather than the code section. DinoRand only supports the DRM-free executable and " +
                    "will not modify a protected game.");
        }

        return ExeProtectionResult.Clean;
    }

    /// <summary>True if <paramref name="exePath"/> is wrapped by a protector and must not be patched.</summary>
    public static bool IsProtected(string exePath) => Inspect(exePath).IsProtected;

    // ---- minimal read-only PE header parse (no external dependency) ----

    private readonly record struct PeFacts(int SectionCount, int BlankSectionNames, int EntryPointSectionIndex);

    private static bool TryParse(ReadOnlySpan<byte> exe, out PeFacts pe)
    {
        pe = default;
        if (exe.Length < 0x40 || exe[0] != (byte)'M' || exe[1] != (byte)'Z')
            return false;
        int eLfanew = BinaryPrimitives.ReadInt32LittleEndian(exe.Slice(0x3C));
        // Subtract-form bound: `eLfanew + 0x2C` overflows int for a corrupt e_lfanew near
        // int.MaxValue and would index past the span. 0x2C covers the deepest fixed-header read
        // below (entry-point RVA at PE+0x28..0x2C), so a truncated header is also rejected here.
        if (eLfanew <= 0 || eLfanew > exe.Length - 0x2C)
            return false;
        if (exe[eLfanew] != (byte)'P' || exe[eLfanew + 1] != (byte)'E' || exe[eLfanew + 2] != 0 || exe[eLfanew + 3] != 0)
            return false;

        int numSections = BinaryPrimitives.ReadUInt16LittleEndian(exe.Slice(eLfanew + 6));
        int sizeOptHdr = BinaryPrimitives.ReadUInt16LittleEndian(exe.Slice(eLfanew + 20));
        uint entryRva = BinaryPrimitives.ReadUInt32LittleEndian(exe.Slice(eLfanew + 24 + 16));
        int secTable = eLfanew + 24 + sizeOptHdr;
        if (numSections <= 0 || numSections > 96 || secTable + numSections * 40 > exe.Length)
            return false;

        int blank = 0, epIdx = -1;
        for (int i = 0; i < numSections; i++)
        {
            int off = secTable + i * 40;
            bool nameBlank = true;
            for (int b = 0; b < 8; b++)
                if (exe[off + b] != 0) { nameBlank = false; break; }
            if (nameBlank) blank++;

            uint vSize = BinaryPrimitives.ReadUInt32LittleEndian(exe.Slice(off + 8));
            uint vAddr = BinaryPrimitives.ReadUInt32LittleEndian(exe.Slice(off + 12));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(exe.Slice(off + 16));
            uint span = Math.Max(vSize, rawSize);
            if (epIdx < 0 && entryRva >= vAddr && entryRva < vAddr + span)
                epIdx = i;
        }

        pe = new PeFacts(numSections, blank, epIdx);
        return true;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length) return -1;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}

/// <summary>The protector class a <c>DINO.exe</c> was identified as.</summary>
public enum ExeProtectionKind
{
    /// <summary>DRM-free, normally-linked executable — patchable.</summary>
    None,
    /// <summary>Wrapped by The Enigma Protector (the Steam release's DRM).</summary>
    EnigmaProtector,
    /// <summary>Wrapped by an unidentified packer/protector (structural signals only).</summary>
    Packed,
}

/// <summary>Outcome of <see cref="ExeProtection.Inspect(string)"/>: whether the exe is protected and why.</summary>
public sealed record ExeProtectionResult(ExeProtectionKind Kind, string Detail)
{
    /// <summary>A DRM-free, patchable executable.</summary>
    public static readonly ExeProtectionResult Clean = new(ExeProtectionKind.None, "DRM-free executable.");

    /// <summary>True when the executable must not be modified (protected by a DRM wrapper / packer).</summary>
    public bool IsProtected => Kind != ExeProtectionKind.None;
}

/// <summary>
/// Thrown when an operation would read or modify a <c>DINO.exe</c> that is protected by a DRM wrapper
/// (e.g. The Enigma Protector on the Steam build). DinoRand refuses to patch protected executables — both
/// for Terms-of-Service compliance and because the patch offsets are invalid against an encrypted image.
/// </summary>
public sealed class DrmProtectedExeException : Exception
{
    /// <summary>The path of the protected executable.</summary>
    public string ExePath { get; }

    /// <summary>How the executable was classified.</summary>
    public ExeProtectionResult Detection { get; }

    public DrmProtectedExeException(string exePath, ExeProtectionResult detection)
        : base(detection.Detail)
    {
        ExePath = exePath;
        Detection = detection;
    }
}
