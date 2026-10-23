namespace CesiumNet.Gallery;

/// <summary>Token kinds produced by <see cref="CodeTokenizer"/>.</summary>
public enum TokenKind { Plain, Keyword, Type, String, Comment, Number, Method }

/// <summary>A highlighted span of source code.</summary>
public readonly record struct CodeToken(TokenKind Kind, string Text);

/// <summary>
/// A small, dependency-free C# tokenizer for syntax highlighting in both gallery front-ends.
/// It is intentionally approximate: good enough for readable samples, not a compiler.
/// </summary>
public static class CodeTokenizer
{
    private static readonly HashSet<string> Keywords =
    [
        "var", "new", "await", "async", "return", "if", "else", "for", "foreach", "in", "while", "true", "false", "null",
        "public", "private", "static", "readonly", "const", "class", "record", "struct", "using", "namespace", "void",
        "double", "int", "string", "bool", "float", "long", "this", "is", "not", "and", "or", "with", "switch", "case",
        "break", "continue", "try", "catch", "finally", "throw", "default", "out", "ref", "params", "get", "set", "init",
        "sealed", "override", "virtual", "abstract", "nameof", "typeof", "when", "yield", "do", "object", "char", "byte",
    ];

    public static IEnumerable<CodeToken> Tokenize(string code)
    {
        var i = 0;
        var n = code.Length;
        var plainStart = 0;

        while (i < n)
        {
            var c = code[i];
            int start;
            TokenKind? kind = null;

            if (c == '/' && i + 1 < n && code[i + 1] == '/')
            {
                start = i;
                while (i < n && code[i] != '\n') i++;
                kind = TokenKind.Comment;
            }
            else if (c == '"' || (c == '$' && i + 1 < n && code[i + 1] == '"') || (c == '@' && i + 1 < n && code[i + 1] == '"'))
            {
                start = i;
                if (c != '"') i++;
                i++;
                while (i < n && code[i] != '"' && code[i] != '\n') { if (code[i] == '\\') i++; i++; }
                if (i < n && code[i] == '"') i++;
                kind = TokenKind.String;
            }
            else if (c == '\'' && i + 2 < n)
            {
                start = i;
                i++;
                while (i < n && code[i] != '\'' && code[i] != '\n') { if (code[i] == '\\') i++; i++; }
                if (i < n) i++;
                kind = TokenKind.String;
            }
            else if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(code[i - 1]) && code[i - 1] != '_'))
            {
                start = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_') &&
                       !(code[i] == '.' && i + 1 < n && !char.IsDigit(code[i + 1]))) i++;
                kind = TokenKind.Number;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                start = i;
                while (i < n && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                var word = code[start..i];
                if (Keywords.Contains(word)) kind = TokenKind.Keyword;
                else if (char.IsUpper(word[0]))
                {
                    var j = i;
                    while (j < n && code[j] == ' ') j++;
                    kind = j < n && code[j] == '(' ? TokenKind.Method : TokenKind.Type;
                }
                else continue;
            }
            else
            {
                i++;
                continue;
            }

            if (start > plainStart) yield return new CodeToken(TokenKind.Plain, code[plainStart..start]);
            yield return new CodeToken(kind!.Value, code[start..i]);
            plainStart = i;
        }

        if (plainStart < n) yield return new CodeToken(TokenKind.Plain, code[plainStart..]);
    }
}
