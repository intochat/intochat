using DigitalBrain.Testing;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.Modules.Web.Search;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using IntoChat.OS.Tests.Support.Fakes.Web;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Support.Fakes;

/// <summary>
/// Extension methods that register IntoChat.OS module edge fakes into a <see cref="FakeSet"/>.
/// </summary>
public static class OsFakes
{
    /// <summary>Registers the in-memory <see cref="FakeTableStore"/> for the Postgres module.</summary>
    public static FakeSet Postgres(this FakeSet fakes)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        return fakes
            .Add<ITableStore, FakeTableStore>(new())
            .Add<ITablePlacements, SharedTablePlacements>(new());
    }

    /// <summary>Registers <see cref="FakeSearchAdapter"/> and configures web search to use it.</summary>
    public static FakeSet WebSearch(this FakeSet fakes, Action<FakeSearchAdapter>? seed = null)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        var adapter = new FakeSearchAdapter();
        seed?.Invoke(adapter);
        return fakes
            .Add<ISearchAdapter, FakeSearchAdapter>(adapter)
            .Add<IOptions<WebModuleOptions>, OptionsWrapper<WebModuleOptions>>(
                new OptionsWrapper<WebModuleOptions>(new WebModuleOptions { Provider = SearchProvider.Searxng }));
    }
}
