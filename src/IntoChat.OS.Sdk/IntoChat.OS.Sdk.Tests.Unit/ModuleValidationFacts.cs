using IntoChat.OS.Sdk.Configuration;
using IntoChat.OS.Sdk.Modules;
using IntoChat.OS.Sdk.Validation;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;

namespace IntoChat.OS.Sdk.Tests.Unit;

public sealed class ModuleValidationFacts
{
    [Fact]
    public void CompileAndBindRejectMissingRequiredSettingsBeforeCustomValidation()
    {
        var options = new CountingOptions { Label = "secret-sentinel" };
        var compile = Assert.Throws<ModuleValidationException>(() => ModuleOptionsSerialization.Compile<CountingModule, CountingOptions>(options));
        Assert.Equal(0, options.ValidationCount);
        var bind = Assert.Throws<ModuleValidationException>(() => new ConfigurationBuilder().Build().GetModuleOptions<CountingOptions>("checked"));
        Assert.Equal(compile.Issues, bind.Issues);
        Assert.Equal(ModuleOptionsSerialization.OptionsKey("checked") + ":Endpoint", Assert.Single(compile.Issues).Path);
        Assert.Equal("required", compile.Issues[0].Code);
        Assert.DoesNotContain("secret-sentinel", compile.ToString());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("present", 1)]
    public void RequiredSettingsRejectNullBlankAndDefault(string? text, int count)
    {
        var failure = Assert.Throws<ModuleValidationException>(() => ModuleOptionsSerialization.ValidateRequired(new RequiredOptions { Text = text, Count = count }, "required"));
        Assert.Contains(failure.Issues, issue => issue.Path == ModuleOptionsSerialization.OptionsKey("required") + ":Flag" && issue.Code == "required");
        Assert.Equal(text == "present" ? 1 : 3, failure.Issues.Length);
    }

    [Fact]
    public void CompileAndBindRunCustomValidationOnceAndNormalizeNestedPaths()
    {
        var source = new CountingOptions { Endpoint = new("https://example.test"), Label = "secret-sentinel" };
        var compiled = ModuleOptionsSerialization.Compile<CountingModule, CountingOptions>(source);
        Assert.Equal(1, source.ValidationCount);
        var bound = new ConfigurationBuilder().AddInMemoryCollection(compiled.Settings).Build().GetModuleOptions<CountingOptions>("checked");
        Assert.Equal(1, bound.ValidationCount);

        source.Reject = true;
        var compile = Assert.Throws<ModuleValidationException>(() => ModuleOptionsSerialization.Compile<CountingModule, CountingOptions>(source));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ModuleOptionsSerialization.OptionsKey("checked") + ":Endpoint"] = "https://example.test",
            [ModuleOptionsSerialization.OptionsKey("checked") + ":Label"] = "secret-sentinel",
            [ModuleOptionsSerialization.OptionsKey("checked") + ":Reject"] = "true",
        }).Build();
        var bind = Assert.Throws<ModuleValidationException>(() => configuration.GetModuleOptions<CountingOptions>("checked"));
        Assert.Equal(compile.Issues, bind.Issues);
        Assert.Equal(ModuleOptionsSerialization.OptionsKey("checked") + ":Recipients[0].Address", Assert.Single(bind.Issues).Path);
        Assert.DoesNotContain("secret-sentinel", bind.ToString());
    }

    [Fact]
    public void ValidatorBugsPropagateInsteadOfBecomingUserErrors()
    {
        var failure = new InvalidOperationException("validator bug");
        var thrown = Assert.Throws<InvalidOperationException>(() => ModuleValidation.Validate("value", new ThrowingValidator(failure)));
        Assert.Same(failure, thrown);
    }

    [Fact]
    public void ScalarLengthAndUtf8BytesHaveIndependentInclusiveBounds()
    {
        var accepted = new ValidationRules();
        accepted.StringLength("subject", "😀é", 2, 2);
        accepted.Utf8Length("subject", "😀é", 6);
        accepted.ArrayLength<string>("to", ["a", "b"], 2, 2);
        accepted.Range("limit", 10, 1, 10);
        accepted.HttpUri("endpoint", new Uri("https://example.test"));
        Assert.Empty(accepted.Issues);

        var rejected = new ValidationRules();
        rejected.StringLength("subject", "😀é", 0, 1);
        rejected.Utf8Length("body", "😀é", 5);
        rejected.ArrayLength<string>("to", ["a", "b"], 0, 1);
        rejected.Range("limit", 11, 1, 10);
        rejected.HttpUri("endpoint", new Uri("/relative", UriKind.Relative));
        Assert.Equal<string>(["subject", "body", "to", "limit", "endpoint"], rejected.Issues.Select(issue => issue.Path));
        Assert.DoesNotContain("😀é", string.Join(" ", rejected.Issues.Select(issue => issue.Message)));
    }

    [Theory]
    [InlineData("file:///tmp/file")]
    [InlineData("ftp://example.test")]
    public void HttpUriRejectsOtherSchemes(string value)
    {
        var rules = new ValidationRules();
        rules.HttpUri("endpoint", new Uri(value));
        Assert.Equal("uri", Assert.Single(rules.Issues).Code);
    }

    [Fact]
    public void RequiredAndLowerBoundsPreserveCallerPaths()
    {
        var rules = new ValidationRules();
        rules.Required("to[0]", " ");
        rules.Required("body", null);
        rules.StringLength("subject", "x", 2, 10);
        rules.ArrayLength("to", Array.Empty<string>(), 1, 100);
        rules.Range("limit", 0, 1, 10);
        Assert.Equal<string>(["to[0]", "body", "subject", "to", "limit"], rules.Issues.Select(issue => issue.Path));
    }

    [Fact]
    public void ThrowIfInvalidKeepsSafeIssueDetails()
    {
        var failure = Assert.Throws<ModuleValidationException>(() => ModuleValidation.ThrowIfInvalid("secret-sentinel", new RejectingValidator()));
        Assert.Equal(new ValidationIssue("Recipients[0].Address", "address", "A valid address is required."), Assert.Single(failure.Issues));
        Assert.DoesNotContain("secret-sentinel", failure.ToString());
    }

    private sealed class ThrowingValidator(Exception failure) : IModuleValidator<string>
    {
        public ValidationIssue[] Validate(string value) => throw failure;
    }

    private sealed class RejectingValidator : IModuleValidator<string>
    {
        public ValidationIssue[] Validate(string value) => [new("Recipients[0].Address", "address", "A valid address is required.")];
    }

    private sealed class RequiredOptions
    {
        [Setting(Required = true)] public string? Text { get; init; }
        [Setting(Required = true)] public int Count { get; init; }
        [Setting(Required = true)] public bool Flag { get; init; }
    }

    public sealed class CountingOptions : IModuleOptions
    {
        [Setting(Required = true)] public Uri? Endpoint { get; set; }
        public string? Label { get; set; }
        public bool Reject { get; set; }
        public int ValidationCount { get; private set; }
        public void Validate()
        {
            ValidationCount++;
            if (Reject) { ModuleValidation.ThrowIfInvalid(Label ?? "", new RejectingValidator()); }
        }
    }

    public sealed class CountingModule() : Module<CountingOptions>(new("checked"))
    {
        public override void Configure(ISiloBuilder silo) { }
    }
}
