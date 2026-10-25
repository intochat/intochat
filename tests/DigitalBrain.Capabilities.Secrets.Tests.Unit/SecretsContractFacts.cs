using System.Reflection;
using System.Text.Json;
using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Capabilities.Secrets.Tests.Unit;

public sealed class SecretsCapabilityFacts
{
    [Fact]
    public void Secrets_is_secrets_v1()
    {
        var attribute = typeof(ISecrets).GetCustomAttribute<CapabilityAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(Capability.Secrets.V1, attribute.Id);
    }

    // D-15: the caller comes from the runtime context, never from a parameter.
    [Fact]
    public void No_member_takes_a_caller()
    {
        var parameters = typeof(ISecrets).GetMethods().SelectMany(method => method.GetParameters()).ToList();
        Assert.NotEmpty(parameters);
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType.Name.Contains("Caller", StringComparison.Ordinal));
    }

    [Fact]
    public void Members_are_set_remove_resolve()
    {
        Assert.Equal(["Remove", "Resolve", "Set"], typeof(ISecrets).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(typeof(Task<SecretRef>), typeof(ISecrets).GetMethod("Set")!.ReturnType);
        Assert.Equal(typeof(Task<string>), typeof(ISecrets).GetMethod("Resolve")!.ReturnType);
        Assert.Equal(typeof(SecretRef), typeof(ISecrets).GetMethod("Resolve")!.GetParameters()[0].ParameterType);
    }

    [Fact]
    public async Task Resolve_returns_the_value_set_and_remove_makes_it_not_found()
    {
        ISecrets secrets = new MemorySecrets();
        var reference = await secrets.Set("github", "GitHub token", "s3cr3t-value");
        Assert.True(reference.IsSet);
        Assert.DoesNotContain("s3cr3t-value", reference.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-value", JsonSerializer.Serialize(reference), StringComparison.Ordinal);
        Assert.Equal("s3cr3t-value", await secrets.Resolve(reference));

        await secrets.Remove("github");
        var error = await Assert.ThrowsAsync<SecretNotFound>(() => secrets.Resolve(reference));
        Assert.Equal("github", error.Name);
    }

    [Fact]
    public async Task Resolving_an_unset_reference_is_not_found()
    {
        ISecrets secrets = new MemorySecrets();
        await Assert.ThrowsAsync<SecretNotFound>(() => secrets.Resolve(SecretRef.Unset("github", "GitHub token")));
    }

    private sealed class MemorySecrets : ISecrets
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public Task<SecretRef> Set(string name, string label, string value, CancellationToken cancellationToken = default)
        {
            values[name] = value;
            return Task.FromResult(new SecretRef { Reference = name, Label = label, Status = SecretStatus.Set });
        }

        public Task Remove(string name, CancellationToken cancellationToken = default)
        {
            values.Remove(name);
            return Task.CompletedTask;
        }

        public Task<string> Resolve(SecretRef secret, CancellationToken cancellationToken = default) =>
            secret.IsSet && values.TryGetValue(secret.Reference, out var value)
                ? Task.FromResult(value)
                : Task.FromException<string>(new SecretNotFound(secret.Reference));
    }
}

public sealed class SecretRefFacts
{
    [Fact]
    public void Round_trips_through_json()
    {
        var reference = new SecretRef { Reference = "github", Label = "GitHub token", Status = SecretStatus.Set };
        var json = JsonSerializer.Serialize(reference);
        Assert.Equal(reference, JsonSerializer.Deserialize<SecretRef>(json));
        Assert.Contains("\"reference\":\"github\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isSet", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_is_unset()
    {
        var reference = new SecretRef();
        Assert.False(reference.IsSet);
        Assert.Equal("", reference.Reference);
    }

    [Fact]
    public void Unset_factory_keeps_name_and_label()
    {
        var reference = SecretRef.Unset("github", "GitHub token");
        Assert.Equal(SecretStatus.Unset, reference.Status);
        Assert.Equal("github", reference.Reference);
        Assert.Equal("GitHub token", reference.Label);
    }

    [Fact]
    public void Carries_no_value_property()
    {
        Assert.Equal(["IsSet", "Label", "Reference", "Status"], typeof(SecretRef).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ToString_names_the_reference_only()
    {
        Assert.Equal("secret github (set)", new SecretRef { Reference = "github", Label = "GitHub token", Status = SecretStatus.Set }.ToString());
        Assert.Equal("secret github (unset)", SecretRef.Unset("github", "x").ToString());
    }
}

public sealed class SecretErrorFacts
{
    [Fact]
    public void Errors_carry_the_name_never_a_value()
    {
        var notFound = new SecretNotFound("github");
        var denied = new SecretDenied("github");
        Assert.Equal("secret not-found: github", notFound.Message);
        Assert.Equal("secret denied: github", denied.Message);
        Assert.Equal("not-found", notFound.Code);
        Assert.Equal("denied", denied.Code);
        Assert.IsAssignableFrom<SecretException>(denied);
    }

    [Fact]
    public void Blank_name_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SecretNotFound(" "));
    }
}
