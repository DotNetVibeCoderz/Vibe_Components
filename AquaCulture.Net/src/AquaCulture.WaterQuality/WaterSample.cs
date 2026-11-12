namespace AquaCulture.WaterQuality;

/// <summary>The fifteen monitored water-quality parameters.</summary>
public enum WaterParameter
{
    Temperature, Ph, DissolvedOxygen, Orp, Salinity, Tds, Tss, Alkalinity,
    Ammonia, Nitrite, Nitrate, Phosphate, Bod, Cod, Turbidity,
}

/// <summary>Display metadata for a parameter.</summary>
public sealed record ParameterInfo(WaterParameter Parameter, string Code, Core.Text2 Name, string Unit, double Weight);

public static class WaterParameters
{
    /// <summary>Metadata and WQI weights (weights sum to 1).</summary>
    public static readonly IReadOnlyDictionary<WaterParameter, ParameterInfo> Info = new Dictionary<WaterParameter, ParameterInfo>
    {
        [WaterParameter.Temperature] = new(WaterParameter.Temperature, "TEMP", new("Temperature", "Suhu"), "°C", 0.12),
        [WaterParameter.Ph] = new(WaterParameter.Ph, "PH", new("pH", "pH"), "", 0.12),
        [WaterParameter.DissolvedOxygen] = new(WaterParameter.DissolvedOxygen, "DO", new("Dissolved oxygen", "Oksigen terlarut"), "mg/L", 0.18),
        [WaterParameter.Orp] = new(WaterParameter.Orp, "ORP", new("Oxidation-reduction potential", "Potensial redoks"), "mV", 0.04),
        [WaterParameter.Salinity] = new(WaterParameter.Salinity, "SAL", new("Salinity", "Salinitas"), "ppt", 0.07),
        [WaterParameter.Tds] = new(WaterParameter.Tds, "TDS", new("Total dissolved solids", "Padatan terlarut total"), "mg/L", 0.01),
        [WaterParameter.Tss] = new(WaterParameter.Tss, "TSS", new("Total suspended solids", "Padatan tersuspensi total"), "mg/L", 0.03),
        [WaterParameter.Alkalinity] = new(WaterParameter.Alkalinity, "ALK", new("Alkalinity", "Alkalinitas"), "mg/L CaCO₃", 0.06),
        [WaterParameter.Ammonia] = new(WaterParameter.Ammonia, "TAN", new("Total ammonia nitrogen", "Amonia total (TAN)"), "mg/L", 0.14),
        [WaterParameter.Nitrite] = new(WaterParameter.Nitrite, "NO2", new("Nitrite-N", "Nitrit-N"), "mg/L", 0.08),
        [WaterParameter.Nitrate] = new(WaterParameter.Nitrate, "NO3", new("Nitrate-N", "Nitrat-N"), "mg/L", 0.03),
        [WaterParameter.Phosphate] = new(WaterParameter.Phosphate, "PO4", new("Phosphate", "Fosfat"), "mg/L", 0.03),
        [WaterParameter.Bod] = new(WaterParameter.Bod, "BOD", new("Biochemical oxygen demand", "Kebutuhan oksigen biokimia"), "mg/L", 0.03),
        [WaterParameter.Cod] = new(WaterParameter.Cod, "COD", new("Chemical oxygen demand", "Kebutuhan oksigen kimiawi"), "mg/L", 0.02),
        [WaterParameter.Turbidity] = new(WaterParameter.Turbidity, "TURB", new("Turbidity", "Kekeruhan"), "NTU", 0.04),
    };

    public static bool TryParse(string code, out WaterParameter p)
    {
        foreach (var i in Info.Values)
            if (i.Code.Equals(code, StringComparison.OrdinalIgnoreCase) || i.Parameter.ToString().Equals(code, StringComparison.OrdinalIgnoreCase)) { p = i.Parameter; return true; }
        switch (code.ToLowerInvariant())
        {
            case "temp_c" or "suhu" or "t": p = WaterParameter.Temperature; return true;
            case "oxygen" or "do_mg_l" or "oksigen": p = WaterParameter.DissolvedOxygen; return true;
            case "nh3" or "nh4" or "tan" or "amonia": p = WaterParameter.Ammonia; return true;
            case "sal" or "salinitas" or "salinity_ppt": p = WaterParameter.Salinity; return true;
            case "ntu" or "kekeruhan": p = WaterParameter.Turbidity; return true;
        }
        p = default; return false;
    }
}

/// <summary>A water sample or a sensor snapshot. Unmeasured parameters are null.</summary>
public sealed record WaterSample
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string PondId { get; init; } = "";
    public double? TemperatureC { get; init; }
    public double? Ph { get; init; }
    public double? DissolvedOxygen { get; init; }
    public double? OrpMv { get; init; }
    public double? SalinityPpt { get; init; }
    public double? Tds { get; init; }
    public double? Tss { get; init; }
    public double? Alkalinity { get; init; }
    /// <summary>Total ammonia nitrogen (TAN, mg/L).</summary>
    public double? Ammonia { get; init; }
    public double? Nitrite { get; init; }
    public double? Nitrate { get; init; }
    public double? Phosphate { get; init; }
    public double? Bod { get; init; }
    public double? Cod { get; init; }
    public double? TurbidityNtu { get; init; }

    public double? this[WaterParameter p] => p switch
    {
        WaterParameter.Temperature => TemperatureC,
        WaterParameter.Ph => Ph,
        WaterParameter.DissolvedOxygen => DissolvedOxygen,
        WaterParameter.Orp => OrpMv,
        WaterParameter.Salinity => SalinityPpt,
        WaterParameter.Tds => Tds,
        WaterParameter.Tss => Tss,
        WaterParameter.Alkalinity => Alkalinity,
        WaterParameter.Ammonia => Ammonia,
        WaterParameter.Nitrite => Nitrite,
        WaterParameter.Nitrate => Nitrate,
        WaterParameter.Phosphate => Phosphate,
        WaterParameter.Bod => Bod,
        WaterParameter.Cod => Cod,
        WaterParameter.Turbidity => TurbidityNtu,
        _ => null,
    };

    /// <summary>Copy with one parameter replaced.</summary>
    public WaterSample With(WaterParameter p, double? value) => p switch
    {
        WaterParameter.Temperature => this with { TemperatureC = value },
        WaterParameter.Ph => this with { Ph = value },
        WaterParameter.DissolvedOxygen => this with { DissolvedOxygen = value },
        WaterParameter.Orp => this with { OrpMv = value },
        WaterParameter.Salinity => this with { SalinityPpt = value },
        WaterParameter.Tds => this with { Tds = value },
        WaterParameter.Tss => this with { Tss = value },
        WaterParameter.Alkalinity => this with { Alkalinity = value },
        WaterParameter.Ammonia => this with { Ammonia = value },
        WaterParameter.Nitrite => this with { Nitrite = value },
        WaterParameter.Nitrate => this with { Nitrate = value },
        WaterParameter.Phosphate => this with { Phosphate = value },
        WaterParameter.Bod => this with { Bod = value },
        WaterParameter.Cod => this with { Cod = value },
        WaterParameter.Turbidity => this with { TurbidityNtu = value },
        _ => this,
    };

    /// <summary>Parameters that have a value.</summary>
    public IEnumerable<(WaterParameter Parameter, double Value)> Measured()
    {
        foreach (WaterParameter p in Enum.GetValues<WaterParameter>())
            if (this[p] is { } v && double.IsFinite(v)) yield return (p, v);
    }

    /// <summary>Merges newer non-null values over this sample (for building a snapshot from streaming sensors).</summary>
    public WaterSample Merge(WaterSample newer)
    {
        var s = this with { Timestamp = newer.Timestamp > Timestamp ? newer.Timestamp : Timestamp };
        foreach (var (p, v) in newer.Measured()) s = s.With(p, v);
        return s;
    }
}
