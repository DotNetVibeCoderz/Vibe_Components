namespace Electrical.Emt;

/// <summary>Settings of an EMT run.</summary>
public sealed record EmtOptions
{
    /// <summary>Fixed integration time step Δt (s). Default 50 µs.</summary>
    public double TimeStep { get; init; } = 50e-6;

    /// <summary>Simulated time span (s); the run takes round(StopTime/Δt) steps.</summary>
    public double StopTime { get; init; } = 0.1;

    /// <summary>
    /// Critical damping adjustment (CDA, Marti &amp; Lin): after every topology change the solver takes two backward-Euler half steps
    /// (Δt/2), which keeps the network matrix unchanged and removes the sustained trapezoidal oscillations caused by current
    /// interruption or voltage jumps. Default true.
    /// </summary>
    public bool CriticalDampingAdjustment { get; init; } = true;

    /// <summary>Store every n-th full step (≥ 1).</summary>
    public int RecordEvery { get; init; } = 1;

    /// <summary>Record branch currents (node voltages are always recorded). Default true.</summary>
    public bool RecordCurrents { get; init; } = true;

    /// <summary>Use the Rust kernels when available (results are identical to the managed path). Default true.</summary>
    public bool PreferNative { get; init; } = true;

    /// <summary>Maximum re-solutions per step while diode/thyristor states settle.</summary>
    public int MaxSwitchIterations { get; init; } = 16;

    public static EmtOptions Default { get; } = new();
}
