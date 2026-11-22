/*
# Customer Researcher
Role: Default
Shipped: true
Operations: research, clear, query

Researches target companies, scores ICP fit, and maintains a structured table of customer profiles.
*/
using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
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
    string Query,
    IReadOnlyList<CustomerProfile> Profiles,
    string? LastMessage)
{
    public CustomerResearcherState() : this("", [], null) { }
}

[Signal("customer-researcher.research")]
public sealed record ResearchCompany(
    string CompanyName,
    string? Industry = null,
    string? Summary = null,
    int FitScore = 80) : Signal;

[Signal("customer-researcher.clear")]
public sealed record ClearResearch() : Signal;

[Signal("customer-researcher.query")]
public sealed record QueryProfiles(string? Filter = null) : Signal;

[Neuron("app.customer-researcher"), Screen("app.customer-researcher", Title = "Customer Researcher")]
public sealed class CustomerResearcherApp(ILogger<CustomerResearcherApp>? logger = null) : Screen<CustomerResearcherState>
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

    private bool _tableDefined;

    [On]
    public async Task Research(ResearchCompany request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CompanyName))
        {
            await Show(State with { LastMessage = "Company name is required." });
            return;
        }

        var companyName = request.CompanyName.Trim();
        var industry = string.IsNullOrWhiteSpace(request.Industry) ? "General" : request.Industry.Trim();
        var summary = string.IsNullOrWhiteSpace(request.Summary)
            ? await TryResearchSummaryAsync(companyName, industry) ?? $"Profile researched for {companyName}."
            : request.Summary.Trim();
        var fitScore = Math.Clamp(request.FitScore, 0, 100);
        var status = fitScore >= 85 ? "High Fit" : fitScore >= 60 ? "Qualified" : "Low Fit";

        var profile = new CustomerProfile(companyName, industry, summary, fitScore, status);
        var updated = new List<CustomerProfile>(State.Profiles.Count + 1);
        var replaced = false;
        foreach (var existing in State.Profiles)
        {
            if (string.Equals(existing.CompanyName, companyName, StringComparison.OrdinalIgnoreCase))
            {
                updated.Add(profile);
                replaced = true;
            }
            else
            {
                updated.Add(existing);
            }
        }

        if (!replaced)
        {
            updated.Add(profile);
        }

        await Show(State with
        {
            Profiles = updated,
            LastMessage = $"Researched {companyName} ({industry}, fit {fitScore.ToString(CultureInfo.InvariantCulture)}).",
        });

        await TryPersistProfileAsync(profile);
    }

    [On]
    public async Task Clear(ClearResearch request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existingProfiles = State.Profiles;

        await Show(State with
        {
            Query = "",
            Profiles = [],
            LastMessage = "Cleared all researched customer profiles.",
        });

        await TryClearProfilesAsync(existingProfiles);
    }

    private async Task<string?> TryResearchSummaryAsync(string companyName, string industry)
    {
        if (!Implements<IWebSearch>(new NeuronId(Id.Brain, "web.search", IWebSearch.DefaultKey)))
        {
            return null;
        }

        try
        {
            var webSearch = Get<IWebSearch>(IWebSearch.DefaultKey);
            var query = string.Equals(industry, "General", StringComparison.OrdinalIgnoreCase)
                ? $"{companyName} company overview"
                : $"{companyName} {industry} company overview";
            var results = await webSearch.Search(query, count: 3);
            var snippet = results
                .Select(static r => string.IsNullOrWhiteSpace(r.Snippet) ? r.Title : r.Snippet)
                .FirstOrDefault(static s => !string.IsNullOrWhiteSpace(s));
            return snippet?.Trim();
        }
        catch (Exception ex)
        {
            logger?.LogSwallowed(ex, "querying web search for customer company overview");
            return null;
        }
    }

    private async Task TryPersistProfileAsync(CustomerProfile profile)
    {
        if (!Implements<ITable>(new NeuronId(Id.Brain, "postgres.table", ProfilesTableKey)))
        {
            return;
        }

        try
        {
            var table = Get<ITable>(ProfilesTableKey);
            if (!_tableDefined)
            {
                await table.Define(ProfilesTableDefinition);
                _tableDefined = true;
            }

            await table.Upsert(
                [new TableValue("company_name", JsonSerializer.Serialize(profile.CompanyName))],
                [
                    new TableValue("industry", JsonSerializer.Serialize(profile.Industry)),
                    new TableValue("summary", JsonSerializer.Serialize(profile.Summary)),
                    new TableValue("fit_score", profile.FitScore.ToString(CultureInfo.InvariantCulture)),
                    new TableValue("status", JsonSerializer.Serialize(profile.Status)),
                ]);
        }
        catch (Exception ex)
        {
            logger?.LogSwallowed(ex, "persisting customer profile to Postgres customer_profiles table");
        }
    }

    private async Task TryClearProfilesAsync(IReadOnlyList<CustomerProfile> profiles)
    {
        if (profiles.Count == 0
            || !Implements<ITable>(new NeuronId(Id.Brain, "postgres.table", ProfilesTableKey)))
        {
            return;
        }

        try
        {
            var table = Get<ITable>(ProfilesTableKey);
            foreach (var profile in profiles)
            {
                await table.Delete([new TableValue("company_name", JsonSerializer.Serialize(profile.CompanyName))]);
            }
        }
        catch (Exception ex)
        {
            logger?.LogSwallowed(ex, "clearing customer profiles from Postgres customer_profiles table");
        }
    }

    [On]
    public Task Query(QueryProfiles request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var filter = request.Filter?.Trim() ?? "";
        return Show(State with
        {
            Query = filter,
            LastMessage = string.IsNullOrEmpty(filter)
                ? "Showing all customer profiles."
                : $"Filtered profiles by \"{filter}\".",
        });
    }

    protected override View Render(CustomerResearcherState state)
    {
        var profiles = state.Profiles ?? [];
        var visible = string.IsNullOrWhiteSpace(state.Query)
            ? profiles
            : profiles.Where(p =>
                p.CompanyName.Contains(state.Query, StringComparison.OrdinalIgnoreCase)
                || p.Industry.Contains(state.Query, StringComparison.OrdinalIgnoreCase)
                || p.Summary.Contains(state.Query, StringComparison.OrdinalIgnoreCase)).ToArray();

        var avgFit = profiles.Count > 0
            ? ((int)Math.Round(profiles.Average(p => p.FitScore))).ToString(CultureInfo.InvariantCulture)
            : "0";
        var highFitCount = profiles.Count(p => p.FitScore >= 85).ToString(CultureInfo.InvariantCulture);

        var body = VStack(Space.Md)[
            HeaderBar(
                "Customer Researcher",
                "Company intelligence & ICP fit scoring",
                StatusPill(profiles.Count > 0 ? StatusKind.Done : StatusKind.Idle, $"{profiles.Count} researched"),
                GhostButton("Clear", Icons.Trash2)
                    .Key("btn:clear")
                    .Small()
                    .Sends(new Command(".", "customer-researcher.clear"))),
            Grid(3, Space.Sm)[
                StatCard("kpi-cr-total", "Researched", profiles.Count.ToString(CultureInfo.InvariantCulture), "Total companies", BadgeTone.Accent, Icons.Search),
                StatCard("kpi-cr-avg-fit", "Avg Fit Score", avgFit, "Out of 100", BadgeTone.Done, Icons.Sparkles),
                StatCard("kpi-cr-high-fit", "High Fit", highFitCount, "Score >= 85", BadgeTone.Neutral, Icons.Check)
            ],
            HStack(Space.Sm)[
                TextField("Company Name", "e.g. Acme Corp")
                    .Key("input:company")
                    .Value(state.Query),
                PrimaryButton("Research Company", Icons.Search)
                    .Key("btn:research")
                    .Sends(new Command(".", "customer-researcher.research"))
            ],
            !string.IsNullOrWhiteSpace(state.LastMessage)
                ? Banner("Status", state.LastMessage, tone: "neutral", key: "banner:status")
                : null,
            visible.Count > 0
                ? Table(
                    "Researched Profiles",
                    ["Company", "Industry", "Summary", "Fit", "Status"],
                    visible.Select(p => (IReadOnlyList<string>)[
                        p.CompanyName,
                        p.Industry,
                        p.Summary,
                        p.FitScore.ToString(CultureInfo.InvariantCulture),
                        p.Status,
                    ]).ToArray())
                : Empty("No companies researched yet", "Send a ResearchCompany signal or enter a company name to begin.")
        ];

        return new View("Customer Researcher", AppShell("customer-researcher-shell", sidebar: null, body: body));
    }
}
