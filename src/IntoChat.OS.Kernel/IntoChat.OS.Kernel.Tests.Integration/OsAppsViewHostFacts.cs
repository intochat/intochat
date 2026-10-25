using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The Apps value tree as a real IView accepts it: a long library, every detail state, the modal and a toast.
public sealed class OsAppsViewHostFacts
{
    private static AppDetail Detail(string status = "available", string? installed = null, AppRequirements? requires = null,
        AppRequirements? consented = null, AppSlot[]? slots = null, string publisher = "os", string app = "calculator") => new(
        publisher, app, "Calculator", "Adds things up.", null, "1.2.0", installed, installed is null ? null : "1.1.0", false,
        requires ?? new(), consented, slots ?? [], status, null);

    private static AppsModel Model(AppDetail? detail = null, string? share = "https://os.example.test/#/open?application=calculator&publisher=os",
        bool confirm = false, IReadOnlyList<AppDetail>? installed = null, IReadOnlyList<AppDetail>? available = null) => new(
        installed ?? [Detail("installed", "calculator")], available ?? [Detail()],
        [.. Enumerable.Range(0, 40).Select(i => new GalleryEntry("others", "", "c" + i, "C" + i, "Connect.", null, i % 2 == 0, false, null, "connection", "c" + i, i % 3 != 0))],
        detail, share, confirm, null, null);

    [Fact]
    public async Task TheTreePassesViewValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<FlutterModule>().StartAsync(ct);
        var person = await brain.PersonAsync("apps", ct);
        using var caller = person.AsCaller();
        var app = IApp.Key(person.BrainId, "apps");
        var full = Detail("available", requires: new(["person.name"], ["gmail"], ["WEATHER_TOKEN"]), slots: [new("mail", "gmail")]);
        var many = Enumerable.Range(0, 80).Select(i => Detail(app: "app" + i)).ToArray();
        var view = brain.Get<IView>(app + "/view");
        long version = 1;
        foreach (var model in new[]
        {
            Model(installed: many, available: many) with { Toast = AppsCopy.LoadFailed, Notice = AppsCopy.LoadFailed },
            Model(full),
            Model(Detail("installed", "calculator"), confirm: true, share: null) with { Toast = "Removed Calculator." },
            Model(Detail("update", "calculator", requires: new(["person.name"]), consented: new())),
            Model(Detail("unavailable")),
        })
        {
            await view.Publish(new UiView(1, version++, AppsView.Render(app, model)));
            Assert.NotNull(await view.Read());
        }
    }
}
