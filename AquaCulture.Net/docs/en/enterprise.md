# Enterprise edition (v2.0)

*Made by Gravicode Studios, led by Kang Fadhil.* · [Bahasa Indonesia](../id/enterprise.md)

## Traceability: GS1 and EPCIS 2.0 (`AquaCulture.Traceability`)

```csharp
var setup = new TraceabilitySetup(farmGln, hatcheryGln, seedGtin, harvestGtin, companyPrefix)
{
    FeedLots = new Dictionary<string, (string, string)> { ["GROWER-B"] = (feedGtin, "GB-2607") },
};
var events = FarmEpcis.CycleEvents(farm, cycle, setup, seedLot: "PL10-2605-A")       // hatchery → pond → harvest
    .Concat(FarmEpcis.PackAndShip(Gs1.MakeSscc(3, companyPrefix, 42), [new(harvestLot, 1000)], farmLocation, plant, time));
File.WriteAllText("cycle.epcis.jsonld", EpcisJson.ToDocument(events));            // EPCIS 2.0 JSON-LD for any EPCIS repository

var graph = new TraceGraph(events);
graph.TraceBack(Gs1.Sscc(sscc));                    // pallet → harvest lot → seed lot and feed lots
graph.TraceForward(Gs1.Lot(feedGtin, "GB-2607"));   // recall: every harvest lot and pallet that feed lot reached
```

- Identifiers use GS1 Digital Link URIs (`https://id.gs1.org/01/{GTIN}/10/{lot}`, `/414/{GLN}/254/{pond}`, `/00/{SSCC}`) with
  verified check digits.
- A harvest is a `TransformationEvent`: the seed lot and the feed lots go in and the harvest lot comes out. Its ILMD carries the GDST
  farm-harvest data: species, production method `AQUACULTURE`, harvest date, pond, cycle and count per kg.

## ESG accounting (`AquaCulture.Esg`)

```csharp
var cycleReport = EsgAccounting.Cycle(cycle, new CycleResources
{
    ElectricityKwh = 9_000, DieselLitres = 200, WaterIntakeM3 = 30_000, ConvertedMangroveHa = 0, RestoredMangroveHa = 2,
});
var farmReport = EsgAccounting.Farm(farm, c => resourcesOf(c));   // harvest-weighted intensities + findings (EN/ID)
```

| Indicator | How it is computed |
|---|---|
| kg CO₂e per kg harvested | feed × feed footprint + kWh × grid factor + litres diesel × 2.68 + mangrove conversion − restoration |
| Mangrove conversion | 1 023 Mg C/ha (Donato et al. 2011) × 44/12, spread over 20 years after clearing (LCA convention), prorated to the cycle |
| Energy, water | kWh/kg and m³/kg; `PondWaterUse` estimates exchange, evaporation and seepage |
| N and P discharge | feed N (protein / 6.25) and P, plus stocked biomass, minus harvested N and P (kg per tonne harvested) |

The grid factor (0.85 kg/kWh) and the feed footprint (1.8 kg CO₂e/kg) are **indicative defaults**. Set them in `EsgFactors` from your
utility and your feed mill.

## Shrimp hatchery (`AquaCulture.Hatchery`)

```csharp
var brood = new BroodstockUnit();                          // SPF + PCR-cleared animals (WSSV, IHHNV, TSV, YHV, IMNV, AHPND, EHP)
brood.Spawnings.Add(new Spawning(date, "F01", "M01", eggs: 250_000, nauplii: 180_000));
var cull = brood.CullCandidates(today);                    // weak or idle females

var batch = new LarvalBatch { Id = "B1", TankId = "LRT-04", Stocked = day0, NaupliiStocked = 2_000_000, TankVolumeM3 = 10, TemperatureC = 29 };
var (stage, plDay) = batch.StageOn(today);                 // N → Z1–Z3 → M1–M3 → PL (PL1 ≈ day 8–9 at 29 °C, Q10 2.2)
var feed = batch.DailyFeed(today);                         // algae litres, Artemia millions, micro-diet g

var quality = PlQualityAssessment.Evaluate(new PlSample { PlDay = 10, StressTestSurvival = 0.92, LengthCv = 0.10, MuscleGutRatio = 4.5, Pcr = pcr });
var seedLotEvent = SeedLots.Commissioning(batch, brood, sample, seedGtin, hatcheryGln, harvestDate);   // EPCIS, with genealogy and PCR in ILMD
```

- PL quality is scored 0–100. Any positive PCR, stress-test survival below 80 % or necrosis above 5 % fails the lot.
- Stage durations, feeding levels and survivals are **indicative** hatchery practice. Set your own protocol.