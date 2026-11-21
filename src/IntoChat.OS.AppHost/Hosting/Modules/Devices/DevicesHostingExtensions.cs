using System.Buffers.Text;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Aspire.Hosting;

internal sealed record DeviceThisPayload(string? MachineId);

internal sealed record DeviceInstallPayload(string Model);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DeviceThisPayload))]
[JsonSerializable(typeof(DeviceInstallPayload))]
internal sealed partial class DevicesHostingJson : JsonSerializerContext;

public static class DevicesHostingExtensions
{
    public const string InstallModelCommandName = "install-model";

    internal static readonly string OptionsSectionKey =
        ModuleConfiguration.ModuleOptionsSection("devices");

    internal static readonly string OllamaEndpointKey = $"{OptionsSectionKey}:Ollama:Endpoint";

    internal static readonly string PrefetchSectionKey = $"{OptionsSectionKey}:Prefetch";

    public static IResourceBuilder<TResource> WithInstallModelCommand<TResource>(
        this IResourceBuilder<TResource> builder,
        IntoChatOSBuilder? brain = null)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.Annotations.OfType<ResourceCommandAnnotation>()
            .Any(static a => string.Equals(a.Name, InstallModelCommandName, StringComparison.Ordinal)))
        {
            return builder;
        }

        return builder.WithCommand(
            InstallModelCommandName,
            "Install model",
            context => ExecuteInstallCommandAsync(builder, brain, context),
            new CommandOptions
            {
                IconName = "ArrowDownload",
                UpdateState = static context =>
                    string.Equals(context.ResourceSnapshot.State?.Text, KnownResourceStates.Running, StringComparison.Ordinal)
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Disabled,
            });
    }

    private static async Task<ExecuteCommandResult> ExecuteInstallCommandAsync<TResource>(
        IResourceBuilder<TResource> builder,
        IntoChatOSBuilder? brain,
        ExecuteCommandContext context)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints
    {
        var ct = context.CancellationToken;
        var defaultModel = await ResolveFirstPrefetchModelAsync(builder, brain, ct).ConfigureAwait(false);
        var modelId = await ResolveModelIdAsync(context.Services, defaultModel, ct).ConfigureAwait(false);
        if (modelId is null)
        {
            return CommandResults.Success();
        }
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return CommandResults.Failure("No model id provided and no Prefetch model configured.");
        }

        var endpoint = builder.GetEndpoint("http");
        if (!endpoint.IsAllocated || !Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var kernelUri))
        {
            return CommandResults.Failure("Kernel HTTP endpoint is not allocated.");
        }

        var token = ResolvePresenceToken(brain);
        try
        {
            using var http = new HttpClient { BaseAddress = kernelUri, Timeout = TimeSpan.FromSeconds(15) };
            return await ForwardInstallAsync(http, modelId, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OsConsoleLog.For(typeof(DevicesHostingExtensions)).LogWarning(ex, "AppHost install-model command failed for '{ModelId}'.", modelId);
            return CommandResults.Failure(ex.Message);
        }
    }

    internal static async Task<ExecuteCommandResult> ForwardInstallAsync(
        HttpClient http,
        string modelId,
        string? presenceToken,
        CancellationToken ct)
    {
        const string thisPath = "/brains/owner/devices/this";
        using var thisRequest = new HttpRequestMessage(HttpMethod.Get, thisPath);
        AttachPresence(thisRequest, presenceToken);
        using var thisResponse = await http.SendAsync(thisRequest, ct).ConfigureAwait(false);
        if (!thisResponse.IsSuccessStatusCode)
        {
            return CommandResults.Failure($"GET {thisPath} returned {(int)thisResponse.StatusCode} {thisResponse.ReasonPhrase}.");
        }

        var payload = await thisResponse.Content.ReadFromJsonAsync(DevicesHostingJson.Default.DeviceThisPayload, ct).ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.MachineId))
        {
            return CommandResults.Failure($"Kernel returned no machine id from {thisPath}.");
        }

        var installPath = $"/brains/owner/devices/{Uri.EscapeDataString(payload.MachineId.Trim())}/install";
        using var installRequest = new HttpRequestMessage(HttpMethod.Post, installPath)
        {
            Content = JsonContent.Create(new DeviceInstallPayload(modelId.Trim()), DevicesHostingJson.Default.DeviceInstallPayload),
        };
        AttachPresence(installRequest, presenceToken);
        using var installResponse = await http.SendAsync(installRequest, ct).ConfigureAwait(false);
        if (!installResponse.IsSuccessStatusCode)
        {
            var body = await installResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return CommandResults.Failure($"POST {installPath} returned {(int)installResponse.StatusCode}: {body}");
        }

        return CommandResults.Success();
    }

    private static void AttachPresence(HttpRequestMessage request, string? presenceToken)
    {
        if (!string.IsNullOrWhiteSpace(presenceToken))
        {
            request.Headers.TryAddWithoutValidation(PresenceProof.HeaderName, presenceToken.Trim());
        }
    }

    private static async Task<string?> ResolveModelIdAsync(IServiceProvider services, string? defaultModel, CancellationToken ct)
    {
#pragma warning disable ASPIREINTERACTION001
        var interaction = services.GetService<IInteractionService>();
        if (interaction is { IsAvailable: true })
        {
            var result = await interaction.PromptInputAsync(
                "Install model",
                "Enter a model id to queue for download on this device.",
                "Model ID",
                string.IsNullOrWhiteSpace(defaultModel) ? "gemma4:e2b" : defaultModel,
                cancellationToken: ct).ConfigureAwait(false);
            if (result.Canceled)
            {
                return null;
            }
            return result.Data?.Value?.Trim() ?? string.Empty;
        }
#pragma warning restore ASPIREINTERACTION001

        return defaultModel ?? string.Empty;
    }

    private static async Task<string?> ResolveFirstPrefetchModelAsync<TResource>(
        IResourceBuilder<TResource> builder,
        IntoChatOSBuilder? brain,
        CancellationToken ct)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints
    {
        var firstKey = $"{PrefetchSectionKey}:0";
        if (brain?.ApplicationBuilder.Configuration[firstKey] is { Length: > 0 } configured)
        {
            return configured.Trim();
        }

        var envName = ModuleConfiguration.EnvironmentName(firstKey);
        var env = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var envContext = new EnvironmentCallbackContext(builder.ApplicationBuilder.ExecutionContext, builder.Resource, env, ct);
        foreach (var callback in builder.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(envContext).ConfigureAwait(false);
        }

        return env.TryGetValue(envName, out var value) && value?.ToString() is { Length: > 0 } fromEnv
            ? fromEnv.Trim()
            : null;
    }

    internal static string? ResolvePresenceToken(IntoChatOSBuilder? brain)
    {
        if (ShellHostingExtensions.TryGetPresenceToken(brain) is { Length: > 0 } shellToken)
        {
            return shellToken;
        }

        var config = brain?.ApplicationBuilder.Configuration;
        if (config?[PresenceProof.ConfigurationKey] is { Length: > 0 } configuredToken)
        {
            return configuredToken.Trim();
        }

        var tokenFile = config?[PresenceProof.TokenFileConfigurationKey]
            ?? (config is not null ? Path.Combine(IntoChatOSHome.Resolve(config), "run", "presence") : null);
        if (!string.IsNullOrWhiteSpace(tokenFile) && File.Exists(tokenFile))
        {
            var token = DecodePresenceFileMaterial(File.ReadAllBytes(tokenFile));
            if (token is not null)
            {
                return token;
            }
        }

        if (Environment.GetEnvironmentVariable("INTOCHAT_PRESENCE_TOKEN") is { Length: > 0 } envToken)
        {
            return envToken.Trim();
        }

        return null;
    }

    internal static string? DecodePresenceFileMaterial(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.Length == 0)
        {
            return null;
        }

        if (rawBytes.Length == PresenceProof.SecretByteCount && !IsAllPrintableAscii(rawBytes))
        {
            return Base64Url.EncodeToString(rawBytes);
        }

        if (IsAllPrintableAscii(rawBytes))
        {
            var text = Encoding.UTF8.GetString(rawBytes).Trim();
            return text.Length > 0 ? text : null;
        }

        return Base64Url.EncodeToString(rawBytes);
    }

    private static bool IsAllPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is (byte)'\r' or (byte)'\n' or (byte)'\t')
            {
                continue;
            }

            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
