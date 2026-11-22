using IntoChat.OS.Modules.Postgres;
using IntoChat.OS.Modules.Postgres.Tables;
using Xunit;

namespace IntoChat.OS.Tests.Support.Conformance;

/// <summary>
/// Conformance kit for the Postgres <c>ITableStore</c> outside edge: verifies Define, Upsert (insert, update,
/// idempotent no-op), Read, Page (limit/offset), Delete, Drop, and table isolation.
/// </summary>
public abstract class TableStoreConformanceKit
{
    private static readonly TableDefinition ProfilesSchema = new(
        [
            new TableColumn("id", "text"),
            new TableColumn("name", "text"),
            new TableColumn("score", "number"),
        ],
        ["id"]);

    /// <summary>Creates a fresh, empty table store instance for a single fact.</summary>
    internal abstract ITableStore CreateStore();

    /// <summary>Define creates a table, is idempotent for compatible schemas, and rejects incompatible schemas.</summary>
    [Fact]
    public async Task DefineCreatesTableAndRejectsIncompatibleSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        await store.DefineAsync(TablePlacement.Shared, "profiles_define", ProfilesSchema, ct).ConfigureAwait(false);
        await store.DefineAsync(TablePlacement.Shared, "profiles_define", ProfilesSchema, ct).ConfigureAwait(false);

        var incompatible = new TableDefinition(
            [
                new TableColumn("id", "text"),
                new TableColumn("score", "text"),
            ],
            ["id"]);

        await Assert.ThrowsAsync<PostgresQueryException>(
            () => store.DefineAsync(TablePlacement.Shared, "profiles_define", incompatible, ct)).ConfigureAwait(false);
    }

    /// <summary>Upsert inserts new rows, updates changed rows, is an idempotent no-op when unchanged, and Read returns them.</summary>
    [Fact]
    public async Task UpsertInsertUpdateIdempotentNoOpAndRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.DefineAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, ct).ConfigureAwait(false);

        TableValue[] key = [new("id", "\"acme\"")];
        TableValue[] initial = [new("name", "\"Acme Corp\""), new("score", "90")];

        Assert.Null(await store.ReadAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, key, ct).ConfigureAwait(false));

        Assert.True(await store.UpsertAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, key, initial, ct).ConfigureAwait(false));

        var inserted = await store.ReadAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, key, ct).ConfigureAwait(false);
        Assert.NotNull(inserted);
        Assert.Equal("\"Acme Corp\"", ValueOf(inserted, "name"));
        Assert.Equal("90", ValueOf(inserted, "score"));

        // Re-upserting identical values is an idempotent no-op.
        Assert.False(await store.UpsertAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, key, initial, ct).ConfigureAwait(false));

        // Updating a non-key column mutates the row and returns true.
        TableValue[] updated = [new("name", "\"Acme Logistics\""), new("score", "95")];
        Assert.True(await store.UpsertAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, key, updated, ct).ConfigureAwait(false));

        var afterUpdate = await store.ReadAsync(TablePlacement.Shared, "profiles_upsert", ProfilesSchema, key, ct).ConfigureAwait(false);
        Assert.NotNull(afterUpdate);
        Assert.Equal("\"Acme Logistics\"", ValueOf(afterUpdate, "name"));
        Assert.Equal("95", ValueOf(afterUpdate, "score"));
    }

    /// <summary>Page returns rows in deterministic order respecting offset and limit.</summary>
    [Fact]
    public async Task PageRespectsOffsetAndLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.DefineAsync(TablePlacement.Shared, "profiles_page", ProfilesSchema, ct).ConfigureAwait(false);

        foreach (var (id, name, score) in new[]
        {
            ("a", "Alpha", 10),
            ("b", "Beta", 20),
            ("c", "Gamma", 30),
            ("d", "Delta", 40),
        })
        {
            await store.UpsertAsync(
                TablePlacement.Shared,
                "profiles_page",
                ProfilesSchema,
                [new("id", $"\"{id}\"")],
                [new("name", $"\"{name}\""), new("score", $"{score}")],
                ct).ConfigureAwait(false);
        }

        var firstPage = await store.PageAsync(TablePlacement.Shared, "profiles_page", ProfilesSchema, offset: 0, limit: 2, ct).ConfigureAwait(false);
        Assert.Equal(["\"a\"", "\"b\""], firstPage.Select(row => ValueOf(row, "id")).ToArray());

        var middlePage = await store.PageAsync(TablePlacement.Shared, "profiles_page", ProfilesSchema, offset: 1, limit: 2, ct).ConfigureAwait(false);
        Assert.Equal(["\"b\"", "\"c\""], middlePage.Select(row => ValueOf(row, "id")).ToArray());

        var tailPage = await store.PageAsync(TablePlacement.Shared, "profiles_page", ProfilesSchema, offset: 3, limit: 5, ct).ConfigureAwait(false);
        Assert.Equal(["\"d\""], tailPage.Select(row => ValueOf(row, "id")).ToArray());

        var emptyPage = await store.PageAsync(TablePlacement.Shared, "profiles_page", ProfilesSchema, offset: 10, limit: 5, ct).ConfigureAwait(false);
        Assert.Empty(emptyPage);
    }

    /// <summary>Delete removes an existing row and reports whether a row was deleted.</summary>
    [Fact]
    public async Task DeleteRemovesRowAndReportsExistence()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.DefineAsync(TablePlacement.Shared, "profiles_delete", ProfilesSchema, ct).ConfigureAwait(false);

        TableValue[] key = [new("id", "\"globex\"")];
        await store.UpsertAsync(
            TablePlacement.Shared,
            "profiles_delete",
            ProfilesSchema,
            key,
            [new("name", "\"Globex\""), new("score", "70")],
            ct).ConfigureAwait(false);

        Assert.True(await store.DeleteAsync(TablePlacement.Shared, "profiles_delete", ProfilesSchema, key, ct).ConfigureAwait(false));
        Assert.Null(await store.ReadAsync(TablePlacement.Shared, "profiles_delete", ProfilesSchema, key, ct).ConfigureAwait(false));
        Assert.False(await store.DeleteAsync(TablePlacement.Shared, "profiles_delete", ProfilesSchema, key, ct).ConfigureAwait(false));
    }

    /// <summary>Drop removes the table so subsequent reads fail and re-defining starts empty.</summary>
    [Fact]
    public async Task DropRemovesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.DefineAsync(TablePlacement.Shared, "profiles_drop", ProfilesSchema, ct).ConfigureAwait(false);

        TableValue[] key = [new("id", "\"initech\"")];
        await store.UpsertAsync(
            TablePlacement.Shared,
            "profiles_drop",
            ProfilesSchema,
            key,
            [new("name", "\"Initech\""), new("score", "50")],
            ct).ConfigureAwait(false);

        await store.DropAsync(TablePlacement.Shared, "profiles_drop", ct).ConfigureAwait(false);

        await Assert.ThrowsAsync<PostgresQueryException>(
            () => store.ReadAsync(TablePlacement.Shared, "profiles_drop", ProfilesSchema, key, ct)).ConfigureAwait(false);

        await store.DefineAsync(TablePlacement.Shared, "profiles_drop", ProfilesSchema, ct).ConfigureAwait(false);
        Assert.Null(await store.ReadAsync(TablePlacement.Shared, "profiles_drop", ProfilesSchema, key, ct).ConfigureAwait(false));
    }

    /// <summary>Rows are isolated across distinct table names and distinct placements.</summary>
    [Fact]
    public async Task TablesAreIsolatedByNameAndPlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var otherPlacement = TablePlacement.BrainDatabase("brain_other");

        await store.DefineAsync(TablePlacement.Shared, "table_a", ProfilesSchema, ct).ConfigureAwait(false);
        await store.DefineAsync(TablePlacement.Shared, "table_b", ProfilesSchema, ct).ConfigureAwait(false);
        await store.DefineAsync(otherPlacement, "table_a", ProfilesSchema, ct).ConfigureAwait(false);

        TableValue[] key = [new("id", "\"shared-key\"")];
        await store.UpsertAsync(
            TablePlacement.Shared,
            "table_a",
            ProfilesSchema,
            key,
            [new("name", "\"FromTableA\""), new("score", "1")],
            ct).ConfigureAwait(false);

        Assert.Null(await store.ReadAsync(TablePlacement.Shared, "table_b", ProfilesSchema, key, ct).ConfigureAwait(false));
        Assert.Null(await store.ReadAsync(otherPlacement, "table_a", ProfilesSchema, key, ct).ConfigureAwait(false));

        await store.UpsertAsync(
            TablePlacement.Shared,
            "table_b",
            ProfilesSchema,
            key,
            [new("name", "\"FromTableB\""), new("score", "2")],
            ct).ConfigureAwait(false);

        var rowA = await store.ReadAsync(TablePlacement.Shared, "table_a", ProfilesSchema, key, ct).ConfigureAwait(false);
        var rowB = await store.ReadAsync(TablePlacement.Shared, "table_b", ProfilesSchema, key, ct).ConfigureAwait(false);
        Assert.NotNull(rowA);
        Assert.NotNull(rowB);
        Assert.Equal("\"FromTableA\"", ValueOf(rowA, "name"));
        Assert.Equal("\"FromTableB\"", ValueOf(rowB, "name"));
    }

    private static string ValueOf(TableValue[] row, string column)
        => Assert.Single(row, v => string.Equals(v.Column, column, StringComparison.Ordinal)).Json;
}
