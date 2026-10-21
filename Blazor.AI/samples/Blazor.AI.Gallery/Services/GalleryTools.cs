using System.ComponentModel;

namespace Blazor.AI.Gallery.Services;

/// <summary>Demo tools registered for agents. Real apps would call their own services here.</summary>
public static class GalleryTools
{
    private static readonly Dictionary<string, (string Status, string Eta)> Orders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INV-1001"] = ("Shipped via JNE", "arrives tomorrow"),
        ["INV-1002"] = ("Being packed", "ships today"),
        ["INV-1003"] = ("Delivered", "delivered on Monday"),
    };

    public static string LookupOrder([Description("Order number, e.g. INV-1001")] string orderNumber) =>
        Orders.TryGetValue(orderNumber.Trim(), out var o) ? $"{orderNumber}: {o.Status}, {o.Eta}." : $"No order found with number {orderNumber}.";

    public static string ExchangeRate([Description("Source currency, e.g. USD")] string from, [Description("Target currency, e.g. IDR")] string to)
    {
        var perUsd = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["USD"] = 1, ["IDR"] = 16_250, ["EUR"] = 0.92, ["SGD"] = 1.34, ["JPY"] = 151, ["MYR"] = 4.7 };
        return perUsd.TryGetValue(from, out var a) && perUsd.TryGetValue(to, out var b)
            ? $"1 {from.ToUpperInvariant()} ≈ {b / a:N2} {to.ToUpperInvariant()} (indicative demo rate)"
            : "Unsupported currency. Supported: USD, IDR, EUR, SGD, JPY, MYR.";
    }
}
