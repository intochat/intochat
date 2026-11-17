namespace DigitalBrain.Rules;

/// <summary>
/// A domain refusal: an aggregate said no. A refusal is a value, never an exception; the adapter publishes it as <see cref="Rejected"/>.
/// Each context declares its refusals as records deriving from this one and lists them in a union.
/// </summary>
/// <param name="Code">The stable wire code, for example <c>operation.plan-mismatch</c>. Clients and Ino match on it; it never changes.</param>
public abstract record Refusal(string Code)
{
    /// <summary>What a person is told.</summary>
    public abstract string Message { get; }

    /// <summary>The published rejection of <paramref name="operation"/>.</summary>
    /// <param name="operation">The refused operation.</param>
    public Rejected ToRejected(string operation) => new(operation, [Code], [Message]);
}
