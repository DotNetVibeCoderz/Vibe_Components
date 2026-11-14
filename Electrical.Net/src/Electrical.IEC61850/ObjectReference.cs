namespace Electrical.IEC61850;

/// <summary>Functional constraints (IEC 61850-7-2 §6.6).</summary>
public enum FunctionalConstraint
{
    /// <summary>Status information.</summary>
    ST,
    /// <summary>Measurands (analogue values).</summary>
    MX,
    /// <summary>Control.</summary>
    CO,
    /// <summary>Configuration.</summary>
    CF,
    /// <summary>Description.</summary>
    DC,
    /// <summary>Setting (outside setting groups).</summary>
    SP,
    /// <summary>Setting group.</summary>
    SG,
    /// <summary>Setting group editable.</summary>
    SE,
    /// <summary>Substitution.</summary>
    SV,
    /// <summary>Extended definition.</summary>
    EX,
    /// <summary>Service response.</summary>
    SR,
    /// <summary>Buffered report.</summary>
    BR,
    /// <summary>Unbuffered report.</summary>
    RP,
    /// <summary>Log control.</summary>
    LG,
    /// <summary>GOOSE control.</summary>
    GO,
    /// <summary>GSSE control.</summary>
    GS,
    /// <summary>Multicast sampled-value control.</summary>
    MS,
    /// <summary>Unicast sampled-value control.</summary>
    US,
    /// <summary>Operate received.</summary>
    OR,
    /// <summary>Blocking.</summary>
    BL,
}

/// <summary>
/// IEC 61850 object reference <c>LDName/LNName[.DO[.SDO…][.DA[.BDA…]]]</c>, optionally qualified by a functional constraint
/// written as <c>[FC]</c> suffix (e.g. <c>IED1CTRL/XCBR1.Pos.stVal[ST]</c>).
/// </summary>
public sealed record ObjectReference(string LogicalDevice, string LogicalNode, IReadOnlyList<string> Path, FunctionalConstraint? Fc = null)
{
    /// <summary>Parses "LD/LN.DO.DA" or "LD/LN.DO.DA[FC]".</summary>
    public static ObjectReference Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        FunctionalConstraint? fc = null;
        string s = text.Trim();
        if (s.EndsWith(']'))
        {
            int open = s.LastIndexOf('[');
            if (open < 0) throw new FormatException($"Invalid object reference '{text}'.");
            fc = Enum.Parse<FunctionalConstraint>(s[(open + 1)..^1], ignoreCase: true);
            s = s[..open];
        }
        int slash = s.IndexOf('/');
        if (slash <= 0) throw new FormatException($"Object reference '{text}' lacks a logical device ('LD/LN…').");
        var parts = s[(slash + 1)..].Split('.');
        if (parts[0].Length == 0 || parts.Any(p => p.Length == 0)) throw new FormatException($"Invalid object reference '{text}'.");
        return new ObjectReference(s[..slash], parts[0], parts[1..], fc);
    }

    public static bool TryParse(string text, out ObjectReference? reference)
    {
        try { reference = Parse(text); return true; }
        catch (Exception e) when (e is FormatException or ArgumentException) { reference = null; return false; }
    }

    /// <summary>Data object name (first path element) or null for an LN reference.</summary>
    public string? DataObject => Path.Count > 0 ? Path[0] : null;

    /// <summary>Appends a child name.</summary>
    public ObjectReference Child(string name, FunctionalConstraint? fc = null) => this with { Path = [.. Path, name], Fc = fc ?? Fc };

    /// <summary>MMS variable name inside the domain (LD): <c>LN$FC$DO$DA</c>. Requires a functional constraint.</summary>
    public string ToMmsVariable()
    {
        if (Fc is null) throw new InvalidOperationException("MMS names need a functional constraint.");
        return Path.Count == 0 ? $"{LogicalNode}${Fc}" : $"{LogicalNode}${Fc}${string.Join('$', Path)}";
    }

    /// <summary>Parses an MMS domain + item ("IED1CTRL", "XCBR1$ST$Pos$stVal").</summary>
    public static ObjectReference FromMms(string domain, string item)
    {
        var p = item.Split('$');
        if (p.Length < 2) return new ObjectReference(domain, p[0], []);
        return new ObjectReference(domain, p[0], p[2..], Enum.Parse<FunctionalConstraint>(p[1], true));
    }

    public bool Equals(ObjectReference? o) => o is not null && LogicalDevice == o.LogicalDevice && LogicalNode == o.LogicalNode && Fc == o.Fc &&
        Path.SequenceEqual(o.Path);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    public override string ToString() =>
        $"{LogicalDevice}/{LogicalNode}{(Path.Count > 0 ? "." + string.Join('.', Path) : "")}{(Fc is null ? "" : $"[{Fc}]")}";
}

/// <summary>Logical-node class description from IEC 61850-7-4.</summary>
public sealed record LogicalNodeClassInfo(string LnClass, char Group, string GroupName, string Description, string DescriptionId,
    IReadOnlyList<string> TypicalDataObjects);

/// <summary>Catalog of common logical-node classes (IEC 61850-7-4 Ed.2) with English and Indonesian descriptions.</summary>
public static class LogicalNodeCatalog
{
    private static readonly Dictionary<char, string> Groups = new()
    {
        ['A'] = "Automatic control", ['C'] = "Supervisory control", ['F'] = "Functional blocks", ['G'] = "Generic function references",
        ['I'] = "Interfacing and archiving", ['K'] = "Mechanical and non-electrical primary equipment", ['L'] = "System logical nodes",
        ['M'] = "Metering and measurement", ['P'] = "Protection functions", ['Q'] = "Power quality events detection",
        ['R'] = "Protection-related functions", ['S'] = "Supervision and monitoring", ['T'] = "Instrument transformers and sensors",
        ['X'] = "Switchgear", ['Y'] = "Power transformers and related functions", ['Z'] = "Further power system equipment",
    };

    private static readonly LogicalNodeClassInfo[] Items =
    [
        N("LLN0", "Logical node zero (logical device common data)", "Logical node nol (data umum perangkat logis)", "Mod", "Beh", "Health", "NamPlt"),
        N("LPHD", "Physical device information", "Informasi perangkat fisik", "PhyNam", "PhyHealth", "Proxy"),
        N("ATCC", "Automatic tap changer controller", "Pengendali pengubah tap otomatis", "TapChg", "BndCtr", "LTCBlk"),
        N("AVCO", "Voltage control", "Kendali tegangan", "SptVol", "BndWid"),
        N("ARCO", "Reactive power control", "Kendali daya reaktif", "VarSpt"),
        N("CSWI", "Switch controller", "Pengendali sakelar", "Pos", "OpOpn", "OpCls"),
        N("CILO", "Interlocking", "Interlock", "EnaOpn", "EnaCls"),
        N("CALH", "Alarm handling", "Penanganan alarm", "GrAlm", "GrWrn"),
        N("CPOW", "Point-on-wave switching", "Penyakelaran point-on-wave", "TmExc"),
        N("GGIO", "Generic process I/O", "I/O proses generik", "Ind", "SPCSO", "AnIn"),
        N("GAPC", "Generic automatic process control", "Kendali proses otomatis generik", "Str", "Op"),
        N("IHMI", "Human machine interface", "Antarmuka manusia-mesin"),
        N("ITCI", "Telecontrol interface", "Antarmuka telekontrol"),
        N("MMXU", "Measurement (three-phase, RMS)", "Pengukuran (tiga fasa, RMS)", "TotW", "TotVAr", "TotVA", "Hz", "PPV", "PhV", "A", "W", "PF"),
        N("MMXN", "Measurement (non-phase related)", "Pengukuran (tidak terkait fasa)", "Amp", "Vol", "Watt"),
        N("MMTR", "Metering (energy)", "Pengukuran energi", "TotWh", "TotVArh", "SupWh", "DmdWh"),
        N("MSQI", "Sequence and imbalance", "Urutan dan ketidakseimbangan", "SeqA", "SeqV", "ImbA"),
        N("MHAI", "Harmonics and interharmonics", "Harmonik dan interharmonik", "HA", "HPhV", "ThdA", "ThdPhV"),
        N("PTOC", "Time overcurrent protection", "Proteksi arus lebih waktu", "Str", "Op", "StrVal", "TmMult", "TmACrv"),
        N("PIOC", "Instantaneous overcurrent protection", "Proteksi arus lebih seketika", "Str", "Op", "StrVal"),
        N("PDIS", "Distance protection", "Proteksi jarak", "Str", "Op", "PoRch", "PhStr", "GndStr"),
        N("PDIF", "Differential protection", "Proteksi diferensial", "Str", "Op", "DifAClc", "RstA", "LinCapac"),
        N("PTOV", "Overvoltage protection", "Proteksi tegangan lebih", "Str", "Op", "StrVal"),
        N("PTUV", "Undervoltage protection", "Proteksi tegangan kurang", "Str", "Op", "StrVal"),
        N("PTOF", "Overfrequency protection", "Proteksi frekuensi lebih", "Str", "Op", "StrVal"),
        N("PTUF", "Underfrequency protection", "Proteksi frekuensi kurang", "Str", "Op", "StrVal"),
        N("PTRC", "Protection trip conditioning", "Pengkondisian trip proteksi", "Tr", "Op", "Str"),
        N("PDOP", "Directional overpower protection", "Proteksi daya lebih berarah", "Str", "Op"),
        N("PSCH", "Protection scheme (teleprotection)", "Skema proteksi (teleproteksi)", "ProTx", "ProRx", "Op"),
        N("PTEF", "Transient earth fault protection", "Proteksi gangguan tanah transien", "Str", "Op"),
        N("RREC", "Autoreclosing", "Penutup balik otomatis", "Op", "AutoRecSt", "RecCyc"),
        N("RBRF", "Breaker failure", "Kegagalan pemutus", "OpEx", "OpIn"),
        N("RDRE", "Disturbance recorder function", "Perekam gangguan", "RcdMade", "RcdStr"),
        N("RSYN", "Synchronism-check", "Pemeriksa sinkronisasi", "Rel", "VInd", "AngInd", "HzInd"),
        N("RFLO", "Fault locator", "Pencari lokasi gangguan", "FltZ", "FltDiskm"),
        N("SIMG", "Insulation medium supervision (gas)", "Pengawasan media isolasi (gas)", "Pres", "Den", "InsAlm"),
        N("SCBR", "Circuit breaker supervision", "Pengawasan pemutus tenaga", "ColOpn", "AbrAlm", "SumSwARs"),
        N("SPTR", "Power transformer supervision", "Pengawasan transformator daya", "HPTmpAlm", "GasInsAlm"),
        N("TCTR", "Current transformer", "Transformator arus", "AmpSv", "ARtg"),
        N("TVTR", "Voltage transformer", "Transformator tegangan", "VolSv", "VRtg"),
        N("XCBR", "Circuit breaker", "Pemutus tenaga (circuit breaker)", "Pos", "BlkOpn", "BlkCls", "CBOpCap", "OpCnt"),
        N("XSWI", "Circuit switch (disconnector, earthing switch)", "Sakelar (pemisah, sakelar pentanahan)", "Pos", "BlkOpn", "BlkCls", "SwTyp"),
        N("YPTR", "Power transformer", "Transformator daya", "HiTmpAlm", "OilFil"),
        N("YLTC", "Tap changer", "Pengubah tap", "TapPos", "EndPosR", "EndPosL"),
        N("YEFN", "Earth fault neutralizer (Petersen coil)", "Penetral gangguan tanah (kumparan Petersen)", "ColPos"),
        N("ZBAT", "Battery", "Baterai", "Vol", "Amp", "BatHi", "BatLo"),
        N("ZCAP", "Capacitor bank", "Bank kapasitor", "CapDS"),
        N("ZGEN", "Generator", "Generator", "GnOpSt"),
        N("ZLIN", "Power line", "Saluran daya", "LinLenKm", "RPs", "XPs"),
        N("ZINV", "Power converter / inverter", "Konverter daya / inverter", "WRtg", "VarRtg"),
        N("ZREA", "Reactor", "Reaktor", "ReaDS"),
    ];

    private static readonly Dictionary<string, LogicalNodeClassInfo> ByClass = Items.ToDictionary(i => i.LnClass, StringComparer.OrdinalIgnoreCase);

    private static LogicalNodeClassInfo N(string cls, string en, string id, params string[] dos) =>
        new(cls, cls[0], cls == "LLN0" || cls == "LPHD" ? Groups['L'] : Groups.GetValueOrDefault(cls[0], "Other"), en, id, dos);

    /// <summary>All catalogued LN classes.</summary>
    public static IReadOnlyList<LogicalNodeClassInfo> All => Items;

    /// <summary>Looks up a class (e.g. "XCBR"); null when unknown.</summary>
    public static LogicalNodeClassInfo? Find(string lnClass) => ByClass.GetValueOrDefault(lnClass);

    /// <summary>Group name for an LN class prefix letter (e.g. 'P' → "Protection functions").</summary>
    public static string GroupName(char group) => Groups.GetValueOrDefault(char.ToUpperInvariant(group), "Other");

    /// <summary>Human-readable description ("Circuit breaker"), or the group name for unknown classes.</summary>
    public static string Describe(string lnClass, bool indonesian = false) =>
        Find(lnClass) is { } i ? (indonesian ? i.DescriptionId : i.Description) : lnClass.Length > 0 ? GroupName(lnClass[0]) : "";
}
