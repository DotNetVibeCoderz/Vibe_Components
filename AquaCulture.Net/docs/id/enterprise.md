# Edisi enterprise (v2.0)

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/enterprise.md)

## Ketertelusuran: GS1 dan EPCIS 2.0 (`AquaCulture.Traceability`)

```csharp
var setup = new TraceabilitySetup(glnTambak, glnHatchery, gtinBenur, gtinPanen, prefixPerusahaan)
{
    FeedLots = new Dictionary<string, (string, string)> { ["GROWER-B"] = (gtinPakan, "GB-2607") },
};
var events = FarmEpcis.CycleEvents(farm, siklus, setup, seedLot: "PL10-2605-A")      // hatchery → kolam → panen
    .Concat(FarmEpcis.PackAndShip(Gs1.MakeSscc(3, prefixPerusahaan, 42), [new(lotPanen, 1000)], lokasiTambak, pabrik, waktu));
File.WriteAllText("siklus.epcis.jsonld", EpcisJson.ToDocument(events));           // EPCIS 2.0 JSON-LD untuk repositori EPCIS mana pun

var graf = new TraceGraph(events);
graf.TraceBack(Gs1.Sscc(sscc));                    // palet → lot panen → lot benur dan lot pakan
graf.TraceForward(Gs1.Lot(gtinPakan, "GB-2607"));  // recall: semua lot panen dan palet yang terkena lot pakan itu
```

- Pengenal memakai URI GS1 Digital Link (`https://id.gs1.org/01/{GTIN}/10/{lot}`, `/414/{GLN}/254/{kolam}`, `/00/{SSCC}`) dengan
  check digit yang sudah diverifikasi.
- Panen dicatat sebagai `TransformationEvent`: lot benur dan lot pakan masuk, lot panen keluar. ILMD-nya memuat data panen tambak
  versi GDST: spesies, metode produksi `AQUACULTURE`, tanggal panen, kolam, siklus, dan size per kg.

## Akuntansi ESG (`AquaCulture.Esg`)

```csharp
var laporanSiklus = EsgAccounting.Cycle(siklus, new CycleResources
{
    ElectricityKwh = 9_000, DieselLitres = 200, WaterIntakeM3 = 30_000, ConvertedMangroveHa = 0, RestoredMangroveHa = 2,
});
var laporanTambak = EsgAccounting.Farm(farm, c => sumberDaya(c));   // intensitas tertimbang panen + temuan (EN/ID)
```

| Indikator | Cara hitung |
|---|---|
| kg CO₂e per kg panen | pakan × jejak pakan + kWh × faktor grid + liter solar × 2,68 + konversi mangrove − restorasi |
| Konversi mangrove | 1.023 Mg C/ha (Donato dkk. 2011) × 44/12, dibagi rata 20 tahun sejak pembukaan (konvensi LCA), proporsional terhadap lama siklus |
| Energi, air | kWh/kg dan m³/kg; `PondWaterUse` memperkirakan pergantian air, penguapan, dan rembesan |
| Buangan N dan P | N pakan (protein / 6,25) dan P, ditambah biomassa tebar, dikurangi N dan P yang terpanen (kg per ton panen) |

Faktor grid (0,85 kg/kWh) dan jejak pakan (1,8 kg CO₂e/kg) adalah **nilai bawaan indikatif**. Isi `EsgFactors` dengan angka dari PLN dan
pabrik pakan Anda.
