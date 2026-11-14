namespace Electrical.Core.Network;

/// <summary>Bus type for power-flow formulation.</summary>
public enum BusType
{
    /// <summary>Load bus: P and Q specified.</summary>
    PQ = 1,
    /// <summary>Generator bus: P and |V| specified.</summary>
    PV = 2,
    /// <summary>Reference (swing) bus: |V| and angle specified.</summary>
    Slack = 3,
    /// <summary>Out of service / isolated.</summary>
    Isolated = 4,
}

/// <summary>WGS-84 location (degrees).</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    /// <summary>Great-circle distance in km (haversine).</summary>
    public double DistanceKm(GeoPoint other)
    {
        const double r = 6371.0088;
        double dLat = (other.Latitude - Latitude) * Math.PI / 180, dLon = (other.Longitude - Longitude) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Latitude * Math.PI / 180) * Math.Cos(other.Latitude * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

/// <summary>A network node. Shunt Gs/Bs are in MW/Mvar consumed/injected at V = 1 pu.</summary>
public sealed class Bus
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public BusType Type { get; set; } = BusType.PQ;
    /// <summary>Nominal line-to-line voltage [kV].</summary>
    public double BaseKv { get; set; } = 1;
    /// <summary>Voltage magnitude [pu] (initial guess or solved value).</summary>
    public double Vm { get; set; } = 1;
    /// <summary>Voltage angle [degrees].</summary>
    public double VaDeg { get; set; }
    public double Vmax { get; set; } = 1.1;
    public double Vmin { get; set; } = 0.9;
    /// <summary>Shunt conductance [MW at 1 pu].</summary>
    public double Gs { get; set; }
    /// <summary>Shunt susceptance [Mvar injected at 1 pu].</summary>
    public double Bs { get; set; }
    public int Area { get; set; } = 1;
    public int Zone { get; set; } = 1;
    public GeoPoint? Location { get; set; }

    public override string ToString() => $"Bus {Id}{(Name is null ? "" : $" ({Name})")} [{Type}, {BaseKv} kV]";
}

/// <summary>ZIP load composition (fractions sum to 1): constant impedance, current and power.</summary>
public readonly record struct ZipModel(double Z, double I, double P)
{
    public static readonly ZipModel ConstantPower = new(0, 0, 1);
    public static readonly ZipModel ConstantImpedance = new(1, 0, 0);
    public static readonly ZipModel ConstantCurrent = new(0, 1, 0);
    /// <summary>Load scaling factor at voltage magnitude v [pu].</summary>
    public double Factor(double v) => Z * v * v + I * v + P;
}

public sealed class Load
{
    public int Bus { get; set; }
    public string? Name { get; set; }
    /// <summary>Active power [MW].</summary>
    public double P { get; set; }
    /// <summary>Reactive power [Mvar].</summary>
    public double Q { get; set; }
    public bool InService { get; set; } = true;
    public ZipModel Model { get; set; } = ZipModel.ConstantPower;
    /// <summary>Optional category used by forecasting / demand response (e.g. "residential").</summary>
    public string? Category { get; set; }
}

public enum GeneratorKind { Thermal, Hydro, Gas, Nuclear, Wind, Solar, Battery, Grid, Diesel, Geothermal, Other }

/// <summary>Polynomial cost C(P) = C2·P² + C1·P + C0 [$/h], P in MW.</summary>
public readonly record struct PolynomialCost(double C2, double C1, double C0)
{
    public double Evaluate(double pMw) => (C2 * pMw + C1) * pMw + C0;
    public double Marginal(double pMw) => 2 * C2 * pMw + C1;
}

public sealed class Generator
{
    public int Bus { get; set; }
    public string? Name { get; set; }
    public GeneratorKind Kind { get; set; } = GeneratorKind.Thermal;
    /// <summary>Active power output [MW].</summary>
    public double P { get; set; }
    /// <summary>Reactive power output [Mvar].</summary>
    public double Q { get; set; }
    public double Qmax { get; set; } = 9999;
    public double Qmin { get; set; } = -9999;
    /// <summary>Voltage set-point [pu].</summary>
    public double Vset { get; set; } = 1;
    public double Pmax { get; set; } = 9999;
    public double Pmin { get; set; }
    /// <summary>Machine base [MVA] for dynamic and short-circuit data.</summary>
    public double MBase { get; set; } = 100;
    public bool InService { get; set; } = true;
    public PolynomialCost Cost { get; set; } = new(0, 0, 0);
    /// <summary>Ramp limit [MW/h] for unit commitment / dispatch (∞ when unspecified).</summary>
    public double RampRate { get; set; } = double.PositiveInfinity;

    // Machine data (pu on MBase) for short-circuit and stability studies.
    public double Xd { get; set; } = 1.8;
    public double XdPrime { get; set; } = 0.3;
    public double XdSubtransient { get; set; } = 0.2;
    public double X2 { get; set; } = 0.2;
    public double X0 { get; set; } = 0.1;
    public double Ra { get; set; }
    /// <summary>Inertia constant H [s] on MBase.</summary>
    public double H { get; set; } = 5;
    /// <summary>Damping coefficient D [pu].</summary>
    public double D { get; set; } = 2;
}

public enum BranchKind { Line, Cable, Transformer }

/// <summary>Transformer winding connection (affects zero-sequence network).</summary>
public enum WindingConnection { YNyn, YNd, Dyn, Yy, Dd, Yd }

/// <summary>π-model branch. R, X, B in pu on the system base; a non-zero <see cref="Tap"/> marks a transformer (MATPOWER convention).</summary>
public sealed class Branch
{
    public int From { get; set; }
    public int To { get; set; }
    public string? Name { get; set; }
    public BranchKind Kind { get; set; } = BranchKind.Line;
    public double R { get; set; }
    public double X { get; set; }
    /// <summary>Total line charging susceptance [pu].</summary>
    public double B { get; set; }
    /// <summary>Long-term thermal rating [MVA] (0 = unlimited).</summary>
    public double RateA { get; set; }
    /// <summary>Off-nominal turns ratio at the from side (0 or 1 = nominal).</summary>
    public double Tap { get; set; }
    /// <summary>Phase shift [degrees].</summary>
    public double ShiftDeg { get; set; }
    public bool InService { get; set; } = true;
    /// <summary>Zero-sequence parameters [pu]; NaN = derive (3× positive sequence for lines).</summary>
    public double R0 { get; set; } = double.NaN;
    public double X0 { get; set; } = double.NaN;
    public double B0 { get; set; } = double.NaN;
    public WindingConnection Connection { get; set; } = WindingConnection.YNyn;
    /// <summary>Length [km] (informational, used by GIS and reliability).</summary>
    public double LengthKm { get; set; }

    public bool IsTransformer => Kind == BranchKind.Transformer || (Tap != 0 && Tap != 1) || ShiftDeg != 0;
    public double EffectiveTap => Tap == 0 ? 1 : Tap;
    public double ZeroSeqR => double.IsNaN(R0) ? (IsTransformer ? R : 3 * R) : R0;
    public double ZeroSeqX => double.IsNaN(X0) ? (IsTransformer ? X : 3 * X) : X0;
    public double ZeroSeqB => double.IsNaN(B0) ? (IsTransformer ? 0 : 0.6 * B) : B0;

    public override string ToString() => $"{Kind} {From}-{To}{(Name is null ? "" : $" ({Name})")}";
}
