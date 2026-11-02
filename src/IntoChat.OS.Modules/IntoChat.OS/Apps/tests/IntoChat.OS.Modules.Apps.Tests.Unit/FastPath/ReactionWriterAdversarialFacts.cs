namespace IntoChat.OS.Modules.Apps.Tests.Unit.FastPath;

public sealed class ReactionWriterAdversarialFacts
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
    public async Task Gate_TopLevelStatementsBeforePartialClass_Rejects()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "var x = 1;\npublic sealed partial class MyApp { [On(\"tasks/done\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("Top-level statements are not allowed; declare a partial class with [On(\"...\")] reaction methods.", error);
    }

    [Fact]
    public async Task Gate_TopLevelStatementsAfterPartialClass_RejectsWithSyntaxError()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\")] public void Handle() {} }\nSystem.Console.WriteLine(123);"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith("reaction.cs:", StringComparison.Ordinal) && (e.Contains("CS8803") || e.Contains("CS8802")));
    }

    [Fact]
    public async Task Gate_NonPartialClassWithOnMethod_Rejects()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public class MyApp { [On(\"tasks/done\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction must declare a partial class extending the app class.", error);
    }

    [Fact]
    public async Task Gate_PartialClassWithNonPartialHelperHavingOn_Rejects()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public class Helper { [On(\"tasks/done\")] public void Handle() {} }\npublic sealed partial class MyApp { public void DoNothing() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction partial class must declare at least one method decorated with [On(\"...\")].", error);
    }

    [Fact]
    public async Task Gate_SyntaxErrorInsideOnMethod_ReturnsSyntaxDiagnostics()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\")] public void Handle() { int x = ; } }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith("reaction.cs:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gate_SyntaxErrorInAttribute_ReturnsSyntaxDiagnostics()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\" public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith("reaction.cs:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not a json string")]
    [InlineData("{ \"path\": \"reaction.cs\" ")]
    [InlineData("[1, 2, 3]")]
    public async Task Gate_MalformedJson_ReturnsDescriptiveError(string invalidJson)
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();

        var (written, errors) = await writer.Gate(invalidJson, app, CancellationToken.None);

        Assert.Null(written);
        var error = Assert.Single(errors);
        Assert.StartsWith("The reply was not the JSON object {\"path\": \"name.cs\", \"source\": \"...\"}:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gate_EmptyJsonObject_ReturnsMissingPathError()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();

        var (written, errors) = await writer.Gate("{}", app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Equal("The reply names no path.", error);
    }

    [Fact]
    public async Task Gate_JsonWrappedInMarkdownFences_ParsesSuccessfully()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            ```json
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\")] public void Handle() {} }"
            }
            ```
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal("reaction.cs", written.Path);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\r\\n\\t  ")]
    public async Task Gate_WhitespaceOrEmptySource_Rejects(string emptySource)
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = $$"""
            {
                "path": "reaction.cs",
                "source": "{{emptySource}}"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Equal("The reply's source is empty.", error);
    }

    [Theory]
    [InlineData("app.cs", "app.cs is the app's declaration, never a reaction: name a new .cs file.")]
    [InlineData("manifest.json", "A reaction is a .cs file.")]
    [InlineData("../escape.cs", "A reaction path is a file name inside the app.")]
    [InlineData("/root.cs", "A reaction path is a file name inside the app.")]
    [InlineData("sub\\\\file.cs", "A reaction path is a file name inside the app.")]
    public async Task Gate_InvalidPaths_RejectsWithAppropriateMessage(string path, string expectedError)
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = $$"""
            {
                "path": "{{path}}",
                "source": "public sealed partial class MyApp { [On(\"x\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public async Task Gate_SandboxMissing_ReturnsSandboxMissingError()
    {
        var writer = new ReactionWriter(sandbox: null);
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Equal(AppAuthoringPolicy.SandboxMissing, error);
    }

    [Fact]
    public async Task Gate_SandboxThrowsInvalidOperationException_ReturnsErrorMessage()
    {
        var sandbox = new TestSandbox(_ => throw new InvalidOperationException("Compilation service unavailable"));
        var writer = new ReactionWriter(sandbox);
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"tasks/done\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Equal("Compilation service unavailable", error);
    }

    [Fact]
    public async Task Gate_MultipleProgramFiles_CompilesWithAppFiles()
    {
        IReadOnlyDictionary<string, string>? compiledFiles = null;
        var sandbox = new TestSandbox(files =>
        {
            compiledFiles = files;
            return new ScriptCompilationCheck(true, []);
        });
        var writer = new ReactionWriter(sandbox);
        var existingFiles = new Dictionary<string, string>
        {
            ["main.cs"] = "public sealed partial class MyApp : App<State> {}",
            ["helper.cs"] = "public static class Helper { public static int Val => 1; }",
            ["app.cs"] = "// Manifest file - should be excluded from shared program files",
            ["readme.txt"] = "Not a program file"
        };
        var app = CreateSnapshot(files: existingFiles);
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"x\")] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        Assert.Empty(errors);
        Assert.NotNull(compiledFiles);
        Assert.True(compiledFiles.ContainsKey("main.cs"));
        Assert.True(compiledFiles.ContainsKey("helper.cs"));
        Assert.True(compiledFiles.ContainsKey("reaction.cs"));
        Assert.False(compiledFiles.ContainsKey("app.cs"));
        Assert.False(compiledFiles.ContainsKey("readme.txt"));
    }

    [Fact]
    public void FindAppClassName_MultipleAppClasses_ReturnsFirstEncountered()
    {
        var files = new Dictionary<string, string>
        {
            ["first.cs"] = """
                [App]
                public sealed partial class FirstApp : App<FirstState> {}
                """,
            ["second.cs"] = """
                [App]
                public sealed partial class SecondApp : App<SecondState> {}
                """
        };
        var app = CreateSnapshot(files: files);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal("FirstApp", className);
    }

    [Theory]
    [InlineData("DigitalBrain.App", "QualifiedApp")]
    [InlineData("AppAttribute", "AttrApp")]
    [InlineData("App", "StandardApp")]
    public void FindAppClassName_AttributeVariations_ResolvesCorrectly(string attrName, string expectedClass)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cs"] = $$"""
                [{{attrName}}]
                public sealed partial class {{expectedClass}} : App<State> {}
                """
        };
        var app = CreateSnapshot(files: files);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal(expectedClass, className);
    }

    [Theory]
    [InlineData("3D Viewer", "3DViewerApp")]
    [InlineData("my-awesome_app.v1", "MyAwesomeAppV1App")]
    [InlineData("Calculator", "CalculatorApp")]
    [InlineData("CalculatorApp", "CalculatorApp")]
    [InlineData("Notes App", "NotesApp")]
    [InlineData("", "App")]
    [InlineData("   ", "App")]
    [InlineData(null, "App")]
    public void FindAppClassName_UnusualManifestTitles_DerivesExpectedPascalName(string? title, string expected)
    {
        var app = new AppSnapshot("test", new AppManifest(title!, "Desc", [], [], []), "", [], [], [], "1.0.0", AppStatus.Live, [], []);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal(expected, className);
    }

    [Fact]
    public async Task Gate_PropertyWithOnAttribute_RejectsBecauseItIsNotAMethod()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [On(\"x\")] public string Name { get; set; } }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction partial class must declare at least one method decorated with [On(\"...\")].", error);
    }

    [Fact]
    public async Task Gate_LocalFunctionWithOnAttribute_RejectsBecauseItIsNotAClassMethod()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { public void Setup() { [On(\"x\")] void Local() {} } }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction partial class must declare at least one method decorated with [On(\"...\")].", error);
    }

    [Fact]
    public async Task Gate_MethodWithOtherAttribute_Rejects()
    {
        var writer = new ReactionWriter(new TestSandbox());
        var app = CreateSnapshot();
        var reply = """
            {
                "path": "reaction.cs",
                "source": "public sealed partial class MyApp { [System.Obsolete] public void Handle() {} }"
            }
            """;

        var (written, errors) = await writer.Gate(reply, app, CancellationToken.None);

        Assert.NotNull(written);
        var error = Assert.Single(errors);
        Assert.Contains("The reaction partial class must declare at least one method decorated with [On(\"...\")].", error);
    }

    [Fact]
    public void FindAppClassName_IgnoresEmptyOrNonCsFiles()
    {
        var files = new Dictionary<string, string>
        {
            ["empty.cs"] = "   ",
            ["readme.md"] = "[App] class InReadme {}",
            ["data.json"] = "{}",
            ["valid.cs"] = """
                [App]
                public sealed partial class ResolvedApp : App<State> {}
                """
        };
        var app = CreateSnapshot(files: files);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal("ResolvedApp", className);
    }

    [Fact]
    public void FindAppClassName_UnparseableSyntaxInAppFile_FallsBackToManifestTitleWithoutThrowing()
    {
        var files = new Dictionary<string, string>
        {
            ["broken.cs"] = "this is total syntax garbage !!! %%% ;;; ;;; ;;; {"
        };
        var app = CreateSnapshot(title: "Fallback Title", files: files);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal("FallbackTitleApp", className);
    }

    [Fact]
    public void FindAppClassName_MemberSyntaxErrorsInAppClass_FindsClassIdentifier()
    {
        var files = new Dictionary<string, string>
        {
            ["with_member_error.cs"] = """
                [App]
                public sealed partial class ResilientApp : App<State>
                {
                    int x = ; // syntax error inside member
                }
                """
        };
        var app = CreateSnapshot(title: "Fallback Title", files: files);
        var className = ReactionWriter.FindAppClassName(app);
        Assert.Equal("ResilientApp", className);
    }
}

