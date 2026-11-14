using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Electrical.Core;
using Electrical.Core.Network;

namespace Electrical.Interop;

/// <summary>
/// Reader and writer for Siemens PTI PSS/E RAW power-flow data, revisions 33 and 35 (34 is read like 35, 32 and older like 33).
/// <para>
/// Read: case identification (SBASE [MVA], BASFRQ [Hz]), bus, load (constant P/I/Y parts → <see cref="ZipModel"/>), fixed shunt
/// (→ bus Gs/Bs), generator, non-transformer branch (line shunts GI/BI/GJ/BJ → bus shunts), v35 system switching devices
/// (→ low-impedance branches), two-winding transformers (CW = 1/2/3 winding-voltage codes, CZ = 1/2/3 impedance codes,
/// CM = 1/2 magnetizing codes, WINDV/NOMV, ANG, RATA) and three-winding transformers (converted to a star equivalent with an
/// internal bus, as MATPOWER's <c>psse2mpc</c> does). Switched shunts are frozen at their current value BINIT. Area, zone, owner and
/// the remaining sections are parsed and skipped; data in unsupported device sections (HVDC, FACTS, …) produces a warning.
/// v35 <c>@!</c> header lines are used to locate fields by name when present, and quoted strings may contain blanks, commas and slashes.
/// </para>
/// <para>Write: emits a RAW file that PSS/E and <see cref="Read"/> accept, with all transformers as two-winding CW=1/CZ=1/CM=1 records.</para>
/// </summary>
public static partial class PsseRaw
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly string[] OrderV33 =
    [
        "bus", "load", "fixedshunt", "generator", "branch", "transformer", "area", "twoterminaldc", "vscdc", "impedancecorrection",
        "multiterminaldc", "multisection", "zone", "interarea", "owner", "facts", "switchedshunt", "gne", "inductionmachine",
    ];

    private static readonly string[] OrderV35 =
    [
        "systemwide", "bus", "load", "fixedshunt", "generator", "branch", "switchingdevice", "transformer", "area", "twoterminaldc", "vscdc",
        "impedancecorrection", "multiterminaldc", "multisection", "zone", "interarea", "owner", "facts", "switchedshunt", "gne",
        "inductionmachine", "substation",
    ];

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["systemwide"] = "SYSTEM-WIDE", ["bus"] = "BUS", ["load"] = "LOAD", ["fixedshunt"] = "FIXED SHUNT", ["generator"] = "GENERATOR",
        ["branch"] = "BRANCH", ["switchingdevice"] = "SYSTEM SWITCHING DEVICE", ["transformer"] = "TRANSFORMER", ["area"] = "AREA",
        ["twoterminaldc"] = "TWO-TERMINAL DC", ["vscdc"] = "VSC DC LINE", ["impedancecorrection"] = "IMPEDANCE CORRECTION",
        ["multiterminaldc"] = "MULTI-TERMINAL DC", ["multisection"] = "MULTI-SECTION LINE", ["zone"] = "ZONE", ["interarea"] = "INTER-AREA TRANSFER",
        ["owner"] = "OWNER", ["facts"] = "FACTS DEVICE", ["switchedshunt"] = "SWITCHED SHUNT", ["gne"] = "GNE DEVICE",
        ["inductionmachine"] = "INDUCTION MACHINE", ["substation"] = "SUBSTATION",
    };

    private static readonly HashSet<string> Unsupported = ["twoterminaldc", "vscdc", "multiterminaldc", "facts", "gne", "inductionmachine"];

    /// <summary>Reads RAW data from text, or from a file when <paramref name="textOrPath"/> is the path of an existing file.</summary>
    public static ImportResult Read(string textOrPath)
    {
        var text = InteropText.ResolveText(textOrPath, out var path);
        var warnings = new List<string>();
        var net = Parse(text, warnings);
        if (path is not null && net.Name == "psse") net.Name = Path.GetFileNameWithoutExtension(path);
        return new ImportResult(net, NetworkFormat.PsseRaw, warnings);
    }

    /// <summary>Reads a RAW file.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static ImportResult Load(string path) =>
        File.Exists(path) ? Read(Path.GetFullPath(path)) : throw new FileNotFoundException("RAW file not found.", path);

    /// <summary>Parses RAW text into a network; conversion notes are appended to <paramref name="warnings"/>.</summary>
    /// <exception cref="FormatException">The case identification record is missing or malformed.</exception>
    public static PowerNetwork Parse(string text, ICollection<string>? warnings = null)
    {
        warnings ??= [];
        var lines = InteropText.Lines(text);
        int i = 0;
        while (i < lines.Length && (lines[i].Trim().Length == 0 || lines[i].TrimStart().StartsWith("@!", StringComparison.Ordinal))) i++;
        if (i >= lines.Length) throw new FormatException("Empty PSS/E RAW data.");
        var id = InteropText.Tokenize(lines[i], out _);
        if (id.Count < 2 || !InteropText.TryNum(id[0], out double ic) || !InteropText.TryNum(id[1], out double sbase))
            throw new FormatException($"Invalid PSS/E case identification record: '{lines[i].Trim()}'.");
        int rev = id.Count > 2 && InteropText.TryNum(id[2], out double rv) && rv > 0 ? (int)rv : 33;
        double basfrq = id.Count > 5 && InteropText.TryNum(id[5], out double f) && f > 0 ? f : 60;
        if (ic == 1) warnings.Add("IC = 1 (change case): data are read as a complete base case.");
        if (rev is not (33 or 34 or 35)) warnings.Add($"RAW revision {rev} is not formally supported; read with the {(rev > 35 ? "v35" : "v33")} layout.");
        var net = new PowerNetwork { Name = "psse", BaseMva = sbase > 0 ? sbase : 100, FrequencyHz = basfrq };
        i++;
        var title = i < lines.Length ? lines[i].Trim() : "";
        if (title.Length > 0) net.Name = title.Length > 60 ? title[..60].Trim() : title;
        i += 2; // two title lines

        var order = rev >= 34 ? OrderV35 : OrderV33;
        var r = new RawReader(net, warnings, rev);
        string section = order[0];
        bool first = true;
        var headers = new List<Dictionary<string, int>>();
        bool lastWasHeader = false;
        var ignoredReported = new HashSet<string>();
        for (; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("@!", StringComparison.Ordinal))
            {
                if (!lastWasHeader) headers.Clear();
                headers.Add(HeaderMap(trimmed[2..]));
                lastWasHeader = true;
                continue;
            }
            lastWasHeader = false;
            if (trimmed.Length == 0) continue;
            var tok = InteropText.Tokenize(raw, out var comment);
            if (tok.Count == 0) continue;
            if (tok.Count == 1 && tok[0] is "Q" or "q") break;
            if (first && section == "systemwide" && char.IsDigit(trimmed[0]) && tok.Count > 1 && tok[0] != "0") section = "bus"; // no system-wide block
            first = false;
            if (tok.Count == 1 && tok[0] == "0")
            {
                if (section == "substation") continue; // nested sub-blocks until Q
                string? next = comment is null ? null : SectionFromComment(comment);
                int idx = Array.IndexOf(order, section);
                section = next ?? (idx >= 0 && idx + 1 < order.Length ? order[idx + 1] : "other");
                headers.Clear();
                continue;
            }
            var hdr = headers.Count > 0 ? headers[0] : null;
            switch (section)
            {
                case "bus": r.Bus(new Rec(tok, hdr)); break;
                case "load": r.Load(new Rec(tok, hdr)); break;
                case "fixedshunt": r.FixedShunt(new Rec(tok, hdr)); break;
                case "generator": r.Generator(new Rec(tok, hdr)); break;
                case "branch": r.Branch(new Rec(tok, hdr)); break;
                case "switchingdevice": r.SwitchingDevice(new Rec(tok, hdr)); break;
                case "switchedshunt": r.SwitchedShunt(new Rec(tok, hdr)); break;
                case "transformer":
                {
                    bool three = new Rec(tok, null).I(2, 0) != 0;
                    int n = three ? 5 : 4;
                    var recs = new List<Rec> { new(tok, hdr) };
                    int j = i + 1;
                    while (recs.Count < n && j < lines.Length)
                    {
                        var t = lines[j].Trim();
                        if (t.Length > 0 && !t.StartsWith("@!", StringComparison.Ordinal))
                            recs.Add(new Rec(InteropText.Tokenize(lines[j], out _), null));
                        j++;
                    }
                    i = j - 1;
                    if (recs.Count < n) throw new FormatException("Truncated transformer record at end of file.");
                    r.Transformer(recs);
                    break;
                }
                default:
                    if (Unsupported.Contains(section) && ignoredReported.Add(section))
                        warnings.Add($"{Titles[section]} data are not supported and were ignored.");
                    break;
            }
        }
        r.Finish();
        return net;
    }

    private static Dictionary<string, int> HeaderMap(string header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var toks = InteropText.Tokenize(header.Replace("/", " "), out _);
        for (int k = 0; k < toks.Count; k++)
        {
            var name = toks[k].Trim().Trim('\'', '"').Trim();
            if (name.Length > 0) map.TryAdd(name, k);
        }
        return map;
    }

    private static string? SectionFromComment(string comment)
    {
        var m = BeginRegex().Match(comment);
        if (!m.Success) return null;
        var s = m.Groups[1].Value.ToUpperInvariant();
        if (s.Contains("SWITCHED SHUNT")) return "switchedshunt";
        if (s.Contains("FIXED SHUNT")) return "fixedshunt";
        if (s.Contains("SUBSTATION")) return "substation";
        if (s.Contains("SWITCHING DEVICE")) return "switchingdevice";
        if (s.Contains("INDUCTION")) return "inductionmachine";
        if (s.Contains("MULTI-SECTION") || s.Contains("MULTISECTION")) return "multisection";
        if (s.Contains("MULTI-TERMINAL") || s.Contains("MULTITERMINAL")) return "multiterminaldc";
        if (s.Contains("TWO-TERMINAL") || s.Contains("TWO TERMINAL")) return "twoterminaldc";
        if (s.Contains("VSC")) return "vscdc";
        if (s.Contains("IMPEDANCE CORRECTION")) return "impedancecorrection";
        if (s.Contains("INTER-AREA") || s.Contains("INTERAREA")) return "interarea";
        if (s.Contains("TRANSFORMER")) return "transformer";
        if (s.Contains("GENERATOR")) return "generator";
        if (s.Contains("BRANCH")) return "branch";
        if (s.Contains("LOAD")) return "load";
        if (s.Contains("BUS")) return "bus";
        if (s.Contains("AREA")) return "area";
        if (s.Contains("ZONE")) return "zone";
        if (s.Contains("OWNER")) return "owner";
        if (s.Contains("FACTS")) return "facts";
        if (s.Contains("GNE")) return "gne";
        if (s.Contains("SYSTEM-WIDE") || s.Contains("SYSTEM WIDE")) return "systemwide";
        return "other";
    }

    [GeneratedRegex(@"BEGIN\s+(.+?)\s+DATA", RegexOptions.IgnoreCase)]
    private static partial Regex BeginRegex();

    /// <summary>A tokenized record with optional field names from the preceding <c>@!</c> header.</summary>
    private readonly struct Rec(List<string> tokens, Dictionary<string, int>? names)
    {
        public int Count => tokens.Count;

        private int Idx(string? name, int fallback) =>
            name is not null && names is not null && names.TryGetValue(name, out int k) && k < tokens.Count ? k : fallback;

        public string S(int idx, string def = "", string? name = null)
        {
            int k = Idx(name, idx);
            return k < tokens.Count && tokens[k].Length > 0 ? tokens[k] : def;
        }

        public double D(int idx, double def = 0, string? name = null)
        {
            int k = Idx(name, idx);
            return k < tokens.Count && InteropText.TryNum(tokens[k], out double v) ? v : def;
        }

        public int I(int idx, int def = 0, string? name = null) => (int)Math.Round(D(idx, def, name));
    }

    private sealed class RawReader(PowerNetwork net, ICollection<string> warnings, int rev)
    {
        private readonly bool _v35 = rev >= 34;
        private readonly Dictionary<int, Bus> _buses = [];
        private int _nextStar;
        private bool _magWarned, _zipWarned, _remoteWarned;
        private double S => net.BaseMva;

        private Bus? BusOf(int id, string what)
        {
            if (_buses.TryGetValue(id, out var b)) return b;
            warnings.Add($"{what} references unknown bus {id}; record ignored.");
            return null;
        }

        public void Bus(Rec r)
        {
            int ide = r.I(3, 1, "IDE");
            var b = new Bus
            {
                Id = r.I(0, 0, "I"), Name = NullIfEmpty(r.S(1, "", "NAME")), BaseKv = r.D(2, 0, "BASKV"),
                Type = ide switch { 2 => BusType.PV, 3 => BusType.Slack, 4 => BusType.Isolated, _ => BusType.PQ },
                Area = r.I(4, 1, "AREA"), Zone = r.I(5, 1, "ZONE"), Vm = r.D(7, 1, "VM"), VaDeg = r.D(8, 0, "VA"),
                Vmax = r.D(9, 1.1, "NVHI"), Vmin = r.D(10, 0.9, "NVLO"),
            };
            if (b.BaseKv <= 0) b.BaseKv = 1;
            if (!_buses.TryAdd(b.Id, b)) { warnings.Add($"Duplicate bus {b.Id} ignored."); return; }
            net.Buses.Add(b);
        }

        public void Load(Rec r)
        {
            int bus = r.I(0, 0, "I");
            if (BusOf(bus, "Load") is null) return;
            double pl = r.D(5, 0, "PL"), ql = r.D(6, 0, "QL"), ip = r.D(7, 0, "IP"), iq = r.D(8, 0, "IQ"), yp = r.D(9, 0, "YP"), yq = r.D(10, 0, "YQ");
            double p = pl + ip + yp, q = ql + iq - yq; // PSS/E: YQ < 0 for an inductive load
            var model = ZipModel.ConstantPower;
            if (ip != 0 || yp != 0 || iq != 0 || yq != 0)
            {
                if (p != 0) model = new ZipModel(yp / p, ip / p, pl / p);
                else if (q != 0) model = new ZipModel(-yq / q, iq / q, ql / q);
                bool qDiffers = p != 0 && q != 0 && (Math.Abs(-yq / q - model.Z) > 1e-6 || Math.Abs(iq / q - model.I) > 1e-6);
                if (qDiffers && !_zipWarned)
                {
                    warnings.Add("Some loads have different constant-P/I/Y splits for P and Q; the active-power split is used for both.");
                    _zipWarned = true;
                }
            }
            net.Loads.Add(new Load { Bus = bus, P = p, Q = q, InService = r.I(2, 1, "STATUS") != 0, Model = model, Name = NullIfEmpty(r.S(1, "", "ID")) });
        }

        public void FixedShunt(Rec r)
        {
            var b = BusOf(r.I(0, 0, "I"), "Fixed shunt");
            if (b is null || r.I(2, 1, "STATUS") == 0) return;
            b.Gs += r.D(3, 0, "GL");
            b.Bs += r.D(4, 0, "BL");
        }

        public void Generator(Rec r)
        {
            int bus = r.I(0, 0, "I");
            if (BusOf(bus, "Generator") is null) return;
            int o = _v35 ? 1 : 0; // v35 inserts NREG after IREG
            int ireg = r.I(7, 0, "IREG");
            if (ireg != 0 && ireg != bus && !_remoteWarned)
            {
                warnings.Add("Remote voltage regulation (IREG) is not modelled; generators regulate their own bus.");
                _remoteWarned = true;
            }
            double mbase = r.D(8 + o, S, "MBASE");
            var g = new Generator
            {
                Bus = bus, P = r.D(2, 0, "PG"), Q = r.D(3, 0, "QG"), Qmax = r.D(4, 9999, "QT"), Qmin = r.D(5, -9999, "QB"), Vset = r.D(6, 1, "VS"),
                MBase = mbase > 0 ? mbase : S, InService = r.I(14 + o, 1, "STAT") != 0, Pmax = r.D(16 + o, 9999, "PT"), Pmin = r.D(17 + o, -9999, "PB"),
                Ra = r.D(9 + o, 0, "ZR"),
            };
            double zx = r.D(10 + o, 0, "ZX");
            if (zx > 0) g.XdSubtransient = zx;
            net.Generators.Add(g);
        }

        public void Branch(Rec r)
        {
            int f = r.I(0, 0, "I"), t = Math.Abs(r.I(1, 0, "J"));
            var bf = BusOf(f, "Branch");
            var bt = BusOf(t, "Branch");
            if (bf is null || bt is null) return;
            int rateIdx = _v35 ? 7 : 6, giIdx = _v35 ? 19 : 9;
            var br = new Branch
            {
                From = f, To = t, R = r.D(3, 0, "R"), X = r.D(4, 0, "X"), B = r.D(5, 0, "B"),
                RateA = r.D(rateIdx, 0, _v35 ? "RATE1" : "RATEA"), InService = r.I(giIdx + 4, 1, "ST") != 0,
                LengthKm = r.D(giIdx + 6, 0, "LEN"), Name = _v35 ? NullIfEmpty(r.S(6, "", "NAME")) : null,
            };
            if (br.InService)
            {
                bf.Gs += r.D(giIdx, 0, "GI") * S; bf.Bs += r.D(giIdx + 1, 0, "BI") * S;
                bt.Gs += r.D(giIdx + 2, 0, "GJ") * S; bt.Bs += r.D(giIdx + 3, 0, "BJ") * S;
            }
            net.Branches.Add(br);
        }

        public void SwitchingDevice(Rec r)
        {
            int f = r.I(0, 0, "I"), t = r.I(1, 0, "J");
            if (BusOf(f, "Switching device") is null || BusOf(t, "Switching device") is null) return;
            double x = Math.Max(r.D(3, 0.0001, "X"), 0.0001);
            net.Branches.Add(new Branch
            {
                From = f, To = t, X = x, RateA = r.D(4, 0, "RATE1"), InService = r.I(16, 1, "STAT") != 0, Name = NullIfEmpty(r.S(19, "", "NAME")),
            });
            if (!warnings.Contains(SwitchWarn)) warnings.Add(SwitchWarn);
        }

        private const string SwitchWarn = "System switching devices were modelled as low-impedance branches (X ≥ 0.0001 pu).";

        public void SwitchedShunt(Rec r)
        {
            var b = BusOf(r.I(0, 0, "I"), "Switched shunt");
            if (b is null) return;
            int o = _v35 ? 2 : 0; // v35 adds ID and NREG
            if (r.I(_v35 ? 4 : 3, 1, "STAT") == 0) return;
            double binit = r.D(9 + o, 0, "BINIT");
            b.Bs += binit;
            warnings.Add($"Switched shunt at bus {b.Id} modelled as a fixed shunt at its current value {binit.ToString("0.###", Inv)} Mvar.");
        }

        /// <summary>Winding ratio in pu of the bus base voltage (CW code).</summary>
        private static double Windv(int cw, double w, double nomv, double busKv) => cw switch
        {
            2 => w / busKv,
            3 => w * (nomv > 0 ? nomv : busKv) / busKv,
            _ => w,
        };

        /// <summary>Winding-pair impedance on the system base (CZ code). <paramref name="vratio"/> = (NOMV / bus kV)² of the reference winding.</summary>
        private Complex Zsys(int cz, double r, double x, double sb, double vratio)
        {
            if (sb <= 0) sb = S;
            switch (cz)
            {
                case 2: return new Complex(r, x) * (S / sb) * vratio;
                case 3:
                {
                    double rpu = r / 1e6 / sb; // load loss [W] → pu on winding base
                    double xpu = Math.Sqrt(Math.Max(0, x * x - rpu * rpu));
                    return new Complex(rpu, xpu) * (S / sb) * vratio;
                }
                default: return new Complex(r, x);
            }
        }

        public void Transformer(List<Rec> recs)
        {
            var l1 = recs[0];
            int bi = l1.I(0, 0, "I"), bj = Math.Abs(l1.I(1, 0, "J")), bk = Math.Abs(l1.I(2, 0, "K"));
            int cw = l1.I(4, 1, "CW"), cz = l1.I(5, 1, "CZ"), cm = l1.I(6, 1, "CM"), stat = l1.I(11, 1, "STAT");
            double mag1 = l1.D(7, 0, "MAG1"), mag2 = l1.D(8, 0, "MAG2");
            string? name = NullIfEmpty(l1.S(10, "", "NAME"));
            var busI = BusOf(bi, "Transformer");
            var busJ = BusOf(bj, "Transformer");
            if (busI is null || busJ is null) return;
            var l2 = recs[1];
            var w1 = recs[2];

            double nomv1 = w1.D(1), kv1 = busI.BaseKv;
            double t1 = Windv(cw, w1.D(0, 1), nomv1, kv1);
            double vr1 = nomv1 > 0 ? nomv1 * nomv1 / (kv1 * kv1) : 1;

            // magnetizing branch → shunt at winding-1 bus
            if (mag1 != 0 || mag2 != 0)
            {
                double g, b;
                if (cm == 2)
                {
                    double sb12 = l2.D(2, S);
                    g = mag1 / 1e6 / S;
                    double y = mag2 * (sb12 > 0 ? sb12 : S) / S;
                    b = -Math.Sqrt(Math.Max(0, y * y - g * g));
                }
                else { g = mag1; b = mag2; }
                if (stat != 0) { busI.Gs += g * S; busI.Bs += b * S; }
                if (!_magWarned) { warnings.Add("Transformer magnetizing admittances were added as shunts at the winding-1 bus."); _magWarned = true; }
            }

            if (bk == 0)
            {
                var w2 = recs[3];
                double nomv2 = w2.D(1);
                double t2 = Windv(cw, w2.D(0, 1), nomv2, busJ.BaseKv);
                var z = Zsys(cz, l2.D(0), l2.D(1), l2.D(2, S), vr1) * (t2 * t2);
                net.Branches.Add(new Branch
                {
                    From = bi, To = bj, Kind = BranchKind.Transformer, R = z.Real, X = z.Imaginary, Tap = t1 / t2, ShiftDeg = w1.D(2),
                    RateA = w1.D(3), InService = stat != 0, Name = name,
                });
                return;
            }

            var busK = BusOf(bk, "Transformer");
            if (busK is null) return;
            var w2b = recs[3];
            var w3 = recs[4];
            double t2b = Windv(cw, w2b.D(0, 1), w2b.D(1), busJ.BaseKv);
            double t3 = Windv(cw, w3.D(0, 1), w3.D(1), busK.BaseKv);
            double nomv2b = w2b.D(1), nomv3 = w3.D(1);
            double vr2 = nomv2b > 0 ? nomv2b * nomv2b / (busJ.BaseKv * busJ.BaseKv) : 1;
            double vr3 = nomv3 > 0 ? nomv3 * nomv3 / (busK.BaseKv * busK.BaseKv) : 1;
            var z12 = Zsys(cz, l2.D(0), l2.D(1), l2.D(2, S), vr1);
            var z23 = Zsys(cz, l2.D(3), l2.D(4), l2.D(5, S), vr2);
            var z31 = Zsys(cz, l2.D(6), l2.D(7), l2.D(8, S), vr3);
            var z1 = (z12 + z31 - z23) / 2;
            var z2 = (z12 + z23 - z31) / 2;
            var z3 = (z23 + z31 - z12) / 2;
            if (_nextStar == 0) _nextStar = _buses.Keys.Max() + 1;
            while (_buses.ContainsKey(_nextStar)) _nextStar++;
            var star = new Bus
            {
                Id = _nextStar++, Name = name is null ? $"STAR {bi}-{bj}-{bk}" : $"STAR {name}", BaseKv = busI.BaseKv, Area = busI.Area, Zone = busI.Zone,
                Vm = l2.D(9, 1), VaDeg = l2.D(10, 0), Type = stat == 0 ? BusType.Isolated : BusType.PQ,
            };
            if (star.Vm <= 0) star.Vm = 1;
            _buses.Add(star.Id, star);
            net.Buses.Add(star);
            void Add(int from, Complex z, double tap, double ang, double rate, bool on, string suffix) => net.Branches.Add(new Branch
            {
                From = from, To = star.Id, Kind = BranchKind.Transformer, R = z.Real, X = z.Imaginary, Tap = tap, ShiftDeg = ang, RateA = rate,
                InService = on, Name = name is null ? null : $"{name} {suffix}",
            });
            Add(bi, z1, t1, w1.D(2), w1.D(3), stat is 1 or 2 or 3, "W1");
            Add(bj, z2, t2b, w2b.D(2), w2b.D(3), stat is 1 or 3 or 4, "W2");
            Add(bk, z3, t3, w3.D(2), w3.D(3), stat is 1 or 2 or 4, "W3");
            warnings.Add($"Three-winding transformer {bi}-{bj}-{bk} converted to a star equivalent with internal bus {star.Id}.");
        }

        public void Finish()
        {
            if (net.Buses.Count == 0) warnings.Add("No bus data found.");
            if (net.SlackBus is null && net.Buses.Count > 0) warnings.Add("No swing bus (IDE = 3) in the case.");
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ───────────────────────────── writer ─────────────────────────────

    /// <summary>
    /// Writes <paramref name="net"/> as PSS/E RAW revision <paramref name="version"/> (33 or 35). Bus shunts become fixed shunts,
    /// loads keep their ZIP split as PL/IP/YP, transformers are two-winding with CW = CZ = CM = 1 (WINDV1 = tap, ANG1 = phase shift).
    /// Charging susceptance on a transformer is written as magnetizing susceptance MAG2 (a warning is added).
    /// The output is deterministic, so <c>Write(Read(Write(net)))</c> reproduces the same text.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Version other than 33 or 35.</exception>
    public static string Write(PowerNetwork net, int version = 33, ICollection<string>? warnings = null)
    {
        if (version is not (33 or 35)) throw new ArgumentOutOfRangeException(nameof(version), "Supported RAW revisions: 33, 35.");
        bool v35 = version == 35;
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append('\n');
        void H(string s) { if (v35) L("@!" + s); }
        void End(string sec, string? next) => L(next is null ? $"0 / END OF {sec} DATA" : $"0 / END OF {sec} DATA, BEGIN {next} DATA");
        int nameLen = v35 ? 40 : 12;
        string Q(string? s, int pad = 0)
        {
            s = (s ?? "").Replace('\'', '"').Replace("\"", "");
            if (s.Length > nameLen) s = s[..nameLen];
            return "'" + s.PadRight(pad) + "'";
        }

        H("IC,SBASE,REV,XFRRAT,NXFRAT,BASFRQ");
        L($"0, {F(net.BaseMva)}, {version}, 0, 1, {F(net.FrequencyHz)}     / {ElectricalInfo.Name} {ElectricalInfo.Version}");
        L(net.Name.Replace('\n', ' '));
        L(ElectricalInfo.CreditEn);
        if (v35)
        {
            L("GENERAL, THRSHZ=0.0001, PQBRAK=0.7, BLOWUP=5.0, MAXISOLLVLS=4, CAMAXREPTSLN=20, CHKDUPCNTLBL=0");
            End("SYSTEM-WIDE", "BUS");
        }

        H("   I,'NAME        ', BASKV, IDE,AREA,ZONE,OWNER, VM, VA, NVHI, NVLO, EVHI, EVLO");
        foreach (var b in net.Buses)
            L($"{b.Id}, {Q(b.Name, 12)}, {F(b.BaseKv)}, {(int)b.Type}, {b.Area}, {b.Zone}, 1, {F(b.Vm)}, {F(b.VaDeg)}, {F(b.Vmax)}, {F(b.Vmin)}, {F(b.Vmax)}, {F(b.Vmin)}");
        End("BUS", "LOAD");

        H("   I,'ID',STAT,AREA,ZONE, PL, QL, IP, IQ, YP, YQ, OWNER,SCALE,INTRPT" + (v35 ? ",  DGENP,  DGENQ, DGENM, LOADTYPE" : ""));
        var loadIds = new Dictionary<int, int>();
        foreach (var l in net.Loads)
        {
            int n = loadIds[l.Bus] = loadIds.GetValueOrDefault(l.Bus) + 1;
            var bus = net.ContainsBus(l.Bus) ? net.GetBus(l.Bus) : null;
            var m = l.Model;
            double sum = m.Z + m.I + m.P;
            if (sum == 0) m = ZipModel.ConstantPower;
            string pl = F(l.P * m.P), ql = F(l.Q * m.P), ip = F(l.P * m.I), iq = F(l.Q * m.I), yp = F(l.P * m.Z), yq = F(-l.Q * m.Z);
            L($"{l.Bus}, '{n}', {(l.InService ? 1 : 0)}, {bus?.Area ?? 1}, {bus?.Zone ?? 1}, {pl}, {ql}, {ip}, {iq}, {yp}, {yq}, 1, 1, 0" +
              (v35 ? ", 0.0, 0.0, 0, ''" : ""));
        }
        End("LOAD", "FIXED SHUNT");

        H("   I,'ID',STATUS, GL, BL");
        foreach (var b in net.Buses.Where(b => b.Gs != 0 || b.Bs != 0))
            L($"{b.Id}, '1', 1, {F(b.Gs)}, {F(b.Bs)}");
        End("FIXED SHUNT", "GENERATOR");

        H(v35
            ? "   I,'ID', PG, QG, QT, QB, VS, IREG, NREG, MBASE, ZR, ZX, RT, XT, GTAP,STAT, RMPCT, PT, PB, BASLOD,O1, F1, O2, F2, O3, F3, O4, F4,WMOD, WPF"
            : "   I,'ID', PG, QG, QT, QB, VS, IREG, MBASE, ZR, ZX, RT, XT, GTAP,STAT, RMPCT, PT, PB, O1, F1, O2, F2, O3, F3, O4, F4,WMOD, WPF");
        var genIds = new Dictionary<int, int>();
        foreach (var g in net.Generators)
        {
            int n = genIds[g.Bus] = genIds.GetValueOrDefault(g.Bus) + 1;
            string head = $"{g.Bus}, '{n}', {F(g.P)}, {F(g.Q)}, {F(g.Qmax)}, {F(g.Qmin)}, {F(g.Vset)}, 0, ";
            string mach = $"{F(g.MBase)}, {F(g.Ra)}, {F(g.XdSubtransient)}, 0.0, 0.0, 1.0, {(g.InService ? 1 : 0)}, 100.0, {F(g.Pmax)}, {F(g.Pmin)}, ";
            L(head + (v35 ? "0, " : "") + mach + (v35 ? "0, " : "") + "1, 1.0, 0, 1.0, 0, 1.0, 0, 1.0, 0, 1.0");
        }
        End("GENERATOR", "BRANCH");

        string rates(double r) => string.Join(", ", Enumerable.Repeat(F(r), v35 ? 12 : 3));
        H(v35
            ? "   I,     J,'CKT', R, X, B,'NAME', RATE1, RATE2, RATE3, RATE4, RATE5, RATE6, RATE7, RATE8, RATE9, RATE10, RATE11, RATE12, GI, BI, GJ, BJ,STAT,MET, LEN, O1, F1, O2, F2, O3, F3, O4, F4"
            : "   I,     J,'CKT', R, X, B, RATEA, RATEB, RATEC, GI, BI, GJ, BJ, ST, MET, LEN, O1, F1, O2, F2, O3, F3, O4, F4");
        var ckts = new Dictionary<(int, int), int>();
        string Ckt(Branch br)
        {
            var key = (Math.Min(br.From, br.To), Math.Max(br.From, br.To));
            return (ckts[key] = ckts.GetValueOrDefault(key) + 1).ToString(Inv);
        }
        bool IsXfmr(Branch br) => br.Kind == BranchKind.Transformer || br.Tap != 0 || br.ShiftDeg != 0;
        foreach (var br in net.Branches.Where(b => !IsXfmr(b)))
        {
            string ckt = Ckt(br);
            L($"{br.From}, {br.To}, '{ckt}', {F(br.R)}, {F(br.X)}, {F(br.B)}, " + (v35 ? Q(br.Name) + ", " : "") + rates(br.RateA) +
              $", 0.0, 0.0, 0.0, 0.0, {(br.InService ? 1 : 0)}, 1, {F(br.LengthKm)}, 1, 1.0, 0, 1.0, 0, 1.0, 0, 1.0");
        }
        if (v35)
        {
            End("BRANCH", "SYSTEM SWITCHING DEVICE");
            End("SYSTEM SWITCHING DEVICE", "TRANSFORMER");
        }
        else End("BRANCH", "TRANSFORMER");

        if (v35)
        {
            L("@!   I,     J,     K,'CKT',CW,CZ,CM, MAG1, MAG2,NMETR,'NAME',STAT,O1, F1, O2, F2, O3, F3, O4, F4,'VECGRP',ZCOD");
            L("@!   R1-2, X1-2, SBASE1-2");
            L("@!WINDV1, NOMV1, ANG1, WDG1RATE1, WDG1RATE2, WDG1RATE3, WDG1RATE4, WDG1RATE5, WDG1RATE6, WDG1RATE7, WDG1RATE8, WDG1RATE9, WDG1RATE10, WDG1RATE11, WDG1RATE12,COD1,CONT1,NODE1, RMA1, RMI1, VMA1, VMI1, NTP1,TAB1, CR1, CX1, CNXA1");
            L("@!WINDV2, NOMV2");
        }
        foreach (var br in net.Branches.Where(IsXfmr))
        {
            string ckt = Ckt(br);
            if (br.B != 0) warnings?.Add($"Transformer {br.From}-{br.To}: charging B = {br.B.ToString("0.#####", Inv)} pu written as magnetizing susceptance MAG2.");
            L($"{br.From}, {br.To}, 0, '{ckt}', 1, 1, 1, 0.0, {F(br.B)}, 2, {Q(br.Name, 12)}, {(br.InService ? 1 : 0)}, 1, 1.0, 0, 1.0, 0, 1.0, 0, 1.0, '            '" +
              (v35 ? ", 0" : ""));
            L($"{F(br.R)}, {F(br.X)}, {F(net.BaseMva)}");
            L($"{F(br.EffectiveTap)}, 0.0, {F(br.ShiftDeg)}, {rates(br.RateA)}, 0, 0, " + (v35 ? "0, " : "") + "1.1, 0.9, 1.1, 0.9, 33, 0, 0.0, 0.0, 0.0");
            L("1.0, 0.0");
        }
        End("TRANSFORMER", "AREA");

        H("I, ISW, PDES, PTOL, 'ARNAME'");
        foreach (int a in net.Buses.Select(b => b.Area).Distinct().Order())
        {
            int sw = net.Buses.FirstOrDefault(b => b.Area == a && b.Type == BusType.Slack)?.Id ?? 0;
            L($"{a}, {sw}, 0.0, 10.0, 'AREA {a}'");
        }
        End("AREA", "TWO-TERMINAL DC");
        End("TWO-TERMINAL DC", "VSC DC LINE");
        End("VSC DC LINE", "IMPEDANCE CORRECTION");
        End("IMPEDANCE CORRECTION", "MULTI-TERMINAL DC");
        End("MULTI-TERMINAL DC", "MULTI-SECTION LINE");
        End("MULTI-SECTION LINE", "ZONE");
        H("I,'ZONAME'");
        foreach (int z in net.Buses.Select(b => b.Zone).Distinct().Order()) L($"{z}, 'ZONE {z}'");
        End("ZONE", "INTER-AREA TRANSFER");
        End("INTER-AREA TRANSFER", "OWNER");
        H("I,'OWNAME'");
        L("1, 'OWNER 1'");
        End("OWNER", "FACTS DEVICE");
        End("FACTS DEVICE", "SWITCHED SHUNT");
        End("SWITCHED SHUNT", "GNE DEVICE");
        End("GNE DEVICE", "INDUCTION MACHINE");
        if (v35)
        {
            End("INDUCTION MACHINE", "SUBSTATION");
            End("SUBSTATION", null);
        }
        else End("INDUCTION MACHINE", null);
        L("Q");
        return sb.ToString();
    }

    /// <summary>Writes a RAW file (see <see cref="Write"/>).</summary>
    public static void Save(PowerNetwork net, string path, int version = 33) => File.WriteAllText(path, Write(net, version));

    /// <summary>Shortest round-trip formatting with a decimal point; ±∞ written as ±9999.</summary>
    private static string F(double v)
    {
        if (double.IsPositiveInfinity(v)) v = 9999;
        if (double.IsNegativeInfinity(v)) v = -9999;
        if (double.IsNaN(v)) v = 0;
        if (v == 0) return "0.0";
        var s = v.ToString("R", Inv);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }
}
