using IntoChat.OS.Cli;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Unit.Cli;

// The command line names the kernel's edge and the brain; the proof of who is asking comes from the environment only.
public sealed class CliOptionsFacts
{
    private static CliOptions Parse(Dictionary<string, string>? environment, params string[] args)
        => CliOptions.Parse(args, name => environment?.GetValueOrDefault(name));

    [Fact]
    public void Defaults_AreTheLocalKernelAndTheOwnerBrain_WithNoProof()
    {
        var options = Parse(null, "runs");

        Assert.Equal("runs", options.Verb);
        Assert.Equal(new Uri(CliOptions.DefaultBase), options.Base);
        Assert.Equal("owner", options.Brain);
        Assert.Equal(20, options.Limit);
        Assert.Null(options.Proof);
    }

    [Fact]
    public void BaseBrainAndLimit_ComeFromTheCommandLine_BeforeTheEnvironment()
    {
        var options = Parse(new() { ["INTOCHAT_BASE"] = "https://env.example", ["INTOCHAT_BRAIN"] = "env" },
            "send", "--base", "https://os.example", "app", "--brain", "home", "go", "--limit", "5");

        Assert.Equal(new Uri("https://os.example"), options.Base);
        Assert.Equal("home", options.Brain);
        Assert.Equal(5, options.Limit);
        Assert.Equal(["app", "go"], options.Arguments);
    }

    [Fact]
    public void BaseAndBrain_FallBackToTheEnvironment()
    {
        var options = Parse(new() { ["INTOCHAT_BASE"] = "https://env.example", ["INTOCHAT_BRAIN"] = "env" }, "runs");

        Assert.Equal(new Uri("https://env.example"), options.Base);
        Assert.Equal("env", options.Brain);
    }

    [Theory]
    [InlineData("tcp://localhost:30000")]
    [InlineData("not a url")]
    public void ABaseThatIsNotHttp_IsRefused(string address)
        => Assert.Throws<ArgumentException>(() => Parse(null, "runs", "--base", address));

    // The cluster options are gone with the cluster client: nothing on the command line names a gateway or a person.
    [Fact]
    public void TheGatewayAndPersonOptions_AreNoLongerOptions()
    {
        var options = Parse(null, "runs", "--gateway", "30000", "--principal", "someone");

        Assert.Equal(["--gateway", "30000", "--principal", "someone"], options.Arguments);
        Assert.Null(options.Proof);
    }

    [Fact]
    public void ThePresenceToken_IsTheLocalProof_AndWinsOverTheOthers()
    {
        var options = Parse(new() { [CliProof.PresenceVariable] = "token", [CliProof.BasicVariable] = "owner:pw" }, "runs");

        Assert.Equal(new CliProof(CliProofKind.Presence, "token"), options.Proof);
        Assert.Equal([new KeyValuePair<string, string>(PresenceProof.HeaderName, "token")], options.Proof!.Headers());
    }

    [Fact]
    public void ABasicCredential_TravelsAsAnAuthorizationHeader()
    {
        var options = Parse(new() { [CliProof.BasicVariable] = "owner:pw" }, "runs", "--base", "https://os.example");

        Assert.Equal(CliProofKind.Basic, options.Proof!.Kind);
        Assert.Equal([new KeyValuePair<string, string>("Authorization", "Basic " + Convert.ToBase64String("owner:pw"u8.ToArray()))], options.Proof.Headers());
    }

    [Fact]
    public void ASession_TravelsAsItsCookie()
    {
        var options = Parse(new() { [CliProof.SessionVariable] = "session=abc" }, "runs", "--base", "https://os.example");

        Assert.Equal([new KeyValuePair<string, string>("Cookie", "session=abc")], options.Proof!.Headers());
    }

    [Theory]
    [InlineData("INTOCHAT_BASIC", "no-colon")]
    [InlineData("INTOCHAT_SESSION", "no-equals")]
    public void AMalformedCredential_IsRefused(string variable, string value)
        => Assert.Throws<ArgumentException>(() => Parse(new() { [variable] = value }, "runs"));

    [Theory]
    [InlineData("INTOCHAT_BASIC", "owner:pw")]
    [InlineData("INTOCHAT_SESSION", "session=abc")]
    public void APasswordOrSession_GoesToAnotherMachineOverHttpsOnly(string variable, string value)
    {
        Assert.Throws<ArgumentException>(() => Parse(new() { [variable] = value }, "runs", "--base", "http://os.example"));
        Assert.NotNull(Parse(new() { [variable] = value }, "runs", "--base", "http://localhost:5080").Proof);
        Assert.NotNull(Parse(new() { [variable] = value }, "runs", "--base", "https://os.example").Proof);
    }

    [Fact]
    public void AProof_NeverPrintsItsSecret()
    {
        var options = Parse(new() { [CliProof.PresenceVariable] = "very-secret-token" }, "runs");

        Assert.DoesNotContain("very-secret-token", options.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownVerb_PrintsTheUsage()
    {
        var error = new StringWriter();

        var code = await CliCommands.Run(["frobnicate"], TextWriter.Null, error, _ => null);

        Assert.Equal(2, code);
        Assert.StartsWith("usage:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCallMessage_IsTheKernelsProtobufCallRequest()
    {
        var request = new NeuronsEdge.NeuronCall("owner", "C", "k", "M", "{}"u8.ToArray());
        var bytes = request.Encode();

        var parsed = IntoChat.OS.Kernel.Platform.Proto.CallRequest.Parser.ParseFrom(bytes);
        Assert.Equal(("owner", "C", "k", "M", "{}"), (parsed.BrainId, parsed.Contract, parsed.Key, parsed.Method, parsed.Arguments.ToStringUtf8()));

        var reply = new IntoChat.OS.Kernel.Platform.Proto.CallReply { Result = Google.Protobuf.ByteString.CopyFromUtf8("[1]") };
        Assert.Equal("[1]"u8.ToArray(), NeuronsEdge.NeuronCall.DecodeReply(Google.Protobuf.MessageExtensions.ToByteArray(reply)));
    }
}
