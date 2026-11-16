using System.Net;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.Cloud.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Lobby.Tests;

public sealed class LobbyFlowFacts
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public void Lobby_posture_is_not_open()
    {
        Assert.NotEqual(IdentityPosture.Open, LobbyModule.Posture);
        Assert.Equal(IdentityPosture.Secured, LobbyModule.Posture);
    }

    [Theory]
    [InlineData("cloud", true)]
    [InlineData("local", false)]
    [InlineData("local+cloud", false)]
    [InlineData(null, false)]
    public void OnlyCloudLoadsTheLobby(string? topology, bool loads) => Assert.Equal(loads, LobbyModule.LoadsUnder(topology));

    [Fact]
    public void TheManifestRequiresIdentityAndNamesTheTokenSecret()
    {
        var manifest = ModuleManifest.Of(typeof(LobbyModule));
        Assert.Equal("lobby", manifest.Id);
        Assert.Contains(manifest.Requires, capability => capability == DigitalBrain.Capabilities.Capability.Identity.V1);
        Assert.Contains(manifest.Secrets, secret => secret.Name == LobbyModule.TokenSecret);
        Assert.Contains(manifest.Settings, setting => setting.Section == LobbyModule.OptionsSection);
    }

    [Fact]
    public void ANewSessionSetsTheLobbyCookie()
    {
        var http = Context("203.0.113.8");
        var result = LobbyHttpSurface.Session(http, Options(), new LobbyVisitorBudget(), new FixedKey(), Clock());
        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var cookie = Cookie(http, LobbyToken.CookieName);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(LobbyToken.TryRead(Key, TokenOf(cookie), Now, out _, out var prefix));
        Assert.Equal("203.0.113", prefix);
    }

    [Fact]
    public void ATamperedExpiredOrForeignTokenIsRejected()
    {
        var keys = new FixedKey();
        var budget = new LobbyVisitorBudget();
        var good = LobbyToken.Issue(Key, "visitor-a", "203.0.113", Now.AddMinutes(30));
        var tampered = good[..^1] + (good[^1] == 'a' ? "b" : "a");
        Assert.Equal(StatusCodes.Status401Unauthorized, Status(LobbyHttpSurface.Session(Context("203.0.113.8", tampered), Options(), budget, keys, Clock())));

        var expired = LobbyToken.Issue(Key, "visitor-a", "203.0.113", Now.AddMinutes(-1));
        Assert.Equal(StatusCodes.Status401Unauthorized, Status(LobbyHttpSurface.Session(Context("203.0.113.8", expired), Options(), budget, keys, Clock())));

        var otherPrefix = LobbyToken.Issue(Key, "visitor-a", "198.51.100", Now.AddMinutes(30));
        Assert.Equal(StatusCodes.Status401Unauthorized, Status(LobbyHttpSurface.Session(Context("203.0.113.8", otherPrefix), Options(), budget, keys, Clock())));
    }

    [Fact]
    public void TheTwentyFirstVisitorOnAPrefixIsUnavailableAndIdleOnesAreCollected()
    {
        var budget = new LobbyVisitorBudget();
        var clock = new FakeTimeProvider(Now);
        for (var i = 0; i < 20; i++)
        {
            var http = Context("203.0.113.8");
            var result = LobbyHttpSurface.Session(http, Options(), budget, new FixedKey(), clock);
            Assert.Contains("visitorId", Body(result), StringComparison.Ordinal);
        }

        var refusedHttp = Context("203.0.113.9");
        var refused = LobbyHttpSurface.Session(refusedHttp, Options(), budget, new FixedKey(), clock);
        Assert.Contains("unavailable", Body(refused), StringComparison.Ordinal);
        Assert.Equal("", Cookie(refusedHttp, LobbyToken.CookieName));

        clock.Advance(TimeSpan.FromMinutes(31));
        var againHttp = Context("203.0.113.8");
        LobbyHttpSurface.Session(againHttp, Options(), budget, new FixedKey(), clock);
        Assert.Contains(LobbyToken.CookieName, Cookie(againHttp, LobbyToken.CookieName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handoff_sets_member_session_cookie()
    {
        var (neuron, auth, clock, context) = await Visitor("visitor-a");
        var member = Person();
        auth.Next.Enqueue(new LobbyAuthResult("code", null, null));
        await neuron.Submit(new LobbyAuthCommand("signup", "visitor-a", "ada@example.com", "secret", "signup", true));
        Assert.Equal("code", neuron.Screen);
        Assert.Null(neuron.HandoffCode);

        auth.Next.Enqueue(new LobbyAuthResult("login", null, member));
        await neuron.Submit(new LobbyAuthCommand("code", "visitor-a", null, "123456", "signup"));
        Assert.False(neuron.HoldsAccountData);
        Assert.NotNull(neuron.HandoffCode);
        Assert.Contains(context.Published, signal => signal is LobbyEnded endedSignal && endedSignal.HandoffCode == neuron.HandoffCode);

        var issuer = new RecordingIssuer();
        var token = LobbyToken.Issue(Key, "visitor-a", "203.0.113", clock.GetUtcNow().AddMinutes(30));
        var http = Context("203.0.113.8", token);
        http.Request.Body = JsonBody(neuron.HandoffCode!);
        var result = await LobbyHttpSurface.Handoff(http, new Desk(neuron), Options(), new FixedKey(), issuer, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, Status(result));
        var session = Cookie(http, "intochat_session");
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", session, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(token, TokenOf(session));
        Assert.Equal(member.PrincipalId, issuer.Last?.Member.PrincipalId);
        Assert.Contains("expires=", Cookie(http, LobbyToken.CookieName), StringComparison.OrdinalIgnoreCase);
        Assert.Null(neuron.HandoffCode);
    }

    [Fact]
    public async Task Handoff_code_is_single_use()
    {
        var (neuron, auth, clock, _) = await Visitor("visitor-a");
        auth.Next.Enqueue(new LobbyAuthResult("login", null, Person()));
        await neuron.Submit(new LobbyAuthCommand("code", "visitor-a", "ada@example.com", "123456", "signup"));
        var code = neuron.HandoffCode!;
        var issuer = new RecordingIssuer();
        var token = LobbyToken.Issue(Key, "visitor-a", "203.0.113", clock.GetUtcNow().AddMinutes(30));
        var first = Context("203.0.113.8", token);
        first.Request.Body = JsonBody(code);
        Assert.Equal(StatusCodes.Status200OK, Status(await LobbyHttpSurface.Handoff(first, new Desk(neuron), Options(), new FixedKey(), issuer, clock, CancellationToken.None)));

        var second = Context("203.0.113.8", token);
        second.Request.Body = JsonBody(code);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(await LobbyHttpSurface.Handoff(second, new Desk(neuron), Options(), new FixedKey(), issuer, clock, CancellationToken.None)));
        Assert.Equal(1, issuer.Calls);
        Assert.DoesNotContain("intochat_session", second.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);

        auth.Next.Enqueue(new LobbyAuthResult("login", null, Person()));
        await neuron.Submit(new LobbyAuthCommand("code", "visitor-a", null, "123456", "signup"));
        var stale = neuron.HandoffCode!;
        clock.Advance(TimeSpan.FromSeconds(61));
        var late = Context("203.0.113.8", token);
        late.Request.Body = JsonBody(stale);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(await LobbyHttpSurface.Handoff(late, new Desk(neuron), Options(), new FixedKey(), issuer, clock, CancellationToken.None)));

        var (otherNeuron, otherAuth, _, _) = await Visitor("visitor-b");
        otherAuth.Next.Enqueue(new LobbyAuthResult("login", null, Person()));
        await otherNeuron.Submit(new LobbyAuthCommand("code", "visitor-b", null, "123456", "signup"));
        var foreign = Context("203.0.113.8", LobbyToken.Issue(Key, "visitor-a", "203.0.113", clock.GetUtcNow().AddMinutes(30)));
        foreign.Request.Body = JsonBody(otherNeuron.HandoffCode!);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(await LobbyHttpSurface.Handoff(foreign, new Desk(neuron), Options(), new FixedKey(), issuer, clock, CancellationToken.None)));
    }

    [Fact]
    public async Task Google_state_mismatch_is_rejected()
    {
        var (neuron, _, clock, _) = await Visitor("visitor-a");
        var nonce = await neuron.BeginGoogle();
        var issuer = new RecordingIssuer();
        var token = LobbyToken.Issue(Key, "visitor-a", "203.0.113", clock.GetUtcNow().AddMinutes(30));
        var bad = LobbyToken.SignState(Key, "visitor-a", "other-nonce", clock.GetUtcNow().AddMinutes(5));
        var http = Context("203.0.113.8", token);
        http.Request.QueryString = new QueryString("?state=" + Uri.EscapeDataString(bad));
        var result = await LobbyHttpSurface.GoogleCallbackAsync(http, new Desk(neuron), Options(), new FixedKey(), clock);
        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal(0, issuer.Calls);
        Assert.Equal("google", neuron.Screen);
        Assert.Equal("failed", neuron.Error);
        Assert.NotEqual("other-nonce", nonce);

        var (again, _, _, _) = await Visitor("visitor-c");
        var stored = await again.BeginGoogle();
        var good = LobbyToken.SignState(Key, "visitor-c", stored, clock.GetUtcNow().AddMinutes(5));
        var ok = Context("203.0.113.8", LobbyToken.Issue(Key, "visitor-c", "203.0.113", clock.GetUtcNow().AddMinutes(30)));
        ok.Request.QueryString = new QueryString("?state=" + Uri.EscapeDataString(good));
        Assert.Equal(StatusCodes.Status200OK, Status(await LobbyHttpSurface.GoogleCallbackAsync(ok, new Desk(again), Options(), new FixedKey(), clock)));
        Assert.Equal(0, issuer.Calls);
    }

    [Fact]
    public async Task AForeignOriginIsRejected()
    {
        var (neuron, auth, clock, _) = await Visitor("visitor-a");
        auth.Next.Enqueue(new LobbyAuthResult("login", null, Person()));
        await neuron.Submit(new LobbyAuthCommand("code", "visitor-a", null, "123456", "signup"));
        var http = Context("203.0.113.8", LobbyToken.Issue(Key, "visitor-a", "203.0.113", clock.GetUtcNow().AddMinutes(30)));
        http.Request.Headers.Origin = "https://evil.example";
        http.Request.Body = JsonBody(neuron.HandoffCode!);
        var issuer = new RecordingIssuer();
        Assert.Equal(StatusCodes.Status400BadRequest, Status(await LobbyHttpSurface.Handoff(http, new Desk(neuron), Options(), new FixedKey(), issuer, clock, CancellationToken.None)));
        Assert.Equal(0, issuer.Calls);
        Assert.NotNull(neuron.HandoffCode);
    }

    [Fact]
    public async Task ResetEndsOnResetDoneAndDoesNotSignIn()
    {
        var (neuron, _, _, _) = await Visitor("visitor-a");
        await neuron.FinishReset();
        Assert.Equal("reset-done", neuron.Screen);
        Assert.Null(neuron.HandoffCode);
        Assert.False(neuron.HoldsAccountData);
        await neuron.ReturnToLogin();
        Assert.Equal("login", neuron.Screen);
        Assert.Null(neuron.HandoffCode);
    }

    [Fact]
    public async Task ThreeWrongFactorsStayOnTheFactorScreenWithoutASession()
    {
        var (neuron, auth, _, _) = await Visitor("visitor-a");
        auth.Next.Enqueue(new LobbyAuthResult("factor", null, null));
        auth.Next.Enqueue(new LobbyAuthResult("factor", "wrong", null));
        auth.Next.Enqueue(new LobbyAuthResult("factor", "wrong", null));
        auth.Next.Enqueue(new LobbyAuthResult("factor", "rate", null));
        await neuron.Submit(new LobbyAuthCommand("login", "visitor-a", "ada@example.com", "secret", null));
        await neuron.Submit(new LobbyAuthCommand("factor", "visitor-a", null, "111111", null));
        await neuron.Submit(new LobbyAuthCommand("factor", "visitor-a", null, "111111", null));
        await neuron.Submit(new LobbyAuthCommand("factor", "visitor-a", null, "111111", null));
        Assert.Equal("factor", neuron.Screen);
        Assert.Null(neuron.HandoffCode);
    }

    [Fact]
    public async Task SignupForwardsConsentAndDoesNotIssueWhenConsentIsRefused()
    {
        var (neuron, auth, _, _) = await Visitor("visitor-a");
        auth.Next.Enqueue(new LobbyAuthResult("signup", "invalid", null));
        await neuron.Submit(new LobbyAuthCommand("signup", "visitor-a", "ada@example.com", "secret", "signup", false));
        Assert.False(auth.Last!.Consent);
        Assert.Null(neuron.HandoffCode);
        auth.Next.Enqueue(new LobbyAuthResult("code", null, null));
        await neuron.Submit(new LobbyAuthCommand("signup", "visitor-a", "ada@example.com", "secret", "signup", true));
        Assert.True(auth.Last!.Consent);
        Assert.Equal("code", neuron.Screen);
    }

    [Fact]
    public void ExistingAndUnknownAccountsRenderTheSameTree()
    {
        var text = new TableLocalizer();
        var wrong = LobbyScreens.Render(text, "en", new LobbyState { Screen = "login", Error = "wrong" });
        var unknown = LobbyScreens.Render(text, "en", new LobbyState { Screen = "login", Error = "unknown" });
        Assert.Equal(Json(wrong), Json(unknown));
        var invalid = LobbyScreens.Render(text, "en", new LobbyState { Screen = "signup", Error = "invalid" });
        var exists = LobbyScreens.Render(text, "en", new LobbyState { Screen = "signup", Error = "exists" });
        Assert.Equal(Json(invalid), Json(exists));
    }

    [Fact]
    public void SignupStartsWithConsentOffAndTheSubmitDisabled()
    {
        var view = LobbyScreens.Golden(new TableLocalizer(), "signup.default");
        var buttons = Buttons(view.Root).ToArray();
        var submit = buttons.Single(button => button.Emit!.Signal == "submit");
        Assert.False(submit.Enabled);
        var consent = buttons.Single(button => button.Emit!.Signal == "consent");
        Assert.Equal(ButtonTone.Neutral, consent.Tone);
        Assert.Equal("You need to agree to continue.", consent.Label);
        Assert.Contains(Texts(view.Root), value => value.Contains("Terms of Service", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTwentyNineLobbyGoldensMatch()
    {
        var source = Environment.GetEnvironmentVariable("LOBBY_GOLDEN_DIR");
        var directory = string.IsNullOrEmpty(source) ? Path.Combine(AppContext.BaseDirectory, "Goldens", "Lobby") : source;
        var localizer = new TableLocalizer();
        Assert.Equal(29, LobbyScreens.GoldenNames.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in LobbyScreens.GoldenNames)
        {
            var path = Path.Combine(directory, name + ".json");
            var actual = Json(LobbyScreens.Golden(localizer, name)).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
            if (!string.IsNullOrEmpty(source))
            {
                Directory.CreateDirectory(source);
                File.WriteAllText(path, actual);
            }
            var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void CodeFieldsAreTextFieldsAndResetDoesNotAutoSignIn()
    {
        var localizer = new TableLocalizer();
        var code = LobbyScreens.Golden(localizer, "code.default");
        Assert.Contains(Fields(code.Root), field => field.Name == "code");
        var done = LobbyScreens.Golden(localizer, "reset-done.default");
        Assert.Contains(Texts(done.Root), value => value.Contains("Log in with your new password", StringComparison.Ordinal));
        Assert.DoesNotContain(Texts(done.Root), value => value.Contains("Save and log in", StringComparison.Ordinal));
        Assert.Contains(Buttons(LobbyScreens.Golden(localizer, "login.default").Root), button => button.Tone == ButtonTone.Neutral && button.Label == "Forgot password?");
    }

    private static async Task<(LobbyNeuron Neuron, Scripted Auth, FakeTimeProvider Clock, FakeNeuronContext Context)> Visitor(string key)
    {
        var auth = new Scripted();
        var clock = new FakeTimeProvider(Now);
        var services = new ServiceCollection()
            .AddSingleton<ILobbyAuth>(auth)
            .AddSingleton<ILocalizer>(new TableLocalizer())
            .AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();
        var neuron = ActivatorUtilities.CreateInstance<LobbyNeuron>(services);
        var context = new FakeNeuronContext(new NeuronId(new BrainId("lobby"), LobbyNeuron.Type, key));
        NeuronAttachment.Attach(neuron, context);
        await NeuronAttachment.Activate(neuron, CancellationToken.None);
        return (neuron, auth, clock, context);
    }

    private static Member Person() => new()
    {
        PrincipalId = "ada",
        AccountId = "acc-ada",
        BrainId = "brain-ada",
        Role = MemberRole.Owner,
        DisplayName = "Ada",
        JoinedAt = Now,
    };

    private static LobbyModuleOptions Options() => new() { VisitorsPerPrefix = 20, BrainId = "lobby" };

    private static FakeTimeProvider Clock() => new(Now);

    private static DefaultHttpContext Context(string ip, string? lobby = null)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        http.Request.Host = new HostString("lobby.example");
        if (lobby is not null) { http.Request.Headers.Cookie = LobbyToken.CookieName + "=" + lobby; }
        return http;
    }

    private static int Status(IResult result) => result is IStatusCodeHttpResult coded ? coded.StatusCode ?? StatusCodes.Status200OK : 0;

    private static string Body(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new EmptyServices() };
        http.Response.Body = new MemoryStream();
        result.ExecuteAsync(http).GetAwaiter().GetResult();
        http.Response.Body.Position = 0;
        return new StreamReader(http.Response.Body).ReadToEnd();
    }

    private static string Cookie(HttpContext http, string name)
    {
        foreach (var value in http.Response.Headers.SetCookie)
        {
            if (value is not null && value.StartsWith(name + "=", StringComparison.Ordinal)) { return value; }
        }

        return "";
    }

    private static string TokenOf(string setCookie)
    {
        var value = setCookie[(setCookie.IndexOf('=') + 1)..];
        var semi = value.IndexOf(';');
        return semi < 0 ? value : value[..semi];
    }

    private static MemoryStream JsonBody(string code) => new(Encoding.UTF8.GetBytes("{\"code\":\"" + code + "\"}"));

    private static string Json(View view) => JsonSerializer.Serialize(view, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });

    private static IEnumerable<Button> Buttons(Node node) => node switch
    {
        Button button => [button],
        Column column => column.Children.SelectMany(Buttons),
        _ => [],
    };

    private static IEnumerable<string> Texts(Node node) => node switch
    {
        Text text => [text.Value],
        Column column => column.Children.SelectMany(Texts),
        _ => [],
    };

    private static IEnumerable<Field> Fields(Node node) => node switch
    {
        Field field => [field],
        Column column => column.Children.SelectMany(Fields),
        _ => [],
    };

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(Microsoft.Extensions.Logging.ILoggerFactory))
            {
                return Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
            }

            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            {
                return Activator.CreateInstance(typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()))!;
            }

            return null;
        }
    }

    private sealed class FixedKey : ILobbyTokenKey
    {
        public byte[] Key => LobbyFlowFacts.Key;
    }

    private sealed class Scripted : ILobbyAuth
    {
        public Queue<LobbyAuthResult> Next { get; } = new();
        public LobbyAuthCommand? Last { get; private set; }

        public Task<LobbyAuthResult> Submit(LobbyAuthCommand command, CancellationToken cancellationToken = default)
        {
            Last = command;
            return Task.FromResult(Next.Dequeue());
        }
    }

    private sealed class Desk(LobbyNeuron neuron) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => (T)(object)neuron;
    }

    private sealed class RecordingIssuer : ISessionIssuer
    {
        public int Calls { get; private set; }
        public SessionRequest? Last { get; private set; }

        public Task<SessionIssued> IssueAsync(HttpContext http, SessionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = request;
            http.Response.Cookies.Append("intochat_session", "member-session", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
            });
            return Task.FromResult(new SessionIssued("member-session", Now.AddDays(7)));
        }

        public Task EndAsync(HttpContext http, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
