namespace GnuRadio.Analog;

/// <summary>
/// Second-order phase/frequency tracking loop (GNU Radio's <c>blocks::control_loop</c>), the core of
/// PLLs, Costas loops and clock recovery. Gains come from the loop bandwidth (rad/sample) and damping.
/// </summary>
public sealed class ControlLoop
{
    private double _bandwidth;
    private double _damping;

    public ControlLoop(double loopBandwidth, double maxFrequency, double minFrequency, double damping = 0.70710678)
    {
        _damping = damping;
        MaxFrequency = maxFrequency;
        MinFrequency = minFrequency;
        LoopBandwidth = loopBandwidth;
    }

    public double Phase { get; set; }
    public double Frequency { get; set; }
    public double Alpha { get; private set; }
    public double Beta { get; private set; }
    public double MaxFrequency { get; set; }
    public double MinFrequency { get; set; }

    public double LoopBandwidth
    {
        get => _bandwidth;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _bandwidth = value;
            UpdateGains();
        }
    }

    public double Damping
    {
        get => _damping;
        set
        {
            _damping = value;
            UpdateGains();
        }
    }

    private void UpdateGains()
    {
        var w = _bandwidth;
        var denom = 1.0 + 2.0 * _damping * w + w * w;
        Alpha = 4 * _damping * w / denom;
        Beta = 4 * w * w / denom;
    }

    /// <summary>Applies one phase-error sample: updates frequency, then phase.</summary>
    public void Advance(double error)
    {
        Frequency += Beta * error;
        Phase += Frequency + Alpha * error;
        WrapPhase();
        LimitFrequency();
    }

    public void WrapPhase()
    {
        const double twoPi = 2 * Math.PI;
        var p = Phase;
        while (p > twoPi) p -= twoPi;
        while (p < -twoPi) p += twoPi;
        Phase = p;
    }

    public void LimitFrequency()
    {
        if (Frequency > MaxFrequency) Frequency = MaxFrequency;
        else if (Frequency < MinFrequency) Frequency = MinFrequency;
    }

    /// <summary>Wraps an angle into (−π, π].</summary>
    public static double WrapPi(double x)
    {
        x %= 2 * Math.PI;
        if (x > Math.PI) x -= 2 * Math.PI;
        else if (x <= -Math.PI) x += 2 * Math.PI;
        return x;
    }
}
