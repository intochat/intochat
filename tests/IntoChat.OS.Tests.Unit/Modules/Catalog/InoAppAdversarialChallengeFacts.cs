using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoAppAdversarialChallengeFacts
{
    [Fact]
    public async Task InoAppHandlesEmptyAndWhitespacePromptsWithoutCrashing()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend(null, null));
        Assert.Equal("New chat", ino.App.State.Title);
        Assert.Null(ino.App.State.Pending);
        Assert.Null(ino.App.State.ActiveRunId);

        await ino.Request("send", new InoSend("", ""));
        await ino.Request("send", new InoSend("   \r\n\t   ", "   "));
        Assert.False(string.IsNullOrWhiteSpace(ino.App.State.Status));
    }

    [Fact]
    public async Task InoAppHandlesExtremelyLongPromptSafely()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var longPrompt = new string('A', 50000);

        await ino.Request("send", new InoSend(longPrompt));

        Assert.True(ino.App.State.Title.Length <= 52);
    }

    [Fact]
    public async Task InoAppHandlesMalformedSlashCommandsSafely()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("/"));
        await ino.Request("send", new InoSend("/      "));
        await ino.Request("send", new InoSend("/unrecognized_command_xyz foo bar"));
        await ino.Request("send", new InoSend("/model "));
        await ino.Request("send", new InoSend("/rename "));
        await ino.Request("send", new InoSend("/scenario non_existent_scenario"));

        Assert.NotNull(ino.App.State.Status);
    }

    [Fact]
    public async Task InoAppHandlesAdversarialInputsWithoutExceptions()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        string[] adversarialInputs =
        [
            "'; DROP TABLE opportunities; --",
            "../../../../etc/passwd",
            "{0} {1} {2} %s %x %n",
            "\0\a\b\f\v\u200B\u200C\uFEFF",
            "{\"intent\": \"photocraft\", \"override\": true}",
            "routed:fake.hack:exploit",
        ];

        foreach (var input in adversarialInputs)
        {
            await ino.Request("send", new InoSend(input));
        }

        Assert.False(string.IsNullOrWhiteSpace(ino.App.State.Status));
    }

    [Fact]
    public async Task ArtCraftEngineRunner_RejectsUnrecognizedConversationalPrompts_WithoutFabricatingRemoveBackground()
    {
        var photoResult = await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
            "hey, what can you do?",
            explicitParamsJson: null,
            currentDocument: null,
            executionMode: "native-linux",
            studio: null,
            mcpResolver: null);

        Assert.False(photoResult.IsValidCommand);
        Assert.Equal("clarification.required", photoResult.CommandId);
        Assert.Empty(photoResult.CliInvocation);
        Assert.Empty(photoResult.OutputDocument);
        Assert.DoesNotContain("layer.removeBackground", photoResult.Summary, StringComparison.OrdinalIgnoreCase);

        var filmResult = await ArtCraftEngineRunner.ExecuteFilmCraftAsync(
            "hey, what can you do?",
            explicitParamsJson: null,
            currentSequence: null,
            executionMode: "native-linux",
            studio: null,
            mcpResolver: null);

        Assert.False(filmResult.IsValidCommand);
        Assert.Equal("clarification.required", filmResult.CommandId);
        Assert.Empty(filmResult.CliInvocation);
        Assert.Empty(filmResult.OutputDocument);
    }
}
