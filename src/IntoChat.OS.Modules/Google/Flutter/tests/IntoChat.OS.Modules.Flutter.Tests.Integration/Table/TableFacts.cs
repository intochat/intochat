using IntoChat.OS.Modules.Flutter.Table;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Table;

public sealed class TableFacts(FlutterHost host)
{
    [Fact]
    public async Task ReplaceIsTheWholeTableAndReadAnswersItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
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
