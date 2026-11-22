using System.Diagnostics;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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
        var runtimes = services.GetServices<ILocalRuntime>().ToArray();
        if (runtimes.Length == 0)
        {
            return;
        }

        var targets = ConfiguredTargets().Distinct().ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        foreach (var runtime in runtimes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var status = await runtime.Probe(cancellationToken).ConfigureAwait(false);
                if (!status.Reachable)
                {
                    continue;
                }
                await foreach (var card in runtime.Installed(cancellationToken).ConfigureAwait(false))
                {
                    installedIds.Add(card.Id.Value);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogWarmFailed(logger, runtime.Id.Value, failure);
                continue;
            }

            foreach (var modelId in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (!installedIds.Contains(modelId.Value))
                {
                    continue;
                }
                await WarmOneAsync(runtime, modelId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private IEnumerable<ModelId> ConfiguredTargets()
    {
        var ai = options.Value;
        var topology = services.GetService<IConfiguration>()?[IntoChatOSNames.TopologyKey];
        if (!string.Equals(topology, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            var roleMap = ai.Roles.ToRoleMap();
            if (roleMap.Device.TryGetValue(Role.Chat, out var chatId) && !string.IsNullOrWhiteSpace(chatId.Value))
            {
                yield return chatId;
            }
        }

        foreach (var pair in ai.Ollama.Models)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value?.Model))
            {
                var card = KnownModels.Find(pair.Key);
                yield return card?.Id ?? new ModelId(pair.Key);
            }
        }
    }

    private async Task WarmOneAsync(ILocalRuntime runtime, ModelId modelId, CancellationToken cancellationToken)
    {
        var name = modelId.Value;
        try
        {
            using var client = runtime.Chat(modelId);
            if (client is null)
            {
                return;
            }
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
