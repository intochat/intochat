/*
# Customer Researcher
Role: Default
Shipped: true
Operations: research, clear, refresh

Researches target companies, scores ICP fit, and maintains a structured table of customer profiles.
*/
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Modules.Web;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.CustomerResearcher;

public sealed record CustomerProfile(
    string CompanyName,
    string Industry,
    string Summary,
    int FitScore,
    string Status);

public sealed record CustomerResearcherState(
    string Draft,
    CustomerProfile[] Profiles,
    string? LastMessage,
    bool ReadOnly)
{
    public CustomerResearcherState() : this("", [], null, false) { }

    public CustomerResearcherState SetDraft(string? draft) => this with { Draft = draft ?? "" };

    // Backward-compatible read-only shim for state persisted under the previous "Query" field name.
    [JsonPropertyName("Query")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyQuery
    {
        get => null;
        init
        {
            if (string.IsNullOrEmpty(Draft) && !string.IsNullOrEmpty(value))
            {
                Draft = value;
            }
        }
    }
}

[Signal("customer-researcher.research")]
public sealed record ResearchCompany(
    string CompanyName,
    string? Industry = null,
    string? Summary = null,
    int FitScore = 80) : Signal;

[Signal("customer-researcher.clear")]
public sealed record ClearResearch() : Signal;

[Signal("customer-researcher.refresh")]
public sealed record RefreshProfiles() : Signal;

[Neuron("app.customer-researcher")]
[Screen("app.customer-researcher", Title = "Customer Researcher", Icon = Icons.Search, Category = "Workspace")]
[Description("Researches target companies, scores ICP fit, and maintains a structured table of customer profiles.")]
public sealed partial class CustomerResearcherApp(ILogger<CustomerResearcherApp>? logger = null)
    : AggregateScreen<CustomerResearcherState>
{
    public const string ProfilesTableKey = "customer_profiles";

    private static readonly TableDefinition ProfilesTableDefinition = new(
        [
            new TableColumn("company_name", "text"),
            new TableColumn("industry", "text"),
            new TableColumn("summary", "text"),
            new TableColumn("fit_score", "number"),
            new TableColumn("status", "text"),
        ],
        ["company_name"]);

    [Slot(Key = ProfilesTableKey)] private partial ITable Customers { get; }
    [Slot] private partial IWebSearch Search { get; }

    private ILogger Log => logger ?? GetLogger();

    private bool IsOfferedTable => Slots.BindingOf("customers")?.Target is BindingTarget.Offered;

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
        var readOnly = IsOfferedTable;
        try
        {
            if (!readOnly)
            {
                await Customers.Define(ProfilesTableDefinition).ConfigureAwait(true);
            }

            var profiles = await LoadProfilesAsync().ConfigureAwait(true);
            await Show(State with { Profiles = profiles, ReadOnly = readOnly }).ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning(ex, "Customer Researcher table access was denied during activation.");
            await Show(State with
            {
                Profiles = [],
                LastMessage = "This table is no longer shared with you.",
                ReadOnly = readOnly,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Customer Researcher could not load profiles during activation.");
            await Show(State with
            {
                LastMessage = $"Could not load profiles: {ex.Message}",
                ReadOnly = readOnly,
            }).ConfigureAwait(true);
        }
    }

    [On, Description("Research a company and save its profile.")]
    public async Task Research(ResearchCompany request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State.ReadOnly)
        {
            await Show(State with { LastMessage = "This shared table is read-only." }).ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.CompanyName))
        {
            await Show(State with { LastMessage = "Company name is required." }).ConfigureAwait(true);
            return;
        }

        var companyName = request.CompanyName.Trim();
        var industry = string.IsNullOrWhiteSpace(request.Industry) ? "General" : request.Industry.Trim();
        var summary = string.IsNullOrWhiteSpace(request.Summary)
            ? await SearchSummaryAsync(companyName, industry).ConfigureAwait(true) ?? $"Profile researched for {companyName}."
            : request.Summary.Trim();
        var fitScore = Math.Clamp(request.FitScore, 0, 100);
        var status = fitScore >= 85 ? "High Fit" : fitScore >= 60 ? "Qualified" : "Low Fit";

        try
        {
            await Customers.Upsert(
                [new TableValue("company_name", JsonSerializer.Serialize(companyName))],
                [
                    new TableValue("industry", JsonSerializer.Serialize(industry)),
                    new TableValue("summary", JsonSerializer.Serialize(summary)),
                    new TableValue("fit_score", fitScore.ToString(CultureInfo.InvariantCulture)),
                    new TableValue("status", JsonSerializer.Serialize(status)),
                ]).ConfigureAwait(true);

            var profiles = await LoadProfilesAsync().ConfigureAwait(true);
            await Show(State with
            {
                Draft = "",
                Profiles = profiles,
                LastMessage = $"Researched {companyName} ({industry}, fit {fitScore.ToString(CultureInfo.InvariantCulture)}).",
            }).ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning(ex, "Customer Researcher table access was denied while saving a profile.");
            await Show(State with
            {
                Profiles = [],
                LastMessage = "This table is no longer shared with you.",
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Customer Researcher could not save profile.");
            await Show(State with
            {
                LastMessage = $"Could not save profile: {ex.Message}",
            }).ConfigureAwait(true);
        }
    }

    [On, Description("Remove every saved profile.")]
    public async Task Clear(ClearResearch request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (State.ReadOnly)
        {
            await Show(State with { LastMessage = "This shared table is read-only." }).ConfigureAwait(true);
            return;
        }

        try
        {
            var existing = await LoadProfilesAsync().ConfigureAwait(true);
            foreach (var profile in existing)
            {
                await Customers.Delete([new TableValue("company_name", JsonSerializer.Serialize(profile.CompanyName))]).ConfigureAwait(true);
            }

            var remaining = await LoadProfilesAsync().ConfigureAwait(true);
            await Show(State with
            {
                Draft = "",
                Profiles = remaining,
                LastMessage = "Cleared all researched customer profiles.",
            }).ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning(ex, "Customer Researcher table access was denied while clearing profiles.");
            await Show(State with
            {
                Profiles = [],
                LastMessage = "This table is no longer shared with you.",
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Customer Researcher could not clear profiles.");
            await Show(State with
            {
                LastMessage = $"Could not clear profiles: {ex.Message}",
            }).ConfigureAwait(true);
        }
    }

    [On, Description("Reload profiles from the table.")]
    public async Task Refresh(RefreshProfiles request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var readOnly = IsOfferedTable;
        try
        {
            var profiles = await LoadProfilesAsync().ConfigureAwait(true);
            await Show(State with
            {
                Profiles = profiles,
                ReadOnly = readOnly,
                LastMessage = $"Loaded {profiles.Length.ToString(CultureInfo.InvariantCulture)} profiles.",
            }).ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning(ex, "Customer Researcher table access was denied while refreshing profiles.");
            await Show(State with
            {
                Profiles = [],
                ReadOnly = readOnly,
                LastMessage = "This table is no longer shared with you.",
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Customer Researcher could not refresh profiles.");
            await Show(State with
            {
                ReadOnly = readOnly,
                LastMessage = $"Could not refresh profiles: {ex.Message}",
            }).ConfigureAwait(true);
        }
    }

    private async Task<string?> SearchSummaryAsync(string companyName, string industry)
    {
        try
        {
            var query = string.Equals(industry, "General", StringComparison.OrdinalIgnoreCase)
                ? companyName
                : $"{companyName} {industry}";
            var results = await Search.Search(query, count: 3).ConfigureAwait(true);
            var snippet = results
                .Select(static r => string.IsNullOrWhiteSpace(r.Snippet) ? r.Title : r.Snippet)
                .FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s));
            return snippet?.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Web search failed while researching {CompanyName}; using fallback summary.", companyName);
            return null;
        }
    }

    private async Task<CustomerProfile[]> LoadProfilesAsync()
    {
        var rows = await Customers.Page().ConfigureAwait(true);
        var profiles = new List<CustomerProfile>(rows.Length);
        foreach (var row in rows)
        {
            var byCol = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cell in row)
            {
                byCol[cell.Column] = cell.Json;
            }

            var companyName = ReadString(byCol, "company_name");
            if (string.IsNullOrWhiteSpace(companyName))
            {
                continue;
            }

            var industry = ReadString(byCol, "industry") ?? "General";
            var summary = ReadString(byCol, "summary") ?? "";
            var fitScore = ReadInt(byCol, "fit_score");
            var status = ReadString(byCol, "status") ?? (fitScore >= 85 ? "High Fit" : fitScore >= 60 ? "Qualified" : "Low Fit");
            profiles.Add(new CustomerProfile(companyName, industry, summary, fitScore, status));
        }

        return [.. profiles];
    }

    private static string? ReadString(Dictionary<string, string> row, string column)
    {
        if (!row.TryGetValue(column, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<string>(json);
    }

    private static int ReadInt(Dictionary<string, string> row, string column)
    {
        if (!row.TryGetValue(column, out var json) || string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        return (int)Math.Round(JsonSerializer.Deserialize<double>(json));
    }

    protected override Node Render(CustomerResearcherState state)
    {
        var profiles = state.Profiles ?? [];
        var avgFit = profiles.Length > 0
            ? ((int)Math.Round(profiles.Average(p => p.FitScore))).ToString(CultureInfo.InvariantCulture)
            : "0";
        var highFitCount = profiles.Count(p => p.FitScore >= 85).ToString(CultureInfo.InvariantCulture);

        var body = VStack(Space.Md)[
            HeaderBar(
                "Customer Researcher",
                "Company intelligence & ICP fit scoring",
                StatusPill(profiles.Length > 0 ? StatusKind.Done : StatusKind.Idle, $"{profiles.Length} researched"),
                GhostButton("Clear", Icons.Trash2)
                    .Key("btn:clear")
                    .Small()
                    .Disabled(state.ReadOnly)
                    .Sends(Sends(new ClearResearch()))),
            Grid(3, Space.Sm)[
                StatCard("kpi-cr-total", "Researched", profiles.Length.ToString(CultureInfo.InvariantCulture), "Total companies", BadgeTone.Accent, Icons.Search),
                StatCard("kpi-cr-avg-fit", "Avg Fit Score", avgFit, "Out of 100", BadgeTone.Done, Icons.Sparkles),
                StatCard("kpi-cr-high-fit", "High Fit", highFitCount, "Score >= 85", BadgeTone.Neutral, Icons.Check)
            ],
            HStack(Space.Sm)[
                TextField("Company Name", "e.g. Acme Corp")
                    .Key("input:company")
                    .Value(state.Draft)
                    .OnChange<CustomerResearcherState>(s => s.SetDraft(Arg.Text)),
                PrimaryButton("Research Company", Icons.Search)
                    .Key("btn:research")
                    .Disabled(state.ReadOnly)
                    .Sends(Sends(new ResearchCompany(state.Draft)))
            ],
            !string.IsNullOrWhiteSpace(state.LastMessage)
                ? Banner("Status", state.LastMessage, tone: "neutral", key: "banner:status")
                : null,
            profiles.Length > 0
                ? Table(
                    "Researched Profiles",
                    ["Company", "Industry", "Summary", "Fit", "Status"],
                    profiles.Select(p => (IReadOnlyList<string>)[
                        p.CompanyName,
                        p.Industry,
                        p.Summary,
                        p.FitScore.ToString(CultureInfo.InvariantCulture),
                        p.Status,
                    ]).ToArray())
                : Empty("No companies researched yet", "Send a ResearchCompany signal or enter a company name to begin.")
        ];

        return AppShell("customer-researcher-shell", sidebar: null, body: body);
    }
}
