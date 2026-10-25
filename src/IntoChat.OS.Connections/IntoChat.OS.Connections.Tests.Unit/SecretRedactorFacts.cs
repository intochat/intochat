using IntoChat.OS.Framework.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Connections.Tests.Unit;

public sealed class SecretRedactorFacts
{
    [Fact]
    public async Task AValueInsideAnotherIsMaskedWholeLongestFirst()
    {
        var redactor = Redactor("secret-value", "secret-value-and-more");
        Assert.Equal("a **** b", await redactor.Redact("a secret-value-and-more b", CancellationToken.None));
        Assert.Equal(20, await redactor.TailLength(CancellationToken.None));
    }

    [Fact]
    public async Task AValueShorterThanTheMinimumIsLeftAlone()
    {
        var redactor = Redactor("short", "long-enough-value");
        Assert.Equal("short and ****", await redactor.Redact("short and long-enough-value", CancellationToken.None));
    }

    [Fact]
    public async Task NullEmptyAndAVaultWithoutValuesPassThrough()
    {
        var redactor = Redactor("long-enough-value");
        Assert.Null(await redactor.Redact(null, CancellationToken.None));
        Assert.Equal("", await redactor.Redact("", CancellationToken.None));
        var bare = new SecretRedactor(new ServiceCollection().BuildServiceProvider());
        Assert.Equal("long-enough-value", await bare.Redact("long-enough-value", CancellationToken.None));
        Assert.Equal(0, await bare.TailLength(CancellationToken.None));
    }

    [Fact]
    public async Task ValuesAreReadOnEveryCallNotKept()
    {
        var source = new Source("first-secret-value");
        var redactor = new SecretRedactor(new ServiceCollection().AddSingleton<ISecretValueSource>(source).BuildServiceProvider());
        Assert.Equal("****", await redactor.Redact("first-secret-value", CancellationToken.None));
        source.Values = ["second-secret-value"];
        Assert.Equal("first-secret-value", await redactor.Redact("first-secret-value", CancellationToken.None));
        Assert.Equal("****", await redactor.Redact("second-secret-value", CancellationToken.None));
    }

    private static SecretRedactor Redactor(params string[] values)
        => new(new ServiceCollection().AddSingleton<ISecretValueSource>(new Source(values)).BuildServiceProvider());

    private sealed class Source(params string[] values) : ISecretValueSource
    {
        public string[] Values { get; set; } = values;

        public ValueTask<IReadOnlyList<string>> CurrentValues(CancellationToken cancellationToken) => new(Values);
    }
}
