using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Kernel.Composition.Confirmations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

[Trait("host", "own")]
public sealed class DriveOperationFacts
{
    [Fact]
    public async Task Hosted_operation_asks_once_then_completes_through_the_real_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stub = await DriveStub.StartAsync(ct);
        await using var brain = await DriveBrain.Compose(TestBrain.Create().WithNeuronModule<DriveOperationProbeModule>(), stub)
            .ConfigureServices(services => services.Configure<ConfirmationOptions>(options => options.Brains["owner"] = ConfirmationPolicy.Always))
            .StartAsync(ct);
        var person = brain.Owner;
        using var caller = person.AsCaller();
        var key = ConnectionKeys.For(person.BrainId, DriveModule.Connector.Id, "primary");
        await brain.Plain().GetConnectionInbound(key).AcceptGrant(new("google-oauth", "drive-token", "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1), DriveScopes.Write, null, "{}"));
        var broker = brain.Services.GetRequiredService<CapabilityBroker>();
        var storage = Assert.Single(await broker.Storages(new(person.BrainId), ct)).Capability;
        var root = Assert.Single(await storage.Roots(ct));
        var probe = brain.Get<IDriveOperationProbe>(person.Key("drive-operation"));
        var id = await probe.Run(WritePlan.WriteFile(root.Id, "approved.txt", "approved once"u8.ToArray()));
        Assert.StartsWith("op-", id, StringComparison.Ordinal);
        var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(person.BrainId));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ConfirmationInbox snapshot;
        do
        {
            snapshot = await inbox.Read();
            if (snapshot.Pending.Length == 0) { await Task.Delay(20, timeout.Token); }
        } while (snapshot.Pending.Length == 0);
        var request = Assert.Single(snapshot.Pending);
        Assert.Contains("approved.txt", request.Plan.Title, StringComparison.Ordinal);
        await inbox.Resolve(request.PlanId, true);
        var approvedRevision = (await inbox.Read()).Revision;
        string? result;
        do
        {
            result = await probe.Result();
            Assert.Empty((await inbox.Read()).Pending);
            if (result is null) { await Task.Delay(20, timeout.Token); }
        } while (result is null);
        Assert.Equal("succeeded", result);
        Assert.Equal(approvedRevision, (await inbox.Read()).Revision);
        var file = Assert.Single((await storage.List(root.Id, new(), ct)).Items, item => item.Name == "approved.txt");
        Assert.Equal("approved once", await storage.ReadText(file.Id, ct));
    }
}

[DigitalBrain.Module("drive-operation-probe")]
public sealed class DriveOperationProbeModule : DigitalBrain.Module
{
    public static ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

[NeuronAlias("drive-operation"), DefaultNeuronType(DriveOperationProbe.Type)]
public interface IDriveOperationProbe
{
    Task<string> Run(WritePlan plan);
    Task<string?> Result();
}

[DigitalBrain.Neuron(Type)]
public sealed class DriveOperationProbe : DigitalBrain.Neuron, IDriveOperationProbe
{
    public const string Type = "probe.drive-operation";
    private string? _result;

    public async Task<string> Run(WritePlan plan)
    {
        var operation = await Context.Operations.Start(plan, CancellationToken.None);
        await Context.Synapses.Ensure<OperationDone>(new(Id.Brain, "runtime.operation", operation.Id.Value));
        return operation.Id.Value;
    }

    public Task<string?> Result() => Task.FromResult(_result);

    [On]
    public Task Completed(OperationDone done)
    {
        _result = done.Result switch
        {
            OperationResult.Succeeded => "succeeded",
            OperationResult.Failed failed => "failed: " + failed.Code,
            _ => done.Result.GetType().Name,
        };
        return Task.CompletedTask;
    }
}
