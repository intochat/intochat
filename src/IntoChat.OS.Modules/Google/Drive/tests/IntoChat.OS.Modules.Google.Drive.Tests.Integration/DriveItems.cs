using DigitalBrain.Capabilities.Storage;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

internal static class DriveItems
{
    internal static async Task<Item> Find(IStorage storage, string path, CancellationToken ct)
    {
        var item = Assert.Single(await storage.Roots(ct));
        foreach (var name in path.Split('/'))
        {
            string? cursor = null;
            Item? found = null;
            do
            {
                var page = await storage.List(item.Id, new(Cursor: cursor), ct);
                found = page.Items.SingleOrDefault(child => child.Name == name);
                cursor = page.NextCursor;
            } while (found is null && cursor is not null);
            item = Assert.IsType<Item>(found);
        }
        return item;
    }
}
