namespace DigitalBrain.Apps;

public sealed class AppsOptions : IModuleOptions
{
    // Model neurons the authoring agents speak through; unset falls back to the neuron's default.
    public string? AuthorModel { get; set; }
    public string? BuilderModel { get; set; }

    public void Validate()
    {
    }
}
