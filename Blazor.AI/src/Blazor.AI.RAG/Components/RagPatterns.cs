using System.Text.RegularExpressions;

namespace Blazor.AI.RAG;

internal static partial class RagPatterns
{
    /// <summary>Citation markers such as [1] or [12].</summary>
    [GeneratedRegex(@"\[(\d{1,2})\]")]
    public static partial Regex Citation();
}
