using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Identity;
using DigitalBrain.Testing.Conformance.Secrets;
using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Neurons;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Mail;
using IntoChat.Cloud.Mail.Templates;
using IntoChat.Cloud.Organisations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using DigitalBrain.Runtime.Orleans.Hosting;

[assembly: AssemblyFixture(typeof(IntoChat.Cloud.Auth.Tests.AuthHost))]

namespace IntoChat.Cloud.Auth.Tests;

// Mail + Organisations + a licensed Auth in the Secured posture. Person/Owner are never used: built-in register/login are
// unmapped once Auth is registered, so people sign up through Auth's own routes.
public sealed class AuthHost : BrainHostFixture
{
    public const string MailHashingKey = "dGVzdC1oYXNoaW5nLWtleS0zMi1ieXRlcy1sb25nISE=";
    public const string AuthHashingKey = "YXV0aC10ZXN0LWhhc2hpbmcta2V5LTMyLWJ5dGVzLWxvbmch";
    public const string SenderAddress = "DoNotReply@intochat.test";
    public const string PublicBaseUrl = "https://app.test";
    public const string Password = "correct horse battery";
    public const string OsInstall = "https://os.install.test";
    public const string OtherReturnOrigin = "https://www.install.test";

    // A throwaway P-256 pair made in this test process for external sign-in tickets; it never leaves memory.
    public static ECDsa TicketKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);


    public AuthHost() => Sender = new ControllableMailSender(Mail);

    public FakeMailSender Mail { get; } = new();

    public ControllableMailSender Sender { get; }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public RecordingSignInHandler SignIns { get; } = new();

    internal ScriptedGoogleBackchannel Google { get; } = new();

    public HttpClient GoogleClient() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false }) { BaseAddress = Brain.HttpClient.BaseAddress };

    internal CapturingLoggerProvider Logs { get; } = new();

    internal AuthHashing Hashing => Brain.Services.GetRequiredService<AuthHashing>();

    protected override TestBrain Configure(TestBrain brain) => Compose(brain, TestLicense.Valid())
        .ConfigureServices(services =>
        {
            services.AddSingleton<ISignInHandler>(SignIns);
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Sender);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<global::Microsoft.AspNetCore.Authentication.Google.GoogleOptions>("Google", options =>
            {
                options.BackchannelHttpHandler = Google;
                options.Backchannel = new HttpClient(Google);
                options.CorrelationCookie.SecurePolicy = global::Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
            });
            services.AddLogging(logging =>
            {
                logging.AddProvider(Logs);
                logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            });
        });

    // The shape every Auth host shares: the Auth module and the Organisations stand-ins over the in-memory capabilities, in the
    // Secured posture with the deployment secrets the manifest declares.
    internal static TestBrain Compose(
        TestBrain brain,
        string? license,
        string posture = "Secured",
        string? publicBaseUrl = PublicBaseUrl,
        int ipThrottleLimit = 10_000)
    {
        var secrets = new Dictionary<string, string>
        {
            [AuthSecrets.HashingKey] = AuthHashingKey,
            [AuthSecrets.SigningKey] = Convert.ToBase64String(TicketKey.ExportPkcs8PrivateKey()),
            [AuthSecrets.GoogleClientId] = "test-client-id",
            [AuthSecrets.GoogleClientSecret] = "test-client-secret",
        };
        brain = brain
            .WithModule<AuthHostModule>()
            .WithModule<OrganisationsHostModule>()
            .ConfigureServices(services =>
            {
                AuthCapabilities.Register(services, secrets);
                services.TryAddSingleton<IMailSender>(new FakeMailSender());
                services.Configure<AuthModuleOptions>(options =>
                {
                    options.PasswordIterations = 1000;
                    options.IpThrottleLimit = ipThrottleLimit;
                    options.PublicBaseUrl = publicBaseUrl;
                    options.License = license;
                    options.OsInstalls = [OsInstall];
                    options.AllowedReturnOrigins = [OsInstall, OtherReturnOrigin];
                    options.Posture = Enum.Parse<IdentityPosture>(posture);
                });
                services.RemoveAll<LicenseVerifier>();
                services.AddSingleton(_ => TestLicense.Verifier());
            });
        return brain;
    }

    public static string UniqueEmail(string name) => $"{name}-{Guid.NewGuid():N}@example.com";

    // A client that keeps the session cookie the routes issue.
    public HttpClient CookieClient() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = Brain.HttpClient.BaseAddress };

    public string EmailHash(string email) => Hashing.EmailSubject(EmailNormalizer.Normalize(email)!);

    public T Neuron<T>(string fullKey) where T : class => SiloNeurons.Get<T>(Brain.Services, fullKey);

    internal IAuthEmailIndex Index(string email) => Neuron<IAuthEmailIndex>(IAuthEmailIndex.Key(EmailHash(email)));

    internal IAuthCode CodeSlot(string email, CodePurpose purpose) => Neuron<IAuthCode>(IAuthCode.KeyForSubject(EmailHash(email), purpose));

    internal IAuthSessions Sessions(string principalId) => Neuron<IAuthSessions>(IAuthSessions.Key(principalId));

    internal IAuthCredential Credential(string principalId) => Neuron<IAuthCredential>(IAuthCredential.Key(principalId));

    internal IAuthSystemAudit SystemAudit => Neuron<IAuthSystemAudit>(IAuthSystemAudit.Key());

    public IOrgMembership Membership(string principalId) => Neuron<IOrgMembership>(IOrgMembership.Key(principalId));

    public Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) =>
        client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    public static async Task<(HttpStatusCode Status, string Body)> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    public static JsonElement Parse(string body) => JsonDocument.Parse(body).RootElement.Clone();

    public Task<HttpResponseMessage> SignUpRequestAsync(HttpClient client, string email, string password = Password, bool consent = true, string language = "en") =>
        PostAsync(client, "/auth/signup", new { email, password, displayName = "Test Person", language, consent });

    // The newest mail of a template sent to an address, waiting for the off-path sender (up to 5 s).
    public async Task<SentMail> MailAsync(string email, MailTemplateId template, int after = 0)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var matching = Mail.Sent.Where(sent => sent.To == email && sent.Template == template).ToArray();
            if (matching.Length > after)
            {
                return matching[^1];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No {template} mail #{after + 1} arrived.");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    public int MailCount(string email, MailTemplateId? template = null) =>
        Mail.Sent.Count(sent => sent.To == email && (template is null || sent.Template == template));

    public async Task<string> CodeAsync(string email, MailTemplateId template, int after = 0) =>
        (await MailAsync(email, template, after)).Data[MailDataKeys.Code];

    // Signs up through the real routes and returns a cookie client holding the new session.
    public async Task<AuthUser> SignUpAsync(string? email = null, string password = Password)
    {
        email ??= UniqueEmail("user");
        var client = CookieClient();
        var (status, body) = await ReadAsync(await SignUpRequestAsync(client, email, password));
        Assert.Equal(HttpStatusCode.Accepted, status);
        var codeId = Parse(body).GetProperty("codeId").GetString()!;
        var code = await CodeAsync(email, MailTemplateId.SignupCode);
        var (verifyStatus, verifyBody) = await ReadAsync(await PostAsync(client, $"/auth/codes/{codeId}/verify", new { code }));
        Assert.Equal(HttpStatusCode.OK, verifyStatus);
        var ids = Parse(verifyBody);
        return new AuthUser(client, email, password, ids.GetProperty("principalId").GetString()!, ids.GetProperty("accountId").GetString()!, ids.GetProperty("brainId").GetString()!);
    }

    // The member stamped as an authenticated HTTP caller, for direct [Public] neuron calls.
    public static IDisposable AsCaller(AuthUser user)
    {
        var previous = global::Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        global::Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, new CallerContext
        {
            PrincipalId = user.PrincipalId,
            AccountId = user.AccountId,
            BrainId = user.BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        return new Restore(previous);
    }

    private sealed class Restore(object? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null)
            {
                global::Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
            }
            else
            {
                global::Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, previous);
            }
        }
    }
}

public sealed record AuthUser(HttpClient Client, string Email, string Password, string PrincipalId, string AccountId, string BrainId) : IDisposable
{
    public void Dispose() => Client.Dispose();
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyList<string> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}




