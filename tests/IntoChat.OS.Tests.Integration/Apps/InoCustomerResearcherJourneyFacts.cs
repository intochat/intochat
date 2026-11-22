using System.Text.Json;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Tests.Integration.Ino;
using IntoChat.OS.Modules.Postgres.Tables;
using IntoChat.OS.Tests.Integration.Journeys;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.Postgres;
using Microsoft.Extensions.AI;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

/// <summary>
/// End-to-end user journey proving that:
/// 1. Researching "JetBrains" in <see cref="CustomerResearcherApp"/> updates its UI and persists
///    the profile row into <c>postgres.table/customer_profiles</c> (<see cref="ITable"/>).
/// 2. Asking Ino to query and visualize <c>customer_profiles</c> reads the persisted row from
///    the table and renders "JetBrains", "Developer Tools", and "95" on Ino's screen.
/// </summary>
public sealed class InoCustomerResearcherJourneyFacts(InoSiloFixture inoFixture)
    : IClassFixture<InoSiloFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResearchJetBrains_PersistsToCustomerProfilesTable_AndInoVisualizesQueriedRow()
    {
        // --- Turn 1: Research "JetBrains" via CustomerResearcherApp with real TableNeuron + FakeTableStore ---
        await using var os = await Journey.Start(NeuronRuntimeKind.TestOS, f => f.Postgres().WebSearch(), Ct);
        var alice = os.Person("alice");

        await using var researcherApp = await alice.Open<CustomerResearcherApp>(ct: Ct);
        researcherApp.ShouldDisplayText("Customer Researcher");

        await researcherApp.Send(
            new ResearchCompany(
                "JetBrains",
                "Developer Tools",
                "IDEs, Kotlin, and AI coding tools",
                95),
            Ct);

        // (a) CustomerResearcherApp UI displays "JetBrains", "Developer Tools", and "95".
        researcherApp
            .ShouldDisplayText("JetBrains")
            .ShouldDisplayText("Developer Tools")
            .ShouldDisplayText("95");

        // (b) The persisted postgres.table/customer_profiles stores "JetBrains".
        var tableStore = os.Fake<FakeTableStore>();
        var persistedRows = tableStore.RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey);
        Assert.Single(persistedRows);
        Assert.Contains(persistedRows[0], v => v.Column == "company_name" && v.Json == "\"JetBrains\"");
        Assert.Contains(persistedRows[0], v => v.Column == "industry" && v.Json == "\"Developer Tools\"");
        Assert.Contains(persistedRows[0], v => v.Column == "fit_score" && v.Json == "95");

        // --- Turn 2: Ask Ino to query & visualize the persisted customer_profiles table ---
        var brain = inoFixture.NewBrain("crjourney");
        using var _ = inoFixture.AsPerson(brain);

        inoFixture.Model.Respond((_, _, _) =>
        {
            var rows = tableStore.RowsFor(alice.Brain, "app.customer-researcher", CustomerResearcherApp.ProfilesTableKey);

            var lines = new List<string>
            {
                "### Customer Profiles (`customer_profiles`)",
                "",
                "| Company | Industry | Fit Score | Status | Summary |",
                "| --- | --- | --- | --- | --- |",
            };

            foreach (var row in rows)
            {
                var company = Unquote(row, "company_name");
                var industry = Unquote(row, "industry");
                var fitScore = Unquote(row, "fit_score");
                var status = Unquote(row, "status");
                var summary = Unquote(row, "summary");
                lines.Add($"| {company} | {industry} | {fitScore} | {status} | {summary} |");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join("\n", lines))));
        });

        var inoScreen = inoFixture.InoScreen(brain);
        await inoScreen.Request("ino.open", JsonSerializer.SerializeToElement(new InoOpen()));
        await inoFixture.WaitForScreenAsync(
            inoScreen,
            v => InoSiloFixture.TryGetConversation(v, out var conv) && conv.Composer.Enabled,
            Ct);

        await inoFixture.ActOnThreadAsync(
            brain,
            ChatThreadNeuron.ComposerKey,
            "Query and visualize the customer_profiles table.");

        // (c) Ino's visualization renders "JetBrains", "Developer Tools", and "95" from the queried table.
        var completed = await inoFixture.WaitForScreenAsync(
            inoScreen,
            v =>
            {
                var texts = PresentedView.From(v).Texts;
                return texts.Any(t => t.Contains("JetBrains", StringComparison.Ordinal))
                    && InoSiloFixture.TryGetConversation(v, out var conv)
                    && conv.Composer.Enabled;
            },
            Ct);

        var conversation = InoSiloFixture.RequireConversation(completed);
        Assert.Equal("ready", conversation.Status);
        var assistantReply = Assert.Single(conversation.Messages, m => m.Role == "assistant");
        Assert.Contains("JetBrains", assistantReply.Text, StringComparison.Ordinal);
        Assert.Contains("Developer Tools", assistantReply.Text, StringComparison.Ordinal);
        Assert.Contains("95", assistantReply.Text, StringComparison.Ordinal);
    }

    private static string Unquote(TableValue[] row, string column)
    {
        var raw = row.FirstOrDefault(v => v.Column == column)?.Json ?? string.Empty;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }

        return raw;
    }
}
