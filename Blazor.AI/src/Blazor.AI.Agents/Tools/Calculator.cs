using System.Globalization;

namespace Blazor.AI.Agents;

/// <summary>
/// A small, safe arithmetic evaluator (recursive descent): + - * / % ^, parentheses, unary minus, and
/// sqrt, abs, round, floor, ceil, min, max, log, ln, exp, sin, cos, tan, pi, e. No reflection, no code execution.
/// </summary>
public static class Calculator
{
    public static double Evaluate(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var parser = new Parser(expression.Replace(',', '.').Replace('×', '*').Replace('÷', '/'));
        var value = parser.ParseExpression();
        parser.SkipSpaces();
        if (!parser.AtEnd)
        {
            throw new FormatException($"Unexpected '{parser.Current}' at position {parser.Position + 1}.");
        }

        return value;
    }

    private ref struct Parser(string text)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private int _pos;

        public readonly bool AtEnd => _pos >= _text.Length;

        public readonly char Current => _text[_pos];

        public readonly int Position => _pos;

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                _pos++;
            }
        }

        public double ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (Match('+')) left += ParseTerm();
                else if (Match('-')) left -= ParseTerm();
                else return left;
            }
        }

        private double ParseTerm()
        {
            var left = ParsePower();
            while (true)
            {
                SkipSpaces();
                if (Match('*')) left *= ParsePower();
                else if (Match('/'))
                {
                    var right = ParsePower();
                    left = right == 0 ? throw new DivideByZeroException("Division by zero.") : left / right;
                }
                else if (Match('%')) left %= ParsePower();
                else return left;
            }
        }

        private double ParsePower()
        {
            var value = ParseUnary();
            SkipSpaces();
            return Match('^') ? Math.Pow(value, ParsePower()) : value;
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (Match('-')) return -ParseUnary();
            if (Match('+')) return ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (Match('('))
            {
                var inner = ParseExpression();
                Expect(')');
                return inner;
            }

            if (!AtEnd && (char.IsAsciiDigit(Current) || Current == '.'))
            {
                var start = _pos;
                while (!AtEnd && (char.IsAsciiDigit(Current) || Current == '.' || Current is 'e' or 'E' && _pos + 1 < _text.Length && (char.IsAsciiDigit(_text[_pos + 1]) || _text[_pos + 1] is '-' or '+')))
                {
                    if (Current is 'e' or 'E')
                    {
                        _pos++;
                    }

                    _pos++;
                }

                return double.Parse(_text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            if (!AtEnd && char.IsAsciiLetter(Current))
            {
                var start = _pos;
                while (!AtEnd && char.IsAsciiLetterOrDigit(Current))
                {
                    _pos++;
                }

                var name = _text[start.._pos].ToString().ToLowerInvariant();
                switch (name)
                {
                    case "pi": return Math.PI;
                    case "e": return Math.E;
                }

                SkipSpaces();
                Expect('(');
                var args = new List<double> { ParseExpression() };
                SkipSpaces();
                while (Match(','))
                {
                    args.Add(ParseExpression());
                }

                Expect(')');
                return name switch
                {
                    "sqrt" => Math.Sqrt(args[0]),
                    "abs" => Math.Abs(args[0]),
                    "round" => args.Count > 1 ? Math.Round(args[0], (int)args[1]) : Math.Round(args[0]),
                    "floor" => Math.Floor(args[0]),
                    "ceil" => Math.Ceiling(args[0]),
                    "min" => args.Min(),
                    "max" => args.Max(),
                    "log" => args.Count > 1 ? Math.Log(args[0], args[1]) : Math.Log10(args[0]),
                    "ln" => Math.Log(args[0]),
                    "exp" => Math.Exp(args[0]),
                    "sin" => Math.Sin(args[0]),
                    "cos" => Math.Cos(args[0]),
                    "tan" => Math.Tan(args[0]),
                    "pow" => Math.Pow(args[0], args[1]),
                    _ => throw new FormatException($"Unknown function '{name}'."),
                };
            }

            throw new FormatException(AtEnd ? "Unexpected end of expression." : $"Unexpected '{Current}' at position {_pos + 1}.");
        }

        private bool Match(char c)
        {
            SkipSpaces();
            if (!AtEnd && Current == c)
            {
                _pos++;
                return true;
            }

            return false;
        }

        private void Expect(char c)
        {
            if (!Match(c))
            {
                throw new FormatException($"Expected '{c}' at position {_pos + 1}.");
            }
        }
    }
}
