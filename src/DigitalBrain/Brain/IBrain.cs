namespace DigitalBrain;

[Alias("brain"), Orleans.Metadata.DefaultGrainType("brain")]
public interface IBrain : INeuron
{
    Task<BrainSnapshot> Read();
    Task<BrainSnapshot> Establish(EstablishBrain request);
}
