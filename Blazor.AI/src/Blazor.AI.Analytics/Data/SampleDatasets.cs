using Gravicode.Science.GraviFrame;
using Gravicode.Science.GraviLearn;

namespace Blazor.AI.Analytics;

/// <summary>Deterministic synthetic datasets with realistic structure, so every workbench has data out of the box.</summary>
public static class SampleDatasets
{
    public static IReadOnlyList<(string Name, Func<AnalyticsDataset> Create)> All { get; } =
    [
        ("Customer churn", CustomerChurn),
        ("House prices", HousePrices),
        ("Customer segments", Segments),
        ("Crop yield (agritech)", CropYield),
        ("Monthly sales", MonthlySales),
        ("Machine sensor", MachineSensor),
    ];

    public static AnalyticsDataset CustomerChurn()
    {
        var rng = new Random(42);
        const int n = 600;
        double[] tenure = new double[n], charges = new double[n], tickets = new double[n], monthly = new double[n], usage = new double[n], churn = new double[n];
        for (var i = 0; i < n; i++)
        {
            monthly[i] = rng.NextDouble() < 0.45 ? 1 : 0;
            tenure[i] = Math.Round(monthly[i] == 1 ? rng.NextDouble() * 24 : 6 + rng.NextDouble() * 60);
            charges[i] = RoundTo(50_000 + rng.NextDouble() * 150_000, 1_000);
            tickets[i] = Math.Floor(-Math.Log(1 - rng.NextDouble()) * 1.4);
            usage[i] = Math.Round(5 + rng.NextDouble() * 95, 1);
            var logit = -1.2 + 1.6 * monthly[i] - 0.05 * tenure[i] + 0.000012 * charges[i] + 0.45 * tickets[i] - 0.012 * usage[i];
            churn[i] = rng.NextDouble() < 1 / (1 + Math.Exp(-logit)) ? 1 : 0;
        }

        var frame = DataFrame.FromColumns(new Dictionary<string, double[]>
        {
            ["tenureMonths"] = tenure,
            ["monthlyCharges"] = charges,
            ["supportTickets"] = tickets,
            ["monthToMonth"] = monthly,
            ["usageGb"] = usage,
            ["churn"] = churn,
        });
        return new AnalyticsDataset("Customer churn", frame, "churn", MlTask.Classification, "Telecom customers; predict who will cancel (churn = 1).");
    }

    public static AnalyticsDataset HousePrices()
    {
        var rng = new Random(7);
        const int n = 400;
        double[] area = new double[n], beds = new double[n], age = new double[n], distance = new double[n], garden = new double[n], price = new double[n];
        for (var i = 0; i < n; i++)
        {
            area[i] = Math.Round(40 + rng.NextDouble() * 260);
            beds[i] = Math.Clamp(Math.Round(area[i] / 45 + rng.NextDouble() - 0.5), 1, 6);
            age[i] = Math.Round(rng.NextDouble() * 40);
            distance[i] = Math.Round(1 + rng.NextDouble() * 35, 1);
            garden[i] = rng.NextDouble() < 0.4 ? 1 : 0;
            price[i] = RoundTo((180 + 9.5 * area[i] + 60 * beds[i] - 6 * age[i] - 14 * distance[i] + 120 * garden[i] + Gaussian(rng) * 160) * 1_000_000, 1_000_000);
        }

        var frame = DataFrame.FromColumns(new Dictionary<string, double[]>
        {
            ["areaM2"] = area,
            ["bedrooms"] = beds,
            ["ageYears"] = age,
            ["distanceToCbdKm"] = distance,
            ["hasGarden"] = garden,
            ["price"] = price,
        });
        return new AnalyticsDataset("House prices", frame, "price", MlTask.Regression, "Synthetic homes in a metro area; predict the price (Rp).");
    }

    public static AnalyticsDataset Segments()
    {
        var blobs = Datasets.MakeBlobs(samples: 450, features: 4, centers: 4, spread: 1.1, seed: 3);
        var x = blobs.Features;
        var columns = new Dictionary<string, double[]>
        {
            ["visitsPerMonth"] = Scale(x.Column(0).ToArray(), 6, 2.5),
            ["avgBasket"] = Scale(x.Column(1).ToArray(), 250_000, 60_000),
            ["discountShare"] = Scale(x.Column(2).ToArray(), 0.25, 0.08),
            ["appSessions"] = Scale(x.Column(3).ToArray(), 20, 7),
        };
        return new AnalyticsDataset("Customer segments", DataFrame.FromColumns(columns), null, MlTask.Clustering, "Shoppers with four behaviour features; find natural segments.");
    }

    public static AnalyticsDataset CropYield()
    {
        var rng = new Random(11);
        const int n = 360;
        double[] rain = new double[n], fert = new double[n], soilPh = new double[n], temp = new double[n], irrigated = new double[n], yield = new double[n];
        for (var i = 0; i < n; i++)
        {
            rain[i] = Math.Round(800 + rng.NextDouble() * 1600);
            fert[i] = Math.Round(50 + rng.NextDouble() * 250);
            soilPh[i] = Math.Round(4.8 + rng.NextDouble() * 2.8, 1);
            temp[i] = Math.Round(23 + rng.NextDouble() * 9, 1);
            irrigated[i] = rng.NextDouble() < 0.5 ? 1 : 0;
            var phEffect = -0.8 * Math.Pow(soilPh[i] - 6.3, 2);
            yield[i] = Math.Round(3.2 + 0.0012 * rain[i] + 0.006 * fert[i] + phEffect - 0.06 * Math.Pow(temp[i] - 27, 2) + 0.9 * irrigated[i] + Gaussian(rng) * 0.45, 2);
        }

        var frame = DataFrame.FromColumns(new Dictionary<string, double[]>
        {
            ["rainfallMm"] = rain,
            ["fertilizerKgHa"] = fert,
            ["soilPh"] = soilPh,
            ["avgTempC"] = temp,
            ["irrigated"] = irrigated,
            ["yieldTonHa"] = yield,
        });
        return new AnalyticsDataset("Crop yield (agritech)", frame, "yieldTonHa", MlTask.Regression, "Rice paddies; predict yield (ton/ha) from weather, soil and inputs.");
    }

    public static AnalyticsDataset MonthlySales()
    {
        var rng = new Random(5);
        const int n = 60;
        double[] month = new double[n], sales = new double[n], promo = new double[n];
        for (var i = 0; i < n; i++)
        {
            month[i] = i + 1;
            promo[i] = rng.NextDouble() < 0.2 ? 1 : 0;
            var seasonal = 18 * Math.Sin(2 * Math.PI * (i % 12) / 12) + (i % 12 == 11 ? 30 : 0); // December peak
            sales[i] = Math.Round(220 + 2.6 * i + seasonal + 25 * promo[i] + Gaussian(rng) * 9, 1);
        }

        var frame = DataFrame.FromColumns(new Dictionary<string, double[]> { ["month"] = month, ["sales"] = sales, ["promo"] = promo });
        return new AnalyticsDataset("Monthly sales", frame, "sales", MlTask.TimeSeries, "Five years of monthly sales (million Rp) with trend, seasonality and promotions.");
    }

    public static AnalyticsDataset MachineSensor()
    {
        var rng = new Random(9);
        const int n = 400;
        double[] t = new double[n], temperature = new double[n], vibration = new double[n];
        for (var i = 0; i < n; i++)
        {
            t[i] = i;
            temperature[i] = Math.Round(68 + 3 * Math.Sin(i / 25.0) + Gaussian(rng) * 0.8, 2);
            vibration[i] = Math.Round(0.42 + 0.05 * Math.Sin(i / 9.0) + Gaussian(rng) * 0.02, 3);
        }

        foreach (var spike in new[] { 87, 188, 189, 301, 352 })
        {
            temperature[spike] += 9 + rng.NextDouble() * 4;
            vibration[spike] += 0.25;
        }

        var frame = DataFrame.FromColumns(new Dictionary<string, double[]> { ["t"] = t, ["temperatureC"] = temperature, ["vibrationG"] = vibration });
        return new AnalyticsDataset("Machine sensor", frame, "temperatureC", MlTask.TimeSeries, "Factory motor telemetry with a few injected faults; find the anomalies.");
    }

    /// <summary>A small user × item ratings matrix (1-5, 0 = unrated) for recommendation demos.</summary>
    public static (string[] Users, string[] Items, double[,] Ratings) Ratings()
    {
        string[] users = ["Ayu", "Bima", "Citra", "Dewi", "Eko", "Fajar", "Gita", "Hadi"];
        string[] items = ["Kopi Gayo", "Kopi Toraja", "Teh Melati", "Matcha Latte", "Cokelat Panas", "Es Kopi Susu", "Teh Tarik", "Wedang Jahe"];
        var ratings = new double[,]
        {
            { 5, 4, 0, 1, 0, 5, 0, 2 },
            { 4, 5, 1, 0, 2, 4, 0, 0 },
            { 0, 1, 5, 4, 3, 0, 4, 5 },
            { 1, 0, 4, 5, 4, 1, 5, 0 },
            { 5, 5, 0, 0, 1, 0, 2, 1 },
            { 0, 2, 5, 0, 4, 1, 4, 5 },
            { 4, 0, 2, 2, 5, 4, 0, 3 },
            { 2, 1, 4, 5, 0, 0, 5, 4 },
        };
        return (users, items, ratings);
    }

    public static DataFrame ParseCsv(string csv) => DataFrame.ParseCsv(csv);

    private static double[] Scale(double[] values, double mean, double std)
    {
        var m = values.Average();
        var s = Math.Sqrt(values.Average(v => (v - m) * (v - m)));
        return values.Select(v => Math.Round(mean + (v - m) / (s == 0 ? 1 : s) * std, 3)).ToArray();
    }

    private static double RoundTo(double value, double step) => Math.Round(value / step) * step;

    internal static double Gaussian(Random rng)
    {
        var u1 = 1 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
