using AmneziaGeo.Decl;
using AmneziaGeo.Deltas;

// Makes the patch that turns the files a machine holds of an earlier tree into the tree given, and proves it on that
// earlier tree before it is kept.
var tree = default(string);
var earlier = default(string);
var output = default(string);
var asset = default(string);
var held = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tree" when i + 1 < args.Length:
            tree = args[++i];
            break;
        case "--earlier" when i + 1 < args.Length:
            earlier = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            output = args[++i];
            break;
        case "--held" when i + 1 < args.Length:
            held.Add(args[++i]);
            break;
        case "--asset" when i + 1 < args.Length:
            asset = args[++i];
            break;
        default:
            tree = null;
            i = args.Length;
            break;
    }
}

if (tree is null || earlier is null || output is null)
{
    Console.Error.WriteLine("usage: AmneziaGeo.Deltas --tree <folder> --earlier <folder> --out <file> [--held <folder of the tree>]... [--asset <file>]");
    return 2;
}

var builder = new PatchBuilder(TimeSpan.FromMinutes(5), Console.Out);
var summary = await builder.BuildAsync(tree, earlier, held, output, CancellationToken.None).ConfigureAwait(false);
var most = asset is null ? long.MaxValue : (long)(new FileInfo(asset).Length * PatchBuilder.MostShare);
if (summary.Held == 0 || summary.Bytes >= most)
{
    File.Delete(output);
    Console.Out.WriteLine($"{Path.GetFileName(output)}: left out, it saves too little against the whole file");
    return 0;
}

var proof = Path.Combine(Path.GetTempPath(), "amneziageo-patch-" + Guid.NewGuid().ToString("N"));
try
{
    var machine = held.Count == 0 ? new List<string> { earlier } : held.Select(folder => Path.Combine(earlier, folder)).ToList();
    await UpdatePatch.ApplyAsync(output, machine, proof, CancellationToken.None).ConfigureAwait(false);
}
catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
{
    File.Delete(output);
    Console.Out.WriteLine($"{Path.GetFileName(output)}: left out, it does not make the tree from the earlier one: {ex.Message}");
}
finally
{
    if (Directory.Exists(proof))
    {
        Directory.Delete(proof, recursive: true);
    }
}

return 0;
