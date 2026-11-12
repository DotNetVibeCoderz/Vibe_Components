using AquaCulture.Analytics;
using AquaCulture.Core;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Reporting;

public enum ComplianceStandard
{
    /// <summary>Good Aquaculture Practices (FAO technical guidelines / national GAP).</summary>
    Gap,
    /// <summary>Cara Budidaya Ikan yang Baik (Indonesia, KKP).</summary>
    Cbib,
    /// <summary>Aquaculture Stewardship Council farm standard.</summary>
    Asc,
    /// <summary>Best Aquaculture Practices (Global Seafood Alliance) farm standard.</summary>
    Bap,
}

public enum ComplianceSeverity { Minor, Major, Critical }

public enum ComplianceAnswer { Yes, Partial, No, NotApplicable }

public sealed record ChecklistItem(string Id, Text2 Category, Text2 Requirement, ComplianceSeverity Severity, double Weight = 1);

public sealed record NonConformity(ChecklistItem Item, ComplianceAnswer Answer);

public enum ReadinessLevel { Ready, MinorGaps, NotReady }

public sealed record ComplianceResult(ComplianceStandard Standard, double ScorePct, ReadinessLevel Readiness, int Answered, int Applicable,
    IReadOnlyList<NonConformity> NonConformities)
{
    public int Majors => NonConformities.Count(n => n.Item.Severity >= ComplianceSeverity.Major && n.Answer == ComplianceAnswer.No);
    public Text2 ReadinessText => Readiness switch
    {
        ReadinessLevel.Ready => new("Ready for audit", "Siap diaudit"),
        ReadinessLevel.MinorGaps => new("Minor gaps to close", "Ada kekurangan kecil"),
        _ => new("Not ready: close major non-conformities first", "Belum siap: tutup ketidaksesuaian mayor terlebih dulu"),
    };
}

/// <summary>
/// Self-assessment checklists for GAP, CBIB, ASC and BAP. These condense the main clauses of each scheme for farm readiness;
/// they are not a substitute for the official standards and accredited audits.
/// </summary>
public static class ComplianceAudit
{
    private static ChecklistItem I(string id, Text2 cat, string en, string idn, ComplianceSeverity sev = ComplianceSeverity.Minor, double w = 1) => new(id, cat, new(en, idn), sev, w);

    private static readonly Text2 Site = new("Site & infrastructure", "Lokasi & sarana"), Inputs = new("Seed, feed & chemicals", "Benih, pakan & bahan kimia"),
        Health = new("Animal health & biosecurity", "Kesehatan ikan & biosekuriti"), Env = new("Environment", "Lingkungan"),
        Food = new("Food safety & harvest", "Keamanan pangan & panen"), Records = new("Records & traceability", "Pencatatan & ketertelusuran"),
        Social = new("People & community", "Pekerja & masyarakat"), Welfare = new("Animal welfare", "Kesejahteraan hewan");

    public static IReadOnlyList<ChecklistItem> Checklist(ComplianceStandard s) => s switch
    {
        ComplianceStandard.Gap =>
        [
            I("gap-1", Site, "Site free from pollution sources and flooding; land use is legal", "Lokasi bebas sumber pencemaran dan banjir; penggunaan lahan legal", ComplianceSeverity.Major),
            I("gap-2", Site, "Separate inlet and outlet; reservoir or settling pond", "Saluran masuk dan buang terpisah; ada tandon atau kolam pengendapan"),
            I("gap-3", Inputs, "Seed from certified hatcheries, health-checked", "Benih dari pembenihan bersertifikat dan sudah diperiksa kesehatannya", ComplianceSeverity.Major),
            I("gap-4", Inputs, "Feed stored dry, off the floor, used before expiry", "Pakan disimpan kering, tidak menempel lantai, dipakai sebelum kedaluwarsa"),
            I("gap-5", Inputs, "Only approved veterinary drugs, with withdrawal periods respected", "Hanya obat ikan resmi dan masa henti obat dipatuhi", ComplianceSeverity.Critical, 2),
            I("gap-6", Health, "Biosecurity plan: footbaths, equipment disinfection, visitor control", "Rencana biosekuriti: celup kaki, disinfeksi alat, kontrol tamu", ComplianceSeverity.Major),
            I("gap-7", Health, "Daily mortality removal and disposal", "Pengambilan dan pemusnahan ikan mati setiap hari"),
            I("gap-8", Env, "Effluent treated before discharge; sludge managed", "Air buangan diolah sebelum dibuang; lumpur dikelola", ComplianceSeverity.Major),
            I("gap-9", Food, "Hygienic harvest with ice/cold chain", "Panen higienis dengan es/rantai dingin"),
            I("gap-10", Records, "Records of stocking, feed, water quality, treatments and harvest", "Catatan tebar, pakan, kualitas air, pengobatan dan panen", ComplianceSeverity.Major, 1.5),
            I("gap-11", Social, "Workers trained and equipped with protective gear", "Pekerja terlatih dan dilengkapi alat pelindung diri"),
        ],
        ComplianceStandard.Cbib =>
        [
            I("cbib-1", Site, "Lokasi sesuai tata ruang dan bebas pencemaran", "Lokasi sesuai tata ruang dan bebas pencemaran", ComplianceSeverity.Major),
            I("cbib-2", Site, "Inlet/outlet separated, fenced against wild animals", "Saluran pemasukan/pengeluaran terpisah, berpagar dari hewan liar"),
            I("cbib-3", Inputs, "Seed from CPIB-certified hatchery (Cara Pembenihan Ikan yang Baik)", "Benih dari pembenihan bersertifikat CPIB", ComplianceSeverity.Major),
            I("cbib-4", Inputs, "Feed registered with KKP; no feed with hormones", "Pakan terdaftar di KKP; tanpa pakan berhormon", ComplianceSeverity.Major),
            I("cbib-5", Inputs, "Fish drugs registered with KKP, used per dose and label", "Obat ikan terdaftar KKP, dipakai sesuai dosis dan label", ComplianceSeverity.Major),
            I("cbib-6", Inputs, "No banned substances (chloramphenicol, nitrofurans, malachite green)", "Tidak memakai bahan terlarang (kloramfenikol, nitrofuran, malachite green)", ComplianceSeverity.Critical, 2),
            I("cbib-7", Health, "Disease monitoring and reporting to the fisheries office", "Pemantauan penyakit dan pelaporan ke dinas perikanan"),
            I("cbib-8", Health, "Sanitation of ponds, tools and workers", "Sanitasi kolam, peralatan dan pekerja"),
            I("cbib-9", Env, "Waste and effluent managed", "Limbah dan air buangan dikelola", ComplianceSeverity.Major),
            I("cbib-10", Food, "Harvest and handling keep the product clean and cold", "Panen dan penanganan menjaga produk bersih dan dingin"),
            I("cbib-11", Records, "Production records kept (stocking, feed, drugs, water, harvest)", "Pencatatan produksi (tebar, pakan, obat, air, panen)", ComplianceSeverity.Major, 1.5),
            I("cbib-12", Social, "Technical person in charge trained in CBIB", "Penanggung jawab teknis terlatih CBIB"),
        ],
        ComplianceStandard.Asc =>
        [
            I("asc-1", Site, "Legal compliance: land and water rights, permits", "Kepatuhan hukum: hak lahan dan air, perizinan", ComplianceSeverity.Critical, 2),
            I("asc-2", Env, "No conversion of mangroves or sensitive habitat after 1999", "Tidak ada konversi mangrove/habitat sensitif setelah 1999", ComplianceSeverity.Critical, 2),
            I("asc-3", Env, "Effluent monitoring (DO, TSS, N, P) within limits", "Pemantauan air buangan (DO, TSS, N, P) dalam batas", ComplianceSeverity.Major),
            I("asc-4", Env, "Non-lethal predator control; escape prevention", "Pengendalian predator tidak mematikan; pencegahan ikan lepas", ComplianceSeverity.Major),
            I("asc-5", Inputs, "Responsible feed: traceable marine ingredients, FFDR tracked", "Pakan bertanggung jawab: bahan laut tertelusur, FFDR dicatat", ComplianceSeverity.Major),
            I("asc-6", Health, "Veterinary health plan; no prophylactic antibiotics", "Rencana kesehatan hewan; tanpa antibiotik pencegahan", ComplianceSeverity.Major),
            I("asc-7", Health, "No antimicrobials critically important for human medicine", "Tidak ada antimikroba yang sangat penting bagi manusia", ComplianceSeverity.Critical, 2),
            I("asc-8", Social, "No child or forced labour; fair wages; freedom of association", "Tanpa pekerja anak/paksa; upah layak; kebebasan berserikat", ComplianceSeverity.Critical, 2),
            I("asc-9", Social, "Health & safety program and worker grievance mechanism", "Program K3 dan mekanisme pengaduan pekerja"),
            I("asc-10", Social, "Community consultation and conflict resolution", "Konsultasi dan penyelesaian konflik dengan masyarakat"),
            I("asc-11", Env, "Energy and greenhouse-gas records", "Pencatatan energi dan emisi gas rumah kaca"),
            I("asc-12", Records, "Chain of custody and traceability to pond", "Lacak balak dan ketertelusuran sampai kolam", ComplianceSeverity.Major),
        ],
        _ =>
        [
            I("bap-1", Site, "Property rights and regulatory compliance documented", "Hak properti dan kepatuhan regulasi terdokumentasi", ComplianceSeverity.Major),
            I("bap-2", Social, "Community relations; no restriction of public access to resources", "Hubungan masyarakat; tidak membatasi akses publik ke sumber daya"),
            I("bap-3", Social, "Worker safety, training, wages and housing meet law", "Keselamatan, pelatihan, upah dan perumahan pekerja sesuai hukum", ComplianceSeverity.Major),
            I("bap-4", Env, "Effluent monitoring and sediment management", "Pemantauan air buangan dan pengelolaan sedimen", ComplianceSeverity.Major),
            I("bap-5", Env, "Soil and freshwater conservation; salinisation prevented", "Konservasi tanah dan air tawar; salinisasi dicegah"),
            I("bap-6", Inputs, "Chemicals and fuel stored safely; drugs approved and recorded", "Bahan kimia dan BBM disimpan aman; obat resmi dan tercatat", ComplianceSeverity.Major),
            I("bap-7", Welfare, "Stocking density, handling and humane harvest/stunning", "Kepadatan, penanganan dan panen/pemingsanan yang manusiawi", ComplianceSeverity.Major),
            I("bap-8", Health, "Biosecurity and disease management plan", "Rencana biosekuriti dan pengelolaan penyakit", ComplianceSeverity.Major),
            I("bap-9", Food, "Residue and microbial testing; withdrawal periods", "Uji residu dan mikroba; masa henti obat", ComplianceSeverity.Critical, 2),
            I("bap-10", Records, "Lot traceability from pond to processor", "Ketertelusuran lot dari kolam ke unit pengolahan", ComplianceSeverity.Major),
        ],
    };

    public static ComplianceResult Evaluate(ComplianceStandard standard, IReadOnlyDictionary<string, ComplianceAnswer> answers)
    {
        var items = Checklist(standard);
        double num = 0, den = 0; int answered = 0, applicable = 0;
        var ncs = new List<NonConformity>();
        bool blocking = false;
        foreach (var it in items)
        {
            if (!answers.TryGetValue(it.Id, out var a)) a = ComplianceAnswer.No; else answered++;
            if (a == ComplianceAnswer.NotApplicable) continue;
            applicable++;
            den += it.Weight;
            num += it.Weight * (a == ComplianceAnswer.Yes ? 1 : a == ComplianceAnswer.Partial ? 0.5 : 0);
            if (a != ComplianceAnswer.Yes) ncs.Add(new NonConformity(it, a));
            if (a == ComplianceAnswer.No && it.Severity >= ComplianceSeverity.Major) blocking = true;
        }
        double pct = den == 0 ? 100 : Math.Round(100 * num / den, 1);
        var readiness = !blocking && pct >= 90 ? ReadinessLevel.Ready : !blocking && pct >= 70 ? ReadinessLevel.MinorGaps : ReadinessLevel.NotReady;
        return new ComplianceResult(standard, pct, readiness, answered, applicable,
            ncs.OrderByDescending(n => n.Item.Severity).ThenBy(n => n.Answer == ComplianceAnswer.No ? 0 : 1).ToList());
    }
}

/// <summary>Ready-made reports.</summary>
public static class Reports
{
    /// <summary>Cycle report: KPIs, benchmarks, costs, optional growth chart and water-quality table.</summary>
    public static ReportDocument Cycle(CycleKpis k, string pondName, string lang = "en", byte[]? growthChartPng = null,
        IEnumerable<(DateTime Time, double Do, double Temp, double Ph, double Tan)>? water = null)
    {
        string T(string en, string id) => lang == "id" ? id : en;
        var d = new ReportDocument
        {
            Language = lang,
            Title = T($"Culture cycle report — {pondName}", $"Laporan siklus budidaya — {pondName}"),
            Subtitle = $"{k.Species} · {T("cycle", "siklus")} {k.CycleId} · DOC {k.DayOfCulture}",
        };
        d.H(T("Key performance indicators", "Indikator kinerja utama"));
        d.Kv(("ABW (g)", $"{k.Abw:0.##}"), ("ADG (g/day)", $"{k.Adg:0.###}"), ("SGR (%/day)", $"{k.Sgr:0.###}"), ("SR (%)", $"{k.Sr:0.#}"),
             (T("Biomass (kg)", "Biomassa (kg)"), $"{k.BiomassKg:0.#}"), (T("Harvested (kg)", "Dipanen (kg)"), $"{k.HarvestedKg:0.#}"),
             ("FCR", $"{k.Fcr:0.##}"), (T("Productivity (t/ha)", "Produktivitas (t/ha)"), $"{k.ProductivityTonHa:0.##}"),
             (T("Revenue", "Pendapatan"), $"{k.Revenue:N0}"), (T("Total cost", "Total biaya"), $"{k.Costs.Total:N0}"), (T("Profit", "Laba"), $"{k.Profit:N0}"),
             (T("Break-even price / kg", "Harga impas / kg"), $"{k.BreakEvenPrice:N0}"), ("B/C", $"{k.Bcr:0.##}"));
        if (k.WaterHealth is { } wh) d.Kv((T("Water Health Index", "Indeks Kesehatan Air"), $"{wh.Index:0.#}"), (T("Samples good or better (%)", "Sampel baik atau lebih (%)"), $"{wh.PercentGoodOrBetter:0.#}"));
        d.H(T("Benchmarks", "Pembanding"), 2);
        d.List(k.Benchmarks.Select(b => b.Comment[lang]).ToArray());
        d.H(T("Costs", "Biaya"), 2);
        d.Table(T("Costs", "Biaya"), [T("Item", "Komponen"), T("Amount", "Jumlah"), "%"],
            k.Costs.Items.Select(i => (IReadOnlyList<object?>)new object?[] { i.Name[lang], Math.Round(i.Amount), k.Costs.Total > 0 ? Math.Round(100 * i.Amount / k.Costs.Total, 1) : 0.0 }));
        if (growthChartPng is not null) d.Image(growthChartPng, T("Growth curve", "Kurva pertumbuhan"));
        if (water is not null)
            d.Table(T("Water quality", "Kualitas air"), [T("Time", "Waktu"), "DO", T("Temp", "Suhu"), "pH", "TAN"],
                water.Select(w => (IReadOnlyList<object?>)new object?[] { w.Time, w.Do, w.Temp, w.Ph, w.Tan }));
        return d;
    }

    /// <summary>Compliance self-assessment report.</summary>
    public static ReportDocument Compliance(ComplianceResult r, string farmName, string lang = "en")
    {
        string T(string en, string id) => lang == "id" ? id : en;
        var d = new ReportDocument { Language = lang, Title = $"{r.Standard.ToString().ToUpperInvariant()} — {farmName}", Subtitle = T("Compliance self-assessment", "Penilaian mandiri kepatuhan") };
        d.Kv((T("Score", "Skor"), $"{r.ScorePct:0.#} %"), (T("Readiness", "Kesiapan"), r.ReadinessText[lang]), (T("Major non-conformities", "Ketidaksesuaian mayor"), r.Majors.ToString()));
        d.Table(T("Non-conformities", "Ketidaksesuaian"), [T("Clause", "Butir"), T("Category", "Kategori"), T("Requirement", "Persyaratan"), T("Severity", "Tingkat"), T("Answer", "Jawaban")],
            r.NonConformities.Select(n => (IReadOnlyList<object?>)new object?[] { n.Item.Id, n.Item.Category[lang], n.Item.Requirement[lang], n.Item.Severity.ToString(), n.Answer.ToString() }));
        return d;
    }
}

public static class ReportingBuilderExtensions
{
    public static AquaBuilder AddReporting(this AquaBuilder b) => b.TryAddModule("Reporting", _ => { });
}
