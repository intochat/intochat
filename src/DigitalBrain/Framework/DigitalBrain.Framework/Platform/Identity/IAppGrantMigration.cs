using DigitalBrain;
using DigitalBrain.Framework;
using Orleans;

namespace DigitalBrain.Framework.Platform.Identity;

[PlatformOnly]
public interface IAppGrantMigration : IGrainWithStringKey
{
    Task Migrate(string appId, string[] legacyFiles);
}
