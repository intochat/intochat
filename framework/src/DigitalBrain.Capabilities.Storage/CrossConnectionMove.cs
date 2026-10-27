using DigitalBrain.Capabilities;
using DigitalBrain.Operations;
using DigitalBrain.Spi;

namespace DigitalBrain.Capabilities.Storage;

/// <summary>
/// A move or copy between two storage connections (D-04): copy each item (stream read on the source, streaming write on the
/// target), verify each copied file (size and hash when both connections report a comparable one, else a byte compare), and for a
/// move delete the source to the trash only after every item verified. Any failure or cancellation before the delete leaves the
/// source untouched and removes the partial copy best-effort. A source delete that fails after a verified copy ends
/// <c>source-delete-failed</c>: both copies exist and nothing is lost.
/// </summary>
internal sealed class CrossConnectionMove(
    CapabilityConnection<IStorage> source, CapabilityConnection<IStorage> target, WritePlan plan, IOperation operation, StorageWriteRunner runner)
{
    private ItemId? _created;

    public async Task<OperationResult> Run()
    {
        var ct = operation.Cancellation;
        var a = source.Capability;
        var b = target.Capability;
        var root = await a.Stat(plan.Source!.Value, ct).ConfigureAwait(false);
        var tree = new List<Node>();
        await Collect(a, root, -1, tree, ct).ConfigureAwait(false);

        var folder = plan.TargetFolder!.Value;
        var taken = (await StorageWriteRunner.Children(b, folder, ct).ConfigureAwait(false)).Select(item => item.Name).ToList();
        var name = root.Name;
        var policy = plan.Conflict;
        if (taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            switch (plan.Conflict)
            {
                case ConflictPolicy.Fail:
                    return new OperationResult.Failed("conflict", $"storage conflict: {target.ConnectionKey}:{name}");
                case ConflictPolicy.Rename:
                    name = StorageWriteRunner.FreeName(name, taken);
                    policy = ConflictPolicy.Fail;
                    break;
            }
        }
        var move = plan.Kind == WriteKind.Move;
        if (plan.DryRun) { return new OperationResult.Planned(Steps(tree, name, move)); }

        var total = tree.Count + (move ? 1 : 0);
        try
        {
            var ids = new ItemId[tree.Count];
            for (var i = 0; i < tree.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var node = tree[i];
                var parent = i == 0 ? folder : ids[node.Parent];
                var itemName = i == 0 ? name : node.Item.Name;
                var itemPolicy = i == 0 ? policy : ConflictPolicy.Fail;
                if (node.Item.Kind == ItemKind.Folder)
                {
                    var made = await runner.Write(b, WritePlan.CreateFolder(parent, itemName, itemPolicy), ct).ConfigureAwait(false);
                    if (made is not OperationResult.Succeeded) { return await Undo(made).ConfigureAwait(false); }
                    ids[i] = (await StorageWriteRunner.Children(b, parent, ct).ConfigureAwait(false))
                        .First(item => item.Kind == ItemKind.Folder && string.Equals(item.Name, itemName, StringComparison.OrdinalIgnoreCase)).Id;
                    if (i == 0) { _created = ids[i]; }
                }
                else
                {
                    ids[i] = await CopyFile(a, b, node.Item.Id, parent, itemName, itemPolicy, ct).ConfigureAwait(false);
                    if (i == 0) { _created = ids[i]; }
                    if (!await Verified(a, b, node.Item, ids[i], ct).ConfigureAwait(false))
                    {
                        return await Undo(new OperationResult.Failed("verify-failed", $"storage verify-failed: {target.ConnectionKey}:{ids[i].Value}")).ConfigureAwait(false);
                    }
                }
                await operation.Report((i + 1) / (double)total).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await Undo(new OperationResult.Cancelled()).ConfigureAwait(false);
        }
        catch (StorageException error)
        {
            return await Undo(new OperationResult.Failed(error.Code, error.Message)).ConfigureAwait(false);
        }

        if (move)
        {
            // From here the copy is verified and kept whatever happens: a failed delete leaves two copies, never none.
            OperationResult deleted;
            try { deleted = await runner.Write(a, WritePlan.Delete(root.Id), CancellationToken.None).ConfigureAwait(false); }
            catch (StorageException error) { deleted = new OperationResult.Failed(error.Code, error.Message); }
            if (deleted is OperationResult.Failed failed)
            {
                return new OperationResult.Failed("source-delete-failed",
                    $"copied to {target.ConnectionKey}:{_created?.Value} and verified; deleting {source.ConnectionKey}:{root.Id.Value} failed: {failed.Code}");
            }
        }
        await operation.Report(1).ConfigureAwait(false);
        return new OperationResult.Succeeded();
    }

    private static async Task Collect(IStorage storage, Item item, int parent, List<Node> into, CancellationToken ct)
    {
        into.Add(new Node(item, parent));
        if (item.Kind != ItemKind.Folder) { return; }
        var self = into.Count - 1;
        foreach (var child in await StorageWriteRunner.Children(storage, item.Id, ct).ConfigureAwait(false))
        {
            await Collect(storage, child, self, into, ct).ConfigureAwait(false);
        }
    }

    private static async Task<ItemId> CopyFile(IStorage a, IStorage b, ItemId from, ItemId folder, string name, ConflictPolicy policy, CancellationToken ct)
    {
        var read = await a.OpenRead(from, null, ct).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            var write = await b.OpenWrite(WritePlan.WriteFile(folder, name, null, policy), ct).ConfigureAwait(false);
            await using (write.ConfigureAwait(false))
            {
                await read.Content.CopyToAsync(write.Content, ct).ConfigureAwait(false);
                return await write.Commit(ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> Verified(IStorage a, IStorage b, Item original, ItemId copy, CancellationToken ct)
    {
        var copied = await b.Stat(copy, ct).ConfigureAwait(false);
        if (original.Size is { } expected && copied.Size is { } actual && expected != actual) { return false; }
        if (Algorithm(original.Hash) is { } left && left == Algorithm(copied.Hash))
        {
            return string.Equals(original.Hash, copied.Hash, StringComparison.OrdinalIgnoreCase);
        }
        var x = await a.Read(original.Id, null, ct).ConfigureAwait(false);
        var y = await b.Read(copy, null, ct).ConfigureAwait(false);
        return x.Bytes.Span.SequenceEqual(y.Bytes.Span);
    }

    private static string? Algorithm(string? hash)
    {
        if (string.IsNullOrEmpty(hash)) { return null; }
        var colon = hash.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? hash[..colon].ToUpperInvariant() : null;
    }

    // Removes the partial copy best-effort (permanently, falling back to the trash) and returns the result unchanged.
    private async Task<OperationResult> Undo(OperationResult result)
    {
        if (_created is not { } created) { return result; }
        foreach (var permanent in new[] { true, false })
        {
            try
            {
                if (await runner.Write(target.Capability, WritePlan.Delete(created, permanent), CancellationToken.None).ConfigureAwait(false) is OperationResult.Succeeded)
                {
                    break;
                }
            }
            catch (StorageException)
            {
                // Best effort: the source is intact either way.
            }
        }
        return result;
    }

    private List<string> Steps(List<Node> tree, string name, bool move)
    {
        var steps = new List<string>();
        foreach (var (node, index) in tree.Select((node, index) => (node, index)))
        {
            var itemName = index == 0 ? name : node.Item.Name;
            if (node.Item.Kind == ItemKind.Folder)
            {
                steps.Add($"create-folder {target.ConnectionKey}:{itemName}");
            }
            else
            {
                steps.Add($"copy {source.ConnectionKey}:{node.Item.Id.Value} -> {target.ConnectionKey}:{itemName}");
                steps.Add($"verify {target.ConnectionKey}:{itemName}");
            }
        }
        if (move) { steps.Add($"delete {source.ConnectionKey}:{tree[0].Item.Id.Value} to-trash"); }
        return steps;
    }

    private sealed record Node(Item Item, int Parent);
}
