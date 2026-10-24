using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.Flutter.Chat;

namespace IntoChat.OS.Kernel.Tests.Unit;

// Only posted runs drop the user bubble; an attachment-only user turn (empty text) keeps it.
public sealed class AgentChatSourceFacts
{
    [Fact]
    public void AnAttachmentOnlyUserTurnStillShowsItsUserBubble()
    {
        var run = new AgentRun("r1", AgentRunStatus.Completed, "", DateTimeOffset.UtcNow, Reply: "A cat.");
        Assert.Equal([ChatRole.User, ChatRole.Assistant], AgentChatSource.Entries(run).Select(entry => entry.Role));
    }

    [Fact]
    public void APostedRunShowsOneAssistantBubble()
    {
        var run = new AgentRun("post/greeting", AgentRunStatus.Completed, "", DateTimeOffset.UtcNow, Reply: "Hi") { Posted = true };
        Assert.Equal([ChatRole.Assistant], AgentChatSource.Entries(run).Select(entry => entry.Role));
    }

    [Fact]
    public void CompletedTurnsCarryTheModelLabel()
    {
        var model = new ResolvedModel(AiProvider.OpenAI, "gpt-5-mini", null, null, "1", default, null, null);
        var run = new AgentRun("r1", AgentRunStatus.Completed, "hi", DateTimeOffset.UtcNow, Reply: "Hello", Model: model);
        Assert.Equal("gpt-5-mini", AgentChatSource.Entries(run).Last().Model);
        Assert.Null(AgentChatSource.Entries(run with { Model = null }).Last().Model);
    }

    [Fact]
    public void OpeningAnAppAddsAnAppCard()
    {
        var call = new AgentToolCall("c1", "neurons_call",
            """{"contract":"IntoChat.OS.Apps.IApp","key":"brain/apps/notes","method":"Request","arguments":"[\"open\",\"page-7\"]"}""",
            Result: "{}");
        var run = new AgentRun("r1", AgentRunStatus.Completed, "open notes", DateTimeOffset.UtcNow, Reply: "Opened.") { ToolCalls = [call] };
        var card = AgentChatSource.Entries(run).Last().App;
        Assert.Equal(new ChatAppCard("notes", "notes", "", true, "page-7"), card);

        var show = call with { Arguments = """{"contract":"IntoChat.OS.Flutter.Workspace.IWorkspace","key":"brain/workspace","method":"Show","arguments":"[\"notes\",\"op\",3]"}""" };
        Assert.Equal("notes", AgentChatSource.Entries(run with { ToolCalls = [show] }).Last().App?.App);

        // A failed, pending or other request adds no card.
        Assert.Null(AgentChatSource.Entries(run with { ToolCalls = [call with { Error = "no" }] }).Last().App);
        Assert.Null(AgentChatSource.Entries(run with { ToolCalls = [call with { Result = null }] }).Last().App);
        Assert.Null(AgentChatSource.Entries(run with { ToolCalls = [call with { Arguments = call.Arguments.Replace("open", "list") }] }).Last().App);
        Assert.Null(AgentChatSource.Entries(run with { ToolCalls = [call with { Arguments = "not json" }] }).Last().App);
    }

    [Fact]
    public void ALinkPostIsAnAppCardAndItsManifestNamesIt()
    {
        var id = ChatAppCard.LinkPostId(Guid.NewGuid(), "notes", "x");
        var run = new AgentRun("post/" + id, AgentRunStatus.Completed, "", DateTimeOffset.UtcNow, Reply: "Here it is") { Posted = true };
        var card = Assert.Single(AgentChatSource.Entries(run)).App!;
        Assert.Equal(("notes", "x"), (card.App, card.Input));
        Assert.Equal(card with { Title = "Notes", Icon = "note", Installed = true },
            AgentChatSource.Named(card, new IntoChat.OS.Apps.AppManifest("Notes", "", [], [], [], "note")));
        Assert.Equal(card with { Title = "notes", Installed = false }, AgentChatSource.Named(card, null));
        Assert.Equal(card with { Title = "notes", Installed = false },
            AgentChatSource.Named(card, new IntoChat.OS.Apps.AppManifest("", "", [], [], [])));
    }
}
