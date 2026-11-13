namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

[CollectionDefinition(Name)]
public sealed class OsAppHostCollection : ICollectionFixture<OsAppHostFixture>
{
    public const string Name = "os-app-host";
}
