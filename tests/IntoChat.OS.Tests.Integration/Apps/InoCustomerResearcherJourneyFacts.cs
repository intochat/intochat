using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Tests.Integration.Ino;
using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
        // --- Turn 1: Research "JetBrains" via CustomerResearcherApp with an in-memory ITable ---
        var tableStore = new CustomerResearcherAppContractFacts.PostgresTableSpyRecorder();
        var services = new ServiceCollection()
            .AddSingleton(tableStore)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        os.Register<CustomerResearcherApp>();
        os.Register<CustomerResearcherAppContractFacts.SpyPostgresTableNeuron>();

        await using var researcherApp = await AppSession.OpenAsync<CustomerResearcherApp>(os, ct: Ct);
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

        // (b) The persisted postgres.table/customer_profiles returns "JetBrains" when queried.
        var table = os.Get<ITable>(new NeuronId(new BrainId("default"), "postgres.table", CustomerResearcherApp.ProfilesTableKey));
        var defaultCaller = os.Edge.Person("default", "default", new BrainId("default"));
        using (os.Edge.Enter(defaultCaller))
        {
            var readRow = await table.Read([new TableValue("company_name", JsonSerializer.Serialize("JetBrains"))]);
            Assert.NotNull(readRow);
            Assert.Contains(readRow, v => v.Column == "company_name" && v.Json == "\"JetBrains\"");
            Assert.Contains(readRow, v => v.Column == "industry" && v.Json == "\"Developer Tools\"");
            Assert.Contains(readRow, v => v.Column == "fit_score" && v.Json == "95");

            var pagedRows = await table.Page(offset: 0, limit: 50);
            Assert.Single(pagedRows);
            Assert.Contains(pagedRows[0], v => v.Column == "company_name" && v.Json == "\"JetBrains\"");
        }

        // --- Turn 2: Ask Ino to query & visualize the persisted customer_profiles table ---
        var brain = inoFixture.NewBrain("crjourney");
        using var _ = inoFixture.AsPerson(brain);

        inoFixture.Model.Respond(async (_, _, _) =>
        {
            TableValue[][] rows;
            using (os.Edge.Enter(defaultCaller))
            {
                rows = await table.Page(offset: 0, limit: 50);
            }

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

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Join("\n", lines)));
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
