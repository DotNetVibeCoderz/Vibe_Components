using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace GDAL.Net.Gallery.Avalonia.Controls;

/// <summary>Selectable text with lightweight C# / JSON / SQL / XML highlighting.</summary>
public sealed partial class CodeBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> CodeProperty = AvaloniaProperty.Register<CodeBlock, string?>(nameof(Code));
    public static readonly StyledProperty<string> LanguageProperty = AvaloniaProperty.Register<CodeBlock, string>(nameof(Language), "csharp");

    public string? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public string Language { get => GetValue(LanguageProperty); set => SetValue(LanguageProperty, value); }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    private static readonly IBrush Keyword = new SolidColorBrush(Color.Parse("#7FC8F8"));
    private static readonly IBrush Type = new SolidColorBrush(Color.Parse("#8AB17D"));
    private static readonly IBrush Str = new SolidColorBrush(Color.Parse("#F2C94C"));
    private static readonly IBrush Comment = new SolidColorBrush(Color.Parse("#7C8C92"));
    private static readonly IBrush Number = new SolidColorBrush(Color.Parse("#E07A5F"));

    [GeneratedRegex(@"(?<comment>//[^\n]*|--[^\n]*)|(?<str>""""""[\s\S]*?""""""|@?\$?""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*')|(?<num>\b\d[\d_.,]*(?:e[+-]?\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)")]
    private static partial Regex Tokens();

    private static readonly HashSet<string> CsKeywords =
    [
        "using", "var", "new", "foreach", "in", "for", "if", "else", "return", "await", "async", "public", "private", "static", "class",
        "string", "int", "double", "float", "bool", "true", "false", "null", "out", "ref", "is", "not", "and", "or", "switch", "case", "default",
        "SELECT", "FROM", "WHERE", "GROUP", "BY", "ORDER", "DESC", "ASC", "AS", "AND", "OR", "JOIN", "ON", "COUNT", "SUM", "ROUND", "byte", "long",
    ];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty || change.Property == LanguageProperty) Rebuild();
    }

    private void Rebuild()
    {
        var code = Code ?? string.Empty;
        var inlines = new InlineCollection();
        if (code.Length > 20_000 || Language is "text" or "wkt")
        {
            inlines.Add(new Run(code));
            Inlines = inlines;
            return;
        }
        var last = 0;
        foreach (Match m in Tokens().Matches(code))
        {
            if (m.Index > last) inlines.Add(new Run(code[last..m.Index]));
            var run = new Run(m.Value);
            if (m.Groups["comment"].Success) run.Foreground = Comment;
            else if (m.Groups["str"].Success) run.Foreground = Str;
            else if (m.Groups["num"].Success) run.Foreground = Number;
            else if (CsKeywords.Contains(m.Value)) run.Foreground = Keyword;
            else if (char.IsUpper(m.Value[0]) && Language == "csharp") run.Foreground = Type;
            inlines.Add(run);
            last = m.Index + m.Length;
        }
        if (last < code.Length) inlines.Add(new Run(code[last..]));
        Inlines = inlines;
    }
}
