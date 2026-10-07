using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Supabase;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Supabase.Tests;

public sealed class ConnectionCredentialFacts
{
    private const string One = "Host=one;Database=one;Username=reader";
    private const string Two = "Host=two;Database=two;Username=reader";

    [Fact]
    public async Task SameNamedConnectionsInTwoBrainsResolveTheirOwnValue()
    {
        var sources = new RecordingSources();
        await using var brain = await StartAsync(sources);
        try
        {
            await ConnectAsync(brain, "one", One);
            await ConnectAsync(brain, "two", Two);

            Stamp("two");
            await brain.Get<ISupabase>(ConnectionKeys.For(BrainScope.CurrentId(), "supabase", "supabase")).Query(new("select id from people"));
            Assert.Equal(Two, sources.Last);

            Stamp("one");
            await brain.Get<ISupabase>(ConnectionKeys.For(BrainScope.CurrentId(), "supabase", "supabase")).Query(new("select id from people"));
            Assert.Equal(One, sources.Last);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    [Fact]
    public async Task AUserQueryRequiresItsConnectionAndDoesNotFallBackToTheBackingStore()
    {
        var sources = new RecordingSources();
        var platform = new FakeSupabaseProvider();
        await using var brain = await StartAsync(sources, platform);
        try
        {
            Stamp("one");
            await brain.AuthorizeCallerAsync();
            var key = ConnectionKeys.For(BrainScope.CurrentId(), "supabase", "supabase");
            await Assert.ThrowsAsync<InvalidOperationException>(() => brain.Get<ISupabase>(key).Query(new("select id from people")));
            Assert.Null(sources.Last);
            Assert.Empty(platform.ExecutedSql);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    [Fact]
    public async Task AQueryAddressingAnotherBrainsConnectionIsRefused()
    {
        var sources = new RecordingSources();
        await using var brain = await StartAsync(sources);
        try
        {
            await ConnectAsync(brain, "one", One);
            Stamp("two");
            await brain.AuthorizeCallerAsync();
            var other = BrainScope.Create("account-owner", "one").Id;
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => brain.Get<ISupabase>(ConnectionKeys.For(other, "supabase", "supabase")).Query(new("select id from people")));
            Assert.Null(sources.Last);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    // Live tables and their agent tools read through ILiveTableSource: the calling brain's own
    // connection or nothing, never the deployment's backing store.
    [Fact]
    public async Task ALiveTableReadsOnlyTheCallingBrainsOwnConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new RecordingSources();
        var platform = new FakeSupabaseProvider();
        await using var brain = await StartAsync(sources, platform);
        var live = brain.SiloServices.GetRequiredService<ILiveTableSource>();
        try
        {
            Stamp("one");
            await brain.AuthorizeCallerAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => live.DescribeAsync("select id from people", ct));
            Assert.Null(sources.Last);

            await ConnectAsync(brain, "one", One);
            await ConnectAsync(brain, "two", Two);
            Stamp("one");
            await live.DescribeAsync("select id from people", ct);
            Assert.Equal([One], sources.Seen);

            Stamp("two");
            await live.ExecutePlanAsync(new("select id from people", [new("id", "int4", SupabaseTypeMap.Number)], [], null, 0, 10), ct);
            Assert.Equal([One, Two], sources.Seen);
            Assert.Empty(platform.ExecutedSql);
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }

    private static void Stamp(string brainName) => CallerContextStamper.Stamp(new()
    {
        PrincipalId = "owner",
        AccountId = "account-owner",
        BrainId = brainName,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    private static async Task ConnectAsync(ModuleBrain brain, string brainName, string value)
    {
        Stamp(brainName);
        await brain.AuthorizeCallerAsync();
        await brain.Get<IConnection>(ConnectionKeys.For(BrainScope.CurrentId(), "supabase", "supabase")).Connect("connection-string", value);
    }

    private static Task<ModuleBrain> StartAsync(RecordingSources sources, FakeSupabaseProvider? platform = null)
        => ModuleTest.Create().WithModule<SupabaseModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddSingleton<ISupabaseSourceFactory>(sources);
                if (platform is not null) { silo.Services.AddSingleton<ISupabaseBackingStore>(platform); }
            })
            .StartAsync(TestContext.Current.CancellationToken);

    private sealed class RecordingSources : ISupabaseSourceFactory
    {
        private readonly ConcurrentQueue<string> _seen = new();
        public string? Last => _seen.LastOrDefault();
        public string[] Seen => [.. _seen];
        public ISupabaseProvider ForConnectionString(string connectionString)
        {
            _seen.Enqueue(connectionString);
            return new FakeSupabaseProvider();
        }
    }
}
