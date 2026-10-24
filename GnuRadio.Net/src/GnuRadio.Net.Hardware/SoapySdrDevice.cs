using System.Reflection;
using System.Runtime.InteropServices;

namespace GnuRadio.Hardware;

/// <summary>
/// Any radio supported by SoapySDR (GNU Radio's <c>gr-soapy</c>), through the SoapySDR C API. Requires the
/// SoapySDR runtime and the module for your hardware (on Windows: PothosSDR or radioconda; on Linux:
/// <c>libsoapysdr0.8</c> plus <c>soapysdr-module-*</c>). Streams use the CF32 format, so samples arrive as
/// <see cref="Complex32"/> without conversion.
/// </summary>
public sealed unsafe partial class SoapySdrDevice : ISdrDevice
{
    private const int SoapyTx = 0;
    private const int SoapyRx = 1;
    private const int SoapyTimeout = -1;
    private const int SoapyOverflow = -4;
    private const string Library = "SoapySDR";

    private IntPtr _device;
    private IntPtr _stream;
    private int _direction = SoapyRx;
    private double _rate = 2.4e6;
    private double _frequency = 100e6;
    private double _gain = 20;
    private double _bandwidth;
    private bool _agc;

    static SoapySdrDevice()
    {
        NativeLibrary.SetDllImportResolver(typeof(SoapySdrDevice).Assembly, Resolve);
    }

    /// <summary>Opens a device from SoapySDR arguments such as <c>"driver=hackrf"</c> or <c>"driver=uhd,type=b200"</c>.</summary>
    public SoapySdrDevice(string arguments, int channel = 0)
    {
        Arguments = arguments;
        Channel = channel;
        _device = SoapySDRDevice_makeStrArgs(arguments);
        if (_device == IntPtr.Zero) throw new InvalidOperationException($"SoapySDR could not open '{arguments}': {LastError()}");
        Driver = TakeString(SoapySDRDevice_getDriverKey(_device)) ?? "soapy";
        Label = $"{TakeString(SoapySDRDevice_getHardwareKey(_device)) ?? Driver} ({arguments})";
        CanTransmit = SoapySDRDevice_getNumChannels(_device, SoapyTx) > (nuint)channel;
    }

    public string Arguments { get; }
    public int Channel { get; }
    public string Driver { get; }
    public string Label { get; }
    public bool CanTransmit { get; }

    /// <summary>Counts receive overflows (samples lost because the host was too slow).</summary>
    public long Overflows { get; private set; }

    /// <summary>True when the SoapySDR library can be loaded on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return SoapySDR_getAPIVersion() != IntPtr.Zero;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Lists attached devices, each as key/value pairs (driver, label, serial…).</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Enumerate(string filter = "")
    {
        nuint length;
        var list = SoapySDRDevice_enumerateStrArgs(filter, &length);
        var result = new List<IReadOnlyDictionary<string, string>>();
        for (nuint i = 0; i < length; i++)
        {
            var kw = list[i];
            var d = new Dictionary<string, string>();
            for (nuint k = 0; k < kw.Size; k++)
                d[Marshal.PtrToStringUTF8(kw.Keys[k]) ?? ""] = Marshal.PtrToStringUTF8(kw.Values[k]) ?? "";
            result.Add(d);
        }
        if (list != null) SoapySDRKwargsList_clear(list, length);
        return result;
    }

    public double SampleRate
    {
        get => _rate;
        set
        {
            _rate = value;
            Check(SoapySDRDevice_setSampleRate(_device, _direction, (nuint)Channel, value), "setSampleRate");
        }
    }

    public double CenterFrequency
    {
        get => _frequency;
        set
        {
            _frequency = value;
            Check(SoapySDRDevice_setFrequency(_device, _direction, (nuint)Channel, value, null), "setFrequency");
        }
    }

    public double Bandwidth
    {
        get => _bandwidth;
        set
        {
            _bandwidth = value;
            if (value > 0) Check(SoapySDRDevice_setBandwidth(_device, _direction, (nuint)Channel, value), "setBandwidth");
        }
    }

    public double Gain
    {
        get => _gain;
        set
        {
            _gain = value;
            Check(SoapySDRDevice_setGain(_device, _direction, (nuint)Channel, value), "setGain");
        }
    }

    public bool AutomaticGain
    {
        get => _agc;
        set
        {
            _agc = value;
            Check(SoapySDRDevice_setGainMode(_device, _direction, (nuint)Channel, value ? (byte)1 : (byte)0), "setGainMode");
        }
    }

    public void Start(SdrDirection direction = SdrDirection.Receive)
    {
        _direction = direction == SdrDirection.Receive ? SoapyRx : SoapyTx;
        // Apply the cached settings to the chosen direction before streaming.
        SampleRate = _rate;
        CenterFrequency = _frequency;
        if (_agc) AutomaticGain = true;
        else Gain = _gain;
        if (_bandwidth > 0) Bandwidth = _bandwidth;
        var channel = (nuint)Channel;
        _stream = SoapySDRDevice_setupStream(_device, _direction, "CF32", &channel, 1, null);
        if (_stream == IntPtr.Zero) throw new InvalidOperationException($"setupStream failed: {LastError()}");
        Check(SoapySDRDevice_activateStream(_device, _stream, 0, 0, 0), "activateStream");
    }

    public void Stop()
    {
        if (_stream == IntPtr.Zero) return;
        SoapySDRDevice_deactivateStream(_device, _stream, 0, 0);
        SoapySDRDevice_closeStream(_device, _stream);
        _stream = IntPtr.Zero;
    }

    public int Read(Span<Complex32> buffer, TimeSpan timeout)
    {
        fixed (Complex32* p = buffer)
        {
            var buffs = (void*)p;
            int flags;
            long timeNs;
            var n = SoapySDRDevice_readStream(_device, _stream, &buffs, (nuint)buffer.Length, &flags, &timeNs,
                new CLong((nint)timeout.TotalMicroseconds));
            if (n == SoapyTimeout) return 0;
            if (n == SoapyOverflow)
            {
                Overflows++;
                return 0;
            }
            if (n < 0) throw new InvalidOperationException($"readStream failed with {n}: {LastError()}");
            return n;
        }
    }

    public int Write(ReadOnlySpan<Complex32> buffer, TimeSpan timeout)
    {
        fixed (Complex32* p = buffer)
        {
            var buffs = (void*)p;
            int flags = 0;
            var n = SoapySDRDevice_writeStream(_device, _stream, &buffs, (nuint)buffer.Length, &flags, 0,
                new CLong((nint)timeout.TotalMicroseconds));
            if (n == SoapyTimeout) return 0;
            if (n < 0) throw new InvalidOperationException($"writeStream failed with {n}: {LastError()}");
            return n;
        }
    }

    public void Dispose()
    {
        Stop();
        if (_device != IntPtr.Zero)
        {
            SoapySDRDevice_unmake(_device);
            _device = IntPtr.Zero;
        }
    }

    private static void Check(int result, string call)
    {
        if (result != 0) throw new InvalidOperationException($"SoapySDR {call} failed ({result}): {LastError()}");
    }

    private static string LastError() => Marshal.PtrToStringUTF8(SoapySDRDevice_lastError()) ?? "unknown error";

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUTF8(p);
        SoapySDR_free(p);
        return s;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (var candidate in new[] { "SoapySDR", "libSoapySDR.so.0.8", "libSoapySDR.so", "libSoapySDR.dylib" })
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) return handle;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Kwargs
    {
        public nuint Size;
        public IntPtr* Keys;
        public IntPtr* Values;
    }

    [LibraryImport(Library)] private static partial IntPtr SoapySDR_getAPIVersion();
    [LibraryImport(Library)] private static partial void SoapySDR_free(IntPtr ptr);
    [LibraryImport(Library)] private static partial IntPtr SoapySDRDevice_lastError();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] private static partial Kwargs* SoapySDRDevice_enumerateStrArgs(string args, nuint* length);
    [LibraryImport(Library)] private static partial void SoapySDRKwargsList_clear(Kwargs* list, nuint length);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr SoapySDRDevice_makeStrArgs(string args);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_unmake(IntPtr device);
    [LibraryImport(Library)] private static partial IntPtr SoapySDRDevice_getDriverKey(IntPtr device);
    [LibraryImport(Library)] private static partial IntPtr SoapySDRDevice_getHardwareKey(IntPtr device);
    [LibraryImport(Library)] private static partial nuint SoapySDRDevice_getNumChannels(IntPtr device, int direction);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_setSampleRate(IntPtr device, int direction, nuint channel, double rate);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_setFrequency(IntPtr device, int direction, nuint channel, double frequency, Kwargs* args);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_setBandwidth(IntPtr device, int direction, nuint channel, double bandwidth);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_setGain(IntPtr device, int direction, nuint channel, double value);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_setGainMode(IntPtr device, int direction, nuint channel, byte automatic);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr SoapySDRDevice_setupStream(IntPtr device, int direction, string format, nuint* channels, nuint numChannels, Kwargs* args);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_closeStream(IntPtr device, IntPtr stream);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_activateStream(IntPtr device, IntPtr stream, int flags, long timeNs, nuint numElems);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_deactivateStream(IntPtr device, IntPtr stream, int flags, long timeNs);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_readStream(IntPtr device, IntPtr stream, void** buffs, nuint numElems, int* flags, long* timeNs, CLong timeoutUs);
    [LibraryImport(Library)] private static partial int SoapySDRDevice_writeStream(IntPtr device, IntPtr stream, void** buffs, nuint numElems, int* flags, long timeNs, CLong timeoutUs);
}
