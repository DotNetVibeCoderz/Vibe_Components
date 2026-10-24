using YamlDotNet.RepresentationModel;

namespace GnuRadio.Grc;

/// <summary>Whether a block in a .grc file takes part in the flowgraph.</summary>
public enum GrcBlockState
{
    Enabled,
    Disabled,
    Bypassed,
}

/// <summary>One block instance of a .grc file.</summary>
public sealed record GrcBlock(string Name, string Id, IReadOnlyDictionary<string, string> Parameters, GrcBlockState State,
    double X = 0, double Y = 0)
{
    public string Parameter(string key, string fallback = "") => Parameters.TryGetValue(key, out var v) ? v : fallback;
}

/// <summary>A connection: ports are numbers for streams and names for message ports.</summary>
public sealed record GrcConnection(string Source, string SourcePort, string Destination, string DestinationPort)
{
    public bool IsMessage => !int.TryParse(SourcePort, out _) || !int.TryParse(DestinationPort, out _);
}

/// <summary>
/// A GNU Radio Companion flowgraph (.grc, the YAML format of GNU Radio 3.8 and later).
/// </summary>
public sealed class GrcDocument
{
    public string Id { get; init; } = "top_block";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<GrcBlock> Blocks { get; init; } = [];
    public IReadOnlyList<GrcConnection> Connections { get; init; } = [];

    public static GrcDocument Load(string path) => Parse(File.ReadAllText(path));

    public static GrcDocument Parse(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml)) stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new FormatException("Not a GRC YAML document.");

        var blocks = new List<GrcBlock>();
        string id = "top_block", title = "", author = "", description = "";
        if (Child(root, "options") is YamlMappingNode options && Child(options, "parameters") is YamlMappingNode op)
        {
            id = Scalar(op, "id") ?? id;
            title = Scalar(op, "title") ?? "";
            author = Scalar(op, "author") ?? "";
            description = Scalar(op, "description") ?? "";
        }
        if (Child(root, "blocks") is YamlSequenceNode seq)
        {
            foreach (var node in seq.Children.OfType<YamlMappingNode>())
            {
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Child(node, "parameters") is YamlMappingNode p)
                    foreach (var (k, v) in p.Children)
                        parameters[((YamlScalarNode)k).Value ?? ""] = v is YamlScalarNode s ? s.Value ?? "" : v.ToString();
                var state = GrcBlockState.Enabled;
                double x = 0, y = 0;
                if (Child(node, "states") is YamlMappingNode states)
                {
                    state = (Scalar(states, "state") ?? "enabled") switch
                    {
                        "disabled" => GrcBlockState.Disabled,
                        "bypassed" => GrcBlockState.Bypassed,
                        _ => GrcBlockState.Enabled,
                    };
                    if (Child(states, "coordinate") is YamlSequenceNode coord && coord.Children.Count == 2)
                    {
                        double.TryParse(((YamlScalarNode)coord.Children[0]).Value, System.Globalization.CultureInfo.InvariantCulture, out x);
                        double.TryParse(((YamlScalarNode)coord.Children[1]).Value, System.Globalization.CultureInfo.InvariantCulture, out y);
                    }
                }
                blocks.Add(new GrcBlock(Scalar(node, "name") ?? "", Scalar(node, "id") ?? "", parameters, state, x, y));
            }
        }

        var connections = new List<GrcConnection>();
        if (Child(root, "connections") is YamlSequenceNode cseq)
        {
            foreach (var c in cseq.Children.OfType<YamlSequenceNode>())
            {
                var v = c.Children.Select(n => ((YamlScalarNode)n).Value ?? "").ToArray();
                if (v.Length == 4) connections.Add(new GrcConnection(v[0], v[1], v[2], v[3]));
            }
        }
        return new GrcDocument { Id = id, Title = title, Author = author, Description = description, Blocks = blocks, Connections = connections };
    }

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Scalar(YamlMappingNode node, string key) => Child(node, key) is YamlScalarNode s ? s.Value : null;
}
