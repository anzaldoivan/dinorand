namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Extracts the canonical cat-8 (Therizinosaurus) hit/death reaction descriptor records from the
/// Theri's native host RDT (<c>st605</c>) so they can be installed into an EXE cave by
/// <see cref="ExePatcher.RedirectCat8HitDescriptors"/> — the defect-B fix
/// (<c>docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md</c>, RCA <c>THERI-0102-DEFECT-B-HITDEATH-RCA.md</c>).
///
/// <para>The engine addresses these records through the <b>cat-8-exclusive</b> descriptor tables
/// <see cref="ExePatcher.Cat8HitTable17Va"/> / <see cref="ExePatcher.Cat8HitTable15Va"/> as file-form,
/// RDT-base-relative pointers (<c>0x8013xxxx</c>/<c>0x8015xxxx</c>); only indices <c>0..4</c> are ever
/// relocated/resolved. This reads those 10 file-form entries from the <b>unpatched</b> EXE, resolves
/// each against the decompressed st605 RDT, and returns the 10 <b>pointer-free</b> <c>0x14</c>-byte
/// records verbatim in <c>table17[0..4]</c> then <c>table15[0..4]</c> order — exactly the layout
/// <see cref="ExePatcher.RedirectCat8HitDescriptors"/> consumes.</para>
///
/// <para>Must run <b>before</b> the redirect (which overwrites those table entries). The host RDT is
/// always st605 — the authored cat-8 host — independent of the per-swap donor; pulling from any smaller
/// host (st603/st612) would resolve the high-band death descriptors (<c>0x594a8</c>) past the RDT end
/// and throw.</para>
/// </summary>
public static class Cat8HitDescriptors
{
    /// <summary>PSX load base of a room RDT (PSX <c>0x80100000</c>); a file-form pointer's RDT file
    /// offset is <c>value − this</c>. Equal to <c>SpeciesImporter.PsxBase</c>. <c>[verified]</c></summary>
    public const uint RdtPsxBase = 0x80100000;

    /// <summary>Exclusive upper bound of the engine's file-form window from <see cref="RdtPsxBase"/>
    /// (PSX <c>0x80200000</c>); a resolvable RDT pointer lies in <c>[RdtPsxBase, this)</c>.</summary>
    public const uint RdtPsxHi = 0x80200000;

    /// <summary>
    /// Read the 10 cat-8 descriptor table entries from <paramref name="exe"/>, resolve them against the
    /// decompressed st605 RDT <paramref name="hostRdt"/>, and return the 10 records concatenated
    /// (<c>table17[0..4]</c> then <c>table15[0..4]</c>, each <see cref="ExePatcher.HitDescriptorRecordSize"/>
    /// bytes). Throws <see cref="InvalidDataException"/> if a table entry is not an RDT file-form pointer
    /// (wrong EXE build / changed layout) or resolves past the host RDT (wrong host room).
    /// </summary>
    public static byte[] Extract(ReadOnlySpan<byte> exe, ReadOnlySpan<byte> hostRdt)
    {
        int recSize = ExePatcher.HitDescriptorRecordSize;
        var output = new byte[ExePatcher.HitDescriptorTotalRecords * recSize];
        int w = 0;
        foreach (uint tableVa in new[] { ExePatcher.Cat8HitTable17Va, ExePatcher.Cat8HitTable15Va })
        {
            for (int i = 0; i < ExePatcher.HitDescriptorIndexCount; i++)
            {
                uint entry = ExePatcher.ReadUInt32AtVa(exe, tableVa + (uint)i * 4);
                if (entry < RdtPsxBase || entry >= RdtPsxHi)
                    throw new InvalidDataException(
                        $"cat-8 descriptor table 0x{tableVa:X8}[{i}] = 0x{entry:X8} is not an RDT file-form " +
                        "pointer — unexpected DINO.exe build or the descriptor-table layout changed.");
                int off = (int)(entry - RdtPsxBase);
                if (off < 0 || off + recSize > hostRdt.Length)
                    throw new InvalidDataException(
                        $"cat-8 descriptor 0x{tableVa:X8}[{i}] resolves to RDT offset 0x{off:X} (+0x{recSize:X}), " +
                        $"beyond the {hostRdt.Length}-byte host RDT — wrong host room (expected st605).");
                hostRdt.Slice(off, recSize).CopyTo(output.AsSpan(w, recSize));
                w += recSize;
            }
        }
        return output;
    }
}
