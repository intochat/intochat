# DigitalBrain.Testing.Aspire

The Aspire target for `TestBrain`: boot your AppHost and drive the brain over HTTP and an Orleans client.

```csharp
await using var brain = await TestBrain.Create()
    .WithModule<TimeModule>()                                            // composition (in-process target only)
    .WithIntegration(AIModule.Integration, "Endpoint", model.Endpoint)   // typed, any target
    .StartAsync(ct);

var alice = await brain.PersonAsync("alice", ct);                        // registered account + owned brain
using (alice.AsCaller()) { await brain.Get<IApp>(alice.Key("apps/hello")).Read(); }
```

## Targets

- **Aspire** (this package): the AppHost owns composition, so `WithModule` throws; `WithOptions` and `WithIntegration` configure the AppHost.
- **InProcess**: `DigitalBrain.Testing`, same `ITestBrain`.
