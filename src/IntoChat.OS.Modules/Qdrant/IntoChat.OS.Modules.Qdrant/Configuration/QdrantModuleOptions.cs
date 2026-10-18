using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
namespace IntoChat.OS.Qdrant;

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
