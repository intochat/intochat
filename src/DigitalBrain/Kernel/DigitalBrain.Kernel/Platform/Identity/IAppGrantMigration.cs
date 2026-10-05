using DigitalBrain.Contracts;
using Orleans;

namespace DigitalBrain.Kernel.Platform.Identity;

[PlatformOnly]
public interface IAppGrantMigration : IGrainWithStringKey
{
    Task Migrate(string appId, string[] legacyFiles);
}
