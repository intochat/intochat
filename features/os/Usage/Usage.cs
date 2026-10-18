using System.Globalization;
using System.Text;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Compute;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.Text;

public static class Usage
{
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static async Task Compose(IntoChatOSConnection brain)
    {
        var key = Key(brain);
        var scope = key[..key.IndexOf("/apps/", StringComparison.Ordinal)];
        string money(long micros) => "$" + (micros / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
        string content;
        try
        {
            var state = await brain.Get<ICompute>(scope + "/compute").Read();
            var text = new StringBuilder("# Usage\n\n");
            text.AppendLine("**" + money(state.SpentMicros) + "** recorded in this brain over the last 30 days.\n");
            text.AppendLine("Amounts in USD · 1,000,000 micro-credits = $1.\n");
            text.AppendLine("## By app\n");
            if (state.Rows.Length == 500) { text.AppendLine("Showing up to 500 apps, highest spending first.\n"); }
            if (state.Rows.Length == 0) { text.AppendLine("No recorded charges in this period."); }
            foreach (var row in state.Rows)
            {
                var name = string.IsNullOrWhiteSpace(row.Key) ? "System" : IApp.NameOf(scope, row.Key) ?? row.Key;
                text.AppendLine("- **" + name.Replace("*", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "** — " + money(row.Micros) + " · " + row.Count.ToString(CultureInfo.InvariantCulture) + " records");
            }
            text.AppendLine("\nUpdated " + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + ".");
            text.AppendLine("\nSpending belongs to this brain. Locally run work or your own connections can record zero cost.");
            content = text.ToString();
        }
        catch (Exception)
        { content = "# Usage\n\nUsage could not be read. Refresh to try again. No estimate is shown."; }
        await brain.Get<IText>(key + "/summary").Set(content);
        await brain.Get<IButton>(key + "/refresh").Set("Refresh usage", "refresh", true);
        await brain.Get<IButton>(key + "/refresh").SetVisual(new(Icon: "refresh"));
        var layout = brain.Get<ILayout>(key + "/full");
        await layout.Set(new("column", [new("text", key + "/summary"), new("button", key + "/refresh")], Gap: 16, Extents: [0, 44]), (await layout.Read()).Revision);
        await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, CultureInfo.InvariantCulture), new(Views: [new("full", new UiChildRef("layout", key + "/full"))]));
    }
}
