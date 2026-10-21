using Blazor.AI.Decision;

namespace Blazor.AI.Tests;

public class DecisionTests
{
    private static Dictionary<string, object?> Facts(params (string, object?)[] values) =>
        values.ToDictionary(v => v.Item1, v => v.Item2, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("age >= 18 and country in ('ID', 'MY')", true)]
    [InlineData("age < 18 or country = 'SG'", false)]
    [InlineData("not (income / 12 < 1000)", true)]
    public void Expressions_evaluate(string source, bool expected) =>
        Assert.Equal(expected, Expression.Parse(source).EvaluateBool(Facts(("age", 30), ("country", "ID"), ("income", 60000))));

    [Fact]
    public void Expression_reports_syntax_errors_and_never_executes_code()
    {
        Assert.False(Expression.TryParse("age >= (18", out _, out var error));
        Assert.NotNull(error);

        // The language is interpreted (no reflection), so a .NET call is either a parse error or fails at evaluation.
        var path = Path.Combine(Path.GetTempPath(), $"bai-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "keep");
        if (Expression.TryParse($"System.IO.File.Delete('{path.Replace("\\", "/")}')", out var expr, out _))
        {
            Assert.ThrowsAny<Exception>(() => expr!.Evaluate(Facts()));
        }

        Assert.True(File.Exists(path));
        File.Delete(path);
    }

    [Fact]
    public void Rules_fire_in_priority_order_and_chain()
    {
        BusinessRule[] rules =
        [
            new() { Name = "Base", Condition = "true", Actions = "discount = 0.05", Priority = 10 },
            new() { Name = "Loyal", Condition = "years >= 3", Actions = "discount = discount + 0.05", Priority = 5 },
            new() { Name = "Never", Condition = "years > 100", Actions = "discount = 1" },
        ];

        var result = new RuleEngine().Evaluate(rules, Facts(("years", 4)));

        Assert.Equal(0.1, Convert.ToDouble(result["discount"]), 6);
        Assert.Equal(["Base", "Loyal"], result.Fired.Select(r => r.Name));
    }

    [Theory]
    [InlineData(500, 20, "permanent", "Decline")]
    [InlineData(780, 25, "permanent", "Approve")]
    [InlineData(700, 35, "contract", "Approve")]
    [InlineData(620, 35, "contract", "Manual review")]
    public void Loan_table_first_hit(int score, int dti, string employment, string decision)
    {
        var result = DecisionTable.LoanSample().Evaluate(Facts(("creditScore", score), ("dti", dti), ("employment", employment)));
        Assert.Null(result.Error);
        Assert.Equal(decision, result.Outputs[0]["decision"]?.ToString());
    }
}
