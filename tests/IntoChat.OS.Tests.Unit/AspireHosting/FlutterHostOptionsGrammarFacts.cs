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
            [FlutterHostOptions.SectionName + ":IosTransport"] = "Wireless",
            [FlutterHostOptions.SectionName + ":IosDevice"] = "00008140-0011223344556677",
            [FlutterHostOptions.SectionName + ":IosUiBase"] = "http://192.168.0.10:5080",
        }).Build();

        var options = FlutterHostOptions.Overlay(new FlutterHostOptions(), configuration);

        Assert.True(options.WebContainer);
        Assert.Equal(AndroidDebugTransport.Usb, options.AndroidTransport);
        Assert.Equal("47041FDAS002XE", options.AndroidDevice);
        Assert.Equal(IosDebugTransport.Wireless, options.IosTransport);
        Assert.Equal("00008140-0011223344556677", options.IosDevice);
        Assert.Equal("http://192.168.0.10:5080", options.IosUiBase);
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

    [Fact]
    public void ParsesDevicectlAndSimctlAndSelectsIosTransport()
    {
        const string devicectlJson = """
            {
              "result": {
                "devices": [
                  {
                    "hardwareProperties": { "udid": "UDID-WIFI", "platform": "iOS" },
                    "deviceProperties": { "name": "iPhone 17 Pro" },
                    "connectionProperties": { "transportType": "localNetwork", "pairingState": "paired" }
                  },
                  {
                    "hardwareProperties": { "udid": "UDID-USB", "platform": "iOS" },
                    "deviceProperties": { "name": "iPhone 17 Pro USB" },
                    "connectionProperties": { "transportType": "wired", "pairingState": "paired" }
                  },
                  {
                    "hardwareProperties": { "udid": "UDID-UNPAIRED", "platform": "iOS" },
                    "deviceProperties": { "name": "Unpaired iPhone" },
                    "connectionProperties": { "transportType": "localNetwork", "pairingState": "unpaired" }
                  }
                ]
              }
            }
            """;

        const string simctlJson = """
            {
              "devices": {
                "com.apple.CoreSimulator.SimRuntime.iOS-19-0": [
                  { "udid": "SIM-BOOTED", "name": "iPhone 17 Pro", "state": "Booted" },
                  { "udid": "SIM-SHUTDOWN", "name": "iPhone 16", "state": "Shutdown" }
                ]
              }
            }
            """;

        var physical = FlutterClientTargets.ParseDevicectlDevices(devicectlJson);
        var sims = FlutterClientTargets.ParseSimctlBootedDevices(simctlJson);
        var all = physical.Concat(sims).ToArray();

        Assert.Equal(2, physical.Count);
        Assert.Single(sims);
        Assert.Equal("UDID-WIFI", FlutterClientTargets.SelectIosDevice(all, IosDebugTransport.Wireless)?.Id);
        Assert.Equal("UDID-USB", FlutterClientTargets.SelectIosDevice(all, IosDebugTransport.Usb)?.Id);
        Assert.Equal("SIM-BOOTED", FlutterClientTargets.SelectIosDevice(all, IosDebugTransport.Simulator)?.Id);
        Assert.Equal("UDID-WIFI", FlutterClientTargets.SelectIosDevice(all, IosDebugTransport.Auto)?.Id);
        Assert.Equal("http://127.0.0.1:5080", FlutterClientTargets.ResolveIosUiBase(null, 5080, isSimulator: true));
        Assert.Equal("http://192.168.1.5:5080", FlutterClientTargets.ResolveIosUiBase("http://192.168.1.5:5080", 5080, isSimulator: false));
    }
}
