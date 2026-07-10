using System;
using System.Collections.Generic;
using System.IO;
using DinoRand.FileFormats.Exe;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Scrambles the DC1 keypad-code puzzle family (see <c>data/dc1/puzzle-codes.json</c> and
/// <c>docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md</c> §17–18) with one common function: for each of the six
/// 4-digit locks it writes the new code into every recompiled copy of the DINO.exe keypad table (both
/// region halves) and, for the three locks that also have an in-game document stating the code, rewrites the
/// room document's glyph-token run to the same number — preserving <b>displayed == checked</b>.
///
/// <para>The document edit here is the <b>inline-RDT</b> lever (GOG European installs): decompress the room
/// RDT (via <see cref="RoomFile"/>), rewrite the digit glyphs (<see cref="MgmtOfficeDocumentCode"/>),
/// re-LZSS + repack the package entry (<see cref="RoomFile.WriteWithRdt"/>). Rows 3–5 have no readable
/// document, so only the EXE is touched. On REbirth-English installs the document text lives in ddraw.dll
/// instead — the installer routes to <see cref="DinoRand.FileFormats.Exe.RebirthTextPatcher"/> and calls
/// <see cref="Scramble"/> with <c>requireDocuments: false</c> (see
/// <see cref="DinoRand.FileFormats.Exe.Dc1EditionDetector"/> for the routing).</para>
/// </summary>
public static class Dc1PuzzleCodeSync
{
    /// <summary>One 4-digit lock in the shared keypad table.</summary>
    /// <param name="Row">Table row (0..5).</param>
    /// <param name="OriginalDigits">The code as it ships (JP build / JP table half) — used to locate the
    /// document run and to verify the stock table before writing.</param>
    /// <param name="UsDigits">The stock US-half code for the row (rows 3–5 are region-identical) — used
    /// only by <see cref="VerifyStockKeypadTable"/>.</param>
    /// <param name="Name">Human-readable lock name.</param>
    /// <param name="DocFile">Room .dat that displays the code, or null if none.</param>
    /// <param name="DocStage">Stage index for <see cref="RoomFile.Read"/> (metadata only).</param>
    /// <param name="DocRoom">Room index for <see cref="RoomFile.Read"/> (metadata only).</param>
    public sealed record CodeLock(int Row, int[] OriginalDigits, int[] UsDigits, string Name,
                                  string? DocFile, int DocStage, int DocRoom);

    /// <summary>The six locks, in table-row order (JP-build codes; US half per puzzle-codes.json).</summary>
    public static readonly IReadOnlyList<CodeLock> Family = new[]
    {
        new CodeLock(0, new[] { 0, 3, 7, 5 }, new[] { 0, 4, 2, 6 }, "Management Office safe",        "st100.dat", 1, 0),
        new CodeLock(1, new[] { 7, 6, 8, 7 }, new[] { 8, 1, 5, 9 }, "Lounge safe",                   "st200.dat", 2, 0),
        new CodeLock(2, new[] { 5, 0, 3, 7 }, new[] { 7, 2, 4, 8 }, "Computer Room gas-room code",   "st302.dat", 3, 2),
        new CodeLock(3, new[] { 0, 3, 6, 7 }, new[] { 0, 3, 6, 7 }, "Stabilizer Design Room code A", null, 0, 0),
        new CodeLock(4, new[] { 0, 2, 0, 4 }, new[] { 0, 2, 0, 4 }, "Stabilizer Design Room code B", null, 0, 0),
        new CodeLock(5, new[] { 1, 2, 8, 1 }, new[] { 1, 2, 8, 1 }, "Stabilizer Experiment Room safe", null, 0, 0),
    };

    /// <summary>
    /// Verify that <paramref name="exe"/> actually carries the JP-master keypad table — every copy's JP and
    /// US halves must hold the stock codes. The European GOG executables are different builds with a
    /// different <c>.data</c> layout (the table offsets land on unrelated bytes — proven for the French exe:
    /// zero pattern hits, garbage at <c>0x268a50</c>), so writing rows there would corrupt random data AND
    /// desync displayed != checked. Throws with a build-explains-it message instead.
    /// </summary>
    public static void VerifyStockKeypadTable(ReadOnlySpan<byte> exe)
    {
        foreach (var lk in Family)
        {
            foreach (int copy in MgmtOfficeSafeCode.JpRow0FileOffsets)
            {
                foreach (bool us in new[] { false, true })
                {
                    int fo = MgmtOfficeSafeCode.RowFileOffset(copy, lk.Row, us);
                    int[] stock = us ? lk.UsDigits : lk.OriginalDigits;
                    for (int i = 0; i < MgmtOfficeSafeCode.DigitCount; i++)
                    {
                        if (fo + i >= exe.Length || exe[fo + i] != MgmtOfficeSafeCode.EncodeDigit(stock[i]))
                            throw new InvalidOperationException(
                                $"DINO.exe does not hold the stock JP-master keypad table (copy @0x{copy:X}, row {lk.Row}, " +
                                $"{(us ? "US" : "JP")} half mismatch at 0x{fo + i:X}). European GOG builds have a different " +
                                "table layout that is not yet decoded — puzzle-code scramble is only supported on the " +
                                "JP-master dual-region executable (the english/japanese PC port). Nothing was written.");
                    }
                }
            }
        }
    }

    /// <summary>Outcome of scrambling one lock.</summary>
    /// <param name="Lock">The lock definition.</param>
    /// <param name="NewCode">The digits written.</param>
    /// <param name="DocFile">The room file rewritten, or null if the lock has no document.</param>
    /// <param name="DocBytes">The new on-disk bytes for <paramref name="DocFile"/>, or null.</param>
    /// <param name="DocRewritten">True if the document glyph run was located and rewritten.</param>
    public sealed record LockResult(CodeLock Lock, int[] NewCode, string? DocFile, byte[]? DocBytes, bool DocRewritten);

    /// <summary>
    /// Scramble every lock in-place in <paramref name="exe"/>, returning per-lock results (with new document
    /// bytes for the caller to write). Pure w.r.t. the filesystem: <paramref name="readDoc"/> supplies room
    /// bytes and nothing is written here. <paramref name="codeForRow"/> derives the new code for a row (e.g.
    /// <see cref="MgmtOfficeSafeCode.DeriveFromSeed"/> combined with the row). Refuses (via
    /// <see cref="VerifyStockKeypadTable"/>) an exe without the JP-master table before touching anything.
    /// <paramref name="requireDocuments"/> is false only on REbirth-English installs, whose documents live
    /// in ddraw.dll and are synced by <see cref="DinoRand.FileFormats.Exe.RebirthTextPatcher"/> instead of
    /// the room files — the caller MUST apply that lever or displayed != checked.
    /// </summary>
    public static IReadOnlyList<LockResult> Scramble(
        byte[] exe, Func<CodeLock, byte[]?> readDoc, Func<CodeLock, int[]> codeForRow,
        bool requireDocuments = true)
    {
        VerifyStockKeypadTable(exe);
        var results = new List<LockResult>(Family.Count);
        foreach (var lk in Family)
        {
            int[] code = codeForRow(lk);
            if (code.Length != MgmtOfficeSafeCode.DigitCount)
                throw new ArgumentException($"row {lk.Row}: code must be 4 digits");

            MgmtOfficeSafeCode.WriteRow(exe, lk.Row, code, bothRegions: true);

            string? docFile = null; byte[]? docBytes = null; bool rewritten = false;
            if (lk.DocFile is not null && requireDocuments)
            {
                if (readDoc(lk) is { } raw)
                {
                    var room = RoomFile.Read(lk.DocStage, lk.DocRoom, raw);
                    var rdt = (byte[])room.RdtBuffer.Clone();
                    if (MgmtOfficeDocumentCode.TryRewriteKnownCode(rdt, lk.OriginalDigits, code))
                    {
                        docBytes = room.WriteWithRdt(rdt);
                        docFile = lk.DocFile;
                        rewritten = true;
                    }
                }

                // Fail loud: a lock that is SUPPOSED to have a document must actually get rewritten. Silently
                // shipping the EXE code change alone would make the safe accept the new code while the document
                // still shows the old one (displayed != checked). The usual cause is a non-pristine install
                // whose room-document text was repacked/blanked away. Scramble is filesystem-pure and callers
                // (ApplyToInstall) only write after it returns, so throwing here leaves nothing half-applied.
                if (!rewritten)
                    throw new InvalidOperationException(
                        $"Puzzle-code scramble refused for {lk.Name} (row {lk.Row}): could not locate/rewrite the " +
                        $"code {string.Concat(lk.OriginalDigits)} in document '{lk.DocFile}' — the install may not be " +
                        $"pristine (document text missing or altered). Aborting so the safe does not accept " +
                        $"{string.Concat(code)} while the document still shows the old combination (displayed != checked).");
            }
            results.Add(new LockResult(lk, code, docFile, docBytes, rewritten));
        }
        return results;
    }

    /// <summary>
    /// Directory convenience: read <c>DINO.exe</c> + the family's room files from an installed game, scramble,
    /// and write the patched files back. Backs up each target once (<c>.dinorand-codebak</c>) before writing.
    /// Returns the results. <paramref name="codeForRow"/> defaults to a seed-derived per-row code.
    /// </summary>
    public static IReadOnlyList<LockResult> ApplyToInstall(
        string exePath, string dataDir, int seed, Func<CodeLock, int[]>? codeForRow = null)
    {
        codeForRow ??= lk => DeriveRowCode(seed, lk.Row);
        // Backup first and scramble the PRISTINE bytes — a re-run must not verify/derive from an
        // already-scrambled exe. Only the table region is transplanted into the live exe, so the edit
        // stays additive w.r.t. other exe patches (non-compounding, pristine-source + transplant).
        BackupOnce(exePath);
        byte[] pristine = File.ReadAllBytes(exePath + ".dinorand-codebak");

        var results = Scramble(
            pristine,
            lk =>
            {
                var p = Path.Combine(dataDir, lk.DocFile!);
                var bak = p + ".dinorand-codebak";
                if (File.Exists(bak)) return File.ReadAllBytes(bak); // pristine doc from a prior run
                return File.Exists(p) ? File.ReadAllBytes(p) : null;
            },
            codeForRow);

        byte[] exe = File.ReadAllBytes(exePath);
        CopyTableRegion(pristine, exe);
        File.WriteAllBytes(exePath, exe);
        foreach (var r in results)
        {
            if (r is { DocRewritten: true, DocFile: { } f, DocBytes: { } b })
            {
                string p = Path.Combine(dataDir, f);
                BackupOnce(p);
                File.WriteAllBytes(p, b);
            }
        }
        return results;
    }

    /// <summary>Deterministic per-row code from a seed (distinct code per row).</summary>
    public static int[] DeriveRowCode(int seed, int row)
        => MgmtOfficeSafeCode.DeriveFromSeed(unchecked(seed * 31 + row + 1));

    /// <summary>
    /// Transplant the whole keypad-table region (all 6 rows × both halves × all 8 copies) from
    /// <paramref name="from"/> into <paramref name="to"/> — the pristine-source + additive-transplant
    /// contract: scramble a PRISTINE exe image, then move only the table bytes into the live exe so the
    /// edit composes with other exe patches and re-running a seed never compounds.
    /// </summary>
    public static void CopyTableRegion(ReadOnlySpan<byte> from, Span<byte> to)
    {
        foreach (int copy in MgmtOfficeSafeCode.JpRow0FileOffsets)
        {
            int span = 2 * MgmtOfficeSafeCode.RowCount * MgmtOfficeSafeCode.DigitCount; // 12 rows * 4 B
            from.Slice(copy, span).CopyTo(to.Slice(copy, span));
        }
    }

    private static void BackupOnce(string path)
    {
        string bak = path + ".dinorand-codebak";
        if (!File.Exists(bak) && File.Exists(path))
            File.Copy(path, bak);
    }
}
