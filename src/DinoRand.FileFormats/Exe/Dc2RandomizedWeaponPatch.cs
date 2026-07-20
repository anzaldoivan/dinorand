namespace DinoRand.FileFormats.Exe;

/// <summary>Atomic ownership/layout patch for DC2 randomized MAIN weapons. Selection belongs to
/// DinoRand.Randomizer; this type only applies and validates an explicit three-id Regina set.</summary>
public static class Dc2RandomizedWeaponPatch
{
    public static IReadOnlyList<byte> Domain { get; } = new byte[] { 0x03, 0x04, 0x06, 0x07, 0x08, 0x09 };

    private static readonly byte[] Dual = { 0x00, 0x05, 0x10, 0x13, 0x14, 0x16, 0x19 };
    private static readonly byte[] DavidOnly = { 0x0A, 0x0B, 0x15 };
    private static readonly byte[] DylanOnly = { 0x01, 0x11, 0x17 };
    private static readonly byte[] ReginaOnly = { 0x02, 0x12, 0x18 };

    public static void Apply(byte[] exe, IReadOnlyCollection<byte> reginaOnly)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ArgumentNullException.ThrowIfNull(reginaOnly);
        var regina = reginaOnly.ToHashSet();
        if (regina.Count != 3 || !regina.All(Domain.Contains))
            throw new ArgumentException("The Regina layout must contain exactly three distinct randomized MAIN ids.", nameof(reginaOnly));

        var candidate = (byte[])exe.Clone();
        Dc2CrossCharWeaponPatch.Apply(candidate);

        foreach (byte id in Domain)
            Dc2OwnerBits.SetOwners(candidate, id, regina.Contains(id), !regina.Contains(id));
        foreach (byte id in Dual) Dc2OwnerBits.SetOwners(candidate, id, true, true);
        foreach (byte id in DavidOnly) Dc2OwnerBits.SetOwners(candidate, id, false, false);
        foreach (byte id in DylanOnly) Dc2OwnerBits.SetOwners(candidate, id, false, true);
        foreach (byte id in ReginaOnly) Dc2OwnerBits.SetOwners(candidate, id, true, false);

        ValidateOwnedMainSlots(candidate);
        candidate.CopyTo(exe, 0);
    }

    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        var candidate = (byte[])exe.Clone();
        foreach (byte id in Domain)
        {
            Dc2OwnerBits.Revoke(candidate, id, Dc2WeaponOwner.Regina);
            Dc2OwnerBits.Revoke(candidate, id, Dc2WeaponOwner.Dylan);
        }
        Dc2CrossCharWeaponPatch.Restore(candidate);
        candidate.CopyTo(exe, 0);
    }

    /// <summary>Fail when any Regina/Dylan-owned MAIN row resolves through a NULL file slot.</summary>
    public static void ValidateOwnedMainSlots(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        foreach (byte id in Enumerable.Range(0, 12).Select(i => (byte)i))
        {
            foreach (var (owner, target) in new[]
                     {
                         (Dc2WeaponOwner.Regina, Dc2CrossCharTarget.Regina),
                         (Dc2WeaponOwner.Dylan, Dc2CrossCharTarget.Dylan),
                     })
            {
                if (!Dc2OwnerBits.HasOwner(exe, id, owner)) continue;
                int slot = Dc2CrossCharWeaponPatch.CatalogSlot(target, id);
                if (Dc2CrossCharWeaponPatch.ReadCatalogName(exe, slot) is null)
                    throw new InvalidOperationException(
                        $"{owner} owns MAIN weapon 0x{id:X2}, but file-catalog slot 0x{slot:X} is NULL; refusing an unsafe layout.");
            }
        }
    }
}
