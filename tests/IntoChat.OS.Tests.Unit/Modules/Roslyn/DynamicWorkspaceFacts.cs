using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using IntoChat.OS.Modules.Microsoft.Roslyn.Dynamic;

namespace IntoChat.OS.Tests.Unit.Modules.Roslyn;

public sealed class DynamicWorkspaceFacts
{
    [Fact]
    public async Task CodebaseTreeLoadsLazilyExpandsRoslynSymbolsAndQueriesTopic()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"intochat-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "Auth"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "Billing"));

        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "Auth", "AuthMiddleware.cs"),
            """
            namespace Demo.Auth;
            public sealed class AuthMiddleware
            {
                public bool ValidateToken(string token) => !string.IsNullOrEmpty(token);
            }
            """,
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(tempRoot, "Billing", "InvoiceService.cs"),
            """
            namespace Demo.Billing;
            public sealed class InvoiceService
            {
                public decimal Total() => 42m;
            }
            """,
            TestContext.Current.CancellationToken);

        try
        {
            using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
                IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
            await using var os = TestOS.Local();
            os.Register(typeof(RoslynModule).Assembly);

            var tree = os.Get<ICodebaseTree>(new NeuronId(new BrainId("test"), "roslyn.codebase-tree", "demo"));

            // 1. OpenRoot loads only top-level folders lazily (not deep files).
            var initial = await tree.OpenRoot(tempRoot);
            Assert.Contains(initial.Nodes, n => n.Id == "dir:Auth" && !n.ChildrenLoaded);
            Assert.DoesNotContain(initial.Nodes, n => n.Id == "file:Auth/AuthMiddleware.cs");

            // 2. Expand folder -> file -> Roslyn C# symbols.
            var afterFolder = await tree.ExpandNode("dir:Auth");
            Assert.Contains(afterFolder.Nodes, n => n.Id == "file:Auth/AuthMiddleware.cs" && n.Kind == "csharp");

            var afterFile = await tree.ExpandNode("file:Auth/AuthMiddleware.cs");
            Assert.Contains(afterFile.Nodes, n => n.Label == "class AuthMiddleware");
            Assert.Contains(afterFile.Nodes, n => n.Label == "ValidateToken()");

            // 3. QueryTopic("Auth") focuses the tree on auth files & symbols and auto-expands ancestors.
            var focused = await tree.QueryTopic("Auth");
            Assert.Equal("Auth", focused.ActiveQuery);
            Assert.Contains(focused.Nodes, n => n.Id == "file:Auth/AuthMiddleware.cs");
            Assert.Contains(focused.ExpandedIds, id => id == "dir:Auth");
            Assert.DoesNotContain(focused.Nodes, n => n.Id == "file:Billing/InvoiceService.cs");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DynamicNeuronHostCompilesExecutesHotSwapsAndUnloadsWithoutRestart()
    {
        const string sourceV1 = """
            namespace LiveGen;
            public static class GreetingNeuron
            {
                public static string Run(string who) => $"v1:hello:{who}";
            }
            """;

        const string sourceV2 = """
            namespace LiveGen;
            public static class GreetingNeuron
            {
                public static string Run(string who) => $"v2:welcome:{who}";
            }
            """;

        var v1 = DynamicNeuronHost.CompileAndLoad("greeting", sourceV1);
        Assert.True(v1.Success, string.Join('\n', v1.Diagnostics));
        Assert.NotNull(v1.Handle);
        Assert.Equal("v1:hello:ino", v1.Handle!.Invoke("LiveGen.GreetingNeuron", "Run", "ino"));

        // Unload v1 cleanly (zero restart).
        v1.Handle.Dispose();
        Assert.False(v1.Handle.IsLoaded);
        Assert.Throws<ObjectDisposedException>(() => v1.Handle.Invoke("LiveGen.GreetingNeuron", "Run", "ino"));

        // Hot-swap v2 in-process.
        var v2 = DynamicNeuronHost.CompileAndLoad("greeting", sourceV2);
        Assert.True(v2.Success, string.Join('\n', v2.Diagnostics));
        using (v2.Handle)
        {
            Assert.Equal("v2:welcome:ino", v2.Handle!.Invoke("LiveGen.GreetingNeuron", "Run", "ino"));
        }

        // Invalid C# fails gracefully with diagnostics.
        var broken = DynamicNeuronHost.CompileAndLoad("broken", "public class { invalid }");
        Assert.False(broken.Success);
        Assert.NotEmpty(broken.Diagnostics);
    }
}
