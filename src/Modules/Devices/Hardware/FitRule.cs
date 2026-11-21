namespace IntoChat.OS.Modules.Devices;

internal static class FitRule
{
    private const double WeightOverheadFactor = 1.2;
    private const double RuntimeOverheadBytes = 512d * 1024d * 1024d;
    private const double MetalGpuShare = 0.75;
    private const double RamReserveBytes = 4d * 1024d * 1024d * 1024d;
    private const double CpuOffloadShare = 0.8;

    public static Fit Of(long? bytes, Hardware hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        if (bytes is not { } modelBytes || modelBytes <= 0)
        {
            return Fit.Slow;
        }

        var need = (modelBytes * WeightOverheadFactor) + RuntimeOverheadBytes;
        var accelerators = hardware.Accelerators ?? [];
        var hasMetal = accelerators.Contains(Accelerator.Metal);
        var hasDiscreteGpu = accelerators.Contains(Accelerator.Cuda) || accelerators.Contains(Accelerator.DirectML);

        var gpu = hasMetal
            ? hardware.RamBytes * MetalGpuShare
            : hasDiscreteGpu ? hardware.VramBytes : 0d;

        if (need <= gpu)
        {
            return Fit.Good;
        }

        var usableRam = Math.Max(0d, hardware.RamBytes - RamReserveBytes) * CpuOffloadShare;
        return need <= gpu + usableRam ? Fit.Slow : Fit.WontFit;
    }
}
