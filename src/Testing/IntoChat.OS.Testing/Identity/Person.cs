using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Testing;

// A registered account with its own brain: a cookie session on the brain's HTTP edge, and the caller
// context the authenticated edge stamps for it, for direct neuron calls.
public sealed class Person : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly bool _ownsHttp;

    private Person(string principalId, string accountId, string brainId, HttpClient http, bool ownsHttp)
    {
        _ownsHttp = ownsHttp;
        PrincipalId = principalId;
        AccountId = accountId;
        BrainId = brainId;
        Http = http;
    }

    public string PrincipalId { get; }
    public string AccountId { get; }
    public string BrainId { get; }

    // The person's cookie session; for the Open posture's owner, the brain's plain client.
    public HttpClient Http { get; }

    public string Key(string suffix) => $"{BrainId}/{suffix}";

    // A fresh trace paid by this person's account, as the edge begins one per request; both are restored on dispose.
    public IDisposable AsCaller() => AsCallerIn(BrainId);

    // The person inside another brain they are a member of (a circle brain), as the /brains/{brain} route stamps them.
    public IDisposable AsCallerIn(string brainId)
        => Enter(new CallerContext
        {
            PrincipalId = PrincipalId,
            AccountId = AccountId,
            BrainId = brainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });

    // Direct-call identity fixture: add another principal to the same account and brain through
    // the real membership authority. It has no authenticated HTTP session of its own.
    public async Task<Person> AddAccountMemberAsync(ITestBrain brain, string principalId)
    {
        var identity = brain.Services.GetRequiredService<IIdentityClient>();
        var membership = await identity.Membership(BrainId, PrincipalId)
            ?? throw new InvalidOperationException("The fixture person must already be a member.");
        await identity.EnsureMember(membership with { PrincipalId = principalId, DisplayName = principalId });
        return new(principalId, AccountId, BrainId, new HttpClient { BaseAddress = Http.BaseAddress }, ownsHttp: true);
    }

    // Security facts deliberately present a model conversation carrying a user's caller kind.
    // This verifies model-origin rejection even when the kind alone would allow the operation.
    public IDisposable AsModelImpersonatingCaller(string conversationId) => Enter(new CallerContext
    {
        PrincipalId = PrincipalId,
        AccountId = AccountId,
        BrainId = BrainId,
        Kind = CallerKind.User,
        ConversationId = conversationId,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    // One of this person's installed apps, as the app proxy re-stamps a script's calls: the app key is its AppId.
    // With a conversation, the call is the app's model reading on that conversation's behalf.
    public IDisposable AsApp(string appId, string? conversationId = null) => AsAppIn(BrainId, appId, conversationId);

    // An app installed in another brain the person is a member of, running for them: the app proxy stamps the
    // app's own principal, on behalf of this person. An empty app id presents a malformed app stamp (no app key, so
    // no app principal) for facts proving modules refuse an app caller that names no app.
    public IDisposable AsAppIn(string brainId, string appId, string? conversationId = null)
        => Enter(new CallerContext
        {
            PrincipalId = string.IsNullOrWhiteSpace(appId) ? PrincipalId : AppPrincipal.Id(appId),
            OnBehalfOf = PrincipalId,
            AccountId = AccountId,
            BrainId = brainId,
            Kind = CallerKind.App,
            AppId = appId,
            ConversationId = conversationId,
            StampedBy = TrustedEdge.AppProxy,
        });

    // The kernel's scheduler edge: a reminder fires a call into this person's brain with no HTTP session,
    // as the reminder grain stamps it. A Person cannot reach it through the edge; authorization facts present it.
    public IDisposable AsScheduler() => Enter(new CallerContext
    {
        PrincipalId = PrincipalId,
        AccountId = AccountId,
        BrainId = BrainId,
        Kind = CallerKind.Scheduler,
        StampedBy = TrustedEdge.Scheduler,
    });

    // A conversation the person's assistant is running on their behalf, as the assistant tool surface stamps it.
    public IDisposable AsAssistant() => Enter(new CallerContext
    {
        PrincipalId = PrincipalId,
        AccountId = AccountId,
        BrainId = BrainId,
        Kind = CallerKind.Assistant,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    });

    private CallerScope Enter(CallerContext caller)
    {
        var previous = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        var trace = TraceContext.Enter(TraceContext.Begin(caller.AccountId, caller.BrainId, new NeuronId($"{caller.BrainId}/edge")));
        CallerContextStamper.Stamp(caller);
        return new CallerScope(previous, trace);
    }

    // The Open posture's synthetic owner: no cookie, account and brain "owner".
    // Without an HTTP edge, the owner's client has no address.
    /// <summary>The Open posture's synthetic owner (account and brain "owner"); <paramref name="http"/> is the HTTP edge, if any.</summary>
    public static Person OpenOwner(HttpClient? http) => new("owner", "owner", "owner", http ?? NoEdge, ownsHttp: false);

    // Registered without an HTTP edge: no cookie session, a client with no address.
    internal static Person Registered(Member member) => new(member.PrincipalId, member.AccountId, member.BrainId, NoEdge, ownsHttp: false);

    internal static string Password(string principal) => principal + "-password-123";

    private static readonly HttpClient NoEdge = new();

    public static async Task<Person> RegisterAsync(HttpClient origin, string principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = true, CookieContainer = new CookieContainer() })
        {
            BaseAddress = origin.BaseAddress,
            Timeout = TimeSpan.FromMinutes(5),
        };
        try
        {
            using var registered = await client.PostAsJsonAsync("/identity/register",
                new { principalId = principal, displayName = principal, password = Password(principal) }, Json, ct);
            if (registered.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Registering '{principal}' returned {(int)registered.StatusCode}: {await registered.Content.ReadAsStringAsync(ct)}");
            }
            var member = await registered.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return new(principal, member.GetProperty("accountId").GetString()!, member.GetProperty("brainId").GetString()!, client, ownsHttp: true);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) { Http.Dispose(); }
    }

    private sealed class CallerScope(object? previous, IDisposable trace) : IDisposable
    {
        public void Dispose()
        {
            trace.Dispose();
            if (previous is null) { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, previous); }
        }
    }
}
