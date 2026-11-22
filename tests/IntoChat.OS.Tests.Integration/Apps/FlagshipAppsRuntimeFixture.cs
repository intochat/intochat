using DigitalBrain.Testing;
using IntoChat.OS.Apps;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class FlagshipAppsRuntimeFixture : BothRuntimesFixture
{
    protected override IReadOnlyList<Type> Modules { get; } = [typeof(ShippedShellAppsModule)];
}
