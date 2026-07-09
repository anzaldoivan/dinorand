using System;
using System.Collections.Generic;
using System.IO;
using DinoRand.FileFormats.Exe;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Scrambles the DC1 keypad-code puzzle family (see <c>data/dc1/puzzle-codes.json</c> and
/// <c>docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md</c> §17) with one common function: for each of the six
/// 4-digit locks it writes the new code into every recompiled copy of the DINO.exe keypad table (both
/// region halves) and, for the three locks that also have an in-game document stating the code, rewrites the
/// room document's glyph-token run to the same number — preserving <b>displayed == checked</b>.
///
/// <para>The document edit is a real on-disk repack: decompress the room RDT (via <see cref="RoomFile"/>),
/// rewrite the digit glyphs (<see cref="MgmtOfficeDocumentCode"/>), re-LZSS + repack the package entry
/// (<see cref="RoomFile.WriteWithRdt"/>). Rows 3–5 have no readable document, so only the EXE is touched.</para>
/// </summary>
public static class Dc1PuzzleCodeSync
{
    /// <summary>One 4-digit lock in the shared keypad table.</summary>
    /// <param name="Row">Table row (0..5).</param>
    /// <param name="OriginalDigits">The code as it ships (JP build) — used to locate the document run.</param>
    /// <param name="Name">Human-readable lock name.</param>
    /// <param name="DocFile">Room .dat that displays the code, or null if none.</param>
    /// <param name="DocStage">Stage index for <see cref="RoomFile.Read"/> (metadata only).</param>
    /// <param name="DocRoom">Room index for <see cref="RoomFile.Read"/> (metadata only).</param>
    public sealed record CodeLock(int Row, int[] OriginalDigits, string Name,
                                  string? DocFile, int DocStage, int DocRoom);

    /// <summary>The six locks, in table-row order (JP-build codes).</summary>
    public static readonly IReadOnlyList<CodeLock> Family = new[]
    {
        new CodeLock(0, new[] { 0, 3, 7, 5 }, "Management Office safe",        "st100.dat", 1, 0),
        new CodeLock(1, new[] { 7, 6, 8, 7 }, "Lounge safe",                   "st200.dat", 2, 0),
        new CodeLock(2, new[] { 5, 0, 3, 7 }, "Computer Room gas-room code",   "st302.dat", 3, 2),
        new CodeLock(3, new[] { 0, 3, 6, 7 }, "Stabilizer Design Room code A", null, 0, 0),
        new CodeLock(4, new[] { 0, 2, 0, 4 }, "Stabilizer Design Room code B", null, 0, 0),
        new CodeLock(5, new[] { 1, 2, 8, 1 }, "Stabilizer Experiment Room safe", null, 0, 0),
    };

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
    /// <see cref="MgmtOfficeSafeCode.DeriveFromSeed"/> combined with the row).
    /// </summary>
    public static IReadOnlyList<LockResult> Scramble(
        byte[] exe, Func<CodeLock, byte[]?> readDoc, Func<CodeLock, int[]> codeForRow)
    {
        var results = new List<LockResult>(Family.Count);
        foreach (var lk in Family)
        {
            int[] code = codeForRow(lk);
            if (code.Length != MgmtOfficeSafeCode.DigitCount)
                throw new ArgumentException($"row {lk.Row}: code must be 4 digits");

            MgmtOfficeSafeCode.WriteRow(exe, lk.Row, code, bothRegions: true);

            string? docFile = null; byte[]? docBytes = null; bool rewritten = false;
            if (lk.DocFile is not null)
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
        byte[] exe = File.ReadAllBytes(exePath);

        var results = Scramble(
            exe,
            lk => { var p = Path.Combine(dataDir, lk.DocFile!); return File.Exists(p) ? File.ReadAllBytes(p) : null; },
            codeForRow);

        BackupOnce(exePath);
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

    private static void BackupOnce(string path)
    {
        string bak = path + ".dinorand-codebak";
        if (!File.Exists(bak) && File.Exists(path))
            File.Copy(path, bak);
    }
}
