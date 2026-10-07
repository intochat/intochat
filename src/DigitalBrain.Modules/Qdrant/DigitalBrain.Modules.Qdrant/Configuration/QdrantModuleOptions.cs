using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
namespace DigitalBrain.Qdrant;

public sealed class QdrantModuleOptions : IModuleOptions
{
    public bool Host { get; set; }

    public QdrantModuleOptions WithHostedQdrant()
    {
        Host = true;
        return this;
    }

    public void Validate() { }
}
