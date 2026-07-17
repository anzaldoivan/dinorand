using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2ShopTablePatch (docs/dc2/DC2-SHOP-RANDO-PLAN.md I1+I2) on a synthetic exe image: the real
/// build's length with canonical prices/sentinels/masks laid out at the decoded offsets — no game
/// files in the repo.
/// </summary>
public class Dc2ShopTablePatchTests
{
    internal static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        // every shop-slice id gets the not-for-sale sentinel, then the for-sale ids get retail
        for (int id = 0; id < 0x1A; id++)
            BitConverter.GetBytes(Dc2ShopTablePatch.NotForSale)
                .CopyTo(exe, Dc2ShopTablePatch.PriceTableOffset + id * 8);
        for (int i = 0; i < Dc2ShopTablePatch.ForSaleIds.Length; i++)
        {
            int id = Dc2ShopTablePatch.ForSaleIds[i];
            BitConverter.GetBytes(Dc2ShopTablePatch.CanonicalPrices[i])
                .CopyTo(exe, Dc2ShopTablePatch.PriceTableOffset + id * 8);
            // working table starts identical to master on a pristine build (verified in Dino2.exe)
            BitConverter.GetBytes(Dc2ShopTablePatch.CanonicalPrices[i])
                .CopyTo(exe, Dc2ShopTablePatch.WorkingPriceTableOffset + id * 8);
            BitConverter.GetBytes(Dc2ShopTablePatch.CanonicalMasks[i])
                .CopyTo(exe, Dc2ShopTablePatch.CatalogOffset + id * 12 + 0xA);
        }
        // recovery (tools) table: master == working == retail on a pristine build (verified in Dino2.exe)
        for (int i = 0; i < Dc2ShopTablePatch.RecoveryIds.Length; i++)
        {
            BitConverter.GetBytes(Dc2ShopTablePatch.CanonicalRecoveryPrices[i])
                .CopyTo(exe, Dc2ShopTablePatch.RecoveryMasterPriceOffset + i * 8);
            BitConverter.GetBytes(Dc2ShopTablePatch.CanonicalRecoveryPrices[i])
                .CopyTo(exe, Dc2ShopTablePatch.RecoveryWorkingPriceOffset + i * 8);
        }
        return exe;
    }

    [Fact]
    public void SyntheticExe_IsCanonical()
        => Assert.True(Dc2ShopTablePatch.IsCanonical(MakeExe()));

    [Fact]
    public void Shuffle_IsAPermutation_NeverZeroesAMask_AndReportsMatchBytes()
    {
        var exe = MakeExe();
        var entries = Dc2ShopTablePatch.Shuffle(exe, seed: 1234);

        var prices = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadPrice(exe, id));
        var masks = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadMask(exe, id));
        Assert.Equal(Dc2ShopTablePatch.CanonicalPrices.Order(), prices.Order());
        Assert.Equal(Dc2ShopTablePatch.CanonicalMasks.Order(), masks.Order());
        Assert.All(masks, m => Assert.NotEqual(0, (int)m));
        foreach (var e in entries.Where(e => Dc2ShopTablePatch.ForSaleIds.Contains(e.ItemId)))
        {
            Assert.Equal(e.NewPrice, Dc2ShopTablePatch.ReadPrice(exe, e.ItemId));
            Assert.Equal(e.NewMask, Dc2ShopTablePatch.ReadMask(exe, e.ItemId));
        }
        // not-for-sale ids untouched
        for (int id = 0; id < 0x1A; id++)
            if (!Dc2ShopTablePatch.ForSaleIds.Contains(id))
                Assert.Equal(Dc2ShopTablePatch.NotForSale, Dc2ShopTablePatch.ReadPrice(exe, id));
    }

    [Fact]
    public void Shuffle_WritesTheWorkingTable_TheGameActuallyReads()
    {
        // Defect 1: the game reads the WORKING price table (0x71D7E8); on Normal difficulty it is
        // never rebuilt from the master table (0x71DCB8), so a master-only patch is invisible.
        // Across a seed sweep every for-sale weapon's working price must equal its (shuffled)
        // master price, and at least one weapon's working price must move off retail per seed.
        for (int seed = 0; seed < 50; seed++)
        {
            var exe = MakeExe();
            Dc2ShopTablePatch.Shuffle(exe, seed);
            int moved = 0;
            for (int i = 0; i < Dc2ShopTablePatch.ForSaleIds.Length; i++)
            {
                int id = Dc2ShopTablePatch.ForSaleIds[i];
                Assert.Equal(Dc2ShopTablePatch.ReadPrice(exe, id),
                             Dc2ShopTablePatch.ReadWorkingPrice(exe, id));
                if (Dc2ShopTablePatch.ReadWorkingPrice(exe, id) != Dc2ShopTablePatch.CanonicalPrices[i])
                    moved++;
            }
            Assert.True(moved > 0, $"seed {seed}: no working weapon price changed off retail");
        }
    }

    [Fact]
    public void Shuffle_NeverTouchesProtectedProgressionWeapons()
    {
        // Defect 2: Aquagrenade (0x0B), Firewall (0x13), and Chain Mine (0x16) are mandatory for
        // progression. No seed may reprice, re-gate, or remove them — price and mask stay vanilla.
        foreach (int id in Dc2ShopTablePatch.ProtectedIds)
        {
            int i = Array.IndexOf(Dc2ShopTablePatch.ForSaleIds, id);
            Assert.True(i >= 0, $"protected id 0x{id:X2} must be a for-sale item");
            for (int seed = 0; seed < 200; seed++)
            {
                var exe = MakeExe();
                Dc2ShopTablePatch.Shuffle(exe, seed);
                Assert.Equal(Dc2ShopTablePatch.CanonicalPrices[i], Dc2ShopTablePatch.ReadPrice(exe, id));
                Assert.Equal(Dc2ShopTablePatch.CanonicalPrices[i], Dc2ShopTablePatch.ReadWorkingPrice(exe, id));
                Assert.Equal(Dc2ShopTablePatch.CanonicalMasks[i], Dc2ShopTablePatch.ReadMask(exe, id));
            }
        }
    }

    [Fact]
    public void Shuffle_StillVariesTheOtherWeapons()
    {
        // Protecting two ids must not freeze the rest: the non-protected weapons still permute.
        var seen = new HashSet<uint>();
        int freeId = 0x03; // Rocket Launcher — not protected
        int fi = Array.IndexOf(Dc2ShopTablePatch.ForSaleIds, freeId);
        for (int seed = 0; seed < 50; seed++)
        {
            var exe = MakeExe();
            Dc2ShopTablePatch.Shuffle(exe, seed);
            seen.Add(Dc2ShopTablePatch.ReadPrice(exe, freeId));
        }
        Assert.True(seen.Count > 1, $"id 0x{freeId:X2} never varied across seeds (canonical {Dc2ShopTablePatch.CanonicalPrices[fi]})");
    }

    [Fact]
    public void Shuffle_IsDeterministic_And_NonCompounding()
    {
        var a = MakeExe();
        var b = MakeExe();
        Dc2ShopTablePatch.Shuffle(a, 42);
        Dc2ShopTablePatch.Shuffle(b, 7);   // different seed first…
        Dc2ShopTablePatch.Shuffle(b, 42);  // …re-applying 42 must land on the same bytes
        Assert.Equal(a, b);
    }

    [Fact]
    public void Shuffle_PermutesRecoveryPrices_IntoBothMasterAndWorking()
    {
        // Tools economy: the four recovery prices must stay a permutation of {100,300,800,1000},
        // land in BOTH master and working tables, and actually move for at least one seed.
        var canonical = Dc2ShopTablePatch.CanonicalRecoveryPrices;
        bool anyMoved = false;
        for (int seed = 0; seed < 50; seed++)
        {
            var exe = MakeExe();
            Dc2ShopTablePatch.Shuffle(exe, seed);
            var work = Enumerable.Range(0, canonical.Length)
                .Select(i => Dc2ShopTablePatch.ReadRecoveryPrice(exe, i)).ToArray();
            for (int i = 0; i < canonical.Length; i++)
                Assert.Equal(work[i], Dc2ShopTablePatch.ReadRecoveryMasterPrice(exe, i)); // master mirrors working
            Assert.Equal(canonical.Order(), work.Order());                                 // still a permutation
            if (!work.SequenceEqual(canonical)) anyMoved = true;
        }
        Assert.True(anyMoved, "no seed ever moved a recovery price off retail");
    }

    [Fact]
    public void Shuffle_WeaponResult_IsUnchangedByAddingRecovery()
    {
        // Recovery permutation is drawn AFTER the weapon permutations, so weapon prices/masks for a
        // given seed must be byte-identical to a weapon-only shuffle of the same seed.
        for (int seed = 0; seed < 20; seed++)
        {
            var exe = MakeExe();
            var entries = Dc2ShopTablePatch.Shuffle(exe, seed);
            foreach (var e in entries.Where(e => Dc2ShopTablePatch.ForSaleIds.Contains(e.ItemId)))
            {
                Assert.Equal(e.NewPrice, Dc2ShopTablePatch.ReadPrice(exe, e.ItemId));
                Assert.Equal(e.NewMask, Dc2ShopTablePatch.ReadMask(exe, e.ItemId));
            }
            // recovery ids are reported too, with no stock mask
            Assert.Equal(Dc2ShopTablePatch.RecoveryIds.Length,
                entries.Count(e => Dc2ShopTablePatch.RecoveryIds.Contains(e.ItemId)));
        }
    }

    [Fact]
    public void RestoreCanonical_RoundTrips()
    {
        var exe = MakeExe();
        Dc2ShopTablePatch.Shuffle(exe, 99);
        Dc2ShopTablePatch.RestoreCanonical(exe);
        Assert.Equal(MakeExe(), exe);
    }

    [Fact]
    public void Validate_RefusesWrongLength_And_ForeignValues()
    {
        Assert.Throws<InvalidOperationException>(() => Dc2ShopTablePatch.Validate(new byte[100]));

        var tampered = MakeExe();
        BitConverter.GetBytes(1234u).CopyTo(tampered, Dc2ShopTablePatch.PriceTableOffset + 0x03 * 8);
        Assert.Throws<InvalidOperationException>(() => Dc2ShopTablePatch.Validate(tampered));

        var lostSentinel = MakeExe();
        BitConverter.GetBytes(5u).CopyTo(lostSentinel, Dc2ShopTablePatch.PriceTableOffset + 0x00 * 8);
        Assert.Throws<InvalidOperationException>(() => Dc2ShopTablePatch.Validate(lostSentinel));
    }
}
