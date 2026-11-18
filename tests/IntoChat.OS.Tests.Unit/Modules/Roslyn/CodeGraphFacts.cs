using System.Diagnostics;
using DigitalBrain;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Tests;

public sealed class CodeGraphFacts : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), $"intochat-codegraph-{Guid.NewGuid():N}");
    private readonly string _outsideDir = Path.Combine(Path.GetTempPath(), $"intochat-codegraph-outside-{Guid.NewGuid():N}");
    private TestOS? _os;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_rootDir, "modules", "billing"));
        Directory.CreateDirectory(Path.Combine(_rootDir, "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(_rootDir, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(_rootDir, "node_modules", "pkg"));
        Directory.CreateDirectory(_outsideDir);

        await File.WriteAllTextAsync(
            Path.Combine(_rootDir, "modules", "billing", "LedgerStore.cs"),
            """
            namespace Sample.Billing;

            public interface ILedgerStore
            {
                Task<decimal> ReadBalanceAsync(string accountId);
            }

            public sealed class LedgerStore : ILedgerStore
            {
                public const string DefaultCurrency = "USD";

                public Task<decimal> ReadBalanceAsync(string accountId) => Task.FromResult(42m);
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(_rootDir, "modules", "billing", "InvoiceService.cs"),
            """
            namespace Sample.Billing;

            public sealed class InvoiceService(ILedgerStore store)
            {
                public decimal LastTotal { get; private set; }

                public async Task<decimal> ComputeInvoiceTotalAsync(string accountId)
                {
                    var current = await store.ReadBalanceAsync(accountId);
                    LastTotal = NormalizeAmount(current);
                    return LastTotal;
                }

                private static decimal NormalizeAmount(decimal amount) => amount < 0 ? 0 : amount;
            }
            """);

        // Ignored build/dependency directories that must never be indexed.
        await File.WriteAllTextAsync(
            Path.Combine(_rootDir, "bin", "Debug", "IgnoredBinType.cs"),
            "namespace Ignored; public sealed class IgnoredBinSymbol { }");
        await File.WriteAllTextAsync(
            Path.Combine(_rootDir, "obj", "Debug", "IgnoredObjType.cs"),
            "namespace Ignored; public sealed class IgnoredObjSymbol { }");
        await File.WriteAllTextAsync(
            Path.Combine(_rootDir, "node_modules", "pkg", "IgnoredNodeSymbol.cs"),
            "namespace Ignored; public sealed class IgnoredNodeSymbol { }");
        await File.WriteAllTextAsync(
            Path.Combine(_outsideDir, "SecretOutside.cs"),
            "namespace Outside; public sealed class SecretOutsideSymbol { }");

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<RoslynModuleOptions>(opts =>
        {
            opts.AllowedRoots = [_rootDir];
            opts.Repositories["billing-repo"] = _rootDir;
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SolutionWorkspace>();
        services.AddSingleton<CodeGraphService>();
        services.AddSingleton<ISolutionLoader, MSBuildSolutionLoader>();
        services.AddSingleton<CodeFixCatalog>();
        services.AddSingleton<ChangeSetEditor>();

        _os = TestOS.Local(options => options.Services = services.BuildServiceProvider());
        _os.Register(typeof(RoslynModule).Assembly);
    }

    public async ValueTask DisposeAsync()
    {
        if (_os is not null)
        {
            await _os.DisposeAsync();
        }

        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }

        if (Directory.Exists(_outsideDir))
        {
            Directory.Delete(_outsideDir, recursive: true);
        }
    }

    private ICodeGraph Graph => _os!.Get<ICodeGraph>(new NeuronId(new BrainId("local"), "roslyn.codegraph", "roslyn/codegraph"));

    [Fact]
    public async Task SearchSymbolsFindsTypesAndMethodsAndSkipsBuildArtifacts()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        var ct = TestContext.Current.CancellationToken;
        var hit = await Graph.SearchSymbols("billing-repo", "InvoiceService", limit: 10, ct);

        Assert.Equal("billing-repo", hit.Repository);
        var cls = Assert.Single(hit.Symbols, static s => s.Name == "InvoiceService" && s.Kind == "class");
        Assert.Equal("modules/billing/InvoiceService.cs", cls.RelativePath);
        Assert.Equal("Sample.Billing", cls.Container);

        // CamelHumps lookup ("CITA" -> ComputeInvoiceTotalAsync).
        var camel = await Graph.SearchSymbols("billing-repo", "CITA", limit: 10, ct);
        Assert.Contains(camel.Symbols, static s => s.Name == "ComputeInvoiceTotalAsync" && s.Kind == "method");

        // Ignored folders (bin, obj, node_modules) are never indexed.
        var ignored = await Graph.SearchSymbols("billing-repo", "Ignored", limit: 20, ct);
        Assert.Empty(ignored.Symbols);
    }

    [Fact]
    public async Task FileOutlineExtractsNamespaceTypesPropertiesConstantsAndMethods()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        var ct = TestContext.Current.CancellationToken;
        var outline = await Graph.FileOutline("billing-repo", "modules/billing/InvoiceService.cs", ct);

        Assert.Equal("Sample.Billing", outline.Namespace);
        Assert.Equal("modules/billing/InvoiceService.cs", outline.RelativePath);
        Assert.Contains(outline.Members, static m => m.Name == "InvoiceService" && m.Kind == "class" && m.Depth == 0);
        Assert.Contains(outline.Members, static m => m.Name == "LastTotal" && m.Kind == "property" && m.Depth == 1);
        Assert.Contains(outline.Members, static m => m.Name == "ComputeInvoiceTotalAsync" && m.Kind == "method" && m.Depth == 1);
        Assert.Contains(outline.Members, static m => m.Name == "NormalizeAmount" && m.Kind == "method" && m.Depth == 1);
    }

    [Fact]
    public async Task FindCallersResolvesDirectAndQualifiedInvocations()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        var ct = TestContext.Current.CancellationToken;
        var callers = await Graph.FindCallers("billing-repo", "ILedgerStore.ReadBalanceAsync", limit: 10, ct);

        var caller = Assert.Single(callers.Callers);
        Assert.Equal("InvoiceService.ComputeInvoiceTotalAsync", caller.CallerSymbol);
        Assert.Equal("modules/billing/InvoiceService.cs", caller.RelativePath);
        Assert.Contains("store.ReadBalanceAsync", caller.CalleeExpression, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarmCacheRespondsUnder200MillisecondsAndIncrementallyRefreshesModifiedFiles()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        var ct = TestContext.Current.CancellationToken;
        _ = await Graph.SearchSymbols("billing-repo", "InvoiceService", limit: 10, ct);

        var sw = Stopwatch.StartNew();
        var warm = await Graph.SearchSymbols("billing-repo", "ReadBalanceAsync", limit: 10, ct);
        sw.Stop();

        Assert.NotEmpty(warm.Symbols);
        Assert.True(sw.ElapsedMilliseconds < 200, $"Expected warm symbol search < 200ms, got {sw.ElapsedMilliseconds}ms.");

        var addedPath = Path.Combine(_rootDir, "modules", "billing", "RefundProcessor.cs");
        await File.WriteAllTextAsync(
            addedPath,
            """
            namespace Sample.Billing;

            public sealed class RefundProcessor
            {
                public Task ApproveRefundAsync(string refundId) => Task.CompletedTask;
            }
            """,
            ct);

        var refreshed = await Graph.SearchSymbols("billing-repo", "ApproveRefundAsync", limit: 10, ct);
        Assert.Single(refreshed.Symbols, static s => s.Name == "ApproveRefundAsync");
    }

    [Fact]
    public async Task UnauthorizedRepositoryAndPathTraversalAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Graph.SearchSymbols(_outsideDir, "SecretOutsideSymbol", 10, ct));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Graph.FileOutline("billing-repo", "../escape.cs", ct));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Graph.FileOutline("billing-repo", Path.Combine(_rootDir, "modules", "billing", "InvoiceService.cs"), ct));
    }
}
