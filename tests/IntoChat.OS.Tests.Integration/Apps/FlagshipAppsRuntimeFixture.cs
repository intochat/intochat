using DigitalBrain.Testing;
using IntoChat.OS.Apps;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class FlagshipAppsRuntimeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [.. CatalogFamily.Modules, typeof(ShippedShellAppsModule)];
}
