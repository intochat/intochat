using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class HardwareProbeFacts
{
    private const long MiB = 1024L * 1024L;

    [Fact]
    public void NvidiaSmiMegabytesAreSummed() =>
        Assert.Equal(36L * 1024 * MiB, HardwareProbe.ParseNvidiaSmi("12288\n24576\n"));

    [Fact]
    public void CpuIsAlwaysListed()
    {
        var hw = new HardwareProbe().Read();
        Assert.Contains(Accelerator.Cpu, hw.Accelerators);
        Assert.True(hw.RamBytes > 0);
    }
}
