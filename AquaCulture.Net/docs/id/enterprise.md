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

## Hatchery udang (`AquaCulture.Hatchery`)

```csharp
var induk = new BroodstockUnit();                          // induk SPF + lolos PCR (WSSV, IHHNV, TSV, YHV, IMNV, AHPND, EHP)
induk.Spawnings.Add(new Spawning(tanggal, "F01", "M01", eggs: 250_000, nauplii: 180_000));
var afkir = induk.CullCandidates(hariIni);                 // betina lemah atau tidak memijah

var batch = new LarvalBatch { Id = "B1", TankId = "LRT-04", Stocked = hari0, NaupliiStocked = 2_000_000, TankVolumeM3 = 10, TemperatureC = 29 };
var (stadia, hariPl) = batch.StageOn(hariIni);             // N → Z1–Z3 → M1–M3 → PL (PL1 ≈ hari ke-8–9 pada 29 °C, Q10 2,2)
var pakan = batch.DailyFeed(hariIni);                      // liter alga, juta Artemia, gram mikrodiet

var mutu = PlQualityAssessment.Evaluate(new PlSample { PlDay = 10, StressTestSurvival = 0.92, LengthCv = 0.10, MuscleGutRatio = 4.5, Pcr = pcr });
var eventLotBenur = SeedLots.Commissioning(batch, induk, sampel, gtinBenur, glnHatchery, tanggalPanen);   // EPCIS, silsilah dan PCR di ILMD
```

- Mutu PL diberi skor 0–100. Lot dinyatakan tidak lulus jika ada PCR positif, sintasan uji stres di bawah 80 %, atau nekrosis di atas 5 %.
- Durasi stadia, takaran pakan, dan sintasan adalah praktik hatchery yang **indikatif**. Sesuaikan dengan protokol Anda.

## ERP tambak (`AquaCulture.Operations`)

```csharp
var gudang = new Inventory().Add(new InventoryItem("GROWER-2", "Pakan grower 2 mm", ItemCategory.Feed) { LeadTimeDays = 7, SafetyDays = 5 });
var po = new PurchaseOrder { Number = "PO-001", Supplier = "PT Pakan", Date = hariIni };
po.Lines.Add(new PoLine("GROWER-2", 2000, 16_000));
po.Place().Receive(gudang, "GROWER-2", 2000, lotCode: "G2-2606", hariIni, expiry: hariIni.AddMonths(4));   // masuk stok sebagai lot

gudang.IssueFeed("GROWER-2", 120, hariIni, siklus);  // yang paling dulu kedaluwarsa dipakai dulu; tercatat di siklus dengan kode lot dan harga
var saran = gudang.Reorder("GROWER-2", hariIni, plannedDailyUse: rencana.DailyKg);   // titik pesan = pemakaian × (lead time + stok pengaman)

var slip = Payroll.Monthly(karyawan, new Timesheet(karyawan.Id, 25, jamLembur), harvestKg: 12_000, thrHoliday: idulFitri);
```

- Pakan yang dikeluarkan dari gudang tercatat di siklus per lot, dengan kode lot sebagai id pakan dan harga lot sebagai harga. Dengan begitu
  biaya pakan, FCR, dan ketertelusuran EPCIS memakai lot yang sama.
- Penggajian mengikuti aturan Indonesia:
  - Lembur: jam pertama 1,5×, jam berikutnya 2× dari 1/173 upah sebulan.
  - BPJS: JHT 3,7 % + 2 %, JKK sesuai kelas risiko, JKM 0,3 %, JP 2 % + 1 % hingga batas upahnya, dan Kesehatan 4 % + 1 % hingga batasnya.
  - Bonus panen per kg.
  - THR: satu bulan upah setelah 12 bulan bekerja, proporsional jika kurang dari itu.
- Tarif dan batas upah ada di `PayrollRules`; perbarui setiap kali regulasi berubah. PPh 21 tidak dihitung.