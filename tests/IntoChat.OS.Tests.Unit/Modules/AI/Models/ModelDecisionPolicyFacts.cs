using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI.Models;

public sealed class ModelDecisionPolicyFacts
{
    private static ProviderReadiness CloudReady(params AiProvider[] providers)
        => new(providers.ToImmutableHashSet(), TypeSafeReady: false, HasHostChatClient: false);

    private static LocalRuntimeSnapshot SnapshotWithInstalled(
        AiProvider provider,
        bool reachable,
        Uri? endpoint,
        params ModelCard[] cards)
    {
        var allCards = KnownModels.All.ToList();
        var installed = ImmutableDictionary.CreateBuilder<string, InstalledLocalModel>(StringComparer.OrdinalIgnoreCase);
        var runtime = new DummyRuntime(provider);
        var probe = new RuntimeStatus(runtime.Id, reachable, reachable ? "1.0" : null, endpoint);

        if (reachable)
        {
            foreach (var card in cards)
            {
                if (!allCards.Any(c => string.Equals(c.Id.Value, card.Id.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    allCards.Add(card);
                }

                var entry = new InstalledLocalModel(runtime, probe, card);
                installed[card.Id.Value] = entry;
            }
        }

        var probes = ImmutableDictionary<AiProvider, ProviderProbeState>.Empty
            .Add(provider, new ProviderProbeState(reachable, endpoint));

        return new LocalRuntimeSnapshot([.. allCards], installed.ToImmutable(), probes);
    }

    [Theory]
    [InlineData(ModelRole.Chat, "gpt-5.4", "claude-sonnet-5", "deepseek/deepseek-v4.1-flash", "gpt-5.4", AiProvider.OpenAI)]
    [InlineData(ModelRole.Chat, null, "claude-sonnet-5", "deepseek/deepseek-v4.1-flash", "claude-sonnet-5", AiProvider.Anthropic)]
    [InlineData(ModelRole.Chat, null, null, "deepseek/deepseek-v4.1-flash", "deepseek/deepseek-v4.1-flash", AiProvider.OpenRouter)]
    [InlineData(ModelRole.Embed, "text-embedding-3-large", "gemini-embedding-001", "text-embedding-3-small", "text-embedding-3-large", AiProvider.OpenAI)]
    [InlineData(ModelRole.Embed, null, "gemini-embedding-001", "text-embedding-3-small", "gemini-embedding-001", AiProvider.Google)]
    [InlineData(ModelRole.Embed, null, null, "text-embedding-3-small", "text-embedding-3-small", AiProvider.OpenAI)]
    [InlineData(ModelRole.Hear, "gpt-4o-transcribe", "gemini-3.5-transcribe", "gpt-4o-mini-transcribe", "gpt-4o-transcribe", AiProvider.OpenAI)]
    [InlineData(ModelRole.Hear, null, "gemini-3.5-transcribe", "gpt-4o-mini-transcribe", "gemini-3.5-transcribe", AiProvider.Google)]
    [InlineData(ModelRole.Hear, null, null, "gpt-4o-mini-transcribe", "gpt-4o-mini-transcribe", AiProvider.OpenAI)]
    [InlineData(ModelRole.Speak, "gemini-3.8-flash-tts", "gpt-4o-mini-tts", "gemini-3.8-flash-lite-tts", "gemini-3.8-flash-tts", AiProvider.Google)]
    [InlineData(ModelRole.Speak, null, "gpt-4o-mini-tts", "gemini-3.8-flash-lite-tts", "gpt-4o-mini-tts", AiProvider.OpenAI)]
    [InlineData(ModelRole.Speak, null, null, "gemini-3.8-flash-lite-tts", "gemini-3.8-flash-lite-tts", AiProvider.Google)]
    [InlineData(ModelRole.Talk, "gpt-realtime", "gemini-3.8-live", "gpt-realtime", "gpt-realtime", AiProvider.OpenAI)]
    [InlineData(ModelRole.Talk, null, "gemini-3.8-live", "gpt-realtime", "gemini-3.8-live", AiProvider.Google)]
    public void PrecedenceTurnThenPersonThenDeploymentAcrossAllRoles(
        ModelRole role,
        string? turnModel,
        string? personModel,
        string deploymentModel,
        string expectedModel,
        AiProvider expectedProvider)
    {
        var ai = new AIOptions();
        ai.Roles.Cloud[role] = deploymentModel;

        var person = personModel is null
            ? null
            : new UserSettingsState
            {
                Roles = [new RoleChoice(role, new ModelId(personModel))],
            };

        var readiness = CloudReady(AiProvider.OpenAI, AiProvider.Anthropic, AiProvider.Google, AiProvider.OpenRouter);

        var decision = ModelDecisionPolicy.Decide(
            role,
            turnModel is null ? null : new ModelSelection(turnModel),
            person,
            ai,
            IntoChatOSNames.TopologyCloud,
            LocalRuntimeSnapshot.Empty,
            readiness);

        Assert.Null(decision.MissingCredentialProvider);
        Assert.Null(decision.UnavailableDetail);
        Assert.NotNull(decision.Model);
        Assert.Equal((expectedProvider, expectedModel), (decision.Model!.Provider, decision.Model.Model));
    }

    [Fact]
    public void FallsBackToFirstAvailableCardWithRoleAbilityWhenNoDeploymentOrDefaultReady()
    {
        var ai = new AIOptions();
        var readiness = CloudReady(AiProvider.Google);

        var decision = ModelDecisionPolicy.Decide(
            ModelRole.Embed,
            turn: null,
            person: null,
            ai,
            IntoChatOSNames.TopologyCloud,
            LocalRuntimeSnapshot.Empty,
            readiness);

        Assert.NotNull(decision.Model);
        Assert.Equal(AiProvider.Google, decision.Model!.Provider);
        Assert.True(decision.Model.Capabilities.HasFlag(Abilities.Embed));
    }

    [Fact]
    public void ExplicitUnconfiguredCloudChoiceReportsMissingCredentialProvider()
    {
        var ai = new AIOptions();
        var decision = ModelDecisionPolicy.Decide(
            ModelRole.Chat,
            new ModelSelection("claude-sonnet-5"),
            person: null,
            ai,
            IntoChatOSNames.TopologyCloud,
            LocalRuntimeSnapshot.Empty,
            ProviderReadiness.Empty);

        Assert.Null(decision.Model);
        Assert.Equal(AiProvider.Anthropic, decision.MissingCredentialProvider);
    }

    [Fact]
    public void ExplicitDeviceRouteWithToolsFallsBackToCloudAndRecordsReason()
    {
        var ai = new AIOptions();
        ai.Roles.Cloud[ModelRole.Chat] = KnownModels.DeepSeekV41Flash.Id.Value;
        var readiness = CloudReady(AiProvider.OpenRouter);

        var decision = ModelDecisionPolicy.Decide(
            ModelRole.Chat,
            new ModelSelection("device:vlad-pc/qwen2.5-coder:14b"),
            person: null,
            ai,
            IntoChatOSNames.TopologyCloud,
            LocalRuntimeSnapshot.Empty,
            readiness,
            requiresTools: true);

        Assert.NotNull(decision.Model);
        Assert.Equal(KnownModels.DeepSeekV41Flash.Id.Value, decision.Model!.Model);
        Assert.Equal("device:vlad-pc/qwen2.5-coder:14b", decision.FallbackFromDeviceModelId);
        Assert.Equal("tools-unsupported", decision.FallbackReason);
    }

    [Fact]
    public void LocalTopologyPrefersInstalledLocalModelOverCloud()
    {
        var ai = new AIOptions();
        ai.Roles.Device[ModelRole.Chat] = KnownModels.Gemma412B.Id.Value;
        ai.Roles.Cloud[ModelRole.Chat] = KnownModels.Gpt54.Id.Value;

        var local = SnapshotWithInstalled(
            AiProvider.Ollama,
            reachable: true,
            new Uri("http://127.0.0.1:11434"),
            KnownModels.Gemma412B);

        var decision = ModelDecisionPolicy.Decide(
            ModelRole.Chat,
            turn: null,
            person: null,
            ai,
            IntoChatOSNames.TopologyLocalCloud,
            local,
            CloudReady(AiProvider.OpenAI));

        Assert.NotNull(decision.Model);
        Assert.Equal((AiProvider.Ollama, KnownModels.Gemma412B.Id.Value), (decision.Model!.Provider, decision.Model.Model));
    }

    [Fact]
    public void DecideFrontierEscalatesToReadyCloudAndReturnsNullWithoutCloudKeys()
    {
        var ai = new AIOptions();
        ai.Roles.Cloud[ModelRole.Think] = KnownModels.Opus5.Id.Value;

        var none = ModelDecisionPolicy.DecideFrontier(
            ModelRole.Think,
            requiresTools: true,
            ai,
            LocalRuntimeSnapshot.Empty,
            ProviderReadiness.Empty);
        Assert.Null(none);

        var escalated = ModelDecisionPolicy.DecideFrontier(
            ModelRole.Think,
            requiresTools: true,
            ai,
            LocalRuntimeSnapshot.Empty,
            CloudReady(AiProvider.Anthropic));
        Assert.NotNull(escalated);
        Assert.Equal((AiProvider.Anthropic, KnownModels.Opus5.Id.Value), (escalated!.Provider, escalated.Model));
    }

    [Fact]
    public void SelectTalkBindingAndDescribeMissingVoiceRoleFollowTopology()
    {
        var roles = KnownModels.Defaults;

        var cloudTalk = ModelDecisionPolicy.SelectTalkBinding(roles, IntoChatOSNames.TopologyCloud);
        Assert.Null(cloudTalk.UnavailableReason);
        Assert.Equal(KnownModels.GptRealtime.Id, cloudTalk.ModelId);
        Assert.Equal(KnownModels.GptRealtime, cloudTalk.Card);

        var localTalk = ModelDecisionPolicy.SelectTalkBinding(roles, IntoChatOSNames.TopologyLocal);
        Assert.Null(localTalk.ModelId);
        Assert.Null(localTalk.Card);
        Assert.Null(localTalk.UnavailableReason);

        var missingSpeakLocal = ModelDecisionPolicy.DescribeMissingVoiceRole(roles, IntoChatOSNames.TopologyLocal, ModelRole.Speak);
        Assert.Contains("not installed", missingSpeakLocal, StringComparison.OrdinalIgnoreCase);

        var missingHearCloud = ModelDecisionPolicy.DescribeMissingVoiceRole(roles, IntoChatOSNames.TopologyCloud, ModelRole.Hear);
        Assert.Contains("not configured", missingHearCloud, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckCardAvailabilityReportsExactReasons()
    {
        var ai = new AIOptions();
        var offlineOllama = SnapshotWithInstalled(AiProvider.Ollama, reachable: false, endpoint: null);

        var (ollamaReady, ollamaReason) = ModelDecisionPolicy.CheckCardAvailability(
            KnownModels.Gemma412B, ai, offlineOllama, ProviderReadiness.Empty);
        Assert.False(ollamaReady);
        Assert.Equal(ModelDecisionPolicy.OllamaNotRunningReason, ollamaReason);

        var (cloudReady, cloudReason) = ModelDecisionPolicy.CheckCardAvailability(
            KnownModels.Opus5, ai, LocalRuntimeSnapshot.Empty, ProviderReadiness.Empty);
        Assert.False(cloudReady);
        Assert.Equal(ModelDecisionPolicy.NoKeyReason, cloudReason);

        var (foundryReady, foundryReason) = ModelDecisionPolicy.CheckCardAvailability(
            KnownModels.FoundryDeepSeekV32, ai, LocalRuntimeSnapshot.Empty, ProviderReadiness.Empty);
        Assert.False(foundryReady);
        Assert.Equal(ModelDecisionPolicy.NotConfiguredReason, foundryReason);
    }

    [Fact]
    public void ValidateRevisionRejectsTamperedResolvedModel()
    {
        var decision = ModelDecisionPolicy.Decide(
            ModelRole.Chat,
            new ModelSelection("gpt-5.4"),
            person: null,
            new AIOptions(),
            IntoChatOSNames.TopologyCloud,
            LocalRuntimeSnapshot.Empty,
            CloudReady(AiProvider.OpenAI));

        var resolved = decision.Model!;
        ModelDecisionPolicy.ValidateRevision(resolved);

        var tampered = resolved with { Model = "gpt-5.4-nano" };
        Assert.Throws<ArgumentException>(() => ModelDecisionPolicy.ValidateRevision(tampered));
    }

    private sealed class DummyRuntime(AiProvider provider) : ILocalRuntime
    {
        public RuntimeId Id => provider switch
        {
            AiProvider.Ollama => RuntimeId.Ollama,
            AiProvider.FoundryLocal => RuntimeId.FoundryLocal,
            AiProvider.SherpaOnnx => RuntimeId.SherpaOnnx,
            _ => new RuntimeId("custom"),
        };

        public Task<RuntimeStatus> Probe(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ModelCard> Catalog(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<ModelCard> Installed(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<InstallProgress> Install(ModelId model, CancellationToken ct) => throw new NotSupportedException();
        public Task Remove(ModelId model, CancellationToken ct) => throw new NotSupportedException();
        public IChatClient Chat(ModelId model) => throw new NotSupportedException();
    }
}
