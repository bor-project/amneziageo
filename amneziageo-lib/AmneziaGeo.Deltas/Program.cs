using AmneziaGeo.Deltas;

// Makes the deltas of the assets whose lists of files are given from their versions in the earlier releases given.
var version = default(string);
var output = default(string);
var bases = new List<DeltaBase>();
var lists = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--version" when i + 1 < args.Length:
            version = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            output = args[++i];
            break;
        case "--base" when i + 2 < args.Length:
            bases.Add(new DeltaBase(args[i + 1], args[i + 2]));
            i += 2;
            break;
        default:
            lists.Add(args[i]);
            break;
    }
}

if (version is null || output is null || lists.Count == 0)
{
    Console.Error.WriteLine("usage: AmneziaGeo.Deltas --version <version> --out <folder> [--base <version> <folder or address/>]... <list of files>...");
    return 2;
}

Directory.CreateDirectory(output);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("amneziageo-deltas");
var builder = new DeltaBuilder(http, TimeSpan.FromMinutes(5), Console.Out);
foreach (var list in lists)
{
    await builder.BuildAsync(list, version, bases, output, CancellationToken.None).ConfigureAwait(false);
}

return 0;
