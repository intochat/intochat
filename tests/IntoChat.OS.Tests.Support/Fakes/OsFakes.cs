using DigitalBrain.Testing;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Support.Fakes.Postgres;

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
}
