using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Hosting;

public sealed class FlutterHostOptionsGrammarFacts
{
    [Fact]
    public void TheModuleSectionBinds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [FlutterHostOptions.WebContainerKey] = "true",
            [FlutterHostOptions.SectionName + ":AndroidTransport"] = "Usb",
            [FlutterHostOptions.SectionName + ":AndroidDevice"] = "47041FDAS002XE",
        }).Build();

        var options = FlutterHostOptions.Overlay(new FlutterHostOptions(), configuration);

        Assert.True(options.WebContainer);
        Assert.Equal(AndroidDebugTransport.Usb, options.AndroidTransport);
        Assert.Equal("47041FDAS002XE", options.AndroidDevice);
    }

    [Fact]
    public void ParsesMdnsConnectEndpointsOnly()
    {
        const string output = """
            List of discovered mdns services
            adb-47041FDAS002XE-npOx1b	_adb-tls-connect._tcp	192.168.0.164:44495
            adb-47041FDAS002XE-pair	_adb-tls-pairing._tcp	192.168.0.164:39123
            """;

        var endpoints = FlutterClientTargets.ParseMdnsConnectEndpoints(output);

        Assert.Single(endpoints);
        Assert.Equal("192.168.0.164:44495", endpoints[0]);
    }

    [Fact]
    public void ParsesOnlineDevicesAndDistinguishesWirelessFromUsb()
    {
        const string output = """
            List of devices attached
            47041FDAS002XE         device product:komodo model:Pixel_9_Pro_XL
            192.168.0.164:44495    device product:komodo model:Pixel_9_Pro_XL
            offline-serial         offline
            unauth-serial          unauthorized
            """;

        var serials = FlutterClientTargets.ParseOnlineDeviceSerials(output);

        Assert.Equal(["47041FDAS002XE", "192.168.0.164:44495"], serials);
        Assert.False(FlutterClientTargets.IsWirelessSerial("47041FDAS002XE"));
        Assert.True(FlutterClientTargets.IsWirelessSerial("192.168.0.164:44495"));
        Assert.True(FlutterClientTargets.IsWirelessSerial("adb-47041FDAS002XE._adb-tls-connect._tcp"));
    }
}
