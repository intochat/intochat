using System.Text.Json;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Framework.Platform.Entries;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// Settings as a value tree: tabs, each tab's controls by reference, and nothing a person typed in a key or text.
public sealed class OsSettingsViewFacts
{
    private const string App = "b1/apps/settings";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static SettingsModel Model(string tab = "general", bool confirm = false, string? toast = null, int archived = 2, int total = 2) => new(
        tab, App, new("auto", "Automatic", true),
        [new("profile:fast", "Fast", true), new("preset:Big", "Big", false)],
        [.. Enumerable.Range(0, archived).Select(i => new ArchivedRow("Trip " + i, Now.AddHours(-i - 1), "token" + i))], total,
        [new("gmail", "Gmail", "Connected", true), new("salesforce", "Salesforce", "Not connected", false)],
        confirm, toast);

    private static IEnumerable<UiNode> All(UiNode node) => node.Children.SelectMany(All).Prepend(node);

    private static UiNode Find(UiNode root, string key) => All(root).Single(node => node.Key == key);

    private static (string Kind, string Name)[] References(UiNode root)
        => [.. All(root).Where(node => node.Kind == UiNodeKind.Reference).Select(node => (node.Reference!.Kind, node.Reference.Name))];

    [Fact]
    public void TabsAreTheEightInOrderWithBillingDisabled()
    {
        Assert.Equal(
            [("general", "General"), ("models", "AI models"), ("keys", "API keys"), ("connections", "Connections"),
             ("variables", "Variables and secrets"), ("notifications", "Notifications"), ("archived", "Archived chats"), ("danger", "Danger zone")],
            SettingsTabs.All);
        var tabs = Find(SettingsView.Render(Model("keys"), Now), "tabs");
        Assert.Equal(UiNodeKind.Tabs, tabs.Kind);
        Assert.Equal([.. SettingsTabs.All.Select(tab => "tab-" + tab.Id), "tab-billing"], tabs.Children.Select(child => child.Key));
        Assert.Equal("tab-keys", tabs.Children.Single(child => child.Style == UiStyle.Primary).Key);
        Assert.Equal("tab:connections", tabs.Children[3].Action);
        var billing = tabs.Children[^1];
        Assert.Equal(("Billing · Later", "tab:billing", false, UiStyle.Ghost), (billing.Text!, billing.Action!, billing.Enabled, billing.Style));
    }

    [Theory]
    [InlineData("profile", "general")]
    [InlineData("appearance", "general")]
    [InlineData("developer", "general")]
    [InlineData("gallery", "general")]
    [InlineData("ui-kit", "general")]
    [InlineData("advanced", "general")]
    [InlineData("agents", "models")]
    [InlineData("nope", "general")]
    [InlineData(null, "general")]
    [InlineData("archived", "archived")]
    [InlineData("danger", "danger")]
    public void OldSectionNamesMapToTabs(string? section, string tab) => Assert.Equal(tab, SettingsTabs.Id(section));

    public static TheoryData<string, string[]> TabReferences => new()
    {
        { "general", ["preference:pref/display-name", "preference:pref/theme", "preference:pref/compact", "preference:pref/reduced-motion", "preference:pref/new-tab"] },
        { "models", ["preference:pref/default-model"] },
        { "keys", ["entrycontrol:keys/openai", "entrycontrol:keys/anthropic", "entrycontrol:keys/google", "entrycontrol:keys/xai", "entrycontrol:keys/openrouter"] },
        { "connections", ["button:connect/salesforce"] },
        { "variables", ["entrycontrol:variables"] },
        { "notifications", ["preference:pref/notify-agent-finished", "preference:pref/notify-needs-approval", "preference:pref/notify-connection-errors", "preference:pref/notify-product-updates"] },
        { "archived", [] },
        { "danger", [] },
    };

    [Theory]
    [MemberData(nameof(TabReferences))]
    public void EachTabRendersItsControlsByReference(string tab, string[] expected)
    {
        var root = SettingsView.Render(Model(tab), Now);
        var body = Find(root, "tab-body-" + tab);
        Assert.Equal((UiNodeKind.Column, SettingsTabs.Label(tab)), (body.Kind, body.Children[0].Text!));
        Assert.Equal(("heading", "description"), (body.Children[0].Key, body.Children[1].Key));
        Assert.Equal(expected, References(root).Select(r => r.Kind + ":" + r.Name[(App.Length + 1)..]));
        Assert.Single(All(root), node => node.Key.StartsWith("tab-body-", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyProvidersFollowTheKeyedProviders()
        => Assert.Equal(BrainEntries.KeyedProviderIds, SettingsCopy.KeyProviders.Select(provider => provider.Id));

    [Fact]
    public void AutoComesFirstAndUnavailableModelsAreDisabledWithFixedCopy()
    {
        var models = Find(SettingsView.Render(Model("models"), Now), "models");
        Assert.Equal(UiNodeKind.List, models.Kind);
        Assert.Equal(["Auto, cheapest model with tools", "Fast", "Big"], models.Children.Select(row => row.Text!));
        Assert.All(models.Children, row => Assert.Equal(UiNodeKind.Card, row.Kind));
        Assert.Equal([null, null, "Not available. Add a key in API keys or pick another model."], models.Children.Select(row => row.Detail));
        Assert.Equal(("Unavailable", UiNodeKind.Badge), (models.Children[2].Children.Single().Text!, models.Children[2].Children.Single().Kind));
        Assert.Empty(models.Children[1].Children);

        var choices = SettingsView.ModelChoices(Model("models"));
        Assert.Equal(["auto", "profile:fast", "preset:Big"], choices.Select(choice => choice.Id));
        Assert.Equal("Auto, cheapest model with tools", choices[0].Label);
        Assert.Equal([true, true, false], choices.Select(choice => choice.Enabled));
        Assert.Equal(SettingsCopy.ModelUnavailable, choices[2].Detail);
    }

    [Fact]
    public void ArchivedRowsUseIndexKeysAndShowMore()
    {
        var root = SettingsView.Render(Model("archived", archived: 2, total: 101), Now);
        var list = Find(root, "archived");
        Assert.Equal(["archived-0", "archived-1"], list.Children.Select(row => row.Key));
        var row = list.Children[1];
        Assert.Equal(UiNodeKind.Row, row.Kind);
        Assert.Equal(["Trip 1", "2 h ago", "Unarchive"], row.Children.Select(child => child.Text!));
        Assert.Equal("unarchive:token1", row.Children[2].Action);
        Assert.Equal(("Show more", "archived-more"), (Find(root, "archived-more").Text!, Find(root, "archived-more").Action!));

        Assert.DoesNotContain(All(SettingsView.Render(Model("archived", total: 100), Now)), node => node.Key == "archived-more");
        var empty = SettingsView.Render(Model("archived", archived: 0, total: 0), Now);
        Assert.Contains(All(empty), node => node.Text == "Archived chats appear here.");
        Assert.Equal("2026-09-01", SettingsView.Ago(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Now));
    }

    [Fact]
    public void DeleteConfirmIsAModalWithTheAccountButton()
    {
        var closed = SettingsView.Render(Model("danger"), Now);
        var card = Find(closed, "delete-account");
        Assert.Equal(("Delete account", "Opens your account page, where deletion is managed."), (card.Text!, card.Detail!));
        Assert.Equal(("delete", UiStyle.Danger), (card.Children.Single().Action!, card.Children.Single().Style));
        Assert.DoesNotContain(All(closed), node => node.Kind == UiNodeKind.Modal);

        var modal = Find(SettingsView.Render(Model("danger", confirm: true), Now), "confirm-delete");
        Assert.Equal((UiNodeKind.Modal, "Delete your account?", "Deletion is managed on your account page."), (modal.Kind, modal.Text!, modal.Detail!));
        Assert.Equal(new UiChildRef(UIVocabulary.ButtonType, App + "/delete-account"), modal.Children[0].Reference);
        Assert.Equal(("Cancel", "delete-cancel", UiStyle.Ghost), (modal.Children[1].Text!, modal.Children[1].Action!, modal.Children[1].Style));
    }

    [Fact]
    public void TheToastSitsBelowTheBody()
    {
        var root = SettingsView.Render(Model(toast: "Chat restored."), Now);
        Assert.Equal(["tabs", "tab-body-general", "toast"], root.Children.Select(child => child.Key));
        Assert.Equal([40d, 0d, 1d], root.Extents!);
        Assert.Equal(UiTone.Error, Find(SettingsView.Render(Model(toast: SettingsCopy.SaveFailed), Now), "toast").Tone);
        Assert.Equal([40d, 0d], SettingsView.Render(Model(), Now).Extents!);
    }

    [Fact]
    public void NoKeyOrTextCarriesEntryNames()
    {
        foreach (var (tab, _) in SettingsTabs.All)
        {
            var root = SettingsView.Render(Model(tab, confirm: true, toast: "Saved.", total: 150), Now);
            Assert.All(All(root), node => Assert.True(Enum.IsDefined(node.Kind) && (int)node.Kind <= 13));
            var json = JsonSerializer.Serialize(root);
            foreach (var forbidden in new[] { "provider:", "fixture-value", "c-1111" })
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
            }
            Assert.All(All(root), node => Assert.DoesNotContain(":", node.Key, StringComparison.Ordinal));
        }
    }
}
