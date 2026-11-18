namespace DigitalBrain.Invocation;

/// <summary>What a call returns once awaited.</summary>
public enum InvocationResultKind
{
    /// <summary>void, Task or ValueTask: no result.</summary>
    None,

    /// <summary>A value, Task&lt;T&gt; or ValueTask&lt;T&gt;: one result.</summary>
    Value,

    /// <summary>IAsyncEnumerable&lt;T&gt;: a stream of results.</summary>
    Stream,
}
