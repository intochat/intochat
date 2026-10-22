# SDK authoring sample

This small module demonstrates authoring with the SDK, neutral Compute contracts and SDK HTTP helpers. It has no neuron or dependency on the Framework runtime, Authority or Client assemblies. Generator tests compile these exact C# files and compare their generated shell with `Goldens/SampleModule.g.cs`.

`SampleModule.cs` declares two integrations, a price list and two external operations. The generated shell registers the price list before `OnConfigure`, which binds validated options and enrolls the named HTTP client. Options reject missing settings and invalid region, endpoint and page size through the shared SDK validator.

`SamplePriceList.cs` deliberately distinguishes a known-zero `sample.read` operation from an unpriced `sample.refresh` operation. These are illustrative rates for `example.test`, not provider tariff claims. An unpriced operation does not become free: the runtime's funding and admission policy decides whether it may be dispatched.

```csharp
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk.SampleModule;

var options = new SampleOptions { Region = "eu", PageSize = 25 };
options.Validate();
using var request = SampleHttpClient.CreateReadRequest(
    options, Funding.OwnConnection, "sample-invocation");
```

Creating a request sends no traffic. `SampleHttpClient.ReadAsync` sends it through the enrolled HTTP client, forwards cancellation and returns the response for the caller to dispose. Funding comes from the selected connection or deployment resource. The host supplies the trusted context accessor and cost meter; request metadata supplies operation and correlation, not caller or payer authority. Retries and bounded redirects must sit outside the SDK attempt handler.

`SampleHttp.cs` demonstrates the inbound boundary: `/sample/preview` reads at most 1,024 encoded bytes and validates the decoded payload before returning an acceptance result. Its text limits count Unicode scalar values and UTF-8 bytes separately. It creates no provider resource and stores no state.

The runtime counterpart is the [Gmail module](../../IntoChat.OS.Modules/Google/Gmail/IntoChat.OS.Modules.Google.Gmail/GmailModule.cs), its [neuron](../../IntoChat.OS.Modules/Google/Gmail/IntoChat.OS.Modules.Google.Gmail/Gmail/GmailNeuron.cs) and [public contract](../../IntoChat.OS.Modules/Google/Gmail/IntoChat.OS.Modules.Google.Gmail/Contracts/Gmail/IGmail.cs). The planned typed Gmail showcase and live-mailbox verification are separate work; this sample does not claim they are implemented.

Run from the repository root:

```sh
dotnet build src/IntoChat.OS.Sdk/IntoChat.OS.Sdk.SampleModule/IntoChat.OS.Sdk.SampleModule.csproj -c Release
dotnet test --project src/IntoChat.OS.Sdk/IntoChat.OS.Sdk.Generators.Tests.Unit/IntoChat.OS.Sdk.Generators.Tests.Unit.csproj -c Release
```
