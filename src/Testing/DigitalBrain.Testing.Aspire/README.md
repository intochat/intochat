# DigitalBrain.Testing.Aspire

Runs a `TestBrain` against your Aspire AppHost instead of in process. Use it to test the full product composition,
including containers and the resources the AppHost declares, over HTTP and an Orleans client.

## Install

```sh
dotnet add package DigitalBrain.Testing.Aspire
```

The feed and the `read:packages` token are set up as described in the `DigitalBrain.Testing` README.

## Use

```csharp
await using var brain = await TestBrain.Create()
    .On(new AspireTarget<Projects.MyAppHost>())
    .StartAsync(ct);

var client = await brain.CreateHttpClientAsync("web", "https", ct);
var connection = await brain.GetConnectionStringAsync("postgres", ct);
```

The AppHost owns composition, so on this target `WithModule` and `ConfigureServices` are refused.

To share one AppHost across many facts, derive a fixture from `SharedAspireBrain` (a `SharedBrain<ITestBrain>`). It
starts the AppHost once, and `LeaseAsync` hands out the running brain.

## Cost

Every start builds and launches the whole AppHost, containers included. That takes minutes, not seconds, and needs
Docker. Keep these tests in a separate tier (`*.Tests.E2E`) that runs nightly, and use `DigitalBrain.Testing`'s
in-process target for module tests.
