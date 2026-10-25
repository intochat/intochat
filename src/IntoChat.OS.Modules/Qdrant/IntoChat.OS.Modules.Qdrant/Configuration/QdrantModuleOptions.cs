using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Modules;

namespace IntoChat.OS.Modules.Qdrant;

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
