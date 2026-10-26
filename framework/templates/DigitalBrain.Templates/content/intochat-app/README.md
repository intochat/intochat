# AppName

An IntoChat app that follows the app convention.

| File | Holds |
| --- | --- |
| `AppName/AppName.cs` | The `[App]` class: one `[On]` method per operation |
| `AppName/AppName.spec.md` | The spec comment: the description, `Role`, `Shipped`, `Operations` and the scenarios |
| `AppName.Tests.Unit/AppNameFacts.cs` | The generated baseline theory (`AppBaseline`) and one fact per scenario |

```
dotnet build -warnaserror
dotnet test --project AppName.Tests.Unit/AppName.Tests.Unit.csproj
```

The tests run on `TestOS`, without a runtime, a silo or a network. The DigitalBrain analyzers check the convention in the build:
an operation that the spec does not describe, a key built from strings or an empty catch fails it.
