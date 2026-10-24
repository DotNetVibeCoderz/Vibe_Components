using System.Globalization;
using System.Text;

namespace GnuRadio.Grc;

/// <summary>A .grc file turned into a runnable flowgraph.</summary>
public sealed class GrcFlowgraph
{
    internal GrcFlowgraph(GrcDocument document, Flowgraph flowgraph, IReadOnlyDictionary<string, Block> blocks,
        IReadOnlyDictionary<string, object?> variables, IReadOnlyList<string> warnings)
    {
        Document = document;
        Flowgraph = flowgraph;
        Blocks = blocks;
        Variables = variables;
        Warnings = warnings;
    }

    public GrcDocument Document { get; }
    public Flowgraph Flowgraph { get; }

    /// <summary>Created blocks by their GRC instance name (for example <c>qtgui_freq_sink_x_0</c>).</summary>
    public IReadOnlyDictionary<string, Block> Blocks { get; }

    /// <summary>Evaluated variables (<c>samp_rate</c>, slider values…).</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; }

    /// <summary>Blocks or connections that were skipped, with the reason.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public T Block<T>(string name) where T : Block => (T)Blocks[name];

    /// <summary>Loads a .grc file. <paramref name="overrides"/> replace variable values (like GRC parameters on the command line).</summary>
    public static GrcFlowgraph Load(string path, IReadOnlyDictionary<string, object?>? overrides = null) =>
        GrcImporter.Import(GrcDocument.Load(path), overrides);
}

/// <summary>Builds GnuRadio.Net flowgraphs from <see cref="GrcDocument"/>s using <see cref="GrcBlockLibrary"/>.</summary>
public static class GrcImporter
{
    public static GrcFlowgraph Import(GrcDocument document, IReadOnlyDictionary<string, object?>? overrides = null)
    {
        var warnings = new List<string>();
        var (scope, _) = EvaluateVariables(document, overrides, warnings);

        var created = new Dictionary<string, Block>(StringComparer.Ordinal);
        foreach (var b in document.Blocks)
        {
            if (b.State == GrcBlockState.Disabled || IsNonProcessing(b.Id) || b.State == GrcBlockState.Bypassed) continue;
            if (!GrcBlockLibrary.TryGet(b.Id, out var mapping))
            {
                warnings.Add($"{b.Name}: block '{b.Id}' is not supported and was skipped.");
                continue;
            }
            try
            {
                var block = mapping.Create(new GrcBlockContext(b, scope));
                block.Alias = b.Name;
                created[b.Name] = block;
            }
            catch (Exception e) when (e is NotSupportedException or FormatException or KeyNotFoundException or InvalidCastException or ArgumentException)
            {
                warnings.Add($"{b.Name}: {e.Message}");
            }
        }

        var fg = new Flowgraph(document.Id);
        foreach (var c in ResolveConnections(document))
        {
            if (!created.TryGetValue(c.Source, out var src) || !created.TryGetValue(c.Destination, out var dst))
            {
                warnings.Add($"Connection {c.Source}:{c.SourcePort} → {c.Destination}:{c.DestinationPort} skipped (endpoint missing).");
                continue;
            }
            try
            {
                if (c.IsMessage)
                    fg.ConnectMessage(src, c.SourcePort, dst, c.DestinationPort);
                else
                    fg.Connect(src, int.Parse(c.SourcePort, CultureInfo.InvariantCulture), dst, int.Parse(c.DestinationPort, CultureInfo.InvariantCulture));
            }
            catch (ArgumentException e)
            {
                warnings.Add($"Connection {c.Source} → {c.Destination}: {e.Message}");
            }
        }
        foreach (var b in created.Values.Where(b => !b.HasStreamPorts)) fg.Add(b);
        return new GrcFlowgraph(document, fg, created, scope, warnings);
    }

    internal static bool IsNonProcessing(string id) => GrcBlockLibrary.IgnoredIds.Contains(id) || GrcBlockLibrary.VariableIds.ContainsKey(id);

    /// <summary>Evaluates variables in dependency order. Returns the scope and the order they resolved in.</summary>
    internal static (Dictionary<string, object?> Scope, List<GrcBlock> Order) EvaluateVariables(GrcDocument document,
        IReadOnlyDictionary<string, object?>? overrides, List<string> warnings)
    {
        var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
        var order = new List<GrcBlock>();
        var pending = document.Blocks.Where(b => b.State != GrcBlockState.Disabled && GrcBlockLibrary.VariableIds.ContainsKey(b.Id)).ToList();
        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var v in pending.ToArray())
            {
                if (overrides is not null && overrides.TryGetValue(v.Name, out var o))
                {
                    scope[v.Name] = o;
                }
                else
                {
                    try
                    {
                        var raw = v.Parameter(GrcBlockLibrary.VariableIds[v.Id], "0");
                        scope[v.Name] = GrcExpression.Parse(string.IsNullOrWhiteSpace(raw) ? "0" : raw).Evaluate(scope);
                    }
                    catch (KeyNotFoundException)
                    {
                        continue; // depends on a variable not evaluated yet
                    }
                    catch (FormatException e)
                    {
                        scope[v.Name] = v.Parameter(GrcBlockLibrary.VariableIds[v.Id]);
                        warnings.Add($"{v.Name}: kept as text ({e.Message}).");
                    }
                }
                order.Add(v);
                pending.Remove(v);
                progress = true;
            }
        }
        foreach (var v in pending) warnings.Add($"{v.Name}: could not be evaluated (unknown or circular references).");
        return (scope, order);
    }

    /// <summary>Connections with disabled blocks removed and bypassed blocks wired through.</summary>
    internal static IEnumerable<GrcConnection> ResolveConnections(GrcDocument document)
    {
        var states = document.Blocks.ToDictionary(b => b.Name, b => b.State, StringComparer.Ordinal);
        var connections = document.Connections.Where(c =>
            states.GetValueOrDefault(c.Source) != GrcBlockState.Disabled && states.GetValueOrDefault(c.Destination) != GrcBlockState.Disabled).ToList();

        foreach (var c in connections)
        {
            if (states.GetValueOrDefault(c.Destination) == GrcBlockState.Bypassed) continue;
            var source = c.Source;
            var port = c.SourcePort;
            // Walk back through bypassed blocks to the real producer.
            var guard = 0;
            while (states.GetValueOrDefault(source) == GrcBlockState.Bypassed && guard++ < 64)
            {
                var upstream = connections.FirstOrDefault(u => u.Destination == source && !u.IsMessage);
                if (upstream is null) break;
                source = upstream.Source;
                port = upstream.SourcePort;
            }
            if (states.GetValueOrDefault(source) == GrcBlockState.Bypassed) continue;
            yield return c with { Source = source, SourcePort = port };
        }
    }
}

/// <summary>
/// Generates a C# program equivalent to a .grc file — the GnuRadio.Net counterpart of GRC's Python code
/// generation. Unsupported blocks become TODO comments so the output always compiles once they are removed.
/// </summary>
public static class GrcCodeGenerator
{
    public static string Generate(GrcDocument document, bool runForever = false)
    {
        var warnings = new List<string>();
        var (scope, order) = GrcImporter.EvaluateVariables(document, null, warnings);
        var sb = new StringBuilder();
        sb.AppendLine($"// Generated by GnuRadio.Net.Grc from \"{(string.IsNullOrEmpty(document.Title) ? document.Id : document.Title)}\".");
        sb.AppendLine("// GnuRadio.Net — Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.");
        sb.AppendLine("using GnuRadio;");
        foreach (var ns in new[] { "Analog", "Blocks", "Channels", "Digital", "Fft", "Filter", "Sinks", "Synchronization" })
            sb.AppendLine($"using GnuRadio.{ns};");
        sb.AppendLine();

        foreach (var v in order)
        {
            var raw = v.Parameter(GrcBlockLibrary.VariableIds[v.Id], "0");
            string expr;
            try
            {
                expr = GrcExpression.Parse(string.IsNullOrWhiteSpace(raw) ? "0" : raw).ToCSharp();
            }
            catch (FormatException)
            {
                expr = "\"" + raw.Replace("\"", "\\\"") + "\"";
            }
            sb.AppendLine($"var {GrcExpression.CSharpName(v.Name)} = {expr};");
        }
        sb.AppendLine();
        sb.AppendLine($"var fg = new Flowgraph(\"{document.Id}\");");

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in document.Blocks)
        {
            if (b.State != GrcBlockState.Enabled || GrcImporter.IsNonProcessing(b.Id)) continue;
            if (!GrcBlockLibrary.TryGet(b.Id, out var mapping))
            {
                sb.AppendLine($"// TODO: '{b.Id}' ({b.Name}) has no GnuRadio.Net equivalent yet.");
                continue;
            }
            sb.AppendLine($"var {GrcExpression.CSharpName(b.Name)} = {mapping.Code(new GrcBlockContext(b, scope))};");
            emitted.Add(b.Name);
        }
        sb.AppendLine();

        foreach (var c in GrcImporter.ResolveConnections(document))
        {
            if (!emitted.Contains(c.Source) || !emitted.Contains(c.Destination)) continue;
            var src = GrcExpression.CSharpName(c.Source);
            var dst = GrcExpression.CSharpName(c.Destination);
            sb.AppendLine(c.IsMessage
                ? $"fg.ConnectMessage({src}, \"{c.SourcePort}\", {dst}, \"{c.DestinationPort}\");"
                : $"fg.Connect({src}, {c.SourcePort}, {dst}, {c.DestinationPort});");
        }
        sb.AppendLine();
        if (runForever)
        {
            sb.AppendLine("fg.Start();");
            sb.AppendLine("Console.WriteLine(\"Running. Press Enter to stop.\");");
            sb.AppendLine("Console.ReadLine();");
            sb.AppendLine("fg.Stop();");
        }
        else
        {
            sb.AppendLine("fg.Run();");
        }
        return sb.ToString();
    }
}
