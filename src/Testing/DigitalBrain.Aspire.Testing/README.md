# DigitalBrain.Aspire.Testing

Checks on an Aspire application model without running it. Build the graph, then read the environment and configuration
a resource would receive. Nothing starts, so these checks run in milliseconds.

## Install

```sh
dotnet add package DigitalBrain.Aspire.Testing
```

The feed and the `read:packages` token are set up as described in the `DigitalBrain.Testing` README.

## Use

```csharp
var builder = AspireCompositionTest.CreateBuilder();          // run mode; CreatePublishBuilder() for publish mode
var silo = builder.AddMyBrain();                              // your composition extension

var environment = await AspireCompositionTest.EnvironmentOf(builder, silo.Resource, ct);
var configuration = await AspireCompositionTest.ConfigurationOf(builder, silo.Resource, ct);
Assert.Equal("value", configuration["MyModule:Setting"]);
```

`EnvironmentOf` returns the raw environment variables. `ConfigurationOf` returns the same values under their .NET
configuration names.

The DigitalBrain repository keeps these checks in `*.Tests.Hosting` projects.
