using System.Security.Cryptography;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1BlueRaptorDescriptorTests
{
    [Fact]
    public void BlueDescriptorPlan_PatchesOnlyDescriptorZeroMaxHp_AndComposes()
    {
        var exe = new byte[0x270000];
        ExePatcher.WriteUInt16(exe, ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa), 850);
        var hash = Convert.ToHexString(SHA256.HashData(exe));
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(hash, exe.Length),
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        });

        var composed = ExePatchInstallerTestAccess.Compose(exe, plan);

        Assert.Equal((ushort)1500,
            ExePatcher.ReadUInt16AtVa(composed, Dc1BlueRaptorDescriptorPatch.MaxHpVa));
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(composed, ExePatcher.Stage2AiRecordVa + 8 * 4));
        Assert.Equal(5, composed.Count(b => b != exe[b]));
        Assert.Equal((byte)0xDC, composed[ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa)]);
        Assert.Equal((byte)0x05, composed[ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa) + 1]);
        Assert.Equal((byte)0, composed[ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa) + 2]);
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesWrongHashWithoutPartialMutation()
    {
        var exe = new byte[0x270000];
        ExePatcher.WriteUInt16(exe, ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa), 850);
        var before = exe.ToArray();
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(new string('0', 64), exe.Length),
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
        Assert.Equal(before, exe);
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesWrongExpectedByteWithoutPartialMutation()
    {
        var exe = new byte[0x270000];
        ExePatcher.WriteUInt16(exe, ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa), 1000);
        var before = exe.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(exe));
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(hash, exe.Length),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
        Assert.Equal(before, exe);
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesDuplicateConflictingRequests()
    {
        var exe = new byte[0x270000];
        ExePatcher.WriteUInt16(exe, ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa), 850);
        var hash = Convert.ToHexString(SHA256.HashData(exe));
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(hash, exe.Length),
            ExePatchRequest.BlueRaptorDescriptor(new string('1', 64), exe.Length),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
    }

    [Fact]
    public void BlueDescriptorPlan_RefusesWrongBuildLengthWithoutPartialMutation()
    {
        var exe = new byte[0x270000];
        ExePatcher.WriteUInt16(exe, ExePatcher.VaToFileOffset(Dc1BlueRaptorDescriptorPatch.MaxHpVa), 850);
        var before = exe.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(exe));
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.BlueRaptorDescriptor(hash, exe.Length + 1),
        });

        Assert.Throws<InvalidOperationException>(() => ExePatchInstallerTestAccess.Compose(exe, plan));
        Assert.Equal(before, exe);
    }

    [Fact]
    public void BlueDescriptorPlan_JsonRoundTripsWithItsBuildGuards()
    {
        var request = ExePatchRequest.BlueRaptorDescriptor(new string('A', 64), 123);
        var roundTrip = ExePatchPlan.FromJson(
            new ExePatchPlan(ExePatchPlan.CurrentVersion, new[] { request }).ToJson());

        Assert.Equal(request, Assert.Single(roundTrip.Requests));
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
