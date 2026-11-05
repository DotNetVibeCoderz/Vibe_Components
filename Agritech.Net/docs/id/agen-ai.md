# Agen pertanian AI

`FarmAgent` menjawab pertanyaan petani dalam **Bahasa Indonesia atau Inggris**. Agen tidak mengarang angka: model bahasa memilih alat,
lalu alat tersebut menghitung jawaban dari data kebun menggunakan modul-modul Agritech.Net.

```csharp
var agen = new FarmAgent(new FarmContext
{
    FarmName = "Kebun Sukamaju", CropId = "rice", PlantingDate = new(2026, 10, 20), AreaHa = 2,
    Soil = new SoilSample { Ph = 5.4, OrganicCarbonPct = 1.2 }, Location = new(-6.3045, 107.3012),
});
Console.WriteLine(agen.Ask("Berapa dosis pupuk terbaik minggu ini?"));
```

Contoh jawaban nyata (Azure OpenAI gpt-5-mini, padi 24 HST, 2 ha):

> Yang diaplikasikan: Urea (susulan 1). Dosis: 24,0 kg/ha → total untuk 2,00 ha = 48,0 kg urea (jadwal 25–32 HST)…

## Alat yang tersedia
`farm_overview`, `fertilizer_plan`, `irrigation_advice`, `weather_forecast`, `disease_risk`, `pest_risk`, `yield_estimate`, `soil_health`, `crop_recommendation`.

## Penyedia LLM
Semua endpoint Chat Completions yang kompatibel dengan OpenAI dan mendukung *tool calling* dapat dipakai: OpenAI, Azure OpenAI, Hugging Face Router, Ollama, vLLM, dan LM Studio.

| Variabel lingkungan | Contoh |
|---|---|
| `AGRITECH_LLM_PROVIDER` | `azure`, `openai`, `hf`, `ollama`, `custom` |
| `AGRITECH_LLM_ENDPOINT` | `https://sumberdaya-anda.openai.azure.com/` |
| `AGRITECH_LLM_API_KEY` | *(rahasia: jangan disimpan di repo)* |
| `AGRITECH_LLM_MODEL` | `gpt-5-mini` |

## Mode luring
Tanpa konfigurasi, `RuleBasedChatModel` memilih alat berdasarkan kata kunci dwibahasa (pupuk, irigasi, hama, penyakit, cuaca, panen, dan sebagainya)
sehingga agen tetap dapat dipakai di perangkat edge tanpa internet.
