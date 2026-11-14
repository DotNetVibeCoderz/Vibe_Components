using System.Numerics;
using Electrical.Core.Numerics;

namespace Electrical.Dynamics;

/// <summary>Role of a dynamic model in a unit (PSS/E model categories).</summary>
public enum ModelCategory
{
    /// <summary>Synchronous machine (GENCLS, GENROU, GENSAL).</summary>
    Machine,
    /// <summary>Excitation system (SEXS, IEEET1, ESST1A, ESAC4A).</summary>
    Exciter,
    /// <summary>Turbine-governor (TGOV1, HYGOV, GAST, IEEEG1).</summary>
    Governor,
    /// <summary>Power system stabilizer (IEEEST, PSS2B).</summary>
    Stabilizer,
    /// <summary>Renewable generator/converter interface (REGC_A).</summary>
    Converter,
    /// <summary>Renewable electrical control (REEC_A).</summary>
    ElectricalControl,
    /// <summary>Renewable plant controller (REPC_A).</summary>
    PlantControl,
    /// <summary>Grid-forming inverter (droop / virtual synchronous machine).</summary>
    GridForming,
    /// <summary>Dynamic load.</summary>
    Load,
}

/// <summary>
/// Base class of every dynamic model: owns a list of named states (allocated in the constructor; bypassed blocks with zero time
/// constants own no state), limit handling and reference set-points.
/// </summary>
public abstract class DynamicComponent
{
    private readonly List<string> _states = [];

    /// <summary>PSS/E model name (e.g. "GENROU").</summary>
    public abstract string ModelName { get; }
    public abstract ModelCategory Category { get; }
    /// <summary>Local state names in state-vector order.</summary>
    public IReadOnlyList<string> StateNames => _states;
    public int StateCount => _states.Count;

    /// <summary>Allocates a state and returns its local index.</summary>
    protected int AddState(string name) { _states.Add(name); return _states.Count - 1; }

    /// <summary>Allocates a state only when <paramref name="active"/>; returns −1 otherwise.</summary>
    protected int AddStateIf(bool active, string name) => active ? AddState(name) : -1;

    /// <summary>Clamps non-windup limited states after an accepted step.</summary>
    public virtual void ApplyLimits(Span<double> x) { }

    /// <summary>Changes a reference (e.g. "Vref", "Pref", "Qref"). Returns false when the model has no such reference.</summary>
    public virtual bool TrySetReference(string name, double value) => false;

    /// <summary>Reads a reference value.</summary>
    public virtual bool TryGetReference(string name, out double value) { value = double.NaN; return false; }

    /// <summary>Discrete logic after an accepted step (timers, latches).</summary>
    public virtual void AfterStep(double t, ReadOnlySpan<double> x) { }

    /// <summary>Resets discrete memory to its initial value (called by initialization).</summary>
    public virtual void ResetDiscrete() { }

    public override string ToString() => $"{ModelName} ({StateCount} states)";

    protected static void Clamp(Span<double> x, int i, double min, double max)
    {
        if (i >= 0) x[i] = Blocks.Clamp(x[i], min, max);
    }
}

/// <summary>Electrical and mechanical signals of a synchronous machine, shared with its controllers. Quantities in pu on the machine base.</summary>
public sealed class MachineSignals
{
    /// <summary>Terminal voltage phasor (network frame) [pu].</summary>
    public Complex V;
    /// <summary>Stator current phasor out of the machine (network frame) [pu on MBase].</summary>
    public Complex I;
    /// <summary>Terminal voltage magnitude [pu].</summary>
    public double Vt;
    /// <summary>Terminal active power [pu on MBase].</summary>
    public double P;
    /// <summary>Terminal reactive power [pu on MBase].</summary>
    public double Q;
    /// <summary>Air-gap (electrical) torque [pu].</summary>
    public double Te;
    /// <summary>Rotor speed deviation Δω [pu].</summary>
    public double SpeedDeviation;
    /// <summary>Rotor angle δ (q-axis position in the network frame) [rad].</summary>
    public double Angle;
    /// <summary>Field current in Xad·Ifd units [pu].</summary>
    public double XadIfd;
    /// <summary>Field voltage applied to the machine [pu].</summary>
    public double Efd;
    /// <summary>Mechanical power [pu on MBase].</summary>
    public double Pm;
    /// <summary>Stabilizer output added to the exciter summing junction [pu].</summary>
    public double Vs;
    /// <summary>d/q-axis current in the rotor frame [pu].</summary>
    public double Id, Iq;
}

/// <summary>Synchronous machine model. Parameters are in pu on <see cref="MBase"/>, times in seconds.</summary>
public abstract class MachineModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.Machine;
    /// <summary>Machine MVA base (0 = take <c>Generator.MBase</c>).</summary>
    public double MBase { get; set; }
    /// <summary>Armature resistance [pu] (NaN = take <c>Generator.Ra</c>).</summary>
    public double Ra { get; set; } = double.NaN;
    /// <summary>Inertia constant [s]; 0 = infinite bus.</summary>
    public abstract double H { get; }
    /// <summary>Damping [pu power / pu speed].</summary>
    public abstract double D { get; }
    /// <summary>Synchronous angular frequency ωs [rad/s] (set by the system builder).</summary>
    public double OmegaS { get; set; } = 2 * Math.PI * 50;

    public abstract int AngleState { get; }
    public abstract int SpeedState { get; }
    /// <summary>Norton source impedance Ra + jX'' [pu on MBase].</summary>
    public abstract Complex SourceImpedance { get; }
    /// <summary>Internal (subtransient) EMF in the network frame [pu].</summary>
    public abstract Complex InternalVoltage(ReadOnlySpan<double> x);
    /// <summary>Sets Vt, P, Q, Te, Δω, δ, XadIfd, Id, Iq for the given terminal voltage and current.</summary>
    public abstract void ComputeSignals(ReadOnlySpan<double> x, Complex v, Complex i, MachineSignals s);
    /// <summary>State derivatives for field voltage <paramref name="efd"/> and mechanical power <paramref name="pm"/>.</summary>
    public abstract void Derivatives(ReadOnlySpan<double> x, MachineSignals s, double efd, double pm, Span<double> dx);
    /// <summary>Equilibrium states for terminal voltage <paramref name="v"/> and current <paramref name="i"/> (machine base); returns Efd and Pm.</summary>
    public abstract void Initialize(Complex v, Complex i, Span<double> x, out double efd, out double pm);

    /// <summary>Swing equation (PSS/E form): 2H·dΔω/dt = (Pm − D·Δω)/(1 + Δω) − Te.</summary>
    protected double SpeedDerivative(double dw, double pm, double te) => H <= 0 ? 0 : ((pm - D * dw) / (1 + dw) - te) / (2 * H);

    /// <summary>
    /// Refines an initial guess by Newton iteration on [states, Efd, Pm] so that all derivatives vanish and E'' − (Ra + jX'')·I = V.
    /// </summary>
    protected void RefineEquilibrium(Complex v, Complex i, Span<double> x, ref double efd, ref double pm)
    {
        int n = StateCount, m = n + 2;
        var z = new double[m];
        for (int k = 0; k < n; k++) z[k] = x[k];
        z[n] = efd; z[n + 1] = pm;
        var s = new MachineSignals();
        var r = new double[m]; var r2 = new double[m]; var dx = new double[n];
        void Residual(double[] zz, double[] res)
        {
            var xs = zz.AsSpan(0, n);
            var e = InternalVoltage(xs);
            var cur = (e - v) / SourceImpedance;
            ComputeSignals(xs, v, cur, s);
            Derivatives(xs, s, zz[n], zz[n + 1], dx);
            for (int k = 0; k < n; k++) res[k] = dx[k];
            // replace the angle equation (dδ/dt = ωs·Δω, already implied by Δω = 0) with the current match
            var err = cur - i;
            res[AngleState] = err.Real;
            res[n] = err.Imaginary;
            res[n + 1] = dx[AngleState];
        }
        for (int it = 0; it < 30; it++)
        {
            Residual(z, r);
            double nr = r.Max(Math.Abs);
            if (nr < 1e-13) break;
            var j = new DenseMatrix<double>(m, m);
            for (int c = 0; c < m; c++)
            {
                double h = 1e-7 * Math.Max(1, Math.Abs(z[c]));
                double old = z[c];
                z[c] = old + h; Residual(z, r2); z[c] = old;
                for (int q = 0; q < m; q++) j[q, c] = (r2[q] - r[q]) / h;
            }
            double[] step;
            try { step = j.Solve(r); } catch (SingularMatrixException) { break; }
            for (int c = 0; c < m; c++) z[c] -= step[c];
        }
        for (int k = 0; k < n; k++) x[k] = z[k];
        efd = z[n]; pm = z[n + 1];
    }

    /// <summary>Rotor-frame components (d, q) of a network-frame phasor for rotor angle δ.</summary>
    protected static (double d, double q) ToRotor(Complex a, double delta)
    {
        var r = a * Complex.FromPolarCoordinates(1, -(delta - Math.PI / 2));
        return (r.Real, r.Imaginary);
    }

    /// <summary>Network-frame phasor from rotor-frame components.</summary>
    protected static Complex ToNetwork(double d, double q, double delta) => new Complex(d, q) * Complex.FromPolarCoordinates(1, delta - Math.PI / 2);
}

/// <summary>Excitation system. Output Efd [pu]; reference Vref [pu].</summary>
public abstract class ExciterModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.Exciter;
    /// <summary>Voltage reference [pu] (computed at initialization).</summary>
    public double Vref { get; set; }
    /// <summary>Sets states and <see cref="Vref"/> so that the output equals <paramref name="efd0"/>.</summary>
    public abstract void Initialize(double efd0, MachineSignals s, Span<double> x);
    /// <summary>Returns Efd and writes derivatives.</summary>
    public abstract double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx);
    public override bool TrySetReference(string name, double value)
    {
        if (!name.Equals("Vref", StringComparison.OrdinalIgnoreCase)) return false;
        Vref = value; return true;
    }
    public override bool TryGetReference(string name, out double value)
    {
        value = Vref; return name.Equals("Vref", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Turbine-governor. Output Pm [pu on MBase]; reference Pref [pu].</summary>
public abstract class GovernorModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.Governor;
    /// <summary>Load reference [pu] (computed at initialization).</summary>
    public double Pref { get; set; }
    public abstract void Initialize(double pm0, MachineSignals s, Span<double> x);
    /// <summary>Returns Pm and writes derivatives.</summary>
    public abstract double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx);
    public override bool TrySetReference(string name, double value)
    {
        if (!name.Equals("Pref", StringComparison.OrdinalIgnoreCase)) return false;
        Pref = value; return true;
    }
    public override bool TryGetReference(string name, out double value)
    {
        value = Pref; return name.Equals("Pref", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>PSS/E stabilizer input signal codes.</summary>
public enum StabilizerInput
{
    /// <summary>Rotor speed deviation [pu] (code 1).</summary>
    SpeedDeviation = 1,
    /// <summary>Bus frequency deviation [pu] (code 2; approximated by the rotor speed deviation).</summary>
    FrequencyDeviation = 2,
    /// <summary>Generator electrical power [pu on MBase] (code 3).</summary>
    ElectricalPower = 3,
    /// <summary>Generator accelerating power Pm − Pe [pu] (code 4).</summary>
    AcceleratingPower = 4,
    /// <summary>Bus voltage [pu] (code 5).</summary>
    BusVoltage = 5,
}

/// <summary>Power system stabilizer. Output Vs [pu] added to the exciter voltage reference.</summary>
public abstract class StabilizerModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.Stabilizer;
    public abstract void Initialize(MachineSignals s, Span<double> x);
    /// <summary>Returns Vs and writes derivatives.</summary>
    public abstract double Evaluate(ReadOnlySpan<double> x, MachineSignals s, Span<double> dx);

    protected static double Signal(StabilizerInput code, MachineSignals s) => code switch
    {
        StabilizerInput.SpeedDeviation or StabilizerInput.FrequencyDeviation => s.SpeedDeviation,
        StabilizerInput.ElectricalPower => s.P,
        StabilizerInput.AcceleratingPower => s.Pm - s.P,
        StabilizerInput.BusVoltage => s.Vt,
        _ => s.SpeedDeviation,
    };
}

/// <summary>Signals of an inverter-based resource. Currents and powers in pu on the converter MBase.</summary>
public sealed class ConverterSignals
{
    /// <summary>Terminal voltage phasor [pu].</summary>
    public Complex V;
    public double Vt;
    /// <summary>Terminal active/reactive power [pu on MBase].</summary>
    public double P, Q;
    /// <summary>Active current and reactive current (positive = capacitive / injecting Q) delivered to the grid [pu].</summary>
    public double Ip, Iq;
    /// <summary>Current commands from the electrical control [pu].</summary>
    public double Ipcmd, Iqcmd;
    /// <summary>Plant-controller outputs: active power and reactive power references [pu on MBase].</summary>
    public double Pext, Qext;
    /// <summary>Initial P/Q at the terminal [pu on MBase].</summary>
    public double P0, Q0;
    /// <summary>Measured frequency deviation at the terminal [pu].</summary>
    public double FrequencyDeviation;
    /// <summary>True while the electrical control is in voltage-dip mode.</summary>
    public bool VoltageDip;
}

/// <summary>Renewable generator/converter interface (REGC_A family): current-source injection in phase with the terminal voltage.</summary>
public abstract class ConverterModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.Converter;
    /// <summary>Converter MVA base (0 = take <c>Generator.MBase</c>).</summary>
    public double MBase { get; set; }
    public abstract void Initialize(ConverterSignals s, Span<double> x);
    /// <summary>Computes the delivered currents Ip/Iq (after LVACM/HVRCL logic) into <paramref name="s"/>.</summary>
    public abstract void Output(ReadOnlySpan<double> x, ConverterSignals s);
    public abstract void Derivatives(ReadOnlySpan<double> x, ConverterSignals s, Span<double> dx);
}

/// <summary>Renewable electrical control (REEC_A family): produces Ipcmd/Iqcmd.</summary>
public abstract class ElectricalControlModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.ElectricalControl;
    public abstract void Initialize(ConverterSignals s, Span<double> x);
    /// <summary>Sets <see cref="ConverterSignals.Ipcmd"/>/<see cref="ConverterSignals.Iqcmd"/> and writes derivatives.</summary>
    public abstract void Evaluate(double t, ReadOnlySpan<double> x, ConverterSignals s, Span<double> dx);
}

/// <summary>Renewable plant controller (REPC_A family): produces Pext/Qext.</summary>
public abstract class PlantControlModel : DynamicComponent
{
    public override ModelCategory Category => ModelCategory.PlantControl;
    public abstract void Initialize(ConverterSignals s, Span<double> x);
    /// <summary>Sets <see cref="ConverterSignals.Pext"/>/<see cref="ConverterSignals.Qext"/> and writes derivatives.</summary>
    public abstract void Evaluate(ReadOnlySpan<double> x, ConverterSignals s, Span<double> dx);
}
