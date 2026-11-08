using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Auth.Ui;
using IntoChat.Cloud.Localization;
using Microsoft.Extensions.Time.Testing;
using OtpNet;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class AccountFlowFacts
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheAccountSurfaceIdIsTheSettingsMount()
    {
        Assert.Equal("account/view/surface", AuthIdentityProvider.AccountSurfaceId.ToString());
        Assert.Equal(AccountSurfaceNeuron.Type, AuthIdentityProvider.AccountSurfaceId.Type);
        Assert.Equal(AccountSurfaceNeuron.Key, AuthIdentityProvider.AccountSurfaceId.Key);
    }

    [Fact]
    public async Task ProfileSaveStaysDisabledUntilTheDraftChangesAndAStaleVersionWarns()
    {
        var (neuron, _) = await Surface();
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        var save = Buttons(neuron.Show().Root).Single(button => button.Emit!.Signal == "save");
        Assert.False(save.Enabled);
        await neuron.SaveProfile("Ada Berg", neuron.Version);
        Assert.Equal("saved", neuron.Error);
        Assert.Equal(2, neuron.Version);
        await neuron.SaveProfile("Other", neuron.Version - 1);
        Assert.Equal("stale", neuron.Error);
        await neuron.SetAvatar("image/gif", 100);
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("JPG or PNG", StringComparison.Ordinal));
        await neuron.SetAvatar("image/png", AccountSurfaceNeuron.AvatarLimit + 1);
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("5 MB", StringComparison.Ordinal));
        await neuron.SetAvatar("image/jpeg", 1024);
        Assert.DoesNotContain(Texts(neuron.Show().Root), value => value.Contains("5 MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmailChangeHasAFortyTwoSecondCooldownAndSignsOutOtherDevices()
    {
        var (neuron, clock) = await Surface();
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        await neuron.RequestEmail("ada.new@example.com");
        Assert.Equal("email-code", neuron.Screen);
        await neuron.RequestEmail("ada.new@example.com");
        Assert.Equal("wait", neuron.Error);
        clock.Advance(TimeSpan.FromSeconds(42));
        await neuron.RequestEmail("ada.new@example.com");
        Assert.Null(neuron.Error);
        await neuron.ConfirmEmail("000000");
        Assert.Equal("wrong", neuron.Error);
        await neuron.ConfirmEmail("123456");
        Assert.True(neuron.EmailChanged);
        Assert.True(neuron.OthersSignedOut);
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("other devices were signed out", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGoogleOnlyAccountHidesThePasswordSection()
    {
        var (neuron, _) = await Surface();
        await neuron.Open("Ada", "ada@example.com", true, false, null, Devices());
        await neuron.SavePassword("old-password-1", "new-password-12", "new-password-12");
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("sign in with Google", StringComparison.Ordinal));
        Assert.DoesNotContain(Fields(neuron.Show().Root), field => field.Name == "password");
    }

    [Fact]
    public async Task DevicesKeepTheCurrentRowAndConfirmBeforeSigningOutTheOthers()
    {
        var (neuron, _) = await Surface();
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        await neuron.Focus("devices");
        var labels = Texts(neuron.Show().Root).ToArray();
        Assert.True(Array.IndexOf(labels, "This device") < Array.IndexOf(labels, "Chrome"));
        Assert.DoesNotContain(Buttons(neuron.Show().Root), button => button.Emit!.Signal == "logout:this");
        await neuron.Revoke("other");
        Assert.DoesNotContain(Texts(neuron.Show().Root), value => value == "Chrome");
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        await neuron.AskRevokeOthers();
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("Log out of all other devices", StringComparison.Ordinal));
        await neuron.RevokeOthers();
        Assert.True(neuron.OthersSignedOut);
        Assert.DoesNotContain(Texts(neuron.Show().Root), value => value == "Chrome");
        await neuron.EndThisDevice();
        Assert.True(neuron.Ended);
        Assert.Equal("login", neuron.Screen);
    }

    [Fact]
    public async Task TheLicenceNoticeAppearsOnlyWhenOneIsReportedAndALobbyVisitorIsRefused()
    {
        var (neuron, _) = await Surface();
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        Assert.DoesNotContain(Texts(neuron.Show().Root), value => value.Contains("Renews", StringComparison.Ordinal));
        await neuron.Open("Ada", "ada@example.com", false, false, "Renews 2027-01-01", Devices());
        await neuron.Focus("licence");
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("Renews 2027-01-01", StringComparison.Ordinal));
        await neuron.Open("Ada", "ada@example.com", false, true, "Renews 2027-01-01", Devices());
        Assert.Equal("refused", neuron.Show().Title);
        Assert.DoesNotContain(Texts(neuron.Show().Root), value => value == "Profile");
    }

    [Fact]
    public async Task EnrolNeedsARecentSignInAndRendersAPngQrUntilTheStepIsReused()
    {
        var (neuron, clock) = await Surface();
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        await neuron.BeginEnrol(false, "ada@example.com");
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("sign in again before setting up", StringComparison.Ordinal));
        Assert.DoesNotContain(Texts(neuron.Show().Root), value => value.StartsWith("data:image/png", StringComparison.Ordinal));
        await neuron.BeginEnrol(true, "ada@example.com");
        var hidden = Texts(neuron.Show().Root).ToArray();
        var qr = hidden.Single(value => value.StartsWith("data:image/png;base64,", StringComparison.Ordinal));
        var bytes = Convert.FromBase64String(qr["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Contains(hidden, value => value == AccountScreens.QrAlt);
        Assert.DoesNotContain(hidden, value => value.Contains("JBSWY", StringComparison.Ordinal) || value.Length is > 15 and < 40 && value.All(char.IsAsciiLetterOrDigit));
        await neuron.ToggleKey();
        var secret = Texts(neuron.Show().Root).Single(value => value.Length is > 15 and < 40 && value.All(char.IsAsciiLetterOrDigit));
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(clock.GetUtcNow().UtcDateTime);
        await neuron.SubmitTotp(code);
        Assert.Equal("recovery", neuron.Screen);
        Assert.Equal(10, Texts(neuron.Show().Root).Count(value => value.Length == 8));
        Assert.False(Buttons(neuron.Show().Root).Single(button => button.Emit!.Signal == "done").Enabled);
        await neuron.FinishRecovery();
        Assert.Equal("recovery", neuron.Screen);
        await neuron.Acknowledge();
        Assert.True(Buttons(neuron.Show().Root).Single(button => button.Emit!.Signal == "done").Enabled);
        await neuron.SubmitTotp(code);
        Assert.Equal("reused", neuron.Error);
    }

    [Fact]
    public async Task RemoveAndRegenerateAskForConfirmation()
    {
        var (neuron, _) = await Surface();
        await neuron.Open("Ada", "ada@example.com", false, false, null, Devices());
        await neuron.AskRemove();
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("signs you out on your other devices", StringComparison.Ordinal));
        await neuron.RemoveTotp();
        Assert.False(neuron.TotpEnabled);
        Assert.True(neuron.OthersSignedOut);
        await neuron.AskRegenerate();
        Assert.Contains(Texts(neuron.Show().Root), value => value.Contains("current codes will stop working", StringComparison.Ordinal));
        await neuron.Regenerate();
        Assert.Equal(10, Texts(neuron.Show().Root).Count(value => value.Length == 8));
    }

    [Fact]
    public void TheTwentyOneAccountGoldensMatch()
    {
        var source = Environment.GetEnvironmentVariable("ACCOUNT_GOLDEN_DIR");
        var directory = string.IsNullOrEmpty(source) ? Path.Combine(AppContext.BaseDirectory, "Goldens", "Account") : source;
        var localizer = new TableLocalizer();
        Assert.Equal(21, AccountScreens.GoldenNames.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in AccountScreens.GoldenNames)
        {
            var actual = Json(AccountScreens.Golden(localizer, name)).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
            if (!string.IsNullOrEmpty(source))
            {
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, name + ".json"), actual);
            }

            var expected = File.ReadAllText(Path.Combine(directory, name + ".json")).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void DownloadCopiesTheCodesBecauseTheKitHasNoFileDownload()
    {
        var view = AccountScreens.Golden(new TableLocalizer(), "recovery.default");
        var download = Buttons(view.Root).Single(button => button.Emit!.Signal == "download");
        Assert.Equal("Download backup codes (.txt)", download.Label);
        Assert.Null(download.Emit!.Args);
    }

    private static async Task<(AccountSurfaceNeuron Neuron, FakeTimeProvider Clock)> Surface()
    {
        var clock = new FakeTimeProvider(Now);
        var neuron = new AccountSurfaceNeuron(new AcceptingAuth(), new TableLocalizer(), clock);
        var context = new FakeNeuronContext(AuthIdentityProvider.AccountSurfaceId);
        NeuronAttachment.Attach(neuron, context);
        await NeuronAttachment.Activate(neuron, CancellationToken.None);
        return (neuron, clock);
    }

    private static DeviceRow[] Devices() =>
    [
        new() { Id = "other", Label = "Chrome", Current = false },
        new() { Id = "this", Label = "This device", Current = true },
    ];

    private static string Json(View view) => JsonSerializer.Serialize(view, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });

    private static IEnumerable<Button> Buttons(Node node) => node switch
    {
        Button button => [button],
        Column column => column.Children.SelectMany(Buttons),
        Card card => card.Children.SelectMany(Buttons),
        _ => [],
    };

    private static IEnumerable<string> Texts(Node node) => node switch
    {
        Text text => [text.Value],
        Column column => column.Children.SelectMany(Texts),
        Card card => [card.Title, card.Body, .. card.Children.SelectMany(Texts)],
        _ => [],
    };

    private static IEnumerable<Field> Fields(Node node) => node switch
    {
        Field field => [field],
        Column column => column.Children.SelectMany(Fields),
        _ => [],
    };

    private sealed class AcceptingAuth : IAccountAuth
    {
        public Task<bool> ConfirmEmail(string email, string code, CancellationToken cancellationToken = default) => Task.FromResult(code == "123456");

        public Task<bool> ChangePassword(string current, string next, CancellationToken cancellationToken = default) => Task.FromResult(next.Length >= 12);
    }
}
