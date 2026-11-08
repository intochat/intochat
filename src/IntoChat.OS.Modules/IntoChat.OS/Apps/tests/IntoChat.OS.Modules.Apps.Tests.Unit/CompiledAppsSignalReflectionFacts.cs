using DigitalBrain;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class CompiledAppsSignalReflectionFacts
{
    [Signal("test.single")]
    public sealed record SingleTextSignal(string Text, string? OptionalTag = null) : Signal;

    [Signal("test.approval")]
    public sealed record ApprovalSignal(string CardId, bool Approved = true) : Signal;

    [Signal("test.group")]
    public sealed record GroupBySignal(string? Column = null, string? Function = null) : Signal;

    [Signal("test.paged")]
    public sealed record PagedSignal(string Folder, int PageSize = 25) : Signal;

    private sealed class SampleSignalTarget
    {
        public void OnSingle(SingleTextSignal signal) => _ = signal;
        public void OnApproval(ApprovalSignal signal) => _ = signal;
        public void OnGroup(GroupBySignal signal) => _ = signal;
        public void OnPaged(PagedSignal signal) => _ = signal;
    }

    [Fact]
    public void PlainTextFillsTheFirstMemberWholeColonsIncluded()
    {
        var method = typeof(SampleSignalTarget).GetMethod(nameof(SampleSignalTarget.OnSingle))!;
        var parsed = Assert.IsType<SingleTextSignal>(
            CompiledAppsHost.BindArgument(method, "SELECT * FROM deals WHERE url = 'http://localhost:8080'"));

        Assert.Equal("SELECT * FROM deals WHERE url = 'http://localhost:8080'", parsed.Text);
        Assert.Null(parsed.OptionalTag);
    }

    [Fact]
    public void AnEmitsSingleJsonStringArgumentKeepsItsColons()
    {
        var method = typeof(SampleSignalTarget).GetMethod(nameof(SampleSignalTarget.OnSingle))!;
        var parsed = Assert.IsType<SingleTextSignal>(CompiledAppsHost.BindArgument(method, "\"card:approval:approved\""));

        Assert.Equal("card:approval:approved", parsed.Text);
        Assert.Null(parsed.OptionalTag);
    }

    [Fact]
    public void AnEmitsJsonArrayBindsPositionallyWithTypedSegments()
    {
        var approvalMethod = typeof(SampleSignalTarget).GetMethod(nameof(SampleSignalTarget.OnApproval))!;
        var rejected = Assert.IsType<ApprovalSignal>(CompiledAppsHost.BindArgument(approvalMethod, "[\"card-42\",\"false\"]"));
        Assert.Equal("card-42", rejected.CardId);
        Assert.False(rejected.Approved);

        var groupMethod = typeof(SampleSignalTarget).GetMethod(nameof(SampleSignalTarget.OnGroup))!;
        var grouped = Assert.IsType<GroupBySignal>(CompiledAppsHost.BindArgument(groupMethod, "[\"Stage\",\"SUM\"]"));
        Assert.Equal("Stage", grouped.Column);
        Assert.Equal("SUM", grouped.Function);

        var pagedMethod = typeof(SampleSignalTarget).GetMethod(nameof(SampleSignalTarget.OnPaged))!;
        var paged = Assert.IsType<PagedSignal>(CompiledAppsHost.BindArgument(pagedMethod, "[\"/work:a\",50]"));
        Assert.Equal("/work:a", paged.Folder);
        Assert.Equal(50, paged.PageSize);
    }

    [Fact]
    public void AJsonObjectBindsByNameAndEmptyInputIsTheDefault()
    {
        var groupMethod = typeof(SampleSignalTarget).GetMethod(nameof(SampleSignalTarget.OnGroup))!;
        var empty = Assert.IsType<GroupBySignal>(CompiledAppsHost.BindArgument(groupMethod, ""));
        Assert.Null(empty.Column);
        Assert.Null(empty.Function);

        var fromJson = Assert.IsType<GroupBySignal>(CompiledAppsHost.BindArgument(groupMethod, "{\"column\":\"Region\",\"function\":\"AVG\"}"));
        Assert.Equal("Region", fromJson.Column);
        Assert.Equal("AVG", fromJson.Function);
    }
}
