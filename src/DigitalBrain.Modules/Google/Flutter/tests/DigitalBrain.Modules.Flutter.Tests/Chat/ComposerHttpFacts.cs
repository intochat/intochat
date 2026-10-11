using System.Net;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Chat;

[Collection(FlutterHostCollection.Name)]
public sealed class ComposerHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task ComposerEdgeCapturesTheDraftAndKeepsAudioOutOfTheUiSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var name = ws + "/apps/test/composer";
        var composer = brain.Get<IComposer>(name);
        await composer.Configure("conversation", 1);
        using var edit = await person.Http.PostAsJsonAsync($"/brains/{ws}/apps/event",
            new { kind = "composer", name, action = "edit", context = "conversation", value = "HTTP draft" }, ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var id = Guid.NewGuid();
        using var submit = await person.Http.PostAsJsonAsync($"/brains/{ws}/apps/event",
            new { kind = "composer", name, action = "submit", context = "conversation", value = "HTTP draft", operationId = id }, ct);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        Assert.Equal(id, Assert.Single((await composer.Read()).Submissions).Id);
        var capture = Guid.NewGuid();
        await composer.BeginVoice(capture, "conversation");
        using var audio = await person.Http.PostAsJsonAsync($"/brains/{ws}/apps/event",
            new { kind = "composer", name, action = "voice-audio", operationId = capture, value = "AQID", mimeType = "audio/wav" }, ct);
        Assert.Equal(HttpStatusCode.OK, audio.StatusCode);
        var snapshot = await person.Http.GetStringAsync($"/brains/{ws}/apps/node?kind=composer&name={Uri.EscapeDataString(name)}", ct);
        Assert.Contains("transcribing", snapshot);
        Assert.DoesNotContain("AQID", snapshot);
        Assert.DoesNotContain("bytes", snapshot);
    }
}
