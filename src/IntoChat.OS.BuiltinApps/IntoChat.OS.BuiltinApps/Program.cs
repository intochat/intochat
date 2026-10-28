using IntoChat.OS.BuiltinApps;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i + 1 < args.Length; i += 2) { options[args[i]] = args[i + 1]; }
if (!options.TryGetValue("--features", out var features) || !options.TryGetValue("--out", out var output))
{
    await Console.Error.WriteLineAsync("usage: IntoChat.OS.BuiltinApps --features <features/os> --out <compiled-apps/os> [--only <name>]");
    return 2;
}
try
{
    var built = await BuiltinPacker.PackAll(features, output, options.GetValueOrDefault("--only"), CancellationToken.None);
    foreach (var package in built) { Console.WriteLine($"{package.Name} {package.Digest} {package.Length}"); }
    return 0;
}
catch (InvalidDataException error)
{
    await Console.Error.WriteLineAsync(error.Message);
    return 1;
}
