# Memulai

## Kebutuhan

- .NET 10 SDK
- Opsional: Rust (stable) untuk membangun ulang kernel native — binari siap pakai sudah ada di paket `Medical.Native`, dan fallback managed berjalan di semua platform
- Opsional: Python 3.10+ untuk layanan pencitraan MONAI, SQL Server dan Redis untuk EMR produksi

## Instalasi

```bash
dotnet add package Medical.Rules        # sekaligus Medical.Clinical, Medical.Terminology, Medical.Core
dotnet add package Medical.Fhir
```

## Rekam pasien

Semua fitur Medical.Net bekerja pada `PatientRecord` yang immutable (pasien, kunjungan, tanda vital, observasi, diagnosis, obat, alergi). Untuk percobaan gunakan generator deterministik — tanpa data pasien asli dan hasilnya sama di setiap mesin:

```csharp
using Medical.Core;

PatientRecord record = SyntheticData.Record(index: 3, deterioration: 0.8);
Patient p = record.Patient;                     // nama, nomor RM, NIK (struktur valid, provinsi 99), tanggal lahir…
VitalSigns? v = record.LatestVitals;
ClinicalObservation? laktat = record.Latest(Loinc.Lactate);
```

## Skor dan dukungan keputusan

```csharp
ScoreResult news2 = ClinicalScores.News2(v!);
Console.WriteLine($"{news2.Score} {news2.Risk}: {news2.Interpretation.Id}");

foreach (ClinicalAlert a in new ClinicalDecisionSupport().Evaluate(record))
    Console.WriteLine($"[{a.Severity}] {a.Title.Id} — {a.Detail.Id}");
```

## Ekspor ke FHIR

```csharp
var mapper = new FhirMapper();
var ips = mapper.ToPatientSummary(record);                // dokumen FHIR R4 (gaya IPS)
Console.WriteLine(FhirJson.Validate(ips).Issue.Count);    // 0
File.WriteAllText("ips.json", FhirJson.Serialize(ips));
var tx = mapper.ToTransaction(record);                    // transaksi PUT per id untuk server FHIR mana pun
```

## Dependency injection

```csharp
services.AddMedical(m => m
    .AddTerminology()
    .AddRules()
    .AddFhir()
    .AddPrivacy(pseudonymSecret: config["Privacy:Secret"])
    .AddEmr(o => { o.Provider = EmrProvider.SqlServer; o.ConnectionString = config["ConnectionStrings:Emr"]!; o.Redis = "localhost:6379"; })
    .AddAI());
```

Selanjutnya: [Arsitektur](architecture.md) · [Modul](modules.md) · [Perkakas](tools.md)
