using System.Diagnostics;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Settings;

public sealed partial class SettingsApp
{
    private bool ResolveIsCloudTopology()
    {
        var raw = Configuration?[IntoChatOSNames.TopologyKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return State.IsCloudTopology;
        }

        return string.Equals(IntoChatOSNames.CanonicalTopology(raw), IntoChatOSNames.TopologyCloud, StringComparison.Ordinal);
    }

    private string ResolveCloudLinkUrl(string? overrideUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideUrl) && DeviceEnrollUri.TryParseServer(overrideUrl, out var parsedOverride, out _))
        {
            return parsedOverride!.AbsoluteUri.TrimEnd('/');
        }

        var configured = Configuration?["IntoChatOS:CloudUrl"]
            ?? Configuration?["IntoChatOS:PublicUrl"]
            ?? State.CloudLinkUrl;
        if (DeviceEnrollUri.TryParseServer(configured, out var parsedConfig, out _))
        {
            return parsedConfig!.AbsoluteUri.TrimEnd('/');
        }

        return DeviceEnrollUri.DefaultCloudUrl;
    }

    [On]
    public async Task LaunchBrowserPairing(SettingsLaunchBrowserPairing request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cloudBase = ResolveCloudLinkUrl(request.CloudUrl);
        if (!DeviceEnrollUri.TryParseServer(cloudBase, out var serverUri, out var error))
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = error ?? "Cloud URL is invalid.",
                DevicePairingFeedbackIsError = true,
                Status = error ?? "Cloud URL is invalid.",
            });
            return;
        }

        var target = DeviceEnrollUri.BuildBrowserLinkUrl(serverUri);
        try
        {
            if (OpenBrowserAction is not null)
            {
                OpenBrowserAction(target);
            }
            else
            {
                _ = Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            }

            await UpdateAsync(State with
            {
                CloudLinkUrl = cloudBase,
                DevicePairingFeedback = $"Opened {target.AbsoluteUri} in your browser. Complete sign-in to pair this PC.",
                DevicePairingFeedbackIsError = false,
                Status = "Opened browser pairing flow.",
            });
        }
#pragma warning disable CA1031 // System browser launch can fail on headless/restricted hosts.
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Could not launch browser for cloud pairing at {Url}.", target.AbsoluteUri);
            await UpdateAsync(State with
            {
                CloudLinkUrl = cloudBase,
                DevicePairingFeedback = $"Could not open browser automatically. Copy and open: {target.AbsoluteUri}",
                DevicePairingFeedbackIsError = true,
                Status = "Could not open browser automatically.",
            });
        }
#pragma warning restore CA1031
    }

    [On]
    public Task ToggleManualPairing(SettingsToggleManualPairing request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateAsync(State with
        {
            ShowManualPairingForm = !State.ShowManualPairingForm,
            DevicePairingFeedback = null,
            DevicePairingFeedbackIsError = false,
        });
    }

    [On]
    public async Task PairDevice(SettingsPairDevice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rawServer = string.IsNullOrWhiteSpace(request.Server) ? ResolveCloudLinkUrl() : request.Server;
        if (!DeviceEnrollUri.TryParseServer(rawServer, out var serverUri, out var serverError))
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = serverError ?? "Invalid server URL.",
                DevicePairingFeedbackIsError = true,
                Status = serverError ?? "Invalid server URL.",
            });
            return;
        }

        if (!DeviceEnrollUri.ValidateCode(request.Code, out var parsedCode, out var codeError))
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = codeError ?? "Invalid pairing code.",
                DevicePairingFeedbackIsError = true,
                Status = codeError ?? "Invalid pairing code.",
            });
            return;
        }

        if (DeviceLink is not { } link)
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = "Device link service is unavailable in this host.",
                DevicePairingFeedbackIsError = true,
                Status = "Device link service is unavailable.",
            });
            return;
        }

        try
        {
            var status = await link.Join(serverUri!, parsedCode);
            await UpdateAsync(ApplyDeviceLinkStatus(State, status) with
            {
                ShowManualPairingForm = false,
                DevicePairingFeedback = $"Linked to {serverUri!.Host}.",
                DevicePairingFeedbackIsError = false,
                Status = $"Connected this PC to {serverUri.Host}.",
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            Log.LogWarning(ex, "Device pairing failed in Settings.");
            await UpdateAsync(State with
            {
                DevicePairingFeedback = ex.Message,
                DevicePairingFeedbackIsError = true,
                Status = ex.Message,
            });
        }
    }

    [On]
    public async Task DisconnectDevice(SettingsDisconnectDevice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DeviceLink is not { } link)
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = "Device link service is unavailable in this host.",
                DevicePairingFeedbackIsError = true,
            });
            return;
        }

        try
        {
            await link.Leave();
            var status = await link.Status();
            await UpdateAsync(ApplyDeviceLinkStatus(State, status) with
            {
                DevicePairingFeedback = "Disconnected this PC from IntoChat Cloud.",
                DevicePairingFeedbackIsError = false,
                Status = "Disconnected this PC.",
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            Log.LogWarning(ex, "Disconnecting device link failed in Settings.");
            await UpdateAsync(State with
            {
                DevicePairingFeedback = ex.Message,
                DevicePairingFeedbackIsError = true,
                Status = ex.Message,
            });
        }
    }

    [On]
    public async Task MintEnrollmentCode(SettingsMintEnrollmentCode request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (DeviceEnrollment is not { } enrollment)
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = "Device enrollment is unavailable in this host.",
                DevicePairingFeedbackIsError = true,
            });
            return;
        }

        try
        {
            var code = await enrollment.Mint();
            var expiresAt = Context.Time.GetUtcNow() + Code.Validity;
            var serverUrl = ResolveCloudLinkUrl(request.ServerUrl);
            var handoffUri = DeviceEnrollUri.Build(new Uri(serverUrl, UriKind.Absolute), code).OriginalString;
            var refreshed = await RefreshDeviceProjectionAsync(State);
            await UpdateAsync(refreshed with
            {
                MintedEnrollmentCode = code.Value,
                MintedEnrollmentExpiresAt = expiresAt,
                MintedEnrollmentHandoffUri = handoffUri,
                DevicePairingFeedback = "Generated a single-use pairing code.",
                DevicePairingFeedbackIsError = false,
                Status = "Generated device pairing code.",
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = ex.Message,
                DevicePairingFeedbackIsError = true,
                Status = ex.Message,
            });
        }
    }

    [On]
    public async Task RevokeEnrolledDevice(SettingsRevokeEnrolledDevice request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MachineId))
        {
            return;
        }

        if (DeviceEnrollment is not { } enrollment)
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = "Device enrollment is unavailable in this host.",
                DevicePairingFeedbackIsError = true,
            });
            return;
        }

        try
        {
            var machineId = request.MachineId.Trim();
            await enrollment.Revoke(machineId);
            if (ConnectedComputeRouter is not null && TryBrainOrNull() is { Value.Length: > 0 } brain)
            {
                await ConnectedComputeRouter.MarkOfflineAsync(brain, machineId);
            }

            var refreshed = await RefreshDeviceProjectionAsync(State);
            await UpdateAsync(refreshed with
            {
                DevicePairingFeedback = $"Revoked device '{machineId}'.",
                DevicePairingFeedbackIsError = false,
                Status = $"Revoked device '{machineId}'.",
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            await UpdateAsync(State with
            {
                DevicePairingFeedback = ex.Message,
                DevicePairingFeedbackIsError = true,
                Status = ex.Message,
            });
        }
    }

    private async Task<SettingsAppState> RefreshDeviceProjectionAsync(SettingsAppState current)
    {
        var isCloud = ResolveIsCloudTopology();
        var cloudUrl = ResolveCloudLinkUrl();
        var next = current with
        {
            IsCloudTopology = isCloud,
            CloudLinkUrl = cloudUrl,
        };

        if (!isCloud && DeviceLink is { } link)
        {
            try
            {
                var status = await link.Status();
                next = ApplyDeviceLinkStatus(next, status, preserveStoredToggles: true);
            }
#pragma warning disable CA1031 // Device link neuron is optional in minimal test hosts.
            catch (Exception ex)
            {
                Log.LogDebug(ex, "Skipping IDeviceLink.Status in minimal test host.");
            }
#pragma warning restore CA1031
        }

        if (isCloud && DeviceEnrollment is { } enrollment)
        {
            try
            {
                var enrolled = await enrollment.List();
                ConnectedComputeNode[] liveNodes = [];
                if (ConnectedComputeRouter is not null && TryBrainOrNull() is { Value.Length: > 0 } brain)
                {
                    liveNodes = await ConnectedComputeRouter.ListNodesAsync(brain);
                }

                var nodesByMachine = liveNodes.ToDictionary(static n => n.MachineId, StringComparer.Ordinal);
                var rows = enrolled
                    .Select(device =>
                    {
                        nodesByMachine.TryGetValue(device.MachineId, out var live);
                        return new SettingsEnrolledDeviceRow(
                            MachineId: device.MachineId,
                            DeviceName: string.IsNullOrWhiteSpace(device.Name) ? device.MachineId : device.Name,
                            Online: !device.Revoked && live is { Status: ComputeNodeStatus.Online },
                            Revoked: device.Revoked,
                            EnrolledAt: device.EnrolledAt,
                            LastHeartbeatAt: live?.LastHeartbeatAt,
                            AdvertisedCapabilities: live?.AdvertisedCapabilities ?? [],
                            ConsentedFolders: live?.ConsentedFolderLabels ?? []);
                    })
                    .ToArray();

                next = next with { EnrolledDevices = rows };
            }
#pragma warning disable CA1031 // Device enrollment is optional in minimal test hosts.
            catch (Exception ex)
            {
                Log.LogDebug(ex, "Skipping IDeviceEnrollment.List in minimal test host.");
            }
#pragma warning restore CA1031
        }

        return next;
    }

    private static SettingsAppState ApplyDeviceLinkStatus(SettingsAppState state, LinkStatus status, bool preserveStoredToggles = false)
    {
        var hasStoredSandbox = preserveStoredToggles && HasStoredVariable(state, ClientComputeConsentPolicy.SettingsVariableKey);
        var hasStoredFolders = preserveStoredToggles && HasStoredVariable(state, ClientComputeConsentPolicy.AllowWindowsFolderGatewayVariableKey);
        var hasStoredConsole = preserveStoredToggles && HasStoredVariable(state, ClientComputeConsentPolicy.AllowConsoleVariableKey);
        return state with
        {
            DeviceLinkState = status.State,
            DeviceLinkServer = status.Server?.AbsoluteUri.TrimEnd('/'),
            DeviceLinkCircleId = status.CircleId,
            DeviceLinkLastContactedAt = status.LastContactedAt,
            DeviceLinkCapabilities = status.AdvertisedCapabilities ?? [],
            AllowClientCodeSandbox = hasStoredSandbox ? state.AllowClientCodeSandbox : status.AllowClientCodeSandbox,
            AllowWindowsFolderGateway = hasStoredFolders ? state.AllowWindowsFolderGateway : status.AllowWindowsFolderGateway,
            AllowConsole = hasStoredConsole ? state.AllowConsole : status.AllowConsole,
        };
    }

    private static bool HasStoredVariable(SettingsAppState state, string key)
        => (state.Variables ?? []).Any(v => string.Equals(v.Key, key, StringComparison.Ordinal));
}
