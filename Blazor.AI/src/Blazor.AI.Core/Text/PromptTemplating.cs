using System.Text;
using System.Text.RegularExpressions;

namespace Blazor.AI.Core.Text;

/// <summary>Renders <c>{{variable}}</c> templates and lists their variables.</summary>
public static partial class PromptTemplating
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_\.\-]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();

    public static IReadOnlyList<string> Variables(string template) =>
        VariablePattern().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        VariablePattern().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var value) && value is not null ? value : m.Value);

    /// <summary>Renders key/value pairs as a compact bullet list for prompts.</summary>
    public static string Bullets(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                builder.Append("- ").Append(key).Append(": ").AppendLine(value);
            }
        }

        return builder.ToString();
    }
}
