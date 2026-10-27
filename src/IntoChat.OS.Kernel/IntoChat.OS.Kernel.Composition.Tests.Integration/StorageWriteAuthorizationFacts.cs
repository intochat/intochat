using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration;

[Trait("host", "own")]
public sealed class StorageWriteAuthorizationFacts
{
    [Fact]
    public async Task OnlyAnActiveApprovedScopeCanAuthorizeAnExactPrimitiveForTheActualCallerAndConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var authorizer = brain.Services.GetRequiredService<IStorageWriteAuthorizer>();
        var provider = new DigitalBrain.NeuronId(new("owner"), "windows", "integrations/windows/disk");
        var connection = "owner/" + provider.Key;
        var plan = WritePlan.WriteFile(new("root"), "report", "data"u8.ToArray(), ConflictPolicy.Overwrite);
        using var caller = brain.Owner.AsApp("owner/apps/files");
        Assert.False(await authorizer.Invoke(connection, plan, () => authorizer.Consume(provider, plan, ct), ct));
        using (authorizer.BeginApproved(new("owner"), "owner/files/main"))
        {
            Assert.True(await authorizer.Invoke(connection, plan, () => authorizer.Consume(provider, plan, ct), ct));
            await authorizer.Invoke(connection, plan, async () =>
            {
                Assert.True(await authorizer.Consume(provider, plan, ct));
                Assert.False(await authorizer.Consume(provider, plan, ct));
                return true;
            }, ct);
            foreach (var changed in new[]
            {
                plan with { Kind = WriteKind.CreateFolder }, plan with { Source = new("other") },
                plan with { TargetFolder = new("other") }, plan with { Name = "different" },
                plan with { Conflict = ConflictPolicy.Fail }, plan with { Permanent = true },
                plan with { DryRun = true }, plan with { Inline = "diff"u8.ToArray() }, plan with { Inline = null },
            })
            { Assert.False(await authorizer.Invoke(connection, plan, () => authorizer.Consume(provider, changed, ct), ct)); }
            Assert.False(await authorizer.Invoke(connection, plan, () => authorizer.Consume(new(provider.Brain, provider.Type, "integrations/windows/another"), plan, ct), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authorizer.Invoke("another/" + provider.Key, plan, () => Task.FromResult(true), ct));
            await authorizer.Invoke(connection, plan, async () =>
            {
                using var changed = brain.Owner.AsApp("owner/apps/another");
                Assert.False(await authorizer.Consume(provider, plan, ct));
                return true;
            }, ct);
        }
        Assert.False(await authorizer.Invoke(connection, plan, () => authorizer.Consume(provider, plan, ct), ct));
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> escaped;
        using (authorizer.BeginApproved(new("owner"), "owner/files/main"))
        {
            escaped = Task.Run(async () =>
            {
                await resume.Task;
                return await authorizer.Invoke(connection, plan, () => authorizer.Consume(provider, plan, ct), ct);
            }, ct);
        }
        resume.SetResult();
        Assert.False(await escaped);
        var types = brain.Services.GetRequiredService<CompiledTypes>();
        Assert.DoesNotContain(types.Entries, entry => entry.Name.Contains("StorageWriteAuthorizer", StringComparison.Ordinal) || entry.Name.Contains("StorageWriteGrants", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => types.Contract("DigitalBrain.Runtime.Orleans.Operations.IStorageWriteGrants"));
    }
}
