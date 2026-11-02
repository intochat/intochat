namespace IntoChat.OS.Modules.Apps.Tests.Unit.FastPath;

public sealed class ReactionWriterFacts
{
    private static AppSnapshot CreateSnapshot(string name = "test", string title = "TestApp", Dictionary<string, string>? files = null) =>
        new(name, new AppManifest(title, "Description", [], [], []), "", files ?? [], [], [], "1.0.0", AppStatus.Live, [], []);

    private sealed class TestSandbox(Func<IReadOnlyDictionary<string, string>, ScriptCompilationCheck>? onCompile = null) : IScriptSandbox
    {
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(onCompile?.Invoke(files) ?? new ScriptCompilationCheck(true, []));

        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);

        public bool CanRun => true;
        public string AuthoringDescription => "test";
    }

    [Fact]
    public void FindAppClassName_WithDeclaredAppClass_ReturnsAppClassIdentifier()
    {
        var files = new Dictionary<string, string>
        {
            ["main.cs"] = """
                using DigitalBrain;

                [App]
                public sealed partial class TasksApp : App<TasksState>
                {
                }
                """
        };
        var app = CreateSnapshot(files: files);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal("TasksApp", className);
    }

    [Fact]
    public void FindAppClassName_WithoutAppClass_DerivesFromManifestTitle()
    {
        var app1 = CreateSnapshot(title: "Hello World");
        Assert.Equal("HelloWorldApp", ReactionWriter.FindAppClassName(app1));

        var app2 = CreateSnapshot(title: "Calculator App");
        Assert.Equal("CalculatorApp", ReactionWriter.FindAppClassName(app2));

        var app3 = CreateSnapshot(title: "notes_tracker");
        Assert.Equal("NotesTrackerApp", ReactionWriter.FindAppClassName(app3));
    }

    [Fact]
    public async Task Gate_WithValidPartialClassAndOnMethod_Passes()
    {
        var sandbox = new TestSandbox();
        var writer = new ReactionWriter(sandbox);
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\")] public async Task OnDone() { } }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal("reaction.cs", written.Path);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Gate_WithTopLevelStatements_RejectsWithDescriptiveMessage()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "await foreach (var s in brain.On(\"x\", ct)) { }\n"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("Top-level statements are not allowed; declare a partial class with [On(\"...\")] reaction methods.", error);
    }

    [Fact]
    public async Task Gate_WithoutPartialModifier_Rejects()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed class MyApp { [On(\"x\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction must declare a partial class extending the app class.", error);
    }

    [Fact]
    public async Task Gate_WithoutOnMethod_Rejects()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction partial class must declare at least one method decorated with [On(\"...\")].", error);
    }

    [Fact]
    public async Task Gate_WithSyntaxError_ReturnsDiagnostics()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { invalid syntax here }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith("reaction.cs:", StringComparison.Ordinal));
    }

    [Fact]
    public void Refusal_WithRefusalLine_ExtractsReason()
    {
        var reason = ReactionWriter.Refusal("REFUSE: no contract publishes BTC prices\nsome other line");
        Assert.Equal("no contract publishes BTC prices", reason);
    }

    [Fact]
    public void Refusal_WithoutRefusalLine_ReturnsNull()
    {
        var reason = ReactionWriter.Refusal("{\"path\": \"reaction.cs\", \"source\": \"...\"}");
        Assert.Null(reason);
    }

    [Fact]
    public async Task Gate_WhenSandboxFailsCompilation_ReturnsCompilationErrors()
    {
        var sandbox = new TestSandbox(files => new ScriptCompilationCheck(false, [new ScriptCompilationDiagnostic("reaction.cs", "CS0103", 1, "The name 'Foo' does not exist in the current context")]));
        var writer = new ReactionWriter(sandbox);
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"x\")] public void Handle() { Foo(); } }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("CS0103", error);
    }
}
