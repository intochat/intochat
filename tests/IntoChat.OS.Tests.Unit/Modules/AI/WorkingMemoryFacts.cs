using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Connections.Platform.Secrets;
using IntoChat.OS.Modules.AI.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class WorkingMemoryFacts
{
    private static readonly BrainId TestBrain = new("working-mem");

    private static IDisposable AsPerson()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", TestBrain));

    private static TestOS CreateOs(ISecretRedactor? redactor = null)
    {
        var services = new ServiceCollection();
        if (redactor is not null)
        {
            services.AddSingleton(redactor);
        }
        var os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        os.Register<WorkingMemoryNeuron>();
        return os;
    }

    [Fact]
    public async Task SlicesScratchpadAndLazyBriefsKeepStartupPackCompactWithoutDumpingBriefBodies()
    {
        using var person = AsPerson();
        await using var os = CreateOs();
        var wm = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(TestBrain));

        await wm.SetSlice(WorkingMemorySliceKind.Soul, "Direct, analytical engineering assistant.");
        await wm.SetSlice(WorkingMemorySliceKind.User, "Prefers concise plans and single-project builds.");
        await wm.SetSlice(WorkingMemorySliceKind.Project, "project:checkout");

        await wm.UpsertScratchpad(WorkingMemorySection.ActiveThreads, "Checkout retry idempotency audit");
        await wm.UpsertScratchpad(WorkingMemorySection.EnvironmentNotes, "Primary database uses connection env ORDER_DB_URL");
        await wm.UpsertScratchpad(WorkingMemorySection.PendingDecisions, "Choose Redis vs Postgres advisory locks");

        const string DetailedBriefBody = "DEEP_BRIEF_BODY_MARKER: Full 12-stage checkout architecture specification and sequence notes.";
        await wm.PutBrief("project:checkout", "Checkout Service Spec", "project", DetailedBriefBody);
        await wm.PutBrief("skill:migrations", "Zero-Downtime SQL Migrations", "skill", "UNUSED_SKILL_BODY_MARKER: Always add nullable columns first.");

        var pack = await wm.ReadStartupPack();

        Assert.Equal("Direct, analytical engineering assistant.", pack.Soul);
        Assert.Contains("single-project builds", pack.User, StringComparison.Ordinal);
        Assert.Equal("project:checkout", pack.Project);
        Assert.Equal(2, pack.AvailableBriefs.Length);

        // Startup pack includes slices, scratchpad, and brief catalog index, but NEVER dumps brief bodies.
        Assert.Contains("[SOUL]", pack.FormattedPrompt, StringComparison.Ordinal);
        Assert.Contains("[USER]", pack.FormattedPrompt, StringComparison.Ordinal);
        Assert.Contains("## Active Threads", pack.FormattedPrompt, StringComparison.Ordinal);
        Assert.Contains("Checkout retry idempotency audit", pack.FormattedPrompt, StringComparison.Ordinal);
        Assert.Contains("project:checkout", pack.FormattedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("DEEP_BRIEF_BODY_MARKER", pack.FormattedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("UNUSED_SKILL_BODY_MARKER", pack.FormattedPrompt, StringComparison.Ordinal);

        // Lazy-loading fetches the full brief strictly on demand.
        var loaded = await wm.GetBrief("project:checkout");
        Assert.NotNull(loaded);
        Assert.Equal(DetailedBriefBody, loaded.Content);

        var skillBriefs = await wm.ListBriefs("skill");
        Assert.Single(skillBriefs, b => b.Key == "skill:migrations");
    }

    [Fact]
    public async Task FrozenSessionSnapshotPreservesPrefixAcrossMidSessionScratchpadWrites()
    {
        using var person = AsPerson();
        await using var os = CreateOs();
        var wm = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(TestBrain));

        await wm.UpsertScratchpad(WorkingMemorySection.ActiveThreads, "Initial thread v1");
        var session1 = await wm.FreezeSession("conv-1");
        Assert.Equal("conv-1", session1.FrozenSessionId);
        Assert.Equal(["Initial thread v1"], session1.Scratchpad.ActiveThreads);

        // Mid-session writes mutate LiveScratchpad while conv-1 keeps its frozen startup snapshot.
        await wm.UpsertScratchpad(WorkingMemorySection.ActiveThreads, "Mid-session thread v2");
        var stillSession1 = await wm.FreezeSession("conv-1");
        Assert.Equal(["Initial thread v1"], stillSession1.Scratchpad.ActiveThreads);

        var snapshot = await wm.Read();
        Assert.Equal(["Initial thread v1", "Mid-session thread v2"], snapshot.LiveScratchpad.ActiveThreads);

        // Next session promotes LiveScratchpad into FrozenScratchpad.
        var session2 = await wm.FreezeSession("conv-2");
        Assert.Equal("conv-2", session2.FrozenSessionId);
        Assert.Equal(["Initial thread v1", "Mid-session thread v2"], session2.Scratchpad.ActiveThreads);
    }

    [Fact]
    public async Task ScratchpadEnforces2500CharacterCapAndDeduplicatesEntries()
    {
        using var person = AsPerson();
        await using var os = CreateOs();
        var wm = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(TestBrain));

        // Duplicate entries consolidate automatically.
        await wm.UpsertScratchpad(WorkingMemorySection.EnvironmentNotes, "Build with -c Release");
        var deduped = await wm.UpsertScratchpad(WorkingMemorySection.EnvironmentNotes, "build with -c release");
        Assert.Single(deduped.EnvironmentNotes);

        // Replacing a matching entry updates in place.
        var replaced = await wm.UpsertScratchpad(WorkingMemorySection.EnvironmentNotes, "Build individual csproj with -c Release", replaceMatching: "Build with");
        Assert.Single(replaced.EnvironmentNotes);
        Assert.Equal("Build individual csproj with -c Release", replaced.EnvironmentNotes[0]);

        // Over the 2,500-character cap fails with RuleViolationException("scratchpad_cap_exceeded").
        var oversized = new string('x', 2_550);
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => wm.UpsertScratchpad(WorkingMemorySection.ActiveThreads, oversized));
        Assert.Contains("scratchpad_cap_exceeded", ex.Rejected.Rules);
    }

    [Fact]
    public async Task RejectsRawApiKeyLiteralsAndMasksVaultSecretsAcrossRestart()
    {
        using var person = AsPerson();
        await using var os = CreateOs(new MaskingRedactor("vault_secret_token_99"));
        var wm = os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(TestBrain));

        var syntheticKey = string.Concat("OPENAI_", "KEY=", "sk-", "proj-", new string('x', 24));
        var secretEx = await Assert.ThrowsAsync<RuleViolationException>(
            () => wm.UpsertScratchpad(WorkingMemorySection.EnvironmentNotes, syntheticKey));
        Assert.Contains("no_secret_values", secretEx.Rejected.Rules);

        await wm.UpsertScratchpad(WorkingMemorySection.EnvironmentNotes, "Token value vault_secret_token_99 is masked");
        await os.Restart();

        var restored = await os.Get<IWorkingMemory>(IWorkingMemory.NeuronIdOf(TestBrain)).Read();
        var note = Assert.Single(restored.LiveScratchpad.EnvironmentNotes);
        Assert.DoesNotContain("vault_secret_token_99", note, StringComparison.Ordinal);
        Assert.Contains("****", note, StringComparison.Ordinal);
    }

    private sealed class MaskingRedactor(string secret) : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken)
            => ValueTask.FromResult(text?.Replace(secret, "****", StringComparison.Ordinal));

        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(secret.Length);
    }
}
