using IntoChat.OS.Modules.Tools;
using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Microsoft.Playwright;

// A deployment app page's agent API (window.<Api>, e.g. window.filmcraft) as a tool source, keyed
// {brain}/pages/{name}. It runs through the IPlaywright neuron at Driver, which must be attached to the
// page; the page answers only while it renders, so Run reports a hidden page instead of waiting on it.
[Alias("playwright.page-tools"), Orleans.Metadata.DefaultGrainType("playwright.page-tools")]
public interface IPageTools : IToolSource
{
    Task<PageToolsState> Configure(PageToolsSpec spec, CancellationToken ct = default);
    [ReadOnly, AlwaysInterleave] Task<PageToolsState> Read();
}

[GenerateSerializer, Alias("playwright.page-tools-spec")]
public sealed record PageToolsSpec(
    [property: Id(0)] string Title,
    [property: Id(1)] string Driver,
    [property: Id(2)] string Api);

[GenerateSerializer, Alias("playwright.page-tools-state")]
public sealed record PageToolsState(
    [property: Id(0)] string Title,
    [property: Id(1)] string Driver,
    [property: Id(2)] string Api,
    [property: Id(3)] bool Configured);
