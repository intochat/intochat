using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Errors;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class DeviceNeuronFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelId ModelA = new("gemma4:e2b");
    private static readonly ModelId ModelB = new("qwen3.5:4b");
    private static readonly ModelCard CardA = new(ModelA, AiProvider.Ollama, "Gemma 4", "Gemma 4 E2B", Where.Device, Abilities.Text | Abilities.Tools, Tier.Fast, Bytes: 2L * 1024 * 1024 * 1024);
    private static readonly ModelCard CardB = new(ModelB, AiProvider.Ollama, "Qwen 3.5", "Qwen 3.5 4B", Where.Device, Abilities.Text | Abilities.Tools, Tier.Balanced, Bytes: 3L * 1024 * 1024 * 1024);

    private sealed class FakeHardwareProbe : IHardwareProbe
    {
        public Hardware Read() => new(32L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]);
        public void ReportNpu() { }
    }

    private sealed class FakeRuntime : ILocalRuntime
    {
        private sealed class RunHandle
        {
            public Channel<InstallProgress> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<InstallProgress>();
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly ConcurrentDictionary<string, int> _installCalls = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, RunHandle> _runs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, double[]> _scripts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ModelCard> _installed = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ModelCard> _catalog = [CardA, CardB];

        public RuntimeId Id => RuntimeId.Ollama;

        public int InstallCalls(ModelId id) => _installCalls.GetValueOrDefault(id.Value);
        public bool Cancelled(ModelId id) => _tokens.TryGetValue(id.Value, out var ct) && ct.IsCancellationRequested;

        public void Script(ModelId id, IEnumerable<double> percents) => _scripts[id.Value] = [.. percents];

        public void MarkInstalled(ModelCard card) => _installed[card.Id.Value] = card;

        public Task WaitStarted(ModelId id) => GetOrCreateRun(id).Started.Task;

        public void Report(ModelId id, double percent)
            => GetOrCreateRun(id).Channel.Writer.TryWrite(new InstallProgress(id, InstallState.Downloading, percent, (long)(percent * 1_000_000), 100_000_000));

        public void Finish(ModelId id)
        {
            var card = _catalog.First(c => c.Id == id);
            _installed[id.Value] = card;
            var run = GetOrCreateRun(id);
            run.Channel.Writer.TryWrite(new InstallProgress(id, InstallState.Ready, 100, card.Bytes ?? 0, card.Bytes ?? 0));
            run.Channel.Writer.TryComplete();
        }

        public void Fail(ModelId id, string error)
        {
            var run = GetOrCreateRun(id);
            run.Channel.Writer.TryWrite(new InstallProgress(id, InstallState.Failed, 0, 0, 0, error));
            run.Channel.Writer.TryComplete();
        }

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, Reachable: true, Version: "0.9.0", Endpoint: new Uri("http://localhost:11434")));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in _catalog)
            {
                yield return card;
            }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in _installed.Values)
            {
                yield return card;
            }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            var run = new RunHandle();
            _runs.AddOrUpdate(id.Value, run, (_, existing) => existing.Channel.Reader.Completion.IsCompleted ? run : existing);
            run = _runs[id.Value];
            _tokens[id.Value] = ct;
            _installCalls.AddOrUpdate(id.Value, 1, static (_, c) => c + 1);
            run.Started.TrySetResult();

            try
            {
                if (_scripts.TryRemove(id.Value, out var scripted))
                {
                    foreach (var pct in scripted)
                    {
                        ct.ThrowIfCancellationRequested();
                        yield return new InstallProgress(id, InstallState.Downloading, pct, (long)(pct * 1_000_000), 100_000_000);
                    }
                }

                while (await run.Channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (run.Channel.Reader.TryRead(out var item))
                    {
                        yield return item;
                        if (item.State is InstallState.Ready or InstallState.Failed or InstallState.Cancelled)
                        {
                            yield break;
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                run.Channel.Writer.TryComplete();
            }
        }

        public Task Remove(ModelId id, CancellationToken ct)
        {
            _installed.TryRemove(id.Value, out _);
            return Task.CompletedTask;
        }

        public IChatClient Chat(ModelId id) => throw new NotSupportedException();

        private RunHandle GetOrCreateRun(ModelId id)
            => _runs.GetOrAdd(id.Value, static _ => new RunHandle());
    }

    private sealed class FakeCatalogState
    {
        public ConcurrentQueue<(Role Role, ModelId Model)> SetRoles { get; } = new();
        public RoleMap Roles { get; set; } = RoleMap.Empty;
    }

    [Neuron("ai.models")]
    private sealed class FakeModelCatalogNeuron(FakeCatalogState state) : Neuron, IModelCatalog
    {
        public Task<ModelCard[]> List(Abilities need = Abilities.None) => Task.FromResult<ModelCard[]>([]);
        public Task<ModelId> Pick(Role role, ModelId? turn = null) => Task.FromResult(ModelA);
        public Task SetRole(Role role, ModelId model)
        {
            state.SetRoles.Enqueue((role, model));
            return Task.CompletedTask;
        }
        public Task<RoleMap> Roles() => Task.FromResult(state.Roles);
        public Task<ModelAvailability[]> Availability() => Task.FromResult<ModelAvailability[]>([]);
        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _machineFile;
        private readonly ServiceProvider _services;

        public MachineId Machine { get; }
        public FakeRuntime Runtime { get; } = new();
        public FakeCatalogState Catalog { get; } = new();
        public DeviceInstaller Installer { get; }
        public TestOS Os { get; }

        public Harness()
        {
            _machineFile = Path.Combine(Path.GetTempPath(), $"intochat-device-facts-{Guid.NewGuid():N}");
            Machine = new MachineId(_machineFile);
            var edge = IntoChat.OS.Tests.TestEdge.Authority;
            Installer = new DeviceInstaller(edge);

            var sc = new ServiceCollection();
            sc.AddSingleton(Machine);
            sc.AddSingleton<IHardwareProbe>(new FakeHardwareProbe());
            sc.AddSingleton<ILocalRuntime>(Runtime);
            sc.AddSingleton(Installer);
            sc.AddSingleton(Catalog);
            sc.AddSingleton(edge);
            _services = sc.BuildServiceProvider();

            Os = TestOS.Local(options => options.Services = _services);
            Os.Register<DeviceNeuron>();
            Os.Register<FakeModelCatalogNeuron>();
        }

        public IDevice Device(string brain = "owner")
            => Os.Get<IDevice>(new NeuronId(new BrainId(brain), "devices.device", $"devices/{Machine.Value}"));

        public IDeviceInstallReports Reports(string brain = "owner")
            => Os.Get<IDeviceInstallReports>(new NeuronId(new BrainId(brain), "devices.device", $"devices/{Machine.Value}"));

        public IDisposable AsPerson(string brain = "owner")
            => Os.Edge.Enter(Os.Edge.Person(brain, brain, new BrainId(brain)));

        public async ValueTask DisposeAsync()
        {
            await Installer.StopAsync(CancellationToken.None);
            await Os.DisposeAsync();
            Installer.Dispose();
            await _services.DisposeAsync();
            if (File.Exists(_machineFile))
            {
                File.Delete(_machineFile);
            }
        }
    }

    private static Install[] Active(DeviceView view)
        => [.. view.Queue.Where(static i => i.State is InstallState.Queued or InstallState.Downloading or InstallState.Verifying)];

    private static Install? Last(DeviceView view, ModelId id)
        => view.Queue.FirstOrDefault(i => i.Model == id);

    private static async Task Eventually(Func<Task> assertion)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await assertion().ConfigureAwait(false);
                return;
            }
            catch (Exception error)
            {
                last = error;
                await Task.Delay(15, Ct).ConfigureAwait(false);
            }
        }
        await assertion().ConfigureAwait(false);
        if (last is not null)
        {
            throw last;
        }
    }

    private static Task Eventually(Action assertion) => Eventually(() => { assertion(); return Task.CompletedTask; });

    [Fact]
    public async Task OneAtATime()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var device = h.Device();

        await device.Install(ModelA);
        await device.Install(ModelB);

        var active = Active(await device.Read());
        Assert.Equal([InstallState.Downloading, InstallState.Queued], active.Select(static i => i.State));

        h.Runtime.Finish(ModelA);
        await Eventually(async () =>
        {
            var current = Active(await device.Read());
            Assert.Equal(ModelB, Assert.Single(current, static i => i.State == InstallState.Downloading).Model);
        });
        h.Runtime.Finish(ModelB);
    }

    [Fact]
    public async Task TwoBrainsOneDownload()
    {
        await using var h = new Harness();
        using (h.AsPerson("owner"))
        {
            await h.Device("owner").Install(ModelA);
        }
        await h.Runtime.WaitStarted(ModelA);
        using (h.AsPerson("guest"))
        {
            await h.Device("guest").Install(ModelA);
        }

        Assert.Equal(1, h.Runtime.InstallCalls(ModelA));
        h.Runtime.Finish(ModelA);
    }

    [Fact]
    public async Task UseSwitchesOnlyWhenReady()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var device = h.Device();

        await device.Use(Role.Chat, ModelA);
        Assert.Empty(h.Catalog.SetRoles);

        h.Runtime.Finish(ModelA);
        await Eventually(() => Assert.Equal((Role.Chat, ModelA), Assert.Single(h.Catalog.SetRoles)));
    }

    [Fact]
    public async Task AFailedInstallKeepsTheOldModel()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var device = h.Device();

        await device.Use(Role.Chat, ModelA);
        h.Runtime.Fail(ModelA, "disk full");

        await Eventually(async () =>
        {
            var entry = Last(await device.Read(), ModelA);
            Assert.NotNull(entry);
            Assert.Equal((InstallState.Failed, "disk full"), (entry!.State, entry.Error));
        });
        Assert.Empty(h.Catalog.SetRoles);
    }

    [Fact]
    public async Task ProgressIsThrottledToWholePercents()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var device = h.Device();

        h.Runtime.Script(ModelA, Enumerable.Range(0, 1_000).Select(static i => i / 10.0));
        await device.Install(ModelA);
        h.Runtime.Finish(ModelA);

        await Eventually(async () =>
        {
            var entry = Last(await device.Read(), ModelA);
            Assert.Equal(InstallState.Ready, entry?.State);
        });

        var downloadingSignals = h.Os.Journal(new BrainId("owner"))
            .Signals(new NeuronId(new BrainId("owner"), "devices.device", $"devices/{h.Machine.Value}"))
            .Select(static e => e.Signal)
            .OfType<InstallChanged>()
            .Count(static s => s.State == InstallState.Downloading);
        Assert.InRange(downloadingSignals, 1, 101);
    }

    [Fact]
    public async Task ADownloadResumesAfterRestart()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var device = h.Device();

        await device.Install(ModelA);
        await h.Runtime.WaitStarted(ModelA);
        h.Runtime.Report(ModelA, 40);
        await Eventually(async () => Assert.Equal(40d, Last(await device.Read(), ModelA)?.Percent));

        await h.Os.Restart();
        // Activation of the device neuron (e.g. via DevicePrefetch or Read) resumes persisted active queue entries.
        _ = await device.Read();

        await Eventually(() => Assert.Equal(2, h.Runtime.InstallCalls(ModelA)));
        Assert.Single(Active(await device.Read()));
        h.Runtime.Finish(ModelA);
    }

    [Fact]
    public async Task CancelStopsTheRuntime()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var device = h.Device();

        await device.Install(ModelA);
        await h.Runtime.WaitStarted(ModelA);
        await device.Cancel(ModelA);

        Assert.True(h.Runtime.Cancelled(ModelA));
        await Eventually(async () => Assert.Equal(InstallState.Cancelled, Last(await device.Read(), ModelA)?.State));
    }

    [Fact]
    public async Task ARoleModelCannotBeRemoved()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        h.Runtime.MarkInstalled(CardA);
        h.Catalog.Roles = new RoleMap(
            ImmutableDictionary<Role, ModelId>.Empty.Add(Role.Chat, ModelA),
            ImmutableDictionary<Role, ModelId>.Empty);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Device().Remove(ModelA));
        Assert.Contains("Chat", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseRefusesAModelWithoutTheRolesAbility()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => h.Device().Use(Role.Hear, ModelA));
        Assert.Contains("Hear", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherMachineIsRefused()
    {
        await using var h = new Harness();
        using var asOwner = h.AsPerson();
        var other = h.Os.Get<IDevice>(new NeuronId(new BrainId("owner"), "devices.device", "devices/not-this-machine"));
        await Assert.ThrowsAsync<ServiceUnavailableException>(other.Read);
    }

    [Fact]
    public void OldStateWithoutRecentLoads()
    {
        var state = JsonSerializer.Deserialize<DeviceState>("""{"queue":[]}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(state);
        Assert.Empty(state!.Recent);
    }

    [Fact]
    public async Task ReportRefusesNonInstallerCallers()
    {
        await using var h = new Harness();
        using (h.AsPerson())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                h.Reports().Report(new InstallProgress(ModelA, InstallState.Downloading, 50, 0, 0)));
        }

        using var asAgent = h.Os.Edge.Enter(h.Os.Edge.Agent(new Actor.Person("owner", "owner"), new BrainId("owner"), conversation: "main"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            h.Reports().Report(new InstallProgress(ModelA, InstallState.Downloading, 50, 0, 0)));
    }
}
