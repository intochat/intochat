using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Aspire.Hosting;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Testing.Model;

namespace DigitalBrain.OS.Tests.E2E;

// The product boots with a local System One endpoint: the fake model answers /v1/systemone for
// routing and the arg-fill chat, so a request routes through Jev and carries itself out offline.
public sealed class RouterHostFixture : ReferenceBrainFixture
{
    protected override Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
    {
        var root = new Uri(Model.Endpoint, "/").AbsoluteUri.TrimEnd('/');
        Model.ChatFallback = Fill;
        Model.SystemOneOverride = request => Choose(request, "timer", "Start", 0.95);
        return ReferenceBrain.Create(privateConfiguration: new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("typesafe", "ApiKey")] = "fixture-key",
            [IntegrationSchema.Key("typesafe", "Endpoint")] = root,
        })
            .WithOptions<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);
    }

    // The fill prompt carries the brain scope; answer with a valid timer call so the demo is
    // deterministic. A request that is not the fill prompt keeps the echo default; an IApp.Request
    // fill (what the assistant's gate dispatches) opens the calculator instead.
    private static FakeReply? Fill(JsonElement request)
    {
        var text = Text(request);
        if (!text.Contains("Fill one neuron call", StringComparison.Ordinal)) { return null; }
        var scope = Regex.Match(text, @"brain scope is (\S+)\.") is { Success: true } match ? match.Groups[1].Value : "scope";
        return text.Contains("Method: " + nameof(DigitalBrain.Apps.IApp.Request), StringComparison.Ordinal) || text.Contains("Request(", StringComparison.Ordinal)
            ? FakeReply.Text("{\"key\":\"" + scope + "/apps/calculator\",\"arguments\":[\"open\",\"\"]}")
            : FakeReply.Text("{\"key\":\"" + scope + "/timer\",\"arguments\":[\"00:05:00\",null]}");
    }

    internal static string Text(JsonElement request)
        => string.Concat(request.GetProperty("messages").EnumerateArray().SelectMany(message =>
            message.GetProperty("content").ValueKind == JsonValueKind.String
                ? [message.GetProperty("content").GetString() ?? ""]
                : message.GetProperty("content").EnumerateArray().Where(part => part.TryGetProperty("text", out _)).Select(part => part.GetProperty("text").GetString() ?? "")));

    // Scripted Jev for a fact: the capability question picks contractId, the method question picks method.
    internal static string Choose(JsonElement request, string contractId, string method, double confidence)
    {
        var answers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var question in request.GetProperty("questions").EnumerateObject())
        {
            var type = question.Value.GetProperty("type").GetString();
            if (type != "choice")
            {
                answers[question.Name] = type == "noul"
                    ? new { type = "noul", noul = 1.0 }
                    : new { type = "score", score = 0, confidence = 1.0 };
                continue;
            }
            var options = question.Value.GetProperty("criteria").EnumerateObject().Select(option => option.Name).ToArray();
            var pick = options.Contains(contractId) ? contractId : options.Contains(method) ? method
                : options.Contains("something else") ? "something else" : options[0];
            var probabilities = options.ToDictionary(option => option, option => option == pick ? confidence : (options.Length > 1 ? (1 - confidence) / (options.Length - 1) : 0.0));
            answers[question.Name] = new { type = "choice", choice = pick, probabilities, confidence };
        }
        return JsonSerializer.Serialize(new { model = "fake-jev", answers });
    }
}
