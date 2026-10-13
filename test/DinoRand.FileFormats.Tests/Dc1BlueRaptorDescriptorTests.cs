using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1BlueRaptorDescriptorTests
{
    [Fact]
    public void BlueDescriptor_IsPinnedToObservedGogBuildAndExactSourceBytes()
    {
        Assert.Equal(0x00657180u, Dc1BlueRaptorDescriptorPatch.DescriptorTableVa);
        Assert.Equal(0x0065718Au, Dc1BlueRaptorDescriptorPatch.MaxHpVa);
        Assert.Equal(0x25718A, Dc1BlueRaptorDescriptorPatch.MaxHpFileOffset);
        Assert.Equal(Dc1BlueRaptorDescriptorPatch.MaxHpFileOffset,
            ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa));
        Assert.Equal(0x28C000, Dc1BlueRaptorDescriptorPatch.SupportedBuildLength);
        Assert.Equal("7ACC3DC1A3F3398E08E5BDAD746BDBF98CA5507E9B9BA5FBA709031B498D3FCA",
            Dc1BlueRaptorDescriptorPatch.SupportedBuildSha256);
        Assert.Equal(new byte[] { 0x52, 0x03 }, Dc1BlueRaptorDescriptorPatch.ExpectedSourceBytes.ToArray());
    }

    [Fact]
    public void BlueDescriptorPlan_PatchesOnlyDescriptorZeroMaxHp_AndComposes()
    {
        var exe = LoadSupportedExe();
        if (exe is null) return;
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(),
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        });

        var composed = ExePatchInstallerTestAccess.Compose(exe, plan);

        Assert.Equal((ushort)1500,
            ExePatcher.ReadUInt16AtVa(composed, Dc1BlueRaptorDescriptorPatch.MaxHpVa));
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(composed, ExePatcher.Stage2AiRecordVa + 8 * 4));
        Assert.Equal(5, Enumerable.Range(0, composed.Length).Count(i => composed[i] != exe[i]));
        Assert.Equal((byte)0xDC, composed[ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa)]);
        Assert.Equal((byte)0x05, composed[ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa) + 1]);
        Assert.Equal((byte)0, composed[ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa) + 2]);
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesSyntheticImageWithoutPartialMutation()
    {
        var exe = new byte[Dc1BlueRaptorDescriptorPatch.SupportedBuildLength];
        var before = exe.ToArray();
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
        Assert.Equal(before, exe);
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesWrongSourceImageWithoutPartialMutation()
    {
        var exe = new byte[Dc1BlueRaptorDescriptorPatch.SupportedBuildLength];
        var before = exe.ToArray();
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
        Assert.Equal(before, exe);
    }

    [Fact]
    public void BlueDescriptorPlan_AllowsDuplicateIdenticalRequests()
    {
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(),
            ExePatchRequest.BlueRaptorDescriptor(),
        });

        Assert.Single(plan.Requests.Distinct());
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesWrongBuildLengthWithoutPartialMutation()
    {
        var exe = new byte[Dc1BlueRaptorDescriptorPatch.SupportedBuildLength - 1];
        var before = exe.ToArray();
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
        Assert.Equal(before, exe);
    }

    [Fact]
    public void BlueDescriptorPlan_JsonRoundTripsWithItsBuildGuards()
    {
        var request = ExePatchRequest.BlueRaptorDescriptor();
        var roundTrip = ExePatchPlan.FromJson(
            new ExePatchPlan(ExePatchPlan.CurrentVersion, new[] { request }).ToJson());

        Assert.Equal(request, Assert.Single(roundTrip.Requests));
    }

    private static byte[]? LoadSupportedExe()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = Path.Combine(root, "DINO.exe");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static class ExePatchInstallerTestAccess
    {
        public static byte[] Compose(byte[] pristine, ExePatchPlan plan)
        {
            var method = typeof(ExePatchInstaller).GetMethod("ComposeExePatchPlan",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            try
            {
                var composed = method.Invoke(null, new object[] { pristine, plan, ".", "." })!;
                return (byte[])composed.GetType().GetProperty("Bytes")!.GetValue(composed)!;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
