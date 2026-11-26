namespace DigitalBrain.UI.Controls;

/// <summary>The state of a form neuron: the values last submitted, by field key (none while its parent controls the values).</summary>
public sealed record FormState
{
    /// <summary>The last submitted value of each field, by key.</summary>
    [Showcase("""{"kind":"formView","key":"about-you","values":[{"key":"name","value":"Ada"}],"props":{"fields":[{"key":"name","label":"Display name","kind":"Text","maxLength":64},{"key":"bio","label":"Bio","kind":"Multiline","placeholder":"A line about you"},{"key":"age","label":"Age","kind":"Number"},{"key":"id","label":"Account id","kind":"Text","value":"ada-1","isReadOnly":true}],"submitLabel":"Save","isEnabled":true,"automationName":"About you"}}""")]
    [Showcase("""{"kind":"formView","key":"api-key","values":[{"key":"label","value":"Work"}],"props":{"fields":[{"key":"label","label":"Label","kind":"Text"},{"key":"token","label":"API key","kind":"Secret","placeholder":"Stored in your vault"}],"submitLabel":"Seal","isEnabled":true,"automationName":"API key"}}""")]
    [Showcase("""{"kind":"formView","key":"budget","values":[],"props":{"fields":[{"key":"cap","label":"Monthly cap","kind":"Number","value":"50"}],"submitLabel":"Set cap","isEnabled":false,"automationName":"Budget"}}""")]
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool Equals(FormState? other) => other is not null && Values.Count == other.Values.Count
        && Values.All(entry => other.Values.TryGetValue(entry.Key, out var value) && value == entry.Value);

    /// <inheritdoc />
    public override int GetHashCode() => Values.Count;
}

/// <summary>What a form neuron draws: its key and last submitted values. The client draws the fields, submit label and enabled from the child's props.</summary>
/// <param name="Key">The neuron's key, by which the client finds the control.</param>
/// <param name="Values">The last submitted values, ordered by key.</param>
[ViewNode("formView")]
public sealed record FormView(string Key, IReadOnlyList<FormValue> Values) : Node
{
    /// <inheritdoc />
    public bool Equals(FormView? other) => other is not null && Key == other.Key && Values.SequenceEqual(other.Values);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, Values.Count);
}
