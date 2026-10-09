using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Revisions.Aspire.Hosting;

// The revisions module hosts no infrastructure of its own; naming it in the AppHost is what
// projects the deployment's DigitalBrain:Modules:revisions options into the OS environment.
public sealed class RevisionsModuleHosting() : ModuleHosting<RevisionsModule>
{
    public override void Configure(DigitalBrainBuilder brain) { }
}