using Blazor.AI.Visualization;

namespace Blazor.AI.Gallery.Services;

public sealed record Product(string Name, string Category, string Description, decimal Price);

/// <summary>Sample content used across the gallery pages.</summary>
public sealed class DemoData
{
    public IReadOnlyList<Product> Products { get; } =
    [
        new("Kopi Gayo Arabica 250g", "Coffee", "Single-origin Aceh arabica with notes of dark chocolate and spice. Medium roast for pour-over.", 95_000),
        new("Kopi Toraja Kalosi 250g", "Coffee", "Earthy, full-bodied Sulawesi beans with low acidity. Great for espresso and kopi susu.", 105_000),
        new("Teh Melati Premium", "Tea", "Jasmine-scented green tea from Central Java, loose leaf, 100 g tin.", 45_000),
        new("Matcha Ceremonial Grade", "Tea", "Stone-ground Japanese matcha for whisking; vibrant green, smooth and umami.", 180_000),
        new("V60 Dripper Set", "Equipment", "Ceramic pour-over dripper with 100 paper filters and a glass server.", 320_000),
        new("Hand Grinder Pro", "Equipment", "Stainless steel burr grinder with 40 click settings, perfect for travel.", 650_000),
        new("Cold Brew Bottle 1L", "Equipment", "Mesh-filter bottle for overnight cold brew coffee or iced tea.", 210_000),
        new("Gula Aren Syrup", "Pantry", "Palm sugar syrup for es kopi susu and desserts, 500 ml.", 55_000),
        new("Oat Milk Barista", "Pantry", "Plant-based milk that steams and foams like dairy, 1 L.", 48_000),
        new("Coffee Cupping Class", "Experience", "Two-hour guided tasting of five Indonesian origins at our Bandung roastery.", 250_000),
        new("Gift Box Nusantara", "Gift", "Three 100 g origins (Gayo, Toraja, Flores) with tasting cards in a batik box.", 275_000),
        new("Reusable Tumbler 450ml", "Merchandise", "Double-wall insulated tumbler that keeps drinks hot for six hours.", 165_000),
    ];

    public IReadOnlyList<string> Categories { get; } =
    [
        "Software & cloud subscriptions", "Hardware & devices", "Travel & accommodation", "Meals & entertainment", "Training & conferences",
        "Office supplies", "Marketing & advertising", "Consulting & contractors", "Utilities & internet", "Vehicle & fuel", "Medical & wellness",
    ];

    public IReadOnlyList<DashboardMetric> Metrics { get; } =
    [
        new("Revenue", 1_284_500_000, "N0", 12.4, "bi-cash-stack") { Trend = [820, 870, 910, 980, 1040, 1100, 1180, 1284] },
        new("Orders", 9_412, "N0", 8.1, "bi-bag-check") { Trend = [7200, 7600, 7900, 8300, 8600, 8800, 9100, 9412] },
        new("Avg basket", 136_480, "N0", 3.9, "bi-basket") { Trend = [121, 124, 126, 128, 129, 131, 133, 136] },
        new("Churn", 4.2, "0.0'%'", 0.8, "bi-person-dash") { Trend = [3.1, 3.3, 3.2, 3.6, 3.8, 3.9, 4.0, 4.2], LowerIsBetter = true },
    ];

    public IReadOnlyList<ChartSeries> MonthlyRevenue { get; } =
    [
        ChartSeries.FromValues("Online", [420, 455, 470, 510, 540, 575, 610, 668, 640, 702, 730, 790]) with { Area = true },
        ChartSeries.FromValues("Stores", [380, 390, 402, 418, 430, 446, 470, 482, 470, 496, 510, 494]),
    ];

    public IReadOnlyList<BarValue> RegionSales { get; } =
    [
        new("Jabodetabek", 512), new("Jawa Barat", 284), new("Jawa Timur", 231), new("Bali & Nusra", 118), new("Sumatera", 96), new("Sulawesi", 44),
    ];

    public IReadOnlyList<GeoPoint> Cities { get; } =
    [
        new(-6.2088, 106.8456, "Jakarta", 512, "Java"), new(-6.9175, 107.6191, "Bandung", 184, "Java"), new(-7.2575, 112.7521, "Surabaya", 176, "Java"),
        new(-7.7956, 110.3695, "Yogyakarta", 61, "Java"), new(-6.9667, 110.4167, "Semarang", 74, "Java"), new(3.5952, 98.6722, "Medan", 66, "Sumatra"),
        new(-2.9761, 104.7754, "Palembang", 30, "Sumatra"), new(-8.6705, 115.2126, "Denpasar", 118, "Bali & Nusa Tenggara"), new(-5.1477, 119.4327, "Makassar", 44, "Sulawesi"),
        new(1.4748, 124.8421, "Manado", 18, "Sulawesi"), new(-1.2379, 116.8529, "Balikpapan", 27, "Kalimantan"), new(-2.5337, 140.7181, "Jayapura", 9, "Papua"),
    ];

    public IReadOnlyList<SankeyLink> Funnel { get; } =
    [
        new("Search ads", "Landing page", 4200), new("Instagram", "Landing page", 3100), new("Referral", "Landing page", 1200),
        new("Landing page", "Product page", 5600), new("Landing page", "Bounced", 2900),
        new("Product page", "Cart", 2300), new("Product page", "Bounced", 3300),
        new("Cart", "Purchase", 1450), new("Cart", "Abandoned", 850),
    ];

    public IReadOnlyList<TreemapItem> Budget { get; } =
    [
        new("Operations", 420) { Children = [new("Salaries", 260), new("Rent", 90), new("Utilities", 70)] },
        new("Marketing", 260) { Children = [new("Digital ads", 150), new("Events", 60), new("Content", 50)] },
        new("Technology", 210) { Children = [new("Cloud", 90), new("AI APIs", 70), new("Licenses", 50)] },
        new("R&D", 140) { Children = [new("Prototypes", 80), new("Research", 60)] },
        new("Training", 60),
    ];

    public (IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges) Network { get; } =
    (
        [
            new("blazor", "Blazor.AI", "Library", 2), new("core", "Core", "Package"), new("components", "Components", "Package"), new("rag", "RAG", "Package"),
            new("agents", "Agents", "Package"), new("decision", "Decision", "Package"), new("analytics", "Analytics", "Package"), new("viz", "Visualization", "Package"),
            new("local", "LocalModels", "Package"), new("meai", "Microsoft.Extensions.AI", "Dependency"), new("vectordata", "VectorData", "Dependency"),
            new("science", "Gravicode.Science", "Dependency"), new("rlnet", "RLNet", "Dependency"), new("typesafe", "TypeSafeSDK", "Dependency"),
            new("hfnet", "HF.Net", "Dependency"), new("visualnet", "Visual.Net", "Dependency"), new("mcp", "Model Context Protocol", "Dependency"),
        ],
        [
            new("blazor", "core"), new("blazor", "components"), new("components", "core"), new("rag", "components"), new("agents", "components"),
            new("decision", "viz"), new("analytics", "viz"), new("viz", "components"), new("local", "viz"), new("core", "meai"), new("core", "vectordata"),
            new("analytics", "science"), new("analytics", "rlnet"), new("decision", "typesafe"), new("local", "hfnet"), new("viz", "visualnet"), new("agents", "mcp"),
            new("rag", "viz"),
        ]
    );

    public string CompanyHandbook { get; } = """
        # Kopi Nusantara — Employee Handbook

        ## Working hours
        Our roastery in Bandung operates Monday to Saturday from 07:00 to 19:00. Office staff work 09:00 to 17:00 with a one-hour lunch break. Remote work is allowed two days per week after the three-month probation period.

        ## Leave
        Every permanent employee receives 14 days of annual leave per year. Leave requests must be submitted at least five working days in advance through the HR portal. Up to five unused days can be carried over to the next year. Sick leave of more than two days requires a doctor's note.

        ## Expenses
        Travel expenses are reimbursed within 10 working days after the claim is approved. Receipts are mandatory for any expense above Rp 100.000. Meals during business travel are covered up to Rp 250.000 per day. Business class flights are only allowed for flights longer than six hours.

        ## Refunds for customers
        Customers can return unopened coffee within 14 days for a full refund. Opened products can be exchanged within 7 days if there is a quality problem. Refunds are processed to the original payment method within 3 to 5 working days.

        ## Security
        Laptops must use full-disk encryption and a screen lock after five minutes. Customer data may only be stored in the company CRM. Report suspected phishing emails to security@kopinusantara.example within one hour.
        """;

    public string SalesCsv { get; } = """
        region,product,month,units,revenue
        Jabodetabek,Kopi Gayo,Jan,320,30400000
        Jabodetabek,Kopi Toraja,Jan,210,22050000
        Jawa Barat,Kopi Gayo,Jan,180,17100000
        Jawa Barat,Teh Melati,Jan,240,10800000
        Jawa Timur,Kopi Toraja,Jan,150,15750000
        Bali,Matcha,Jan,90,16200000
        Jabodetabek,Kopi Gayo,Feb,350,33250000
        Jabodetabek,Matcha,Feb,120,21600000
        Jawa Barat,Kopi Gayo,Feb,200,19000000
        Jawa Timur,Teh Melati,Feb,170,7650000
        Bali,Kopi Toraja,Feb,110,11550000
        Sumatera,Kopi Gayo,Feb,140,13300000
        Jabodetabek,Kopi Toraja,Mar,260,27300000
        Jawa Barat,Matcha,Mar,85,15300000
        Jawa Timur,Kopi Gayo,Mar,190,18050000
        Bali,Matcha,Mar,130,23400000
        Sumatera,Kopi Toraja,Mar,95,9975000
        Sulawesi,Kopi Toraja,Mar,160,16800000
        """;

    public static async IAsyncEnumerable<double> SensorStream([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var random = new Random();
        var t = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var value = 70 + 4 * Math.Sin(t / 12.0) + random.NextDouble() * 1.6;
            if (random.NextDouble() < 0.03)
            {
                value += 10 + random.NextDouble() * 6;
            }

            yield return Math.Round(value, 2);
            t++;
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }
}
