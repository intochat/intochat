# Modules

A module is the unit of composition: code that implements capabilities and declares what it needs from its host.

## Shape

A module class derives from `Module`, is marked `[Module(id)]` and has a `public static ModuleManifest Manifest` property.
`ModuleManifest.Of(type)` reads both. The id is frozen once published. The analyzer rule `DB0001` fails the build when the attribute or the manifest is missing.

```csharp
[Module(Id)]
public sealed class MODULE_CLASSModule : Module
{
    public const string Id = "MODULE_ID";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [Need.State("state")],
        Settings: [Setting.Of<MODULE_CLASSOptions>()],
        Secrets: []);
}
```

Trimmed from `templates/DigitalBrain.Templates/content/digitalbrain-module/MODULE_NAME/MODULE_CLASSModule.cs`.

## The manifest

| Member | Holds |
| --- | --- |
| `Provides` | The capabilities the module implements, as `CapabilityId` values |
| `Requires` | The capabilities the module calls. A requirement is a need, not a grant |
| `Needs` | The resources the host must supply |
| `Settings` | The options types the module binds: `Setting.Of<TOptions>()` |
| `Secrets` | The secrets the module reads: `Secret.Named(name)` |
| `Privileged` | The privileged capability members the module may call. Anything not listed is refused |
| `Auth` | A `ModuleAuth` (scheme and scopes) when the module connects to an external account |
| `Actions` | The actions [policies](policies.md) can limit |

## Needs

A module does not reference hosting packages. It names what it needs and the host supplies it:

- `Need.State(name)`: a named durable state store.
- `Need.Secrets`: encrypted secrets.
- `Need.Reminders`: durable reminders.
- `Need.Http`: outbound HTTP. `Need.HttpClient(name, baseAddressSetting, requestsPerMinute)`: a named, metered and rate-limited client.
- `Need.Webhook(path, targetNeuronType)`: an inbound webhook that reaches a neuron type.
- `Need.Sandbox`: isolated code execution. `Need.Container(name, image)`: a container beside the host.
- `Need.Model`: a language model.

## Scaffold a module

Install the `DigitalBrain.Templates` package with `dotnet new install`. Then:

```
dotnet new digitalbrain-module -n <ModuleName> --ModuleId <vendor.name> --inTree true
dotnet new digitalbrain-module -n <ModuleName> --ModuleId <vendor.name> --inTree false
```

- `--ModuleId` is required: `vendor.name` in lowercase letters, digits and hyphens, for example `acme.weather`. A generated test checks it.
- `--inTree true` is for a module inside an existing repository. It leaves out the root files.
- `--inTree false` (the default) is for a standalone repository. It adds `Directory.Build.props`, `Directory.Packages.props`, `global.json` and `nuget.config`.

Both create:

- `<ModuleName>/<ModuleName>Module.cs`: the module and its manifest.
- `<ModuleName>/Contracts/`: the public interface other code calls.
- `<ModuleName>/src/`: the module's internal neurons.
- `tests/<ModuleName>.Tests.Unit/` and `tests/<ModuleName>.Tests.Integration/`.

```
dotnet build
dotnet test --project tests/<ModuleName>.Tests.Unit/<ModuleName>.Tests.Unit.csproj
```
