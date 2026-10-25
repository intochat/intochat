using System.ComponentModel;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace IntoChat.OS.Modules.Microsoft.Windows;

// One Windows PC, keyed {brain}/connection/windows/{machineId}. Served only by a silo running on that PC.
[Alias("windows"), Orleans.Metadata.DefaultGrainType("windows")]
public interface IWindows : INeuron
{
    [Description("Facts about this Windows machine: id, name, Windows version, processors and memory.")]
    [ReadOnly, AlwaysInterleave]
    Task<WindowsFacts> Facts();

    [Description("The folders on this machine that files and console commands may use.")]
    [ReadOnly, AlwaysInterleave]
    Task<string[]> Roots();

    [Description("Allow files and console commands inside a folder on this machine.")]
    Task AllowRoot(string path);

    [Description("Stop allowing a folder on this machine.")]
    Task RevokeRoot(string path);
}

[GenerateSerializer, Alias("windows.facts")]
public sealed record WindowsFacts(
    [property: Id(0)] string MachineId,
    [property: Id(1)] string MachineName,
    [property: Id(2)] string WindowsVersion,
    [property: Id(3)] int ProcessorCount,
    [property: Id(4)] long TotalMemoryBytes);
