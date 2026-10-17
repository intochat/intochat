using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Postgres.Tables;

// The app's table: one row per research query, keyed by a hash of the query so a retry is an upsert.
// The platform places it (the bound tables connection, else the deployment's default database).
public static class ResearchStore
{
    static readonly TableDefinition definition = new(
    [
        new TableColumn("research_id", "text"), new TableColumn("company_name", "text"), new TableColumn("website", "text"),
        new TableColumn("location", "text"), new TableColumn("email", "text"), new TableColumn("phone", "text"),
        new TableColumn("industry", "text"), new TableColumn("summary", "text"),
        new TableColumn("evidence", "jsonb"), new TableColumn("updated_at", "timestamptz"),
    ], ["research_id"]);

    // Define is idempotent for a compatible definition, so every use may call it.
    static async Task<ITable> Table(DigitalBrainConnection brain, string appKey)
    {
        var table = brain.Get<ITable>(appKey + "/table");
        await table.Define(definition);
        return table;
    }

    static TableValue[] KeyOf(string appKey, string query) =>
        [new TableValue("research_id", JsonSerializer.Serialize(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appKey + "\n" + query)))))];

    // The stored row as one JSON object, or "null".
    public static async Task<string> Row(DigitalBrainConnection brain, string appKey, string query)
    {
        var row = await (await Table(brain, appKey)).Read(KeyOf(appKey, query));
        return row is null ? "null" : "{" + string.Join(",", row.Select(value => JsonSerializer.Serialize(value.Column) + ":" + value.Json)) + "}";
    }

    public static async Task Save(DigitalBrainConnection brain, string appKey, string query, Company company)
    {
        static string J(string? value) => value is null ? "null" : JsonSerializer.Serialize(value);
        await (await Table(brain, appKey)).Upsert(KeyOf(appKey, query),
        [
            new TableValue("company_name", J(company.CompanyName)), new TableValue("website", J(company.Website)),
            new TableValue("location", J(company.Location)), new TableValue("email", J(company.Email)),
            new TableValue("phone", J(company.Phone)), new TableValue("industry", J(company.Industry)),
            new TableValue("summary", J(company.Summary)), new TableValue("evidence", JsonSerializer.Serialize(company.Evidence)),
            new TableValue("updated_at", JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("O"))),
        ]);
    }
}
