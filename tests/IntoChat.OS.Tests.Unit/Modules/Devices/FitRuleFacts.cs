using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class FitRuleFacts
{
    private const long GiB = 1024L * 1024L * 1024L;

    public static TheoryData<long?, long, long, Accelerator[], Fit> Cases => new()
    {
        { 4L * GiB, 16 * GiB, 0, [Accelerator.Cpu], Fit.Slow },
        { 20L * GiB, 16 * GiB, 0, [Accelerator.Cpu], Fit.WontFit },
        { 9L * GiB, 32 * GiB, 12 * GiB, [Accelerator.Cpu, Accelerator.Cuda], Fit.Good },
        { 12L * GiB, 32 * GiB, 12 * GiB, [Accelerator.Cpu, Accelerator.Cuda], Fit.Slow },
        { 9L * GiB, 16 * GiB, 16 * GiB, [Accelerator.Cpu, Accelerator.Metal], Fit.Good },
        { null, 64 * GiB, 24 * GiB, [Accelerator.Cpu, Accelerator.Cuda], Fit.Slow },
    };

    [Theory, MemberData(nameof(Cases))]
    public void FitFollowsTheFormula(long? bytes, long ram, long vram, Accelerator[] acc, Fit expected) =>
        Assert.Equal(expected, FitRule.Of(bytes, new Hardware(ram, vram, acc)));
}
