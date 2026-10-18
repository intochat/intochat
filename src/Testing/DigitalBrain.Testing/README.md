# DigitalBrain.Testing

The test harness for DigitalBrain modules: a `TestBrain` builder that runs the product recipe in process, a shared host
per test assembly, signal probes, bounded waits, and fakes for the model endpoint, an OAuth token endpoint and telemetry.

## Install

```sh
dotnet add package DigitalBrain.Testing
```

Packages are published to the GitHub Packages feed `https://nuget.pkg.github.com/intochat/index.json`. Until the
repository is public, restoring needs a GitHub token with the `read:packages` scope. Keep the token in an environment
variable and reference it from `nuget.config`:

```xml
<configuration>
  <packageSources>
    <add key="intochat" value="https://nuget.pkg.github.com/intochat/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <intochat>
      <add key="Username" value="%GITHUB_ACTOR%" />
      <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
    </intochat>
  </packageSourceCredentials>
</configuration>
```

The package references `xunit.v3.extensibility.core` (for `IAsyncLifetime` and `AssemblyFixture`). Test projects on
xunit v2 get a version conflict; use xunit v3.

## Quick start

```csharp
await using var brain = await TestBrain.Create()
    .WithModule<MyModule>()
    .StartAsync(ct);
```

`StartAsync` boots a silo with the same recipe the product uses. `WithoutHttp()` skips the HTTP surface when a fact only
talks to grains, which makes start-up cheaper.

## One host per assembly

Starting a host is the expensive part. Start one per test assembly and isolate each fact in a fresh person's brain:

```csharp
[assembly: AssemblyFixture(typeof(MyHost))]

public sealed class MyHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<MyModule>().WithoutHttp();
}

public sealed class MyFacts(MyHost host)
{
    [Fact]
    public async Task SomethingHappens()
    {
        var ct = TestContext.Current.CancellationToken;
        var person = await host.PersonAsync("alice", ct);
        // act as person, then observe:
        // await using var probe = await host.ObserveAsync<MySignal>(source, person, ct);
    }
}
```

- `PersonAsync(name)` creates a fresh principal, account and brain. The name is a prefix made unique within the host.
- `ObserveAsync<T>(source, owner, ct)` subscribes a `SignalProbe<T>` as the owner. The probe is released with the host
  if the fact does not dispose it.
- Hosts with their own collaborators override `InitializeAsync`/`DisposeAsync`. Start the collaborators before the base
  call and stop them after it.

## Fakes

- `FakeModelServer`: an OpenAI-compatible endpoint. `FakeModelServer.StartAsync(ct)`, then `Script(modelId, FakeReply.Text("..."))`,
  wire it with `TestBrain.WithFakeModel(model)`, and read `Requests(modelId)`.
- `TokenEndpointStub`: an OAuth token and profile endpoint for integration modules. `TokenEndpointStub.StartAsync(ct)`
  exposes `TokenEndpoint`, the posted `Forms`, `Refreshes`, and a `RevokedRefreshToken` that is refused.
- `InMemoryTelemetry`: `brain.WithInMemoryTelemetry(out var sink, "MySource")` collects spans and log records;
  read them with `sink.Spans()` and `sink.LogRecords()`.

## Recommended test tiers

The DigitalBrain repository splits each module's tests by project suffix. You can use the same split:

| Suffix | Contents |
|---|---|
| `*.Tests.Unit` | No host, no network, no sleeps. |
| `*.Tests.Integration` | One `BrainHostFixture` per assembly. |
| `*.Tests.Hosting` | Aspire application model only (see `DigitalBrain.Aspire.Testing`). |
| `*.Tests.E2E`, `*.Tests.Browser` | The full product on Aspire, run nightly (see `DigitalBrain.Testing.Aspire`, `DigitalBrain.Testing.Playwright`). |

The handbook has more: <https://intochat.github.io/digitalbrain.docs/reference/testing>.
