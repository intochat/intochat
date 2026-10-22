using System;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Http.Compute;
using IntoChat.OS.Sdk.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Sdk.SampleModule;

// A runtime-free authoring example. The generator tests compile these exact source files.
[Module("sample")]
[Integration("sample-api", "Sample API")]
[Integration("sample-events", "Sample events")]
[Secret("ApiKey", Integration = "sample-api")]
[Setting("Audience", Integration = "sample-events")]
[ModulePriceList(typeof(SamplePriceList))]
[ExternalOperation("sample.read", "sample-api", "request")]
[ExternalOperation("sample.refresh", "sample-api", "request")]
public sealed partial class SampleModule : Module<SampleOptions>, IntoChat.OS.Framework.IHttpModule
{
    partial void OnConfigure(IModuleServices services)
    {
        services.Services.AddSingleton(services.Options<SampleOptions>());
        services.Services.AddMeteredHttpClient("sample-api").AddTypedClient<SampleHttpClient>();
    }

    public void Configure(IEndpointRouteBuilder endpoints) => endpoints.MapPost("/sample/preview", (Delegate)SampleHttp.PreviewAsync);
}

public sealed class SampleOptions : IModuleOptions
{
    [Setting(Required = true)] public string Region { get; init; } = "eu";
    [Setting(Required = true)] public Uri Endpoint { get; init; } = new("https://example.test/");
    [Setting] public int PageSize { get; init; } = 50;

    public void Validate() => ModuleValidation.ThrowIfInvalid(this, new SampleOptionsValidator());
}

public sealed class SampleOptionsValidator : IModuleValidator<SampleOptions>
{
    public ValidationIssue[] Validate(SampleOptions value)
    {
        var rules = new ValidationRules();
        rules.Required(nameof(value.Region), value.Region);
        rules.StringLength(nameof(value.Region), value.Region, 2, 16);
        rules.Required(nameof(value.Endpoint), value.Endpoint);
        rules.HttpUri(nameof(value.Endpoint), value.Endpoint);
        rules.Range(nameof(value.PageSize), value.PageSize, 1, 100);
        return rules.Issues;
    }
}
