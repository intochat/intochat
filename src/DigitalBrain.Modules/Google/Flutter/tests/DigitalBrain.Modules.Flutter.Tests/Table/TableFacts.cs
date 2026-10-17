using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Table;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Table;

public sealed class TableFacts
{
    [Fact]
    public async Task ReplaceIsTheWholeTableAndReadAnswersItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var table = brain.Get<ITable>("grid");

        await table.Replace("People", [new("country", "Country"), new("n", "N")], [["us", "2"], ["uk", "1"]]);
        var read = await table.Read();

        Assert.Equal("People", read.Title);
        Assert.Equal(["country", "n"], read.Columns.Select(column => column.Id).ToArray());
        Assert.Equal(["us", "uk"], read.Rows.Select(row => row[0]).ToArray());
        Assert.Equal(1, read.Version);
        Assert.Equal(new[] { "Columns", "Name", "Rows", "Title", "Version" }, typeof(TableState).GetProperties().Select(p => p.Name).Order().ToArray());
    }
}
