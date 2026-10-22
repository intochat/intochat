using System.Reflection;
using System.Text;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Http.Compute;
using IntoChat.OS.Sdk.Validation;
using Microsoft.AspNetCore.Http;

namespace IntoChat.OS.Sdk.Generators.Tests.Unit;

public sealed class SampleAuthoringFacts
{
    [Fact]
    public void ActualSampleRejectsInvalidOptionsAndDistinguishesZeroFromUnpriced()
    {
        var assembly = CompileSample();
        var optionType = assembly.GetType("IntoChat.OS.Sdk.SampleModule.SampleOptions")!;
        var options = (IModuleOptions)Activator.CreateInstance(optionType)!;
        optionType.GetProperty("Region")!.SetValue(options, "");
        Assert.Throws<ModuleValidationException>(options.Validate);

        var priceType = assembly.GetType("IntoChat.OS.Sdk.SampleModule.SamplePriceList");
        Assert.NotNull(priceType);
        var priceList = (IPriceList)Activator.CreateInstance(priceType)!;
        var known = priceList.Quote(CostKind.ExternalProvider, "sample.read", "request");
        Assert.Equal(PricingStatus.Known, known.Status);
        Assert.Equal(0, known.MicrosPerUnit);
        Assert.Equal("request", known.Unit);
        Assert.Equal(ExternalChargePolicy.EveryDispatchedAttempt, known.ChargePolicy);
        var unpriced = priceList.Quote(CostKind.ExternalProvider, "sample.refresh", "request");
        Assert.Equal(PricingStatus.Unpriced, unpriced.Status);
        Assert.Null(unpriced.MicrosPerUnit);
    }

    [Fact]
    public void ActualSampleBuildsDeclaredRequestMetadataWithoutProviderTraffic()
    {
        var assembly = CompileSample();
        var options = Activator.CreateInstance(assembly.GetType("IntoChat.OS.Sdk.SampleModule.SampleOptions")!);
        var clientType = assembly.GetType("IntoChat.OS.Sdk.SampleModule.SampleHttpClient");
        Assert.NotNull(clientType);
        using var request = Assert.IsType<HttpRequestMessage>(clientType.GetMethod("CreateReadRequest")!.Invoke(null,
            [options, Funding.OwnConnection, "sample-invocation", null]));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://example.test/items?limit=50&region=eu", request.RequestUri!.AbsoluteUri);
        var metadata = Assert.IsType<ExternalCallMetadata>(Assert.Single(request.Options).Value);
        Assert.Equal("sample.read", metadata.Operation);
        Assert.Equal(Funding.OwnConnection, metadata.Funding);
        Assert.Equal("sample-invocation", metadata.InvocationId);
    }

    [Theory]
    [InlineData("{\"text\":\"\"}", 400)]
    [InlineData("{\"text\":\"preview\"}", 200)]
    public async Task ActualSampleHttpValidatesBeforeAccepting(string json, int expected)
    {
        var assembly = CompileSample();
        var context = new DefaultHttpContext { RequestAborted = TestContext.Current.CancellationToken };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var http = assembly.GetType("IntoChat.OS.Sdk.SampleModule.SampleHttp")!;
        var result = await (Task<IResult>)http.GetMethod("PreviewAsync")!.Invoke(null, [context])!;
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private static Assembly CompileSample()
    {
        var sources = GeneratorHarness.SampleSources();
        Assert.NotEmpty(sources);
        var compilation = GeneratorHarness.Compile(sources);
        GeneratorHarness.CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);
        GeneratorHarness.AssertNoErrors(output, diagnostics);
        using var bytes = new MemoryStream();
        var result = output.Emit(bytes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(bytes.ToArray());
    }
}
