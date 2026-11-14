using System.Numerics;

namespace Electrical.Circuit;

/// <summary>DC operating point: node voltages [V] and element currents [A].</summary>
public sealed class DcResult
{
    private readonly IReadOnlyDictionary<string, int> _nodes;
    private readonly double[] _x;
    private readonly IReadOnlyDictionary<string, double> _currents;

    internal DcResult(IReadOnlyDictionary<string, int> nodes, double[] x, IReadOnlyDictionary<string, double> currents, int iterations)
    {
        _nodes = nodes; _x = x; _currents = currents; Iterations = iterations;
    }

    public int Iterations { get; }
    public IEnumerable<string> NodeNames => _nodes.Keys;

    /// <summary>Node voltage to ground [V].</summary>
    public double Voltage(string node) => ElectricCircuit.IsGround(node) ? 0 : _x[_nodes.TryGetValue(node, out int i) ? i : throw new KeyNotFoundException($"Node '{node}' not found.")];

    /// <summary>Voltage between two nodes [V].</summary>
    public double Voltage(string plus, string minus) => Voltage(plus) - Voltage(minus);

    /// <summary>Element current [A] from its first to its second terminal through the element (sources: SPICE sign).</summary>
    public double Current(string element) => _currents.TryGetValue(element, out var c) ? c : throw new KeyNotFoundException($"Element '{element}' not found.");
}

/// <summary>One Bode-plot point.</summary>
public readonly record struct BodePoint(double FrequencyHz, double MagnitudeDb, double PhaseDeg, Complex Value);

/// <summary>AC sweep result: complex node voltages [V] and element currents [A] per frequency.</summary>
public sealed class AcResult
{
    private readonly IReadOnlyDictionary<string, int> _nodes;
    private readonly Complex[][] _x;
    private readonly IReadOnlyDictionary<string, Complex[]> _currents;

    internal AcResult(IReadOnlyDictionary<string, int> nodes, double[] freqs, Complex[][] x, IReadOnlyDictionary<string, Complex[]> currents)
    {
        _nodes = nodes; FrequenciesHz = freqs; _x = x; _currents = currents;
    }

    public IReadOnlyList<double> FrequenciesHz { get; }

    /// <summary>Node voltage phasors [V] over the sweep.</summary>
    public Complex[] Voltage(string node)
    {
        if (ElectricCircuit.IsGround(node)) return new Complex[_x.Length];
        int i = _nodes.TryGetValue(node, out int k) ? k : throw new KeyNotFoundException($"Node '{node}' not found.");
        return _x.Select(v => v[i]).ToArray();
    }

    /// <summary>Element current phasors [A] over the sweep.</summary>
    public Complex[] Current(string element) => _currents.TryGetValue(element, out var c) ? c : throw new KeyNotFoundException($"Element '{element}' not found.");

    /// <summary>Bode data of V(<paramref name="outputNode"/>) / V(<paramref name="inputNode"/>) (or of the output voltage alone when no input node is given).</summary>
    public IReadOnlyList<BodePoint> Bode(string outputNode, string? inputNode = null)
    {
        var vo = Voltage(outputNode);
        var vi = inputNode is null ? null : Voltage(inputNode);
        var r = new BodePoint[vo.Length];
        for (int k = 0; k < vo.Length; k++)
        {
            var h = vi is null ? vo[k] : vo[k] / vi[k];
            r[k] = new BodePoint(FrequenciesHz[k], 20 * Math.Log10(Math.Max(h.Magnitude, 1e-300)), h.Phase * 180 / Math.PI, h);
        }
        return r;
    }
}

/// <summary>Transient result: waveforms sampled at the fixed time step.</summary>
public sealed class TransientResult
{
    private readonly IReadOnlyDictionary<string, int> _nodes;
    private readonly double[][] _x;
    private readonly IReadOnlyDictionary<string, double[]> _currents;

    internal TransientResult(IReadOnlyDictionary<string, int> nodes, double[] time, double[][] x, IReadOnlyDictionary<string, double[]> currents)
    {
        _nodes = nodes; Time = time; _x = x; _currents = currents;
    }

    /// <summary>Time points [s].</summary>
    public IReadOnlyList<double> Time { get; }

    /// <summary>Node voltage waveform [V].</summary>
    public double[] Voltage(string node)
    {
        if (ElectricCircuit.IsGround(node)) return new double[_x.Length];
        int i = _nodes.TryGetValue(node, out int k) ? k : throw new KeyNotFoundException($"Node '{node}' not found.");
        return _x.Select(v => v[i]).ToArray();
    }

    /// <summary>Voltage waveform between two nodes [V].</summary>
    public double[] Voltage(string plus, string minus)
    {
        var a = Voltage(plus); var b = Voltage(minus);
        return a.Select((v, k) => v - b[k]).ToArray();
    }

    /// <summary>Element current waveform [A].</summary>
    public double[] Current(string element) => _currents.TryGetValue(element, out var c) ? c : throw new KeyNotFoundException($"Element '{element}' not found.");

    /// <summary>Value of a waveform at time <paramref name="t"/> by linear interpolation.</summary>
    public double At(double[] waveform, double t)
    {
        if (t <= Time[0]) return waveform[0];
        double dt = Time[1] - Time[0];
        int k = Math.Min((int)(t / dt), Time.Count - 2);
        double w = (t - Time[k]) / dt;
        return waveform[k] + w * (waveform[k + 1] - waveform[k]);
    }
}
