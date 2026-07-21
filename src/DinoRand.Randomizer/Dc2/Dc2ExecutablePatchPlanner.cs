using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2;

/// <summary>Owns DC2 seed/RNG selection while FileFormats validates and applies plans.</summary>
internal static class Dc2ExecutablePatchPlanner
{
    public static Dc2ExecutablePatchPlan PlanMusic(int seed, IReadOnlyDictionary<string, string> classOf)
    {
        ArgumentNullException.ThrowIfNull(classOf);
        var assigned = (string[])Dc2MusicTablePatch.CanonicalNames.Clone();
        uint rng = (uint)seed;
        foreach (string cls in classOf.Values.Distinct().OrderBy(c => c, StringComparer.Ordinal))
        {
            int[] members = Enumerable.Range(0, Dc2MusicTablePatch.MusicSlotCount)
                .Where(k => classOf.TryGetValue(Dc2MusicTablePatch.CanonicalNames[k], out string? c) && c == cls)
                .ToArray();
            int[] permutation = Permutation(members.Length, ref rng);
            for (int i = 0; i < members.Length; i++)
                assigned[members[i]] = Dc2MusicTablePatch.CanonicalNames[members[permutation[i]]];
        }
        return new Dc2ExecutablePatchPlan { MusicNames = assigned };
    }

    public static Dc2ExecutablePatchPlan PlanShop(int seed, byte[] exe, bool shuffleCatalogMasks = true)
    {
        int[] free = Enumerable.Range(0, Dc2ShopTablePatch.ForSaleIds.Length)
            .Where(i => !Dc2ShopTablePatch.ProtectedIds.Contains(Dc2ShopTablePatch.ForSaleIds[i])).ToArray();
        uint rng = (uint)seed;
        int[] pricePermutation = Permutation(free.Length, ref rng);
        int[] maskPermutation = Permutation(free.Length, ref rng);
        int[] recoveryPermutation = Permutation(Dc2ShopTablePatch.RecoveryIds.Length, ref rng);
        var prices = (uint[])Dc2ShopTablePatch.CanonicalPrices.Clone();
        var masks = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadMask(exe, id)).ToArray();
        for (int k = 0; k < free.Length; k++)
        {
            prices[free[k]] = Dc2ShopTablePatch.CanonicalPrices[free[pricePermutation[k]]];
            if (shuffleCatalogMasks)
                masks[free[k]] = Dc2ShopTablePatch.CanonicalMasks[free[maskPermutation[k]]];
        }
        var recovery = recoveryPermutation.Select(i => Dc2ShopTablePatch.CanonicalRecoveryPrices[i]).ToArray();
        return new Dc2ExecutablePatchPlan { ShopPrices = prices, ShopMasks = masks, RecoveryPrices = recovery };
    }

    public static Dc2ExecutablePatchPlan PlanElevatorCodes(int seed)
    {
        uint rng = (uint)seed;
        var codes = new List<string>(Dc2ElevatorCodePatch.Imm32FileOffsets.Count);
        while (codes.Count < Dc2ElevatorCodePatch.Imm32FileOffsets.Count)
        {
            var digits = new char[Dc2ElevatorCodePatch.DigitCount];
            for (int d = 0; d < digits.Length; d++)
                digits[d] = (char)('0' + NextRand(ref rng) % Dc2ElevatorCodePatch.DigitAlphabet);
            string code = new(digits);
            if (!codes.Contains(code)) codes.Add(code);
        }
        return new Dc2ExecutablePatchPlan { ElevatorCodes = codes.ToArray() };
    }

    public static Dc2ExecutablePatchPlan PlanCircuits(Dc2CircuitPatch.RoomSpec room, Random rng)
    {
        var sequences = new int[room.Routines.Count][];
        for (int r = 0; r < room.Routines.Count; r++)
            sequences[r] = GenerateSequence(room.BoxIds, room.Routines[r].VanillaIds.Count, rng);
        return new Dc2ExecutablePatchPlan { CircuitSequences = sequences };
    }

    public static Dc2ExecutablePatchPlan PlanRequiredPlate(Random rng)
        => new() { RequiredPlate = Dc2PlateKeyPatch.PlateIds[rng.Next(Dc2PlateKeyPatch.PlateIds.Count)] };

    private static int[] GenerateSequence(IReadOnlyList<int> alphabet, int length, Random rng)
    {
        if (alphabet.Count < 2 || length < alphabet.Count)
            throw new ArgumentException($"need ≥2 box ids and length ≥ alphabet size (got {alphabet.Count} ids, length {length})");
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            var sequence = new int[length];
            int previous = -1;
            for (int i = 0; i < length; i++)
            {
                int pick;
                do pick = alphabet[rng.Next(alphabet.Count)]; while (pick == previous);
                sequence[i] = pick;
                previous = pick;
            }
            if (alphabet.All(sequence.Contains)) return sequence;
        }
        throw new InvalidOperationException("could not generate a covering blink sequence");
    }

    private static int[] Permutation(int count, ref uint rng)
    {
        var permutation = Enumerable.Range(0, count).ToArray();
        for (int i = count - 1; i > 0; i--)
        {
            int j = (int)(NextRand(ref rng) % (uint)(i + 1));
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }
        return permutation;
    }

    private static uint NextRand(ref uint state)
    {
        state += 0x9E3779B9u;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        return z ^ (z >> 15);
    }
}
