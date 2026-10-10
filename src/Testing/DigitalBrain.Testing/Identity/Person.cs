using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Framework;

namespace DigitalBrain.Testing;

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

    public IDisposable AsCaller()
    {
        var previous = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = PrincipalId,
            AccountId = AccountId,
            BrainId = BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        return new CallerScope(previous);
    }

    // The Open posture's synthetic owner: no cookie, account and brain "owner".
    public static Person OpenOwner(HttpClient http) => new("owner", "owner", "owner", http, ownsHttp: false);

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
                new { principalId = principal, displayName = principal, password = principal + "-password-123" }, Json, ct);
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

    private sealed class CallerScope(object? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null) { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, previous); }
        }
    }
}
