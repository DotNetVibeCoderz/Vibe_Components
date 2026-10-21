using Blazor.AI.Decision;

namespace Blazor.AI.ThreeD;

/// <summary>Sample rule set for <c>RuleGraph3D</c>: an e-commerce loyalty policy with one deliberate conflict.</summary>
public static class RuleGraph3DSamples
{
    public static List<BusinessRule> Rules() =>
    [
        new() { Name = "VIP customer", Condition = "annualSpend >= 10000000 and yearsAsCustomer >= 2", Actions = "tier = 'gold'; discount = 0.15", Priority = 30 },
        new() { Name = "New customer welcome", Condition = "yearsAsCustomer < 1", Actions = "tier = 'new'; discount = 0.05", Priority = 20 },
        new() { Name = "Free shipping", Condition = "orderTotal >= 500000 or tier = 'gold'", Actions = "freeShipping = true", Priority = 10 },
        new() { Name = "Payday promo", Condition = "orderTotal >= 300000", Actions = "discount = 0.1", Priority = 15 },
        new() { Name = "Fraud hold", Condition = "country not in ('ID', 'SG', 'MY') and orderTotal > 20000000", Actions = "hold = true; reason = 'manual fraud check'", Priority = 100, StopOnMatch = true },
    ];
}
