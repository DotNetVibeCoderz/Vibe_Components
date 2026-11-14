# Pasar listrik

> Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/markets.md)

Paket: `Electrical.Markets` (baru di v1.2, bergantung pada `Electrical.Core`, `Electrical.PowerFlow`, dan `Electrical.Optimization`). Isinya:
unit commitment dengan kendala keamanan (SCUC) sebagai MILP, kliring pasar day-ahead dan real-time berbasis penawaran pada jaringan DC,
harga marginal lokasional (LMP) dengan komponen energi / kongesti / rugi, penyelesaian (settlement), dan hak transmisi finansial (FTR).
Model dibangun dengan [`LinearModel`](solver.md) dan diselesaikan oleh `IMilpSolver` mana pun: branch and bound bawaan secara default, atau
HiGHS / OR-Tools melalui paket adapter.

Satuan: MW, $/MWh, $ per periode (MW × $/MWh × jam periode), periode satu jam kecuali `ScucOptions.PeriodHours` menyatakan lain.

## Unit commitment dengan kendala keamanan

`SecurityConstrainedUnitCommitment.Solve` membangun MILP tiga biner (komitmen u, start-up v, shut-down w; Carrión & Arroyo 2006,
Morales-España dkk. 2013):

- biaya pembangkitan linear sepotong-sepotong (blok penawaran, atau `CostSegments` tali busur biaya kuadratik antara Pmin dan Pmax), biaya
  tanpa beban per periode hidup, biaya start-up;
- Pmin·u ≤ P ≤ Pmax·u, waktu hidup/mati minimum (pertidaksamaan turn-on/turn-off Rajan & Takriti), batas ramp dengan ramp start-up/shut-down,
  status dan keluaran awal;
- keseimbangan daya per periode dan cadangan berputar Σ(Pmax·u − P) ≥ f·D + R;
- pada jaringan, batas cabang DC melalui PTDF dan opsional batas pasca-kontingensi N-1 melalui LODF, ditambahkan secara malas (hanya baris
  yang dilanggar, diselesaikan ulang sampai dispatch aman) atau semuanya di awal (`LazyNetworkConstraints = false`).

Harga berasal dari penyelesaian kedua yang linear dengan komitmen dikunci ("restricted pricing" yang dipakai ISO):
LMP<sub>i,t</sub> = λ<sub>t</sub> + f·ρ<sub>t</sub> − Σ<sub>c</sub> μ<sub>c</sub>·PTDF<sub>c,i</sub>.

### Satu bus: Wood & Wollenberg

```csharp
using Electrical.Markets;
using Electrical.Optimization;

double[] demand = [450, 420, 400, 400, 450, 550, 700, 850, 950, 1000, 1050, 1050, 1000, 1000, 950, 900, 950, 1050, 1100, 1000, 850, 700, 550, 480];
var ww = UnitCommitment.WoodWollenbergThreeUnits();
var units = ww.Select(u => ScucUnit.FromUcUnit(u)).ToList();
var r = SecurityConstrainedUnitCommitment.Solve(units, demand, new ScucOptions { ReserveFraction = 0.05, CostSegments = 4 });
Console.WriteLine($"{r.Status}: {r.TotalCost:0} $ (production {r.ProductionCost:0}, start-up {r.StartupCost:0}), gap {r.MipGap:P3}");
for (int g = 0; g < r.UnitCount; g++)
    Console.WriteLine($"{units[g].Name}: " + string.Concat(Enumerable.Range(0, r.Periods).Select(t => r.Commitment[g, t] ? '1' : '0')));
Console.WriteLine($"λ at 18:00 = {r.SystemLambda[18]:0.00} $/MWh, reserve {r.SpinningReserveMw[18]:0} MW");

var dp = UnitCommitment.Solve(ww, demand, reserveFraction: 0.05);  // DP daftar prioritas sebagai pembanding
Console.WriteLine($"DP {dp.TotalCost:0} $");
```

```
Optimal: 183728 $ (production 182628, start-up 1100), gap 0.001%
Unit 1: 000000111111111111111111
Unit 2: 111111111111111111111100
Unit 3: 111111000111110001110000
λ at 18:00 = 10.79 $/MWh, reserve 100 MW
DP 184368 $
```

183 728 $ adalah objektif MILP dengan biaya 4 segmen. Mendispatch ulang komitmen yang sama dengan biaya kuadratik eksak menghasilkan
**183 665 $**, 703 $ (0,38 %) di bawah program dinamis daftar prioritas (184 368 $): MILP mempertahankan unit 3 hidup pada jam-jam awal dan
menghidup-matikannya pada puncak, hal yang tidak bisa dilakukan daftar prioritas. Kasus 3 jam dicek dengan brute force atas semua 512 komitmen.

Field `ScucUnit`: `Name`, `Bus`, `PminMw`, `PmaxMw`, `Cost` (kuadratik) atau `Blocks` + `NoLoadCost`, `StartupCost`, `MinUpHours`,
`MinDownHours`, `RampUpMwPerHour`, `RampDownMwPerHour`, `StartupRampMw`, `ShutdownRampMw`, `InitialHours` (+j hidup / −j mati),
`InitialOutputMw`, `FixedCommitment`. Factory: `ScucUnit.FromUcUnit`, `ScucUnit.FromGenerator`, `SecurityConstrainedUnitCommitment.UnitsOf(net)`.

### Pada jaringan, dengan N-1

```csharp
using Electrical.Core.Network;
using Electrical.Optimization.Highs;

var net = TestCases.Case9();
double peak = net.TotalLoadMw;
double[] demand = [.. Enumerable.Range(0, 24).Select(h => peak * (0.6 + 0.4 * Math.Max(0, Math.Sin((h - 6) * Math.PI / 16))))];
int line89 = net.Branches.FindIndex(b => b.From == 8 && b.To == 9);
var ring = Enumerable.Range(0, net.Branches.Count).Where(l => net.Branches[l].R > 0).ToList();   // enam saluran 230 kV

var options = new ScucOptions
{
    ReserveFraction = 0.1,
    RatingOverrides = new Dictionary<int, double> { [line89] = 57.5 },   // MW
    Solver = new HighsSolver(),                                         // lihat "Solver LP / MILP"
};
var r = SecurityConstrainedUnitCommitment.Solve(net, demand, options);  // unit dari generator jaringan
Console.WriteLine($"{r.Status}: {r.TotalCost:0} $, {r.NetworkIterations} constraint-generation rounds, {r.NetworkConstraints} network rows");
int t = 14;
Console.WriteLine("LMP 14:00: " + string.Join("  ", r.BusIds.Select((b, i) => $"{b}:{r.Lmp[i, t]:0.00}")));
var c = r.LmpComponents[t];                                     // DcLmpComponents: energi + kongesti (+ rugi = 0)
Console.WriteLine($"bus 9: {c.LmpAt(9):0.00} = {c.Energy[8]:0.00} + {c.Congestion[8]:0.00}");
foreach (var b in r.Binding.Where(b => b.Hour == t))
    Console.WriteLine($"{b.Kind} branch {b.Branch} (outage {b.Contingency}): {b.FlowMw:0.0}/{b.LimitMw:0.0} MW, μ {b.ShadowPrice:0.00}");

// N-1: enam saluran 230 kV dengan rating 130 MW dan setiap gangguannya diamankan melalui LODF
var secure = SecurityConstrainedUnitCommitment.Solve(net, demand, options with
{
    RatingOverrides = ring.ToDictionary(l => l, _ => 130.0), Contingencies = ring,
});
Console.WriteLine($"N-1 secure: {secure.Status}, {secure.TotalCost:0} $, " +
                  $"{secure.Binding.Count(b => b.Kind == "ContingencyFlow")} binding contingency rows");
```

```
Optimal: 94257 $, 2 constraint-generation rounds, 11 network rows
LMP 14:00: 1:27.00  2:21.39  3:23.62  4:27.00  5:25.81  6:23.62  7:22.32  8:21.39  9:28.10
bus 9: 28.10 = 27.00 + 1.10
Flow branch 7 (outage -1): 57.5/57.5 MW, μ 8.79
N-1 secure: Optimal, 93647 $, 9 binding contingency rows
```

Dengan saluran 8–9 (cabang 7) ditahan pada 57,5 MW, LMP pukul 14:00 tersebar dari 21,39 $/MWh (bus 2 dan 8) hingga 28,10 $/MWh (bus 9).
Pembangkitan kendala malas hanya memerlukan 11 dari 24 × 9 baris cabang yang mungkin. Penyelesaian kedua memakai rating berbeda (enam saluran
cincin pada 130 MW, 8–9 tidak lagi 57,5 MW) dan mengamankan setiap gangguan saluran cincin. Kasus jaringan 24 jam ini diselesaikan dengan
HiGHS: branch and bound bawaan menyelesaikan kasus satu bus di atas dalam sekitar satu detik, tetapi tidak memiliki presolve atau cut dan jauh
lebih lambat pada model jaringan sehari penuh (lihat [solver mana yang dipakai](solver.md#solver-mana-yang-dipakai)).

`ScucResult` memuat `Commitment`, `Startup`, `Shutdown`, `DispatchMw` [unit, periode], `Lmp` [bus, periode], `SystemLambda`, `LmpComponents`
(satu `DcLmpComponents` per periode), `FlowsMw` [cabang, periode], `SpinningReserveMw`, `Binding` (`MarketConstraint`: jenis "Flow",
"ContingencyFlow", atau "Reserve", jam, cabang, gangguan, harga bayangan, aliran, batas), `Objective`, `ProductionCost`, `StartupCost`,
`MipGap`, `NetworkIterations`, `NetworkConstraints`, serta `MilpSolution` / `PricingSolution` mentah.

## Kliring pasar

`MarketClearing` mengklirkan pasar harga seragam berbasis penawaran: maksimalkan Σ penawaran beli·D − Σ penawaran jual·P (dikurangi biaya
tanpa beban dan start-up) dengan kendala keseimbangan daya, batas cabang (dan opsional N-1) serta, untuk day-ahead, kendala unit commitment.

- `SupplyOffer(Name, Bus, Blocks)`: `OfferBlock` harga-kuantitas mulai 0 MW, ditambah `MinOutputMw`, `NoLoadCost`, `StartupCost`, waktu
  hidup/mati minimum, ramp, `InitialHours`, `InitialOutputMw`, `Committed`.
- `DemandBid(Name, Bus)`: `FixedMw` (tidak responsif harga) ditambah `Blocks` yang responsif harga (kesediaan membayar).
- `ClearSinglePeriod(net, offers, bids)`: satu periode dengan komitmen dari `Committed` (LP murni). `net = null` berarti pasar satu bus.
- `ClearRealTime(net, offers, bids, intervalMinutes: 5)`: dispatch ekonomis berkendala keamanan untuk satu interval; unit yang hidup dibatasi
  kemampuan ramp-nya di sekitar `InitialOutputMw`, biaya start-up dan tanpa beban dianggap hangus.
- `ClearDayAhead(net, offers, bidsPerHour, commit: true)`: kliring berbasis SCUC sepanjang jam (dengan `commit: false` flag `Committed`
  dipakai dan masalahnya menjadi LP multi-periode). Mengembalikan `ScucResult`, satu `MarketClearingResult` per jam, dan total penyelesaian.

### Cek manual dua bus

```csharp
var net = new PowerNetwork { Name = "two-bus", BaseMva = 100 };
net.Buses.Add(new Bus { Id = 1, Type = BusType.Slack });
net.Buses.Add(new Bus { Id = 2 });
net.Branches.Add(new Branch { From = 1, To = 2, X = 0.1, RateA = 100 });     // saluran 100 MW

SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)]), new("G2", 2, [new OfferBlock(200, 50)])];
DemandBid[] bids = [new("L2", 2) { FixedMw = 150 }];
var r = MarketClearing.ClearSinglePeriod(net, offers, bids);

Console.WriteLine($"G1 {r.OfferDispatchMw[0]} MW, G2 {r.OfferDispatchMw[1]} MW, LMP {r.LmpAt(1)} / {r.LmpAt(2)} $/MWh");
var s = r.Settlement;
Console.WriteLine($"loads pay {s.TotalLoadPayment}, generators get {s.TotalGeneratorRevenue}, congestion rent {s.CongestionRent} $");
Console.WriteLine($"energy {r.Components.Energy[1]} + congestion {r.Components.Congestion[1]} at bus 2");

var ftr = FinancialTransmissionRights.Settle([new FtrObligation("F12", 1, 2, 100)], r);
Console.WriteLine($"FTR payout {ftr.TotalPayout} $, surplus {ftr.Surplus} $, adequate {ftr.RevenueAdequate}");
```

```
G1 100 MW, G2 50 MW, LMP 20 / 50 $/MWh
loads pay 7500, generators get 4500, congestion rent 3000 $
energy 20 + congestion 30 at bus 2
FTR payout 3000 $, surplus 0 $, adequate True
```

Secara manual: unit murah dibatasi rating saluran (100 MW), sisa 50 MW berasal dari G2, sehingga bus 2 diberi harga oleh G2 (50 $/MWh) dan
bus 1 oleh G1 (20 $/MWh). Harga bayangan saluran 30 $/MWh; beban membayar 150 × 50 = 7 500 $, pembangkit menerima 100 × 20 + 50 × 50 =
4 500 $, dan selisih 3 000 $ (= 100 MW × 30 $/MWh) adalah sewa kongesti, yang tepat membayar FTR 100 MW dari bus 1 ke bus 2.

### Permintaan responsif harga dan kesejahteraan

```csharp
SupplyOffer[] offers = [new("A", 0, [new OfferBlock(100, 10), new OfferBlock(100, 30)])];
DemandBid[] bids = [new("D", 0) { Blocks = [new OfferBlock(120, 40), new OfferBlock(60, 25)] }];
var r = MarketClearing.ClearSinglePeriod(null, offers, bids);         // null = pasar satu bus
var s = r.Settlement;
Console.WriteLine($"cleared {r.BidClearedMw[0]} MW at {r.LmpAt(0)} $/MWh; CS {s.ConsumerSurplus}, PS {s.ProducerSurplus}, welfare {s.SocialWelfare}");
```

```
cleared 120 MW at 30 $/MWh; CS 1200, PS 2000, welfare 3200
```

Penawaran beli 25 $/MWh berada di bawah penawaran jual marginal 30 $/MWh, sehingga 120 MW terkliring pada 30 $/MWh. `MarketSettlement`
melaporkan `GeneratorRevenue`, `GeneratorCost` (sesuai penawaran), `LoadPayment`, `CongestionRent` (= pembayaran beban − pendapatan
pembangkit), `CongestionRentFromShadowPrices` (Σ μ·aliran, sama pada titik optimum), `ConsumerSurplus` (permintaan tetap dinilai dengan
`MarketOptions.ValueOfLostLoad`, bawaan 10 000 $/MWh), `ElasticConsumerSurplus` (hanya dari penawaran beli yang responsif harga, angka yang
layak dibandingkan bila permintaan tetap dominan), `ProducerSurplus`, dan `SocialWelfare` = CS + PS + sewa kongesti = manfaat bruto − biaya
sesuai penawaran (didominasi penilaian VoLL bila ada permintaan tetap).

### Day-ahead dan real-time

```csharp
var net = TestCases.Case14();
var offers = net.Generators.Select((g, k) => new SupplyOffer($"G{k + 1}", g.Bus,
    [.. new[] { 1 / 6.0, 0.5, 5 / 6.0 }.Select(f => new OfferBlock(g.Pmax / 3, Math.Round(g.Cost.Marginal(f * g.Pmax), 2)))])
    {
        NoLoadCost = k < 2 ? 0 : 100, StartupCost = k < 2 ? 0 : 300, MinOutputMw = k < 2 ? 0 : 10,
        InitialHours = k < 2 ? 24 : -4, MinUpHours = 2,
    }).ToArray();
double[] shape = [0.6, 0.8, 1.0, 1.1];
var bidsPerHour = shape.Select(f => (IReadOnlyList<DemandBid>)net.Loads
    .Select(l => new DemandBid($"L{l.Bus}", l.Bus) { FixedMw = 0.8 * f * l.P, Blocks = [new OfferBlock(0.2 * f * l.P, 45)] }).ToArray()).ToList();
var options = new MarketOptions { Scuc = new ScucOptions { RatingOverrides = new Dictionary<int, double> { [0] = 110, [6] = 30 } } };

var da = MarketClearing.ClearDayAhead(net, offers, bidsPerHour, commit: true, options);
Console.WriteLine($"{da.Status}: λ per hour {string.Join(", ", da.Scuc.SystemLambda.Select(l => l.ToString("0.00")))} $/MWh");
Console.WriteLine($"rent {da.Settlement.CongestionRent:0.00} $, welfare {da.Settlement.SocialWelfare:0} $");
foreach (var p in da.Periods) Console.WriteLine($"h{p.Period}: LMP {p.Lmp.Min():0.00}–{p.Lmp.Max():0.00}, committed {p.Committed.Count(c => c)}");
```

```
Optimal: λ per hour 26.37, 31.06, 34.30, 34.30 $/MWh
rent 4078.62 $, welfare 7231313 $
h0: LMP 6.38–59.72, committed 2
h1: LMP 22.71–45.00, committed 3
h2: LMP 28.10–44.64, committed 3
h3: LMP 27.89–45.00, committed 3
```

Pembangkit peaker (G3–G5) memiliki biaya tanpa beban dan start-up serta mulai dalam keadaan mati; SCUC menghidupkan salah satunya mulai jam
kedua. Kesejahteraan didominasi oleh permintaan tetap yang dinilai pada value of lost load 10 000 $/MWh.

Satu interval real-time dengan unit murah yang dibatasi ramp:

```csharp
SupplyOffer[] offers =
[
    new("Cheap", 0, [new OfferBlock(200, 15)]) { RampUpMwPerHour = 60, RampDownMwPerHour = 60, InitialOutputMw = 50 },
    new("Peaker", 0, [new OfferBlock(200, 40)]) { InitialOutputMw = 60 },
];
var rt = MarketClearing.ClearRealTime(null, offers, [new DemandBid("L", 0) { FixedMw = 120 }], intervalMinutes: 5);
Console.WriteLine($"{rt.OfferDispatchMw[0]} / {rt.OfferDispatchMw[1]} MW, price {rt.LmpAt(0)} $/MWh, load pays {rt.Settlement.TotalLoadPayment} $");
// 55 / 65 MW, price 40 $/MWh, load pays 400 $
```

Unit murah hanya dapat bergerak 60 MW/jam × 5/60 jam = 5 MW, sehingga peaker menjadi marginal dan menetapkan harga; uang dihitung per
interval 5 menit.

## Komponen LMP

Setiap periode yang terkliring membawa `DcLmpComponents(BusIds, Lmp, Energy, Congestion, Loss)`: energi adalah harga di bus referensi
(slack), kongesti = −Σ μ<sub>c</sub>·PTDF<sub>c,i</sub> atas kendala cabang yang terikat, dan rugi bernilai nol pada model DC tanpa rugi.
Untuk harga AC dengan komponen rugi yang nyata gunakan `AcOpf` + `LmpDecomposition` ([Optimasi](optimasi.md#dekomposisi-lmp)).

## Hak transmisi finansial

`FtrObligation(Name, SourceBus, SinkBus, Mw)` membayar MW × (LMP<sub>sink</sub> − LMP<sub>source</sub>) × jam (negatif bila harga terbalik).
`FinancialTransmissionRights.Settle(ftrs, market)` mengembalikan pembayaran, sewa kongesti, surplus, dan `RevenueAdequate`.
`Flows(net, ftrs)` memberikan aliran DC yang ditimbulkan injeksi FTR dan `IsSimultaneouslyFeasible(net, ftrs, options)` memeriksanya terhadap
batas kasus dasar (dan kontingensi). Himpunan FTR yang layak secara simultan selalu didanai oleh sewa kongesti (kecukupan pendapatan,
Hogan 1992); rangkaian uji memeriksanya dengan himpunan FTR acak pada case14 dan menunjukkan bahwa himpunan yang diskalakan 10× (tidak layak)
ditandai.

## Dependency injection

```csharp
using var sp = new ServiceCollection().AddElectrical(b => b.AddMarkets()).BuildServiceProvider();
var market = sp.GetRequiredService<IMarketService>();           // branch and bound bawaan
SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)])];
var r = market.ClearSinglePeriod(null, offers, [new DemandBid("L", 1) { FixedMw = 80 }]);
Console.WriteLine($"{market.Solver.Name}: {r.LmpAt(0)} $/MWh");
```

`AddMarkets()` juga menambahkan modul Optimization. Tambahkan `.AddHighs()` atau `.AddOrTools()` untuk mengganti solver (lihat
[Solver LP / MILP](solver.md)).

## Validasi

| Pemeriksaan | Acuan | Hasil |
|---|---|---|
| Unit Wood & Wollenberg 24 jam, cadangan 5 % | DP daftar prioritas (W&W §4.2): 184 368 $ | komitmen MILP 183 665 $ pada biaya eksak; objektif MILP dalam 0,2 % dari biaya eksaknya |
| Komitmen 3 jam | Brute force atas 512 komitmen | objektif identik |
| Batas ramp dan waktu hidup/mati minimum | Definisi kendala | terpenuhi setiap jam |
| case9, satu saluran dibatasi 70 % aliran puncaknya | Formulasi malas vs semua baris | biaya identik; LMP terpisah; LMP = energi + kongesti hingga 1e-9 |
| case9, N-1 pada enam saluran 230 kV | Aliran DC pasca-kontingensi (LODF) | semua dalam batas; rating 120 MW dilaporkan tidak layak |
| SCUC case14 dengan saluran 120 MW | HiGHS dan OR-Tools | objektif dan LMP sama (1e-3) |
| Pasar dua bus | Perhitungan manual | LMP 20 / 50 $/MWh, sewa 3 000 $ = pembayaran FTR |
| Permintaan elastis, satu bus | Perhitungan manual | 120 MW pada 30 $/MWh, CS 1 200 $, PS 2 000 $ |
| Pasar case14 | Σ μ·aliran, identitas kesejahteraan | sewa = Σ μ·aliran; CS + PS + sewa = manfaat − biaya |
| Interval real-time 5 menit | Perhitungan manual dengan batas ramp | 55 / 65 MW, harga 40 $/MWh |

Galeri: [Unit commitment dengan kendala keamanan](galeri.md) (`scuc`) dan [Kliring pasar listrik](galeri.md) (`market`).
CLI: `eln scuc`, `eln market` ([CLI](cli.md)).

## Batasan

Model jaringan DC (tanpa rugi atau tegangan dalam pasar; harga AC berasal dari `AcOpf`). Unit bergaya termal dengan biaya linear
sepotong-sepotong; belum ada penyimpanan, konfigurasi combined-cycle, respons permintaan dengan payback, atau produk layanan tambahan selain
cadangan berputar. FTR hanya berupa obligasi (tanpa opsi). Branch and bound bawaan ditujukan untuk model kecil dan menengah; gunakan HiGHS
atau OR-Tools untuk masalah day-ahead N-1.

---
*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](../en/markets.md)*
