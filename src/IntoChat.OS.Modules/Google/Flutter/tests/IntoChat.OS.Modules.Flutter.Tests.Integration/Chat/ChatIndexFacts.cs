using IntoChat.OS.Modules.Flutter.Chat;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

public sealed class ChatIndexFacts(FlutterHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IChatIndex Index() => host.Brain.Get<IChatIndex>("index-" + Guid.NewGuid().ToString("N") + "/apps/ino-data/chats");

    private static string NewId() => "c-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task CreateIsIdempotentAndListsNewestFirst()
    {
        var index = Index();
        var first = await index.Create("main", "First");
        var second = await index.Create(NewId(), "Second");
        var again = await index.Create("main", "Ignored");

        Assert.Equal(first, again);
        Assert.Equal("First", (await index.Get("main"))!.Title);
        var page = await index.List(archived: false);
        Assert.Equal(2, page.Total);
        Assert.Equal([second.Id, first.Id], page.Entries.Select(entry => entry.Id));
        Assert.Null(await index.Get(NewId()));
        await Assert.ThrowsAsync<ArgumentException>(() => index.Create("c-nothex", "Bad"));
        await Assert.ThrowsAsync<ArgumentException>(() => index.Create("../main", "Bad"));
    }

    [Fact]
    public async Task ListPagesAndCapsAt500()
    {
        var index = Index();
        for (var at = 0; at < 520; at++) { await index.Create(NewId(), "Chat " + at); }

        var all = await index.List(archived: false);
        var tail = await index.List(archived: false, skip: 400, take: 100);
        var past = await index.List(archived: false, skip: 500, take: 100);

        Assert.Equal(500, all.Total);
        Assert.Equal(100, all.Entries.Length);
        Assert.Equal(100, tail.Entries.Length);
        Assert.Empty(past.Entries);
        Assert.Equal("Chat 519", all.Entries[0].Title);
        Assert.Equal("Chat 20", tail.Entries[^1].Title);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => index.List(archived: false, take: 101));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => index.List(archived: false, take: 0));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => index.List(archived: false, skip: -1));
    }

    [Fact]
    public async Task ArchivedEntriesListOnlyWhenAskedFor()
    {
        var index = Index();
        var kept = NewId();
        var archived = NewId();
        await index.Create(kept, "Kept");
        await index.Create(archived, "Old");
        await index.Archive(archived);

        Assert.Equal([kept], (await index.List(archived: false)).Entries.Select(entry => entry.Id));
        Assert.Equal([archived], (await index.List(archived: true)).Entries.Select(entry => entry.Id));
        await index.Unarchive(archived);
        Assert.Empty((await index.List(archived: true)).Entries);
        Assert.Equal(2, (await index.List(archived: false)).Total);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => index.Archive(NewId()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => index.Unarchive(NewId()));
    }

    [Fact]
    public async Task RenameRejectsEmptyMultiLineAndOverlongTitles()
    {
        var index = Index();
        await index.Create("main", "Start");

        await index.Rename("main", "  Trip plans  ");
        Assert.Equal("Trip plans", (await index.Get("main"))!.Title);
        await index.Rename("main", new string('x', 120));
        foreach (var bad in new[] { "", "   ", "one\ntwo", "one\rtwo", "bell\u0007", "tab\tbed", "rtl‮override", "zero​width", "⁦iso", "bom﻿", "​‏", new string('x', 121) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => index.Rename("main", bad));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => index.Create(NewId(), "a\nb"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => index.Rename(NewId(), "Fine"));
        Assert.Equal(new string('x', 120), (await index.Get("main"))!.Title);
    }

    [Fact]
    public async Task TouchSetsTheTitleOnceFromTheFirstMessage()
    {
        var index = Index();
        var created = NewId();
        await index.Create(created, "New chat");
        var before = (await index.Get(created))!.UpdatedAt;

        await index.Touch(created, "  plan a\n trip to   Lisbon " + new string('z', 80));
        var titled = (await index.Get(created))!;
        await index.Touch(created, "something else");

        Assert.Equal(52, titled.Title.Length);
        Assert.StartsWith("plan a trip to Lisbon zz", titled.Title, StringComparison.Ordinal);
        Assert.True(titled.UpdatedAt >= before);
        Assert.Equal(titled.Title, (await index.Get(created))!.Title);

        var untitled = NewId();
        await index.Create(untitled, "Untitled");
        await index.Touch(untitled, null);
        Assert.Equal("Untitled", (await index.Get(untitled))!.Title);
        await index.Touch(untitled, "bell\u0007 rings");
        Assert.Equal("bell rings", (await index.Get(untitled))!.Title);
        var hidden = NewId();
        await index.Touch(hidden, "‮​");
        Assert.Equal("New chat", (await index.Get(hidden))!.Title);
        await index.Touch(hidden, "‮hi​ there");
        Assert.Equal("hi there", (await index.Get(hidden))!.Title);

        var renamed = NewId();
        await index.Create(renamed, "New chat");
        await index.Rename(renamed, "Mine");
        await index.Touch(renamed, "first message");
        Assert.Equal("Mine", (await index.Get(renamed))!.Title);

        var touched = NewId();
        await index.Touch(touched, "   ");
        Assert.Equal("New chat", (await index.Get(touched))!.Title);
        await Assert.ThrowsAsync<ArgumentException>(() => index.Touch("c-1", "x"));
    }

    [Fact]
    public async Task SetModelPersistsAndNullClears()
    {
        var index = Index();
        await index.Create("main", "Chat");

        await index.SetModel("main", "openai/gpt-5-mini");
        Assert.Equal("openai/gpt-5-mini", (await index.Get("main"))!.ModelChoice);
        await index.SetModel("main", null);
        Assert.Null((await index.Get("main"))!.ModelChoice);
        await Assert.ThrowsAsync<ArgumentException>(() => index.SetModel("main", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => index.SetModel("main", new string('m', 513)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => index.SetModel(NewId(), null));
    }

    [Fact]
    public async Task EntriesSurviveReactivation()
    {
        var index = Index();
        var id = NewId();
        await index.Create(id, "Kept");
        await index.SetModel(id, "auto");
        await index.Archive(id);
        var before = await index.Get(id);

        await host.Brain.DeactivateAsync(index, Ct);

        Assert.Equal(before, await index.Get(id));
        Assert.Equal(1, (await index.List(archived: true)).Total);
    }
}
