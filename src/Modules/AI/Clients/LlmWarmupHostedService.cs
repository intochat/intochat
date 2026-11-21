using System.Diagnostics;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.AI;

internal sealed class LlmWarmupHostedService(
    IServiceProvider services,
    IOptions<AIOptions> options,
    ILogger<LlmWarmupHostedService> logger) : IHostedService
{
    private static readonly TimeSpan WarmBudget = TimeSpan.FromMinutes(3);
    private static readonly ChatMessage WarmPrompt = new(ChatRole.User, " ");
    private static readonly Action<ILogger, string, long, Exception?> LogWarmed =
        LoggerMessage.Define<string, long>(
            LogLevel.Information,
            new EventId(1, nameof(LogWarmed)),
            "Warmed LLM {Model} in {ElapsedMs}ms.");
    private static readonly Action<ILogger, string, Exception?> LogWarmFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, nameof(LogWarmFailed)),
            "LLM warmup failed for {Model}; first chat may still pay cold start.");

    private static readonly ModelCard[] OllamaTargets =
    [
        .. KnownModels.All.Where(static c => c.Provider == AiProvider.Ollama && c.Can.HasFlag(Abilities.Text)),
    ];

    private CancellationTokenSource? _warmup;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warmup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = WarmConfiguredModelsAsync(_warmup.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_warmup is null)
        {
            return Task.CompletedTask;
        }

        return _warmup.CancelAsync();
    }

    private async Task WarmConfiguredModelsAsync(CancellationToken cancellationToken)
    {
        foreach (var card in ConfiguredTargets())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await WarmOneAsync(card, cancellationToken).ConfigureAwait(false);
        }
    }

    private IEnumerable<ModelCard> ConfiguredTargets()
    {
        var models = options.Value.Ollama.Models;
        foreach (var target in OllamaTargets)
        {
            if (!string.IsNullOrWhiteSpace(models.GetValueOrDefault(target.Id.Value)?.Model)
                || models.Any(pair => KnownModels.Find(pair.Key)?.Id == target.Id && !string.IsNullOrWhiteSpace(pair.Value?.Model)))
            {
                yield return target;
            }
        }
    }

    private async Task WarmOneAsync(ModelCard card, CancellationToken cancellationToken)
    {
        var name = card.Id.Value;
        try
        {
            var client = services.GetRequiredKeyedService<IChatClient>(card.Id);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(WarmBudget);

            var clock = Stopwatch.StartNew();
            _ = await client.GetResponseAsync([WarmPrompt], cancellationToken: budget.Token).ConfigureAwait(false);
            LogWarmed(logger, name, clock.ElapsedMilliseconds, null);
        }
        catch (Exception failure) when (failure is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            LogWarmFailed(logger, name, failure);
        }
    }
}
