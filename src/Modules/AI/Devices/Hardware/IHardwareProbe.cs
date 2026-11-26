namespace IntoChat.OS.Modules.Devices;

internal interface IHardwareProbe
{
    Hardware Read();

    void ReportNpu();
}
