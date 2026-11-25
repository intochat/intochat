using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Tests.Unit.Sandbox;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class CodeOrchestrationAstSanitizerFacts
{
    private static readonly ICodeOrchestrationAstSanitizer Sanitizer = new CodeOrchestrationAstSanitizer();

    [Fact]
    public void AcceptsCleanTypedProxyOrchestrationScript()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    await os.Notifications.NotifyAsync("Build summary", "All green", NotificationPriority.Normal, ct);
                    return new OrchestrationResult(
                        Status: "succeeded",
                        Summary: "Sent build summary notification.",
                        Artifacts: [new OrchestrationArtifact("report.md", "markdown", "# Summary")],
                        Metrics: new Dictionary<string, double> { ["notified"] = 1 });
                }
            }
            """;

        var result = Sanitizer.Validate(source);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Empty(result.Violations);
    }

    [Theory]
    [InlineData("using System.Reflection;\npublic static class OrchestrationScript { }", "AST002")]
    [InlineData("using System.Net.Sockets;\npublic static class OrchestrationScript { }", "AST002")]
    [InlineData("using System.Diagnostics.Process;\npublic static class OrchestrationScript { }", "AST002")]
    [InlineData("using System.Runtime.InteropServices;\npublic static class OrchestrationScript { }", "AST002")]
    [InlineData("using System.Runtime.Loader;\npublic static class OrchestrationScript { }", "AST002")]
    [InlineData("using Microsoft.Orleans;\npublic static class OrchestrationScript { }", "AST002")]
    [InlineData("using Orleans;\npublic static class OrchestrationScript { }", "AST002")]
    public void RejectsForbiddenNamespaces(string source, string expectedRuleId)
    {
        var result = Sanitizer.Validate(source);
        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.RuleId == expectedRuleId);
    }

    [Theory]
    [InlineData("public static class OrchestrationScript { public static void X() { var s = System.IO.File.ReadAllText(\"/etc/passwd\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var s = File.ReadAllText(\"secret.txt\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var d = Directory.GetFiles(\".\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { using var fs = new FileStream(\"a.txt\", 0); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { using var sr = new StreamReader(\"a.txt\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { System.Diagnostics.Process.Start(\"cmd.exe\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var p = new Process(); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var c = new global::System.Net.Sockets.TcpClient(); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var h = new HttpClient(); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var e = Environment.GetEnvironmentVariable(\"SECRET\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var t = System.Type.GetType(\"System.IO.File\"); } }")]
    [InlineData("public static class OrchestrationScript { public static void X() { var a = Assembly.Load(\"foo\"); } }")]
    public void RejectsUnauthorizedIoProcessSocketsAndReflection(string source)
    {
        var result = Sanitizer.Validate(source);
        Assert.False(result.IsValid, $"Expected AST violation for: {source}");
        Assert.NotEmpty(result.Violations);
    }

    [Theory]
    [InlineData("public static class OrchestrationScript { public static unsafe void X() { int x = 1; int* p = &x; } }", "AST004")]
    [InlineData("public static class OrchestrationScript { [DllImport(\"kernel32.dll\")] public static extern int Foo(); }", "AST004")]
    [InlineData("public static class OrchestrationScript { public static void X(ISandboxBrain os) { _ = os.Get<IConfirmationRequests>(\"k\"); } }", "AST005")]
    [InlineData("public static class OrchestrationScript { public static void X(ISandboxBrain os) { _ = os.Get<IClientGrants>(\"k\"); } }", "AST005")]
    public void RejectsUnsafePInvokeAndAuthorityContracts(string source, string expectedRuleId)
    {
        var result = Sanitizer.Validate(source);
        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.RuleId == expectedRuleId);
    }

    [Fact]
    public void InspectsContractUsageAndDetectsPersonOnlyCalls()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;
            using IntoChat.OS.Tests.Unit.Sandbox;

            public static class OrchestrationScript
            {
                public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    var widgets = os.Get<IDemoWidgetContract>("default");
                    await widgets.DeleteWidgetAsync("w-42", ct);
                    await os.Notifications.NotifyAsync("Deleted", "w-42", NotificationPriority.High, ct);
                    return new OrchestrationResult("succeeded", "deleted");
                }
            }
            """;

        var catalog = NeuronContractDiscovery.Discover([typeof(IDemoWidgetContract).Assembly])
            .Select(static c => new AstContractDescriptor(
                c.Alias,
                c.FullName,
                c.ShortName,
                c.AllAliases,
                c.Methods.Where(static m => m.RequiresPersonApproval).Select(static m => m.Name).ToArray()))
            .ToArray();
        var usage = Sanitizer.InspectContractUsage(source, catalog);

        Assert.Contains("demo.widgets", usage.ReferencedContracts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("notifications", usage.ReferencedContracts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("IDemoWidgetContract.DeleteWidgetAsync", usage.PersonOnlyCalls, StringComparer.OrdinalIgnoreCase);
    }
}
