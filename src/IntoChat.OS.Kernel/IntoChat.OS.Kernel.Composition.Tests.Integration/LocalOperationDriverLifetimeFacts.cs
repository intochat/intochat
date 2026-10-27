using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Operations;
using IntoChat.OS.Framework.Machine;
using IntoChat.OS.Kernel.Composition.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration;

public sealed class LocalOperationDriverLifetimeFacts
{
    private const long Started = 639287000000000000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalTopologiesRegisterProcessLifetimeEvidence(bool withCloud)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITopologyProfile>(withCloud ? new LocalCloudProfile() : new LocalProfile());
        OrleansHostWiring.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IOperationDriverLifetime>());
    }

    [Fact]
    public void CloudHostsKeepMembershipOnlyRecovery()
    {
        var services = new ServiceCollection();
        services.AddSingleton(OrleansHostWiring.TopologyProfiles[0]);
        OrleansHostWiring.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IOperationDriverLifetime>());
    }

    [Fact]
    public void TheExactProcessIsAliveRegardlessOfItsNetworkEndpoint()
    {
        var probe = new Probe();
        var before = Lifetime(probe);
        var token = Assert.IsType<string>(before.Capture());
        var after = Lifetime(probe);
        Assert.False(after.HasEnded(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewProcessRecognizesTheEndedOriginalEvenWhenPortsChange(bool pidReused)
    {
        var old = new Probe();
        var token = Assert.IsType<string>(Lifetime(old).Capture());
        var replacement = new Probe { CurrentProcessId = 84 };
        replacement.Processes[42] = pidReused ? new(true, Started + TimeSpan.TicksPerSecond) : new(false);
        Assert.True(Lifetime(replacement).HasEnded(token));
    }

    [Fact]
    public void AForeignInstallationIsNeverProbedLocally()
    {
        var token = Assert.IsType<string>(Lifetime(new Probe()).Capture());
        var probe = new Probe();
        Assert.Null(Lifetime(probe, "another-installation").HasEnded(token));
        Assert.Equal(0, probe.Reads);
    }

    [Fact]
    public void ContainersAndUnknownNamespacesProvideNoLifetimeEvidence()
    {
        var token = Assert.IsType<string>(Lifetime(new Probe()).Capture());
        var probe = new Probe { Namespace = null };
        Assert.Null(Lifetime(probe).Capture());
        Assert.Null(Lifetime(probe).HasEnded(token));
        probe.Namespace = "another-pid-namespace";
        Assert.Null(Lifetime(probe).HasEnded(token));
        Assert.Equal(0, probe.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("legacy-silo-address")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Machine\":\"install\",\"Namespace\":\"windows-host\",\"ProcessId\":42,\"StartedUtcTicks\":639287000000000000}")]
    [InlineData("{\"Version\":1,\"Machine\":\"install\",\"Namespace\":\"windows-host\",\"ProcessId\":0,\"StartedUtcTicks\":639287000000000000}")]
    [InlineData("{\"Version\":1,\"Machine\":\"install\",\"Namespace\":\"windows-host\",\"ProcessId\":42,\"StartedUtcTicks\":0}")]
    public void MalformedAndUnsupportedTokensAreUnknown(string token)
    {
        var probe = new Probe();
        Assert.Null(Lifetime(probe).HasEnded(token));
        Assert.Equal(0, probe.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InaccessibleOrUnknownProcessesAreNeverReportedDead(bool denied)
    {
        var token = Assert.IsType<string>(Lifetime(new Probe()).Capture());
        var probe = new Probe { Error = denied ? new Win32Exception(5) : null };
        probe.Processes[42] = default;
        Assert.Null(Lifetime(probe).HasEnded(token));
        Assert.Null(Lifetime(probe).Capture());
    }

    [Fact]
    public void NoMachineIdentityProvidesNoLifetimeEvidence()
    {
        var probe = new Probe();
        var lifetime = new LocalOperationDriverLifetime(null, probe);
        Assert.Null(lifetime.Capture());
        Assert.Null(lifetime.HasEnded(Assert.IsType<string>(Lifetime(new Probe()).Capture())));
        Assert.Equal(0, probe.Reads);
    }

    [Fact]
    public void NativeWindowsObservesTheRealCurrentProcessIncarnation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Native Windows process proof.");
        var probe = new SystemLocalProcessProbe();
        Assert.SkipUnless(probe.Namespace is not null, "Containers retain membership-only recovery.");
        var lifetime = Lifetime(probe);
        var token = Assert.IsType<string>(lifetime.Capture());
        using var process = Process.GetCurrentProcess();
        using var json = JsonDocument.Parse(token);
        Assert.Equal(process.Id, json.RootElement.GetProperty("ProcessId").GetInt32());
        Assert.Equal(process.StartTime.ToUniversalTime().Ticks, json.RootElement.GetProperty("StartedUtcTicks").GetInt64());
        Assert.False(lifetime.HasEnded(token));
    }

    [Fact]
    public async Task NativeWindowsRecognizesAnActualChildProcessExit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Native Windows process proof.");
        var system = new SystemLocalProcessProbe();
        Assert.SkipUnless(system.Namespace is not null, "Containers retain membership-only recovery.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("set /p operation_driver_exit=");
        using var child = Process.Start(start)!;
        try
        {
            var original = Lifetime(new ChildProbe(system, child.Id));
            var token = Assert.IsType<string>(original.Capture());
            var restarted = Lifetime(system);
            Assert.False(restarted.HasEnded(token));
            await child.StandardInput.WriteLineAsync("exit");
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(restarted.HasEnded(token));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    private static LocalOperationDriverLifetime Lifetime(ILocalProcessProbe probe, string machine = "install") => new(new Machine(machine), probe);

    private sealed class Machine(string id) : IMachineIdentity
    {
        public string Id => id;
        public string DisplayName => "test machine";
    }

    private sealed class Probe : ILocalProcessProbe
    {
        public string? Namespace { get; set; } = "windows-host";
        public int CurrentProcessId { get; set; } = 42;
        public Dictionary<int, ProcessObservation> Processes { get; } = new() { [42] = new(true, Started) };
        public Exception? Error { get; set; }
        public int Reads { get; private set; }
        public ProcessObservation Observe(int processId)
        {
            Reads++;
            if (Error is not null) { throw Error; }
            return Processes.GetValueOrDefault(processId);
        }
    }

    private sealed class ChildProbe(ILocalProcessProbe system, int pid) : ILocalProcessProbe
    {
        public string? Namespace => system.Namespace;
        public int CurrentProcessId => pid;
        public ProcessObservation Observe(int processId) => system.Observe(processId);
    }
}
