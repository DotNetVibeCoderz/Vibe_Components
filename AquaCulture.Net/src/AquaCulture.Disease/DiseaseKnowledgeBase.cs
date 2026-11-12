using AquaCulture.Core;
using AquaCulture.Core.Species;

namespace AquaCulture.Disease;

public enum Pathogen { Virus, Bacterium, Microsporidian, Parasite, Fungus, Environmental }

/// <summary>Environmental conditions that raise disease risk.</summary>
public enum RiskFactor { HighTemperature, LowTemperature, TemperatureSwing, LowOxygen, HighAmmonia, SalinityDrop, HighSalinity, HighDensity, OrganicLoad, EarlyCulture, Handling }

/// <summary>An observable clinical sign.</summary>
public sealed record Symptom(string Id, Text2 Name);

/// <summary>A disease with its signs (P(sign | disease)), risk factors, treatment and prevention.</summary>
public sealed record DiseaseInfo
{
    public required string Id { get; init; }
    public required Text2 Name { get; init; }
    public required string Agent { get; init; }
    public Pathogen Pathogen { get; init; }
    public SpeciesGroup Group { get; init; }
    /// <summary>Restricts the disease to these species ids (empty = whole group).</summary>
    public IReadOnlyList<string> Species { get; init; } = [];
    /// <summary>Baseline prevalence among sick-stock reports (prior).</summary>
    public double Prior { get; init; } = 0.05;
    public required IReadOnlyDictionary<string, double> Signs { get; init; }
    public IReadOnlyList<(RiskFactor Factor, double Weight)> RiskFactors { get; init; } = [];
    public required Text2 Treatment { get; init; }
    public required Text2 Prevention { get; init; }
    /// <summary>Notifiable to the competent authority (WOAH-listed).</summary>
    public bool Notifiable { get; init; }

    public bool Affects(SpeciesProfile s) => s.Group == Group && (Species.Count == 0 || Species.Contains(s.Id));
}

/// <summary>Built-in knowledge base of the main shrimp and fish diseases in tropical aquaculture.</summary>
public static class DiseaseKnowledgeBase
{
    public static readonly IReadOnlyList<Symptom> Symptoms =
    [
        new("white_spots_shell", new("White spots on the shell", "Bintik putih pada karapas")),
        new("red_body", new("Reddish body discoloration", "Tubuh kemerahan")),
        new("lethargy", new("Lethargy, swimming at the edge", "Lemas, berenang di pinggir")),
        new("reduced_feeding", new("Reduced feeding", "Nafsu makan turun")),
        new("sudden_mass_mortality", new("Sudden mass mortality", "Kematian massal mendadak")),
        new("pale_hepatopancreas", new("Pale, shrunken hepatopancreas", "Hepatopankreas pucat dan mengecil")),
        new("empty_gut", new("Empty gut", "Usus kosong")),
        new("soft_shell", new("Soft shell", "Cangkang lunak")),
        new("white_feces", new("White feces strings", "Kotoran putih")),
        new("size_variation", new("Slow growth, large size variation", "Pertumbuhan lambat, ukuran tidak seragam")),
        new("white_muscle", new("Opaque white tail muscle", "Otot ekor putih keruh")),
        new("luminescence", new("Glows in the dark (luminous)", "Berpendar dalam gelap")),
        new("black_gills", new("Black or brown gills", "Insang hitam atau coklat")),
        new("gill_necrosis", new("Gill rot / necrosis", "Insang busuk / nekrosis")),
        new("gasping", new("Gasping at the surface", "Megap-megap di permukaan")),
        new("excess_mucus", new("Excess mucus", "Lendir berlebih")),
        new("frayed_fins", new("Frayed, eroded fins", "Sirip geripis")),
        new("skin_ulcers", new("Skin ulcers / red sores", "Borok / luka merah pada kulit")),
        new("hemorrhage", new("Haemorrhages on body or fins", "Pendarahan pada tubuh atau sirip")),
        new("exophthalmia", new("Pop-eye (exophthalmia)", "Mata menonjol (eksoftalmia)")),
        new("erratic_swimming", new("Erratic, spiral swimming", "Berenang tidak teratur / berputar")),
        new("abdominal_swelling", new("Swollen abdomen (dropsy)", "Perut kembung (dropsy)")),
        new("white_spots_skin", new("White spots on skin and fins", "Bintik putih pada kulit dan sirip")),
        new("flashing", new("Flashing / rubbing on objects", "Menggosokkan badan ke benda")),
        new("scale_loss", new("Skin erosion, scale loss", "Kulit terkikis, sisik lepas")),
        new("sunken_eyes", new("Sunken eyes", "Mata cekung")),
        new("cotton_growth", new("Cotton-like growth on skin", "Pertumbuhan seperti kapas pada kulit")),
    ];

    private static Dictionary<string, double> S(params (string, double)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    public static readonly IReadOnlyList<DiseaseInfo> Diseases =
    [
        new()
        {
            Id = "wssv", Name = new("White spot syndrome (WSSV)", "Bercak putih (WSSV)"), Agent = "White spot syndrome virus", Pathogen = Pathogen.Virus,
            Group = SpeciesGroup.Shrimp, Prior = 0.08, Notifiable = true,
            Signs = S(("white_spots_shell", 0.85), ("red_body", 0.6), ("reduced_feeding", 0.8), ("lethargy", 0.7), ("sudden_mass_mortality", 0.85)),
            RiskFactors = [(RiskFactor.TemperatureSwing, 0.5), (RiskFactor.LowTemperature, 0.4), (RiskFactor.SalinityDrop, 0.5), (RiskFactor.HighDensity, 0.3)],
            Treatment = new("No cure. Harvest early if animals are marketable, disinfect (chlorine 30 ppm) before discharge, notify the fisheries office.",
                            "Tidak ada obat. Panen dini bila sudah layak jual, disinfeksi (klorin 30 ppm) sebelum dibuang, laporkan ke dinas perikanan."),
            Prevention = new("SPF post-larvae (PCR-tested), crab/bird fences, reservoir treatment, avoid temperature and salinity shocks.",
                             "Benur SPF (uji PCR), pagar kepiting/burung, tandon dan sterilisasi air, hindari kejutan suhu dan salinitas."),
        },
        new()
        {
            Id = "ahpnd", Name = new("Acute hepatopancreatic necrosis (AHPND/EMS)", "Nekrosis hepatopankreas akut (AHPND/EMS)"), Agent = "Vibrio parahaemolyticus (pirAB toxin)",
            Pathogen = Pathogen.Bacterium, Group = SpeciesGroup.Shrimp, Prior = 0.06, Notifiable = true,
            Signs = S(("pale_hepatopancreas", 0.9), ("empty_gut", 0.8), ("soft_shell", 0.5), ("sudden_mass_mortality", 0.8), ("lethargy", 0.6), ("reduced_feeding", 0.6)),
            RiskFactors = [(RiskFactor.EarlyCulture, 0.7), (RiskFactor.HighSalinity, 0.4), (RiskFactor.HighTemperature, 0.3), (RiskFactor.OrganicLoad, 0.5)],
            Treatment = new("Stop feeding 1–2 days, siphon sludge, raise exchange; harvest if beyond DOC 30. Antibiotics are not recommended.",
                            "Hentikan pakan 1–2 hari, sipon lumpur, tambah pergantian air; panen bila sudah lewat DOC 30. Antibiotik tidak disarankan."),
            Prevention = new("PCR-screened PL, pond bottom drying and liming, biofloc or probiotic Bacillus, avoid overfeeding in the first 35 days.",
                             "Benur lolos uji PCR, pengeringan dan pengapuran dasar, bioflok atau probiotik Bacillus, hindari pakan berlebih pada 35 hari pertama."),
        },
        new()
        {
            Id = "ehp", Name = new("Hepatopancreatic microsporidiosis (EHP)", "Mikrosporidiosis hepatopankreas (EHP)"), Agent = "Enterocytozoon hepatopenaei",
            Pathogen = Pathogen.Microsporidian, Group = SpeciesGroup.Shrimp, Prior = 0.12,
            Signs = S(("size_variation", 0.9), ("white_feces", 0.5), ("empty_gut", 0.3), ("reduced_feeding", 0.4)),
            RiskFactors = [(RiskFactor.OrganicLoad, 0.5), (RiskFactor.HighDensity, 0.4)],
            Treatment = new("No treatment; spores persist. Reduce density, grade and harvest by size, disinfect with quicklime (CaO 6 t/ha) after harvest.",
                            "Tidak ada pengobatan; spora bertahan lama. Kurangi padat tebar, panen bertahap sesuai ukuran, desinfeksi kapur tohor (CaO 6 t/ha) setelah panen."),
            Prevention = new("EHP-free broodstock and PL, quicklime on pond bottoms, avoid fresh feeds (polychaetes, molluscs).",
                             "Induk dan benur bebas EHP, kapur tohor pada dasar kolam, hindari pakan segar (cacing laut, kerang)."),
        },
        new()
        {
            Id = "imnv", Name = new("Infectious myonecrosis (IMNV)", "Mionekrosis infeksius (IMNV)"), Agent = "Infectious myonecrosis virus", Pathogen = Pathogen.Virus,
            Group = SpeciesGroup.Shrimp, Species = ["vaname"], Prior = 0.03, Notifiable = true,
            Signs = S(("white_muscle", 0.9), ("lethargy", 0.5), ("reduced_feeding", 0.4), ("sudden_mass_mortality", 0.3)),
            RiskFactors = [(RiskFactor.TemperatureSwing, 0.5), (RiskFactor.SalinityDrop, 0.4), (RiskFactor.Handling, 0.4), (RiskFactor.LowOxygen, 0.3)],
            Treatment = new("No cure; minimise stress (no netting, stable temperature/salinity) and harvest early if mortality rises.",
                            "Tidak ada obat; kurangi stres (hindari penjaringan, stabilkan suhu/salinitas) dan panen dini bila kematian naik."),
            Prevention = new("SPF PL, avoid handling stress and sudden environmental changes.", "Benur SPF, hindari stres penanganan dan perubahan lingkungan mendadak."),
        },
        new()
        {
            Id = "vibriosis", Name = new("Vibriosis (incl. luminous vibriosis)", "Vibriosis (termasuk udang menyala)"), Agent = "Vibrio harveyi, V. alginolyticus",
            Pathogen = Pathogen.Bacterium, Group = SpeciesGroup.Shrimp, Prior = 0.10,
            Signs = S(("luminescence", 0.6), ("red_body", 0.5), ("reduced_feeding", 0.6), ("lethargy", 0.6), ("black_gills", 0.3), ("soft_shell", 0.3)),
            RiskFactors = [(RiskFactor.HighTemperature, 0.4), (RiskFactor.OrganicLoad, 0.6), (RiskFactor.HighSalinity, 0.3), (RiskFactor.LowOxygen, 0.3)],
            Treatment = new("Siphon sludge, add Bacillus probiotic and molasses, increase exchange; check total Vibrio count (TCBS) < 10³ CFU/mL.",
                            "Sipon lumpur, tambah probiotik Bacillus dan molase, tingkatkan pergantian air; jaga total Vibrio (TCBS) < 10³ CFU/mL."),
            Prevention = new("Good bottom management, stable plankton, avoid overfeeding.", "Pengelolaan dasar kolam yang baik, plankton stabil, hindari pakan berlebih."),
        },
        new()
        {
            Id = "wfd", Name = new("White feces disease", "Penyakit kotoran putih (WFD)"), Agent = "Gregarines / Vibrio / EHP complex", Pathogen = Pathogen.Bacterium,
            Group = SpeciesGroup.Shrimp, Prior = 0.07,
            Signs = S(("white_feces", 0.9), ("reduced_feeding", 0.5), ("empty_gut", 0.4), ("size_variation", 0.3)),
            RiskFactors = [(RiskFactor.OrganicLoad, 0.6), (RiskFactor.HighTemperature, 0.3), (RiskFactor.LowOxygen, 0.3)],
            Treatment = new("Reduce feed 30 %, add garlic-extract or probiotic in feed, siphon floating faeces, strengthen aeration.",
                            "Kurangi pakan 30 %, tambahkan ekstrak bawang putih atau probiotik dalam pakan, ambil kotoran yang mengapung, perkuat aerasi."),
            Prevention = new("Stable algae, regular siphoning, probiotics.", "Plankton stabil, sipon rutin, probiotik."),
        },
        new()
        {
            Id = "black_gill", Name = new("Black gill disease", "Penyakit insang hitam"), Agent = "Organic fouling / fungi / ciliates", Pathogen = Pathogen.Environmental,
            Group = SpeciesGroup.Shrimp, Prior = 0.05,
            Signs = S(("black_gills", 0.9), ("gasping", 0.4), ("lethargy", 0.3)),
            RiskFactors = [(RiskFactor.OrganicLoad, 0.8), (RiskFactor.LowOxygen, 0.4)],
            Treatment = new("Siphon the bottom, exchange water, raise ORP with aeration; shrimp clear the gills at the next moult.",
                            "Sipon dasar, ganti air, naikkan ORP dengan aerasi; insang bersih kembali pada molting berikutnya."),
            Prevention = new("Keep ORP above 100 mV and sludge low.", "Jaga ORP di atas 100 mV dan lumpur rendah."),
        },
        new()
        {
            Id = "columnaris", Name = new("Gill disease / columnaris", "Penyakit insang / kolumnaris"), Agent = "Flavobacterium columnare", Pathogen = Pathogen.Bacterium,
            Group = SpeciesGroup.Fish, Prior = 0.10,
            Signs = S(("gill_necrosis", 0.85), ("gasping", 0.7), ("excess_mucus", 0.5), ("frayed_fins", 0.4), ("skin_ulcers", 0.3), ("reduced_feeding", 0.6)),
            RiskFactors = [(RiskFactor.HighTemperature, 0.5), (RiskFactor.HighAmmonia, 0.5), (RiskFactor.Handling, 0.4), (RiskFactor.HighDensity, 0.3)],
            Treatment = new("Salt bath 1–3 % for 5–10 min or pond salt 3–5 ppt; potassium permanganate 2–4 mg/L; antibiotics only on veterinary prescription.",
                            "Rendam garam 1–3 % selama 5–10 menit atau garam kolam 3–5 ppt; kalium permanganat 2–4 mg/L; antibiotik hanya dengan resep dokter hewan."),
            Prevention = new("Low ammonia, careful handling, stable temperature.", "Amonia rendah, penanganan hati-hati, suhu stabil."),
        },
        new()
        {
            Id = "fin_rot", Name = new("Fin rot", "Busuk sirip"), Agent = "Aeromonas / Pseudomonas spp.", Pathogen = Pathogen.Bacterium,
            Group = SpeciesGroup.Fish, Prior = 0.10,
            Signs = S(("frayed_fins", 0.95), ("hemorrhage", 0.3), ("lethargy", 0.3), ("reduced_feeding", 0.3)),
            RiskFactors = [(RiskFactor.OrganicLoad, 0.5), (RiskFactor.HighDensity, 0.5), (RiskFactor.HighAmmonia, 0.4), (RiskFactor.Handling, 0.3)],
            Treatment = new("Improve water quality, salt 3 ppt, remove badly affected fish; treat with approved antibacterials if it spreads.",
                            "Perbaiki kualitas air, garam 3 ppt, pisahkan ikan yang parah; obati dengan antibakteri resmi bila menyebar."),
            Prevention = new("Lower density, regular exchange, avoid injuries.", "Kurangi kepadatan, ganti air rutin, hindari luka."),
        },
        new()
        {
            Id = "mas", Name = new("Ulcer disease / motile Aeromonas septicaemia (MAS)", "Penyakit borok / MAS (Aeromonas)"), Agent = "Aeromonas hydrophila",
            Pathogen = Pathogen.Bacterium, Group = SpeciesGroup.Fish, Prior = 0.12,
            Signs = S(("skin_ulcers", 0.85), ("hemorrhage", 0.7), ("abdominal_swelling", 0.4), ("exophthalmia", 0.3), ("reduced_feeding", 0.6), ("lethargy", 0.5)),
            RiskFactors = [(RiskFactor.TemperatureSwing, 0.5), (RiskFactor.LowOxygen, 0.4), (RiskFactor.HighAmmonia, 0.4), (RiskFactor.OrganicLoad, 0.4)],
            Treatment = new("Remove dead fish daily, salt 3–5 ppt, improve DO; oxytetracycline in feed only under veterinary advice and with withdrawal time.",
                            "Buang ikan mati setiap hari, garam 3–5 ppt, perbaiki DO; oksitetrasiklin dalam pakan hanya atas saran dokter hewan dan waktu henti obat."),
            Prevention = new("Vaccination (Aeromonas vaccine), stable water quality, avoid stress during rain season.",
                             "Vaksinasi (vaksin Aeromonas), kualitas air stabil, hindari stres saat musim hujan."),
        },
        new()
        {
            Id = "strep", Name = new("Streptococcosis", "Streptokokosis"), Agent = "Streptococcus agalactiae / S. iniae", Pathogen = Pathogen.Bacterium,
            Group = SpeciesGroup.Fish, Species = ["nila", "kakap", "kerapu", "bandeng"], Prior = 0.08,
            Signs = S(("exophthalmia", 0.7), ("erratic_swimming", 0.75), ("hemorrhage", 0.5), ("abdominal_swelling", 0.3), ("sudden_mass_mortality", 0.4), ("reduced_feeding", 0.6)),
            RiskFactors = [(RiskFactor.HighTemperature, 0.7), (RiskFactor.HighDensity, 0.5), (RiskFactor.LowOxygen, 0.4)],
            Treatment = new("Reduce feeding, lower density, keep water cool and oxygenated; licensed antibiotics after sensitivity testing.",
                            "Kurangi pakan, turunkan kepadatan, jaga air sejuk dan cukup oksigen; antibiotik resmi setelah uji sensitivitas."),
            Prevention = new("Streptococcus vaccine for tilapia, avoid water above 31 °C, moderate density.",
                             "Vaksin Streptococcus untuk nila, hindari suhu di atas 31 °C, kepadatan sedang."),
        },
        new()
        {
            Id = "tilv", Name = new("Tilapia lake virus (TiLV)", "Tilapia lake virus (TiLV)"), Agent = "Tilapia tilapinevirus", Pathogen = Pathogen.Virus,
            Group = SpeciesGroup.Fish, Species = ["nila"], Prior = 0.04, Notifiable = true,
            Signs = S(("scale_loss", 0.7), ("lethargy", 0.7), ("sudden_mass_mortality", 0.8), ("exophthalmia", 0.4), ("reduced_feeding", 0.7), ("skin_ulcers", 0.3)),
            RiskFactors = [(RiskFactor.HighTemperature, 0.4), (RiskFactor.Handling, 0.4), (RiskFactor.HighDensity, 0.3)],
            Treatment = new("No cure; isolate the unit, stop fish movements, disinfect equipment, notify authorities.",
                            "Tidak ada obat; isolasi unit, hentikan perpindahan ikan, disinfeksi peralatan, laporkan ke otoritas."),
            Prevention = new("Certified TiLV-free seed, quarantine 2–3 weeks, movement control.", "Benih bersertifikat bebas TiLV, karantina 2–3 minggu, kontrol lalu lintas ikan."),
        },
        new()
        {
            Id = "ich", Name = new("White spot disease (Ich)", "Penyakit bintik putih (Ich)"), Agent = "Ichthyophthirius multifiliis", Pathogen = Pathogen.Parasite,
            Group = SpeciesGroup.Fish, Prior = 0.08,
            Signs = S(("white_spots_skin", 0.9), ("flashing", 0.7), ("excess_mucus", 0.6), ("gasping", 0.4), ("reduced_feeding", 0.4)),
            RiskFactors = [(RiskFactor.LowTemperature, 0.6), (RiskFactor.TemperatureSwing, 0.5), (RiskFactor.HighDensity, 0.3)],
            Treatment = new("Raise temperature to 28–30 °C where possible and salt 3–5 ppt for 7–10 days; formalin baths per label.",
                            "Naikkan suhu ke 28–30 °C bila memungkinkan dan garam 3–5 ppt selama 7–10 hari; rendam formalin sesuai label."),
            Prevention = new("Quarantine new fish, avoid cold shocks.", "Karantina ikan baru, hindari kejutan dingin."),
        },
        new()
        {
            Id = "khv", Name = new("Koi herpesvirus disease (KHV)", "Penyakit koi herpesvirus (KHV)"), Agent = "Cyprinid herpesvirus 3", Pathogen = Pathogen.Virus,
            Group = SpeciesGroup.Fish, Species = ["koi"], Prior = 0.05, Notifiable = true,
            Signs = S(("gill_necrosis", 0.85), ("sunken_eyes", 0.7), ("excess_mucus", 0.5), ("sudden_mass_mortality", 0.8), ("lethargy", 0.6)),
            RiskFactors = [(RiskFactor.TemperatureSwing, 0.5), (RiskFactor.Handling, 0.4)],
            Treatment = new("No cure; isolate, stop movements, disinfect; survivors become carriers.", "Tidak ada obat; isolasi, hentikan perpindahan, disinfeksi; ikan yang selamat menjadi pembawa."),
            Prevention = new("PCR-tested koi, 4-week quarantine at 23–26 °C.", "Koi teruji PCR, karantina 4 minggu pada 23–26 °C."),
        },
        new()
        {
            Id = "saprolegnia", Name = new("Saprolegniasis (cotton wool)", "Saprolegniasis (jamur kapas)"), Agent = "Saprolegnia spp.", Pathogen = Pathogen.Fungus,
            Group = SpeciesGroup.Fish, Prior = 0.05,
            Signs = S(("cotton_growth", 0.95), ("skin_ulcers", 0.3), ("lethargy", 0.3)),
            RiskFactors = [(RiskFactor.LowTemperature, 0.6), (RiskFactor.Handling, 0.5), (RiskFactor.OrganicLoad, 0.3)],
            Treatment = new("Salt bath 2–3 %, remove dead eggs/fish, improve hygiene.", "Rendam garam 2–3 %, buang telur/ikan mati, perbaiki kebersihan."),
            Prevention = new("Avoid injuries, keep temperature stable.", "Hindari luka, jaga suhu stabil."),
        },
    ];

    public static DiseaseInfo Get(string id) => Diseases.First(d => d.Id == id);
    public static Symptom Symptom(string id) => Symptoms.First(s => s.Id == id);
    public static IEnumerable<DiseaseInfo> For(SpeciesProfile s) => Diseases.Where(d => d.Affects(s));

    /// <summary>Symptoms relevant to a species (union of its diseases' signs).</summary>
    public static IEnumerable<Symptom> SymptomsFor(SpeciesProfile s)
    {
        var ids = For(s).SelectMany(d => d.Signs.Keys).ToHashSet();
        return Symptoms.Where(x => ids.Contains(x.Id));
    }
}
