using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.Sdk;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class AppPackageCompilerFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly AppPackageCompiler _compiler = new();

    [Fact]
    public void ShippedPackagesCompileCleanlyAndExtractMetadata()
    {
        foreach (var package in ShippedPackages.All)
        {
            var result = _compiler.Compile(package);
            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
            Assert.NotEmpty(result.AssemblyBytes);
            Assert.NotNull(result.EnrichedPackage);
            Assert.Equal(package.Key, result.EnrichedPackage!.Key);
            Assert.NotEmpty(result.EnrichedPackage.Digest);
        }
    }

    [Fact]
    public void CompilesIdiomaticBrainAppAndExtractsSlotsSignalsAndCommands()
    {
        AppSourceFile[] sources =
        [
            new AppSourceFile("MailWatcher.cs", """
                using System.Threading.Tasks;
                using IntoChat.Sdk;

                namespace Sample;

                public sealed record MailWatcherState { public int Count { get; init; } }
                public sealed record MailWatcherSettings { public string Tag { get; init; } = "vip"; }

                [App("vip-mail", Title = "VIP Mail", Summary = "Notifies on VIP mail")]
                [AppSlot("inbox", typeof(INotifications), Description = "Notification inbox")]
                public sealed class VipMailApp : BrainApp<MailWatcherState, MailWatcherSettings>
                {
                    [OnSignal("mail.received")]
                    public Task HandleMail(string subject)
                        => Brain.Notifications.NotifyAsync("VIP", subject, NotificationPriority.High);

                    [Every("15m")]
                    public Task Sweep() => Brain.LogAsync("swept");

                    [OnCommand("ping", Description = "Ping")]
                    public Task Ping() => Brain.LogAsync(Settings.Tag);
                }
                """),
        ];

        var package = new AppPackage(
            "vip-mail",
            "1.0.0",
            "VIP Mail",
            "Notifies on VIP mail",
            "mail",
            "Automation",
            "alice",
            sources,
            AppPackageNeeds.Empty,
            "{}",
            "");

        var compiled = _compiler.Compile(package);
        Assert.True(compiled.Succeeded, string.Join(Environment.NewLine, compiled.Errors));
        var needs = compiled.EnrichedPackage!.Needs;
        Assert.Contains(needs.Slots, slot => slot.Name == "inbox");
        Assert.Contains("mail.received", needs.Signals);
        Assert.Contains("15m", needs.Schedules);
        Assert.Contains(needs.Commands, cmd => cmd.Name == "ping");
    }

    [Fact]
    public void RejectsBannedDirectDiskAndNetworkApis()
    {
        AppSourceFile[] sources =
        [
            new AppSourceFile("BadApp.cs", """
                using System.IO;
                using IntoChat.Sdk;

                namespace Bad;

                public sealed record EmptyState;
                public sealed record EmptySettings;

                [App("bad-app", Title = "Bad App")]
                public sealed class BadApp : BrainApp<EmptyState, EmptySettings>
                {
                    [OnCommand("steal")]
                    public string Steal() => File.ReadAllText("C:/Windows/win.ini");
                }
                """),
        ];

        var package = new AppPackage(
            "bad-app",
            "1.0.0",
            "Bad App",
            "",
            "apps",
            "Automation",
            "mallory",
            sources,
            AppPackageNeeds.Empty,
            "{}",
            "");

        var compiled = _compiler.Compile(package);
        Assert.False(compiled.Succeeded);
        Assert.Contains(compiled.Errors, err => err.Contains("System.IO.File", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeterministicDigestMatchesAcrossCompilationsAndFakeBrainVerifiesFlow()
    {
        var invoices = ShippedPackages.Find("downloads-invoices")!;
        var first = _compiler.Compile(invoices);
        var second = _compiler.Compile(invoices);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(first.EnrichedPackage!.Digest, second.EnrichedPackage!.Digest);

        var brain = new FakeBrain("b1", "downloads-invoices");
        var folder = brain.ConfigureFolder("downloads", FolderAccess.ReadWrite);
        folder.SeedText("acme-invoice.txt", "Total due: $149.95");

        var loadedEntries = await brain.Folder("downloads").ListAsync(cancellationToken: Ct);
        Assert.Single(loadedEntries);
        await brain.Tables.Table("invoices").UpsertAsync("acme-invoice.txt", "{\"amount\":\"149.95\"}", Ct);
        await brain.Folder("downloads").MoveAsync("acme-invoice.txt", "Processed/acme-invoice.txt", Ct);
        await brain.Notifications.NotifyAsync("Invoice processed", "149.95", cancellationToken: Ct);

        Assert.Single(brain.NotificationsFake.Sent);
        Assert.Single(await brain.Tables.Table("invoices").ListAsync(cancellationToken: Ct));
        Assert.True(await brain.Folder("downloads").ExistsAsync("Processed/acme-invoice.txt", Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Folder("downloads").ExistsAsync("../outside.txt", Ct));
    }

    [Fact]
    public async Task SlotResolverProposesWindowsOrSuggestsDownloadsBinding()
    {
        var resolver = new SlotResolver();
        var downloads = ShippedPackages.Find("downloads-invoices")!;

        var resolution = await resolver.ResolveAsync("local", downloads, cancellationToken: Ct);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(resolution.NeedsApproval);
            Assert.Empty(resolution.MissingConnectors);
            var suggested = Assert.Single(resolution.SuggestedBindings);
            Assert.Equal("downloads", suggested.Slot);
            Assert.EndsWith("Downloads", suggested.HostPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(FolderAccess.ReadWrite, suggested.Access);
            Assert.True(suggested.AutoSuggested);

            var approved = await resolver.ResolveAsync(
                "local",
                downloads,
                [new FolderSlotBinding("downloads", suggested.HostPath, FolderAccess.ReadWrite)],
                Ct);
            Assert.True(approved.Ready);
            Assert.False(approved.NeedsApproval);
        }
        else
        {
            Assert.False(resolution.Ready);
            Assert.NotEmpty(resolution.MissingConnectors);
        }
    }
}
