using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Log;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Media;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Pricing;
using IntoChat.OS.Modules.AI.Testing;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.AI;

[DigitalBrain.Modules.Module(ModuleName)]
public sealed class AIModule : DigitalBrain.Modules.Module, IHttpModule
{
    public static System.Reflection.Assembly? Contracts { get; } = typeof(IntoChat.OS.Modules.AI.Agents.Context.IAgentContext).Assembly;

    public static IntegrationSchema[] Integrations => AiIntegrations.All;
    public static IntegrationSchema OpenAIIntegration => AiIntegrations.OpenAI;
    public static IntegrationSchema OpenRouterIntegration => AiIntegrations.OpenRouter;
    public static IntegrationSchema TypeSafeIntegration => AiIntegrations.TypeSafe;
    public static ConnectorDefinition[] Connectors => ModelConnectors.All;

    internal const string ModuleName = "ai";

    // R4-04: what the module needs from its host. Registrations of neurons that are still grains stay here.
    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<AIOptions>(ModuleConfiguration.ModuleOptionsSection(new ModuleId(ModuleName)))],
        Secrets: []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = AIOptionsRuntime.Read(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAiCredentials, RegistrationCredentials>();
        services.TryAddSingleton<BrainKeyAvailability>();
        services.TryAddSingleton<ProviderPublicEndpoints>();
        services.TryAddSingleton<IPriceList, ModelPriceList>();
        AIClients.Add(services);
        services.AddHttpClient();
        services.TryAddSingleton<Models.Ollama.ILocalOllamaProbe, Models.Ollama.HttpLocalOllamaProbe>();
        services.TryAddSingleton<HttpSystemOne>();
        services.TryAddSingleton<ModelCatalog>();
        services.TryAddSingleton<InferenceService>();
        services.TryAddSingleton<IModelInference>(provider => provider.GetRequiredService<InferenceService>());
        services.TryAddSingleton<OsNeuronSurface>();
        services.AddSingleton<ICapabilitySource, AiModelCapabilities>();
        services.AddMediaNeurons();
        AIClients.AddImageGeneration(services, options);
        VoiceToTextHosting.Add(services, options);
        services.TryAddSingleton<IMemoryEmbedder, HybridMemoryEmbedder>();
        // Memory facts, tasks and agent runs join the brain graph (brain-graph-plan Phase 3 + studio-graph-plan §3.1).
        services.AddSingleton<DigitalBrain.Graph.IGraphSource, Memory.KnowledgeGraphSource>();
        services.TryAddSingleton<ITestVerificationService, TestVerificationService>();
    }

    // The calling brain's model list for the shell's model picker; never endpoints or secrets. The
    // brain is the edge's stamped caller; without one the list is the deployment's.
    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/ai/models", async (HttpContext http, INeuronProxies brain) => Results.Ok(await brain.Get<IModelCatalog>(
            CatalogKey(StampedCaller(http))).List()));
        endpoints.MapPost("/ai/transcriptions", TranscribeAsync);
    }

    private static Caller? StampedCaller(HttpContext http) => http.RequestServices.GetService<ICallerAmbient>()?.Current;

    // Fail closed: only a non-guest person calling directly (not an app, agent or relay) may invoke speech recognition.
    internal static bool IsDirectPersonCaller(Caller? caller)
        => caller is { Actor: Actor.Person person, Direct: true }
            && CallerRules.IsDirectPerson(caller, person.Principal)
            && !GuestSurface.Is(caller)
            && BrainScope.IsValidId(caller.Brain.Value);

    private static async Task<IResult> TranscribeAsync(
        HttpContext http,
        INeuronProxies brain,
        ILoggerFactory? loggerFactory,
        CancellationToken ct)
    {
        if (StampedCaller(http) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!IsDirectPersonCaller(caller))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        byte[] audioBytes;
        string mediaType;
        string fileName;
        try
        {
            (audioBytes, mediaType, fileName) = await ReadAudioUploadAsync(http.Request, ct).ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidDataException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var recognizer = brain.Get<ISpeechRecognizer>(caller.Brain.Value + "/default");
        try
        {
            var result = await recognizer.Recognize(
                new SpeechRecognitionRequest(new MediaPayload(audioBytes, mediaType), fileName),
                ct).ConfigureAwait(false);
            return Results.Ok(new { text = result.Text, model = result.Model });
        }
        catch (NotSupportedException error)
        {
            var reason = error.Message;
            try
            {
                var caps = await recognizer.Describe().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(caps.UnavailableReason))
                {
                    reason = caps.UnavailableReason;
                }
            }
            catch (Exception describeError) when (describeError is not OperationCanceledException)
            {
                var logger = loggerFactory?.CreateLogger<AIModule>() ?? OsConsoleLog.For<AIModule>();
                logger.LogSwallowed(describeError, "reading speech recognition capabilities after unavailable transcription");
            }

            return Results.Problem(reason, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnauthorizedAccessException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidOperationException error)
        {
            return Results.Problem(error.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<(byte[] Bytes, string MediaType, string FileName)> ReadAudioUploadAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                throw new ArgumentException("Audio payload must contain between 1 byte and 8 MiB.");
            }
            if (file.Length > MediaNeuron.MaxPayloadBytes)
            {
                throw new ArgumentException("Media must contain between 1 byte and 8 MiB.");
            }

            var formFileName = string.IsNullOrWhiteSpace(file.FileName) ? "voice.wav" : file.FileName.Trim();
            ValidateFileName(formFileName);
            await using var stream = file.OpenReadStream();
            var bytes = await ReadBoundedBytesAsync(stream, MediaNeuron.MaxPayloadBytes, ct).ConfigureAwait(false);
            var mediaType = NormalizeMediaType(file.ContentType);
            return (bytes, mediaType, formFileName);
        }

        var rawFileName = request.Query.TryGetValue("fileName", out var fileNameValues)
            && !string.IsNullOrWhiteSpace(fileNameValues.FirstOrDefault())
            ? fileNameValues.First()!.Trim()
            : "voice.wav";
        ValidateFileName(rawFileName);
        var rawBytes = await ReadBoundedBytesAsync(request.Body, MediaNeuron.MaxPayloadBytes, ct).ConfigureAwait(false);
        return (rawBytes, NormalizeMediaType(request.ContentType), rawFileName);
    }

    private static void ValidateFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Length > 255 || fileName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("Supply a filename without a path.");
        }
    }

    private static string NormalizeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "audio/wav";
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var trimmed = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
        return trimmed.Length > 0 ? trimmed : "audio/wav";
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new ArgumentException("Media must contain between 1 byte and 8 MiB.");
            }

            ms.Write(buffer, 0, read);
        }

        if (total == 0)
        {
            throw new ArgumentException("Media must contain between 1 byte and 8 MiB.");
        }

        return ms.ToArray();
    }

    // A caller stamped without a valid brain (or no caller) gets the deployment's list, not a 500.
    internal static string CatalogKey(Caller? caller)
        => caller is { For.Account.Length: > 0 } && BrainScope.IsValidId(caller.Brain.Value)
            ? IModelCatalog.Key(caller.Brain.Value) : IModelCatalog.DeploymentKey;
}
