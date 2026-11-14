using System.Text.Json;
using System.Text.Json.Nodes;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.Interop;

/// <summary>
/// Format auto-detection and loading of network files into <see cref="PowerNetwork"/>: MATPOWER (<c>.m</c>), PSS/E RAW (<c>.raw</c>),
/// IEEE Common Data Format and the Electrical.Cloud network JSON. Detection is content based; the file extension only breaks ties.
/// </summary>
public static class NetworkFormats
{
    /// <summary>Detects the format of <paramref name="text"/> from its content.</summary>
    public static NetworkFormat Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (t.StartsWith('{') && t.Contains("\"buses\"", StringComparison.Ordinal)) return NetworkFormat.CloudJson;
        if (t.Contains("mpc.bus", StringComparison.Ordinal) || t.StartsWith("function mpc", StringComparison.Ordinal)) return NetworkFormat.Matpower;
        if (t.Contains("BUS DATA FOLLOWS", StringComparison.OrdinalIgnoreCase)) return NetworkFormat.IeeeCdf;
        if (LooksLikeRaw(t)) return NetworkFormat.PsseRaw;
        return NetworkFormat.Unknown;
    }

    private static bool LooksLikeRaw(string t)
    {
        if (t.Contains("END OF BUS DATA", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var line in InteropText.Lines(t))
        {
            var s = line.Trim();
            if (s.Length == 0 || s.StartsWith("@!", StringComparison.Ordinal)) continue;
            var tok = InteropText.Tokenize(s, out _);
            return tok.Count >= 3 && tok[0] is "0" or "1" && InteropText.TryNum(tok[1], out double sb) && sb > 0
                   && InteropText.TryNum(tok[2], out double rev) && rev is >= 20 and <= 40;
        }
        return false;
    }

    /// <summary>Parses text in the given (or auto-detected) format.</summary>
    /// <exception cref="FormatException">The format cannot be detected or the data are malformed.</exception>
    public static ImportResult Parse(string text, NetworkFormat format = NetworkFormat.Unknown, string? name = null)
    {
        if (format == NetworkFormat.Unknown) format = Detect(text);
        var warnings = new List<string>();
        PowerNetwork net = format switch
        {
            NetworkFormat.Matpower => Matpower.Parse(text, name ?? "case"),
            NetworkFormat.PsseRaw => PsseRaw.Parse(text, warnings),
            NetworkFormat.IeeeCdf => IeeeCdf.Parse(text, warnings),
            NetworkFormat.CloudJson => ParseCloudJson(text),
            _ => throw new FormatException("Unrecognised network format (expected MATPOWER .m, PSS/E .raw, IEEE CDF or Electrical.Cloud JSON)."),
        };
        if (name is not null && net.Name is "psse" or "ieee_cdf" or "network") net.Name = name;
        return new ImportResult(net, format, warnings);
    }

    /// <summary>Loads a network file, detecting its format from the content (falling back to the extension).</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static ImportResult Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Network file not found.", path);
        var text = File.ReadAllText(path);
        var format = Detect(text);
        if (format == NetworkFormat.Unknown) format = FromExtension(path);
        return Parse(text, format, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Format implied by a file extension (<c>.m</c>, <c>.raw</c>, <c>.cdf</c>/<c>.txt</c>, <c>.json</c>).</summary>
    public static NetworkFormat FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m" => NetworkFormat.Matpower,
        ".raw" => NetworkFormat.PsseRaw,
        ".cdf" or ".txt" => NetworkFormat.IeeeCdf,
        ".json" => NetworkFormat.CloudJson,
        _ => NetworkFormat.Unknown,
    };

    /// <summary>Reads the Electrical.Cloud network JSON contract (same fields as <c>Electrical.Cloud.NetworkJson</c>) without reflection.</summary>
    public static PowerNetwork ParseCloudJson(string json)
    {
        var o = (JsonNode.Parse(json) ?? throw new FormatException("Empty JSON.")).AsObject();
        var net = new PowerNetwork
        {
            Name = (string?)o["name"] ?? "network", BaseMva = (double?)o["baseMva"] ?? 100, FrequencyHz = (double?)o["frequencyHz"] ?? 50,
        };
        try
        {
            foreach (var b in o["buses"]?.AsArray() ?? [])
            {
                var bus = new Bus
                {
                    Id = (int)b!["id"]!, Name = (string?)b["name"], Type = Enum.Parse<BusType>((string?)b["type"] ?? "PQ", true),
                    BaseKv = (double?)b["baseKv"] ?? 1, Vm = (double?)b["vm"] ?? 1, VaDeg = (double?)b["vaDeg"] ?? 0,
                    Vmin = (double?)b["vmin"] ?? 0.9, Vmax = (double?)b["vmax"] ?? 1.1, Gs = (double?)b["gs"] ?? 0, Bs = (double?)b["bs"] ?? 0,
                };
                if (b["lat"] is not null && b["lon"] is not null) bus.Location = new GeoPoint((double)b["lat"]!, (double)b["lon"]!);
                net.Buses.Add(bus);
            }
            foreach (var l in o["loads"]?.AsArray() ?? [])
                net.Loads.Add(new Load
                {
                    Bus = (int)l!["bus"]!, Name = (string?)l["name"], P = (double?)l["p"] ?? 0, Q = (double?)l["q"] ?? 0,
                    InService = (bool?)l["inService"] ?? true, Category = (string?)l["category"],
                });
            foreach (var g in o["generators"]?.AsArray() ?? [])
                net.Generators.Add(new Generator
                {
                    Bus = (int)g!["bus"]!, Name = (string?)g["name"], Kind = Enum.Parse<GeneratorKind>((string?)g["kind"] ?? "Thermal", true),
                    P = (double?)g["p"] ?? 0, Q = (double?)g["q"] ?? 0, Vset = (double?)g["vset"] ?? 1, Pmin = (double?)g["pmin"] ?? 0,
                    Pmax = (double?)g["pmax"] ?? 9999, Qmin = (double?)g["qmin"] ?? -9999, Qmax = (double?)g["qmax"] ?? 9999,
                    InService = (bool?)g["inService"] ?? true,
                    Cost = new PolynomialCost((double?)g["c2"] ?? 0, (double?)g["c1"] ?? 0, (double?)g["c0"] ?? 0),
                });
            foreach (var r in o["branches"]?.AsArray() ?? [])
                net.Branches.Add(new Branch
                {
                    From = (int)r!["from"]!, To = (int)r["to"]!, Name = (string?)r["name"],
                    Kind = Enum.Parse<BranchKind>((string?)r["kind"] ?? "Line", true), R = (double?)r["r"] ?? 0, X = (double?)r["x"] ?? 0,
                    B = (double?)r["b"] ?? 0, RateA = (double?)r["rateA"] ?? 0, Tap = (double?)r["tap"] ?? 0, ShiftDeg = (double?)r["shiftDeg"] ?? 0,
                    InService = (bool?)r["inService"] ?? true, LengthKm = (double?)r["lengthKm"] ?? 0,
                });
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or JsonException)
        {
            throw new FormatException($"Invalid network JSON: {e.Message}", e);
        }
        return net;
    }
}

/// <summary>Network import service registered by <see cref="InteropBuilderExtensions.AddInterop"/>.</summary>
public interface INetworkImporter
{
    /// <summary>Loads a file with format auto-detection.</summary>
    ImportResult Load(string path);
    /// <summary>Parses text in the given or detected format.</summary>
    ImportResult Parse(string text, NetworkFormat format = NetworkFormat.Unknown);
    /// <summary>Detects the format of a text.</summary>
    NetworkFormat Detect(string text);
    /// <summary>Writes PSS/E RAW revision 33 or 35.</summary>
    string WriteRaw(PowerNetwork network, int version = 33);
}

internal sealed class NetworkImporter : INetworkImporter
{
    public ImportResult Load(string path) => NetworkFormats.Load(path);
    public ImportResult Parse(string text, NetworkFormat format = NetworkFormat.Unknown) => NetworkFormats.Parse(text, format);
    public NetworkFormat Detect(string text) => NetworkFormats.Detect(text);
    public string WriteRaw(PowerNetwork network, int version = 33) => PsseRaw.Write(network, version);
}

/// <summary>DI registration for the Interop module.</summary>
public static class InteropBuilderExtensions
{
    /// <summary>Registers <see cref="INetworkImporter"/> (singleton).</summary>
    public static ElectricalBuilder AddInterop(this ElectricalBuilder b)
    {
        if (b.TryAddModule("Interop")) b.Services.TryAddSingleton<INetworkImporter, NetworkImporter>();
        return b;
    }
}
