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
    private static byte[] MakeExe()
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
            BitConverter.GetBytes(Dc2ShopTablePatch.CanonicalMasks[i])
                .CopyTo(exe, Dc2ShopTablePatch.CatalogOffset + id * 12 + 0xA);
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
        foreach (var e in entries)
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
