using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Agents;

/// <summary>A tool registered for agents, with its source (built-in, app, MCP server).</summary>
public sealed record ToolInfo(AIFunction Function, string Category, string Source = "built-in")
{
    public string Name => Function.Name;

    public string Description => Function.Description;
}

/// <summary>Named tools agents can use. Register app tools with <see cref="Register(AIFunction, string, string)"/> or <see cref="Register(Delegate, string, string?, string)"/>.</summary>
public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ToolInfo> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry()
    {
        RegisterBuiltIns();
    }

    public IReadOnlyList<ToolInfo> Tools => _tools.Values.OrderBy(t => t.Category).ThenBy(t => t.Name).ToArray();

    public event Action? Changed;

    public ToolInfo? Get(string name) => _tools.GetValueOrDefault(name);

    public void Register(AIFunction function, string category = "App", string source = "app")
    {
        _tools[function.Name] = new ToolInfo(function, category, source);
        Changed?.Invoke();
    }

    public void Register(Delegate method, string name, string? description = null, string category = "App") =>
        Register(AIFunctionFactory.Create(method, name, description), category);

    public bool Remove(string name)
    {
        var removed = _tools.TryRemove(name, out _);
        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    public void RemoveSource(string source)
    {
        foreach (var tool in _tools.Values.Where(t => t.Source == source).ToList())
        {
            _tools.TryRemove(tool.Name, out _);
        }

        Changed?.Invoke();
    }

    public IList<AITool> Resolve(IEnumerable<string> names) =>
        names.Select(n => _tools.GetValueOrDefault(n)?.Function).Where(f => f is not null).Cast<AITool>().ToList();

    private void RegisterBuiltIns()
    {
        Add(AIFunctionFactory.Create(
            ([Description("Arithmetic expression, e.g. (12.5 * 4) / 3 or sqrt(2) ^ 2")] string expression) =>
                Calculator.Evaluate(expression).ToString("0.##########", CultureInfo.InvariantCulture),
            "calculate",
            "Evaluates an arithmetic expression exactly. Use it for any calculation instead of mental math."), "Math");

        Add(AIFunctionFactory.Create(
            ([Description("IANA or Windows time zone id, e.g. Asia/Jakarta. Empty for UTC.")] string? timeZone) =>
            {
                var zone = string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Utc : FindZone(timeZone);
                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
                return $"{now:yyyy-MM-dd HH:mm:ss} ({zone.Id}, {now:dddd})";
            },
            "get_current_time",
            "Returns the current date and time in a time zone."), "Time");

        Add(AIFunctionFactory.Create(
            ([Description("Start date yyyy-MM-dd")] string from, [Description("End date yyyy-MM-dd")] string to) =>
            {
                var a = DateTime.Parse(from, CultureInfo.InvariantCulture);
                var b = DateTime.Parse(to, CultureInfo.InvariantCulture);
                var businessDays = Enumerable.Range(0, Math.Max(0, (b - a).Days)).Select(d => a.AddDays(d)).Count(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
                return $"{(b - a).Days} calendar days, {businessDays} business days";
            },
            "days_between",
            "Counts calendar and business days between two dates."), "Time");

        Add(AIFunctionFactory.Create(
            ([Description("Value to convert")] double value, [Description("Source unit, e.g. km, mi, kg, lb, c, f, m, ft, l, gal")] string from, [Description("Target unit")] string to) =>
                UnitConverter.Convert(value, from, to),
            "convert_units",
            "Converts between common units of length, mass, temperature and volume."), "Math");

        Add(AIFunctionFactory.Create(
            ([Description("Text to analyse")] string text) =>
            {
                var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var sentences = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries).Length;
                return $"{words.Length} words, {text.Length} characters, {sentences} sentences, ~{Math.Max(1, words.Length / 200.0):0.#} min read";
            },
            "text_statistics",
            "Counts words, characters and sentences and estimates reading time."), "Text");

        Add(AIFunctionFactory.Create(
            ([Description("Principal amount")] double principal, [Description("Annual interest rate in percent")] double annualRatePercent, [Description("Term in months")] int months) =>
            {
                var r = annualRatePercent / 100 / 12;
                var payment = r == 0 ? principal / months : principal * r / (1 - Math.Pow(1 + r, -months));
                return $"Monthly payment {payment:0.00}; total paid {payment * months:0.00}; total interest {payment * months - principal:0.00}";
            },
            "loan_payment",
            "Computes the monthly payment of an amortising loan."), "Finance");
    }

    private void Add(AIFunction function, string category) => _tools[function.Name] = new ToolInfo(function, category);

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

internal static class UnitConverter
{
    private static readonly Dictionary<string, (string Dimension, double Factor)> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = ("length", 1), ["km"] = ("length", 1000), ["cm"] = ("length", 0.01), ["mm"] = ("length", 0.001),
        ["mi"] = ("length", 1609.344), ["ft"] = ("length", 0.3048), ["in"] = ("length", 0.0254), ["yd"] = ("length", 0.9144),
        ["kg"] = ("mass", 1), ["g"] = ("mass", 0.001), ["lb"] = ("mass", 0.45359237), ["oz"] = ("mass", 0.028349523125), ["t"] = ("mass", 1000),
        ["l"] = ("volume", 1), ["ml"] = ("volume", 0.001), ["gal"] = ("volume", 3.785411784), ["m3"] = ("volume", 1000),
    };

    public static string Convert(double value, string from, string to)
    {
        from = from.Trim().TrimStart('°');
        to = to.Trim().TrimStart('°');
        if (IsTemperature(from) && IsTemperature(to))
        {
            var celsius = char.ToLowerInvariant(from[0]) switch { 'f' => (value - 32) * 5 / 9, 'k' => value - 273.15, _ => value };
            var result = char.ToLowerInvariant(to[0]) switch { 'f' => celsius * 9 / 5 + 32, 'k' => celsius + 273.15, _ => celsius };
            return $"{value} {from} = {result:0.####} {to}";
        }

        if (!Units.TryGetValue(from, out var a) || !Units.TryGetValue(to, out var b) || a.Dimension != b.Dimension)
        {
            return $"Cannot convert {from} to {to}.";
        }

        return $"{value} {from} = {value * a.Factor / b.Factor:0.####} {to}";
    }

    private static bool IsTemperature(string unit) => unit.ToLowerInvariant() is "c" or "f" or "k" or "celsius" or "fahrenheit" or "kelvin";
}
