using Electrical.Core;

namespace Electrical.Gallery.Shared;

/// <summary>Bilingual text pair (English / Bahasa Indonesia).</summary>
public readonly record struct L(string En, string Id)
{
    public string this[string lang] => lang == "id" ? Id : En;
}

/// <summary>Gallery section that groups related demos.</summary>
public sealed record DemoCategory(string Key, L Name, L Blurb);

/// <summary>
/// One gallery demo. <see cref="Id"/> is the URL slug (<c>/demo/{id}</c>) and the screenshot name (<c>web-{id}.png</c>,
/// <c>desktop-{id}.png</c>). <see cref="Modules"/> are <see cref="ElectricalInfo.Modules"/> names; the home page uses them to mark
/// which modules have a live demo.
/// </summary>
public sealed record DemoInfo(string Id, string Category, L Title, L Summary, string[] Modules, string Code)
{
    /// <summary>NuGet-style package names for the module tags (<c>Electrical.PowerFlow</c> …).</summary>
    public IEnumerable<string> Packages => Modules.Select(m => "Electrical." + m);
}

/// <summary>Short description of a framework module for the home-page module grid.</summary>
public sealed record ModuleInfo(string Name, L Summary);

/// <summary>
/// The gallery's table of contents, shared by the Blazor and Avalonia apps.
/// To add a demo: append a <see cref="DemoInfo"/> to <see cref="Demos"/> and add a scenario class under <c>Scenarios/</c>. A declarative
/// <see cref="IInteractiveScenario"/> registered in <see cref="InteractiveScenarios.All"/> is rendered by both apps without UI code;
/// custom views need one per app (web: <c>Components/Demos/{Name}View.razor</c> + a case in <c>DemoHost.razor</c>;
/// desktop: a case in <c>Views.Build</c>).
/// </summary>
public static class GalleryCatalog
{
    public static readonly L Credit = new(ElectricalInfo.CreditEn, ElectricalInfo.CreditId);
    public static readonly L Tagline = new(ElectricalInfo.Tagline, ElectricalInfo.TaglineId);

    public static readonly DemoCategory[] Categories =
    [
        new("analysis", new("Grid analysis", "Analisis jaringan"),
            new("Steady-state studies on standard test systems.", "Studi kondisi tunak pada sistem uji standar.")),
        new("faults", new("Faults & protection", "Gangguan & proteksi"),
            new("Short-circuit duty, relay grading and arc flash.", "Arus hubung singkat, penyetelan relai, dan arc flash.")),
        new("dynamics", new("Dynamics & power quality", "Dinamika & kualitas daya"),
            new("Stability, harmonics and circuit transients.", "Stabilitas, harmonik, dan transien rangkaian.")),
        new("der", new("Renewables & smart grid", "Energi terbarukan & smart grid"),
            new("PV, wind, storage, EVs and microgrids.", "PV, angin, penyimpanan, EV, dan mikrogrid.")),
        new("planning", new("Markets, forecasting & planning", "Pasar, peramalan & perencanaan"),
            new("Optimal dispatch, forecasts and time-series studies.", "Dispatch optimal, peramalan, dan studi deret waktu.")),
        new("twin", new("Digital twin & AI", "Kembaran digital & AI"),
            new("A live grid model and an operator assistant.", "Model jaringan hidup dan asisten operator.")),
        new("hpc", new("High-performance computing", "Komputasi kinerja tinggi"),
            new("Thousands of power flows in parallel, with Rust kernels.", "Ribuan aliran daya paralel dengan kernel Rust.")),
        new("ops", new("Operations & protocols", "Operasi & protokol"),
            new("Telemetry, alarms and substation messaging.", "Telemetri, alarm, dan pesan gardu induk.")),
        new("data", new("Data exchange & geography", "Pertukaran data & geografi"),
            new("Standard model exchange and maps.", "Pertukaran model standar dan peta.")),
    ];

    public static readonly DemoInfo[] Demos =
    [
        new("powerflow", "analysis",
            new("Power flow & method comparison", "Aliran daya & perbandingan metode"),
            new("Solve a test system with Newton–Raphson, fast-decoupled, Gauss–Seidel, DC and backward/forward sweep, then read the result on an automatic single-line diagram.",
                "Selesaikan sistem uji dengan Newton–Raphson, fast-decoupled, Gauss–Seidel, DC, dan backward/forward sweep, lalu baca hasilnya pada diagram segaris otomatis."),
            ["PowerFlow", "Core", "Visualization"], """
            using Electrical.Core.Network;
            using Electrical.PowerFlow;
            using Electrical.Visualization;

            var net = TestCases.Case14();
            foreach (var load in net.Loads) { load.P *= 1.2; load.Q *= 1.2; }

            var nr = PowerFlowSolver.Solve(net);                       // sparse Newton–Raphson
            var fd = PowerFlowSolver.Solve(net, new() { Method = PowerFlowMethod.FastDecoupled });
            Console.WriteLine(nr);   // converged in 4 it, losses …, Vmin … pu @ bus 14

            string svg = SldRenderer.Render(net, nr);                 // voltage-coloured buses, loading-coloured branches
            string conv = SvgCharts.Line([new ChartSeries("NR", nr.History.Select(Math.Log10).ToList())]);
            """),
        new("contingency", "analysis",
            new("N-1 contingency ranking", "Peringkat kontingensi N-1"),
            new("Trip every branch in turn, re-solve the AC power flow in parallel and rank outages by overloads and voltage violations.",
                "Lepas setiap cabang satu per satu, hitung ulang aliran daya AC secara paralel, dan urutkan gangguan berdasarkan beban lebih dan pelanggaran tegangan."),
            ["PowerFlow", "Core", "Visualization"], """
            using Electrical.Core.Network;
            using Electrical.PowerFlow;

            var net = TestCases.Case9();
            var ranking = ContingencyAnalysis.RunN1(net);   // Parallel.For over branches, warm-started NR

            foreach (var c in ranking.Take(5))
                Console.WriteLine($"{c.Outage,-8} max {c.MaxLoadingPercent:0.0} % on {c.MostLoadedBranch}, " +
                                  $"Vmin {c.MinVoltage:0.000} pu, severity {c.Severity:0.0}");
            """),
        new("distribution", "analysis",
            new("Unbalanced distribution (IEEE 13-node)", "Distribusi tak seimbang (IEEE 13 node)"),
            new("Solve the IEEE 13-node feeder phase by phase, built in code or read from its OpenDSS script, and compare with the published voltages; check regulator taps and voltage unbalance, and compute line impedance matrices from conductor geometry with Carson's equations.",
                "Selesaikan penyulang IEEE 13 node per fasa, dibangun di kode atau dibaca dari skrip OpenDSS-nya, dan bandingkan dengan tegangan terbitan; periksa tap regulator dan ketidakseimbangan tegangan, serta hitung matriks impedansi saluran dari geometri konduktor dengan persamaan Carson."),
            ["Distribution", "Core", "Visualization"], """
            using Electrical.Distribution;

            var net = DistributionTestCases.Ieee13Node(automaticRegulator: true);     // or OpenDssReader.Parse(script)
            var r = UnbalancedPowerFlow.Solve(net);                                   // phase-frame ladder sweep + regulator control
            var b = r.Bus("675");
            Console.WriteLine($"675: {b.VmPu[0]:0.0000} / {b.VmPu[1]:0.0000} / {b.VmPu[2]:0.0000} pu, VUF {b.VoltageUnbalanceFactor:P2}");
            Console.WriteLine($"taps {string.Join("/", r.RegulatorTaps["Reg"])}, losses {r.LossesKw:0.0} kW");

            var z = LineConstants.Overhead(OverheadConfiguration.Spacing500(
                ConductorLibrary.Acsr556_5_26_7, ConductorLibrary.Acsr4_0_6_1,
                ConductorRole.PhaseB, ConductorRole.PhaseA, ConductorRole.PhaseC));   // IEEE config 601
            Console.WriteLine(z);                                                     // 3×3 z_abc in Ω/mile
            """),
        new("stateestimation", "analysis",
            new("State estimation & bad data", "Estimasi keadaan & data buruk"),
            new("Estimate the grid state from noisy SCADA measurements with weighted least squares, then find and remove a gross error with the largest-normalized-residual test.",
                "Estimasi keadaan jaringan dari pengukuran SCADA yang berderau dengan kuadrat terkecil berbobot, lalu temukan dan hapus galat kasar dengan uji residu ternormalisasi terbesar."),
            ["StateEstimation", "PowerFlow"], """
            using Electrical.StateEstimation;

            var meas = MeasurementGenerator.FromPowerFlow(PowerFlowSolver.Solve(net));     // V, P/Q injections, P/Q flows + noise
            var se = WlsEstimator.Estimate(net, meas);
            if (se.BadDataSuspected)                                                       // χ² test on J(x̂)
            {
                var clean = BadDataDetector.IdentifyAndRemove(net, meas, threshold: 3.0);  // LNR test
                Console.WriteLine($"removed {clean.Removed.Count}, J = {clean.Final.ObjectiveJ:0.0}");
            }
            """),
        new("shortcircuit", "faults",
            new("IEC 60909 short circuit", "Hubung singkat IEC 60909"),
            new("Scan the fault level at every bus, then look at one balanced or unbalanced fault: peak, breaking and thermal currents, phase voltages and branch contributions.",
                "Pindai level gangguan di setiap bus, lalu amati satu gangguan seimbang atau tak seimbang: arus puncak, pemutusan, dan termal, tegangan fasa, serta kontribusi cabang."),
            ["ShortCircuit", "Core"], """
            using Electrical.ShortCircuit;

            var scan = ShortCircuitAnalysis.ScanAllBuses(net);                 // I"k3, I"k1, ip, S"k for every bus
            var f = ShortCircuitAnalysis.Calculate(net, busId: 4, FaultType.SingleLineToGround);
            Console.WriteLine(f);   // I"k, ip (κ), Ib, Ith, S"k
            foreach (var v in f.Voltages) Console.WriteLine($"{v.BusId}: {v.Va:0.000} {v.Vb:0.000} {v.Vc:0.000} pu");
            """),
        new("protection", "faults",
            new("Relay coordination & arc flash", "Koordinasi relai & arc flash"),
            new("Grade inverse-time overcurrent relays on a radial feeder for a chosen margin, plot their time–current curves, and compute IEEE 1584 incident energy at an LV board.",
                "Setel relai arus lebih waktu terbalik pada penyulang radial untuk margin tertentu, gambar kurva waktu–arus, dan hitung energi insiden IEEE 1584 di panel TR."),
            ["Protection", "Visualization"], """
            using Electrical.Protection;

            var r1 = new OvercurrentRelay("R1", pickupA: 200, CurveType.IecStandardInverse);
            var r2 = new OvercurrentRelay("R2", 400);
            var r3 = new OvercurrentRelay("R3", 600);
            var grading = Coordination.GradeTimeMultipliers(
                [new(r1, 3000, 3000), new(r2, 5000, 3000), new(r3, 8000, 5000)], ctiS: 0.3);

            var arc = ArcFlash.Calculate(new ArcFlashInput(SystemKv: 0.48, BoltedFaultKa: 25, ArcDurationS: 0, WorkingDistanceMm: 610),
                                         lvBreaker, breakerOpeningS: 0.05);
            Console.WriteLine($"{arc.IncidentEnergyCalCm2:0.0} cal/cm², PPE {arc.PpeCategory}");
            """),
        new("stability", "dynamics",
            new("Transient & voltage stability", "Stabilitas transien & tegangan"),
            new("Apply a three-phase fault on the WSCC 9-bus system, watch the rotor angles swing, find the critical clearing time, and trace PV curves and electromechanical modes.",
                "Terapkan gangguan tiga fasa pada sistem WSCC 9 bus, amati ayunan sudut rotor, cari waktu pemutusan kritis, dan telusuri kurva PV serta mode elektromekanis."),
            ["Stability", "PowerFlow"], """
            using Electrical.Stability;

            var model = ClassicalModel.FromPowerFlow(TestCases.Case9());
            var sim = TransientStability.SimulateFault(model, faultBusId: 7, clearingTimeS: 0.12);
            double cct = TransientStability.CriticalClearingTime(model, faultBusId: 7);

            var pv = VoltageStability.PvCurve(TestCases.Case9());          // continuation power flow
            var modes = SmallSignal.Analyze(TestCases.Case9()).OscillatoryModes;
            """),
        new("harmonic", "dynamics",
            new("Harmonics & resonance", "Harmonik & resonansi"),
            new("Inject typical nonlinear-load spectra, scan the driving-point impedance for capacitor resonance and check every bus against IEEE 519.",
                "Injeksikan spektrum beban nonlinier tipikal, pindai impedansi titik penggerak untuk resonansi kapasitor, dan periksa setiap bus terhadap IEEE 519."),
            ["Harmonic", "PowerFlow"], """
            using Electrical.Harmonic;

            var spectrum = TypicalSpectra.SixPulseRectifier();
            var scan = FrequencyScan.DrivingPoint(net, busId: 9, hMin: 1, hMax: 25);
            var resonances = FrequencyScan.FindResonances(scan);

            var hlf = HarmonicLoadFlow.Run(net, [new HarmonicInjection(9, spectrum)]);
            foreach (var (bus, check) in hlf.CheckIeee519()) Console.WriteLine($"{bus}: THDv {check.TotalMeasured:0.00} %");
            """),
        new("circuit", "dynamics",
            new("Circuit simulation (MNA)", "Simulasi rangkaian (MNA)"),
            new("Parse a SPICE netlist of a series RLC circuit and run a transient step response and an AC sweep with modified nodal analysis.",
                "Urai netlist SPICE rangkaian RLC seri lalu jalankan respons tangga transien dan sapuan AC dengan analisis nodal termodifikasi."),
            ["Circuit"], """
            using Electrical.Circuit;

            var c = Netlist.Parse(netlistText);   // V1 in 0 PULSE(0 10 0.5m 1u 1u 1 2) AC 1 · R1 in a 20 · L1 a b 10m · C1 b 0 10u
            var tr = c.Transient(stopTimeS: 0.02, stepS: 2e-5);           // trapezoidal integration
            double[] vc = tr.Voltage("b");
            var bode = c.AcSweep(10, 100_000).Bode("b", "in");
            """),
        new("renewable", "der",
            new("Solar PV & wind energy", "Energi surya PV & angin"),
            new("Fit a single-diode model to a module datasheet, draw I–V and P–V curves, simulate a clear-sky day, and estimate wind-turbine annual energy from a Weibull regime.",
                "Paskan model dioda tunggal ke lembar data modul, gambar kurva I–V dan P–V, simulasikan hari cerah, dan perkirakan energi tahunan turbin angin dari rezim Weibull."),
            ["Renewable"], """
            using Electrical.Renewable;

            var module = SingleDiodeModel.FromDatasheet(PvDatasheet.KyoceraKC200GT);
            var mpp = module.MaximumPowerPoint(irradiance: 1000, cellTempC: 45);
            var iv = module.IvCurve(800, 45);

            var aep = WindEnergy.AnnualEnergyProduction(WindTurbine.Generic2MW, WeibullDistribution.FromMean(7, k: 2));
            Console.WriteLine($"{aep.AepMwh:0} MWh/yr, CF {aep.CapacityFactor:P1}");
            """),
        new("smartgrid", "der",
            new("Smart grid operations", "Operasi smart grid"),
            new("Reconfigure the 33-bus feeder for minimum losses, fill the night valley with EV charging, dispatch a microgrid optimally, and shave a peak with a battery.",
                "Konfigurasi ulang penyulang 33 bus untuk rugi minimum, isi lembah malam dengan pengisian EV, dispatch mikrogrid secara optimal, dan pangkas puncak dengan baterai."),
            ["SmartGrid", "Renewable", "PowerFlow"], """
            using Electrical.SmartGrid;

            var reconf = FeederReconfiguration.Optimize(FeederReconfiguration.Case33WithTies());
            Console.WriteLine($"{reconf.LossesBeforeMw * 1000:0.0} → {reconf.LossesAfterMw * 1000:0.0} kW");

            var smart = EvCharging.ValleyFilling(EvCharging.SyntheticFleet(80), baseLoadKw);
            var ems = MicrogridEms.Optimal(MicrogridConfig.Demo());       // DP over battery SoC
            """),
        new("optimization", "planning",
            new("OPF, LMP & unit commitment", "OPF, LMP & unit commitment"),
            new("Solve a DC optimal power flow, tighten a line rating to create congestion and watch locational marginal prices split; commit three units over a day.",
                "Selesaikan aliran daya optimal DC, perketat rating saluran untuk menimbulkan kongesti dan lihat harga marginal lokasional terpisah; jadwalkan tiga unit sepanjang hari."),
            ["Optimization", "PowerFlow"], """
            using Electrical.Optimization;

            var opf = DcOpf.Solve(net, new DcOpfOptions { RatingOverrides = new Dictionary<int, double> { [1] = 50 } });
            foreach (var (bus, lmp) in opf.BusIds.Zip(opf.Lmp)) Console.WriteLine($"bus {bus}: {lmp:0.00} $/MWh");

            var uc = UnitCommitment.Solve(UnitCommitment.WoodWollenbergThreeUnits(), hourlyDemandMw);
            Console.WriteLine($"cost {uc.TotalCost:0} $");
            """),
        new("acopf", "planning",
            new("AC optimal power flow", "Aliran daya optimal AC"),
            new("Solve the full AC-OPF with a primal-dual interior point, tighten a line and compare with the DC-OPF; split every LMP into energy, loss, congestion and voltage components and draw the optimal operating point.",
                "Selesaikan AC-OPF penuh dengan titik interior primal-dual, perketat satu saluran dan bandingkan dengan DC-OPF; uraikan setiap LMP menjadi komponen energi, rugi, kongesti, dan tegangan, lalu gambar titik operasi optimal."),
            ["Optimization", "PowerFlow", "Visualization"], """
            using Electrical.Core.Network;
            using Electrical.Optimization;
            using Electrical.Visualization;

            var net = TestCases.Case9();
            var opf = AcOpf.Solve(net, new AcOpfOptions { RatingOverrides = new Dictionary<int, double> { [2] = 42 } });  // line 5-6, MVA
            Console.WriteLine(opf);                                   // cost $/h, losses, LMP range

            var lmp = opf.Decompose();                                // energy + loss + congestion + voltage
            foreach (var b in opf.Binding) Console.WriteLine($"{b.Description}: {b.ShadowPrice:0.000}");
            string svg = SldRenderer.Render(net, opf.ToPowerFlowResult());
            """),
        new("scuc", "planning",
            new("Security-constrained unit commitment", "Unit commitment dengan kendala keamanan"),
            new("Commit units over 24 hours with a MILP (start-ups, minimum up/down times, reserve and DC line limits), compare with the priority-list dynamic program and read hourly LMPs.",
                "Jadwalkan unit selama 24 jam dengan MILP (start-up, waktu hidup/mati minimum, cadangan, dan batas saluran DC), bandingkan dengan program dinamis daftar prioritas, dan baca LMP per jam."),
            ["Markets", "Optimization"], """
            using Electrical.Markets;
            using Electrical.Optimization;

            var units = UnitCommitment.WoodWollenbergThreeUnits().Select(u => ScucUnit.FromUcUnit(u)).ToList();
            var r = SecurityConstrainedUnitCommitment.Solve(units, demandMw, new ScucOptions { ReserveFraction = 0.05 });
            Console.WriteLine($"{r.Status}: {r.TotalCost:0} $, gap {r.MipGap:P3}, {r.MilpSolution.Nodes} nodes");

            // on a network: PTDF line limits (lazy), optional N-1 through LODFs, LMP[bus, hour]
            var grid = SecurityConstrainedUnitCommitment.Solve(TestCases.Case9(), demandMw,
                new ScucOptions { RatingOverrides = new Dictionary<int, double> { [line] = 57.5 } });
            """),
        new("market", "planning",
            new("Electricity market clearing", "Kliring pasar listrik"),
            new("Clear stepwise supply offers against demand bids on a network, see nodal prices split by congestion, settle revenues, payments and congestion rent, and pay out a financial transmission right.",
                "Klirkan penawaran pasokan bertingkat terhadap penawaran permintaan pada jaringan, lihat harga nodal yang terpisah karena kongesti, selesaikan pendapatan, pembayaran, dan sewa kongesti, lalu bayarkan hak transmisi finansial."),
            ["Markets", "Optimization"], """
            using Electrical.Markets;

            SupplyOffer[] offers = [new("G1", 1, [new OfferBlock(200, 20)]), new("G2", 2, [new OfferBlock(200, 50)])];
            DemandBid[] bids = [new("L2", 2) { FixedMw = 150 }];
            var r = MarketClearing.ClearSinglePeriod(net, offers, bids);           // two buses, 100 MW line
            Console.WriteLine($"LMP {r.LmpAt(1)} / {r.LmpAt(2)} $/MWh, rent {r.Settlement.CongestionRent} $");   // 20 / 50, 3000

            var ftr = FinancialTransmissionRights.Settle([new FtrObligation("F12", 1, 2, 100)], r);
            Console.WriteLine($"FTR pays {ftr.TotalPayout} $, adequate {ftr.RevenueAdequate}");
            """),
        new("forecasting", "planning",
            new("Load forecasting", "Peramalan beban"),
            new("Forecast the next days of load with Holt-Winters, temperature regression, similar-day or an ensemble, and score it against the actual curve.",
                "Ramalkan beban beberapa hari ke depan dengan Holt-Winters, regresi suhu, hari serupa, atau ensembel, lalu nilai terhadap kurva aktual."),
            ["Forecasting", "Core"], """
            using Electrical.Forecasting;

            var temp = SyntheticProfiles.Temperature(start, days: 30, TimeSpan.FromHours(1));
            var load = SyntheticProfiles.Load(LoadProfileKind.Residential, start, 30, TimeSpan.FromHours(1), peakMw: 120, temperature: temp);
            var forecast = LoadForecast.Forecast(history, horizon: 48, LoadForecastMethod.Ensemble, temp);
            var error = ForecastMetrics.Evaluate(actual, forecast.Values);    // MAPE, RMSE, MAE, bias
            """),
        new("simulation", "planning",
            new("Time-series & adequacy simulation", "Simulasi deret waktu & kecukupan"),
            new("Run a quasi-static day on the 33-bus feeder with a PV plant and watch the voltage band, then assess generation adequacy (LOLE, EENS) analytically and by Monte-Carlo.",
                "Jalankan satu hari kuasi-statis pada penyulang 33 bus dengan PLTS dan amati pita tegangan, lalu nilai kecukupan pembangkitan (LOLE, EENS) secara analitis dan Monte-Carlo."),
            ["Simulation", "Forecasting", "PowerFlow"], """
            using Electrical.Simulation;

            var qsts = QstsSimulation.Case33Demo(days: 1, pvMw: 2.5).Run();      // 96 power flows
            Console.WriteLine($"Vmin {qsts.VminPu.Min():0.000}, reverse flow {qsts.EnergyExportMwh:0.00} MWh");

            var lole = AdequacyAssessment.SequentialMonteCarlo(units, AdequacyAssessment.SyntheticAnnualLoad(160));
            """),
        new("voltagecontrol", "planning",
            new("Voltage control in time series", "Kendali tegangan dalam deret waktu"),
            new("Simulate the 33-bus feeder behind a substation transformer for a day, with and without an on-load tap changer and a switched capacitor bank, and compare the voltage band, tap moves and switching operations.",
                "Simulasikan penyulang 33 bus di belakang trafo gardu selama sehari, dengan dan tanpa pengubah tap berbeban serta bank kapasitor terkendali, lalu bandingkan pita tegangan, gerakan tap, dan operasi pensaklaran."),
            ["Simulation", "PowerFlow", "Visualization"], """
            using Electrical.Simulation;

            var sim = QstsSimulation.Case33ControlDemo(days: 1, withControl: false);   // tap fixed, no capacitor
            sim.Controllers.Add(new OltcController(branchIndex: 32, controlledBusId: 1)
            {
                Name = "OLTC T1", TargetPu = 1.02, BandwidthPu = 0.0125, TapStepPu = 0.00625, DelaySteps = 1,
            });
            sim.Controllers.Add(new CapacitorController(busId: 30, stageMvar: 0.45, stages: 2)
            {
                Name = "Cap bus 30", OnThreshold = 0.97, OffThreshold = 1.03, DelaySteps = 1,
            });

            var r = sim.Run();
            Console.WriteLine($"violations {r.VoltageViolationSteps()}, Vmin {r.VminPu.Min():0.0000}");
            Console.WriteLine($"tap steps {r.TotalOperations("OLTC T1")}, capacitor ops {r.TotalOperations("Cap bus 30")}");
            double[] tap = r.ControllerState("OLTC T1");                               // position per 15-min step
            """),
        new("digitaltwin", "twin",
            new("Live digital twin", "Kembaran digital langsung"),
            new("Feed a grid twin with simulated RTU scans for a day: it estimates the state every 15 minutes, rejects bad telemetry, explains alarms in English and Indonesian, tracks transformer ageing, and answers what-if questions.",
                "Isi kembaran digital jaringan dengan pindaian RTU simulasi selama sehari: ia mengestimasi keadaan tiap 15 menit, menolak telemetri buruk, menjelaskan alarm dalam bahasa Inggris dan Indonesia, memantau penuaan trafo, dan menjawab pertanyaan bagaimana-jika."),
            ["DigitalTwin", "StateEstimation", "SCADA"], """
            using Electrical.DigitalTwin;

            var result = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions
            {
                Steps = 96, StepSize = TimeSpan.FromMinutes(15), PeakLoadScale = 1.2,
                BadData = [new TwinBadDataInjection(Step: 40, Tag: "BUS14.VM", Value: 0.82)],
            }).Run();
            Console.WriteLine($"max |V| error {result.MaxVoltageErrorPu * 1000:0.0} mpu");
            foreach (var a in result.RaisedAlarms) Console.WriteLine(a.Localized("id"));

            var whatIf = WhatIfSimulator.Run(net, [new OpenBranch(0)]);
            Console.WriteLine(whatIf.Summary);
            """),
        new("agent", "twin",
            new("AI grid-operator agent", "Agen AI operator jaringan"),
            new("Ask about the grid in English or Indonesian. The agent picks power-flow, short-circuit, contingency, forecast and twin tools; it runs offline with a rule-based model, or with an LLM when ELECTRICAL_LLM_* variables are set.",
                "Bertanya tentang jaringan dalam bahasa Inggris atau Indonesia. Agen memilih alat aliran daya, hubung singkat, kontingensi, peramalan, dan twin; berjalan offline dengan model berbasis aturan, atau dengan LLM bila variabel ELECTRICAL_LLM_* diatur."),
            ["AI", "DigitalTwin", "PowerFlow"], """
            using Electrical.AI;

            var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });   // LLM from ELECTRICAL_LLM_*, else offline
            var answer = await agent.AskAsync("Berapa arus hubung singkat di bus 4?");
            Console.WriteLine(answer.Text);
            Console.WriteLine($"tools: {string.Join(", ", answer.ToolsUsed)} · model {answer.Model} · {answer.Language}");
            """),
        new("plf", "hpc",
            new("Probabilistic load flow", "Aliran daya probabilistik"),
            new("Run a Monte-Carlo power flow over correlated load uncertainty and look at the distribution of losses and minimum voltage.",
                "Jalankan aliran daya Monte-Carlo dengan ketidakpastian beban berkorelasi dan lihat sebaran rugi-rugi serta tegangan minimum."),
            ["HPC", "Native", "PowerFlow"], """
            using Electrical.Core.Network;
            using Electrical.HPC;

            var plf = ProbabilisticLoadFlow.Run(TestCases.Case33BW(), new ProbabilisticLoadFlowOptions
            {
                Samples = 1000,           // Gaussian draws from the Rust kernel (managed fallback)
                LoadStdFraction = 0.10,   // independent per-load σ
                SystemStdFraction = 0.05, // common-mode σ
            });

            Console.WriteLine($"{plf.Backend}: {plf.Converged}/{plf.Samples} in {plf.Elapsed.TotalMilliseconds:0} ms");
            Console.WriteLine($"P(Vmin < 0.95) = {plf.ProbabilityAnyUnderVoltage():P1}");
            """),
        new("scada", "ops",
            new("SCADA: RTU, alarms & historian", "SCADA: RTU, alarm & historian"),
            new("Replay a day of operation through a simulated Modbus RTU, scan it into a tag database, raise ISA-18.2 alarms and compress the history with swinging-door.",
                "Putar ulang satu hari operasi melalui RTU Modbus simulasi, pindai ke basis data tag, bangkitkan alarm ISA-18.2, dan kompres riwayat dengan swinging-door."),
            ["SCADA", "PowerFlow"], """
            using Electrical.PowerFlow;
            using Electrical.SCADA;

            var rtu = new RtuSimulator(PowerFlowSolver.Solve(net));        // Float32 input registers + breaker bits
            var tags = new TagDatabase();
            rtu.RegisterTags(tags);

            var alarms = new AlarmManager();
            alarms.Define(new AlarmDefinition("BUS14.VM", Low: 1.02, LowLow: 1.005, Deadband: 0.002));
            alarms.Attach(tags);

            var historian = new Historian(new HistorianTagOptions(CompressionMode.SwingingDoor, Deviation: 0.5));
            historian.Attach(tags);

            foreach (var (t, result) in day) { rtu.Publish(result); tags.Scan(rtu.Map, unitId: 1, t); }
            Console.WriteLine($"BR0.PF compression {historian.CompressionRatio("BR0.PF"):0.0}×");
            """),
        new("iec61850", "ops",
            new("IEC 61850: GOOSE, SV & SCL", "IEC 61850: GOOSE, SV & SCL"),
            new("Build a breaker-trip GOOSE message and a merging-unit Sampled Values frame, inspect every byte, and browse the IED model from an SCL file.",
                "Susun pesan GOOSE trip pemutus dan frame Sampled Values merging unit, periksa setiap byte, dan telusuri model IED dari berkas SCL."),
            ["IEC61850"], """
            using Electrical.IEC61850;

            var state = new GoosePublisherState();
            state.OnStateChange(DateTimeOffset.UtcNow);                 // breaker opened → StNum++
            var pdu = state.CreatePdu("IED1CTRL/LLN0$GO$gcbTrip", "IED1CTRL/LLN0$dsTrip",
                [MmsBitString.FromDoublePoint(DoublePoint.Off), new MmsBitString([0, 0], 13)]);

            byte[] frame = GooseCodec.Encode(new GooseFrame { Vlan = new VlanTag(4, 10), AppId = 0x0001, Pdu = pdu });
            var back = GooseCodec.Decode(frame);                        // BER round trip

            var scl = SclParser.Parse(File.ReadAllText("bay.icd"));
            foreach (var ld in scl.Ieds[0].LDevices) Console.WriteLine(ld.Inst);
            """),
        new("interop", "data",
            new("Model exchange (PSS/E RAW, IEEE CDF, MATPOWER, CIM)", "Pertukaran model (PSS/E RAW, IEEE CDF, MATPOWER, CIM)"),
            new("Read the IEEE 14-bus system from PSS/E RAW v33/v35, IEEE CDF, MATPOWER and CIM, let the format be detected from the content, review the conversion warnings, and check that every route gives the same power flow; then write a case back to RAW.",
                "Baca sistem IEEE 14 bus dari PSS/E RAW v33/v35, IEEE CDF, MATPOWER, dan CIM, biarkan format dideteksi dari isinya, tinjau peringatan konversi, dan periksa bahwa setiap jalur memberi aliran daya yang sama; lalu tulis kembali sebuah kasus ke RAW."),
            ["Interop", "CIM", "PowerFlow"], """
            using Electrical.Core.Network;
            using Electrical.Interop;
            using Electrical.PowerFlow;

            var import = NetworkFormats.Load("ieee14.raw");             // .raw / .cdf / .m / .json, detected from the content
            Console.WriteLine($"{import.Format}: {import.Network.BusCount} buses");
            foreach (var w in import.Warnings) Console.WriteLine($"warning: {w}");

            var pf = PowerFlowSolver.Solve(import.Network);             // 13.393 MW losses, same as case14
            string raw35 = PsseRaw.Write(TestCases.Case9(), version: 35);
            var cdf = IeeeCdf.Read(InteropSamples.Ieee14Cdf);
            """),
        new("cim", "data",
            new("CIM / CGMES export", "Ekspor CIM / CGMES"),
            new("Export a network to CIM RDF/XML (equipment, topology, steady-state hypothesis, state variables), import it back and prove the round trip with a power flow.",
                "Ekspor jaringan ke CIM RDF/XML (peralatan, topologi, hipotesis kondisi tunak, variabel keadaan), impor kembali, dan buktikan hasilnya dengan aliran daya."),
            ["CIM", "PowerFlow"], """
            using Electrical.CIM;
            using Electrical.Core.Network;
            using Electrical.PowerFlow;

            var net = TestCases.Case14();
            string rdf = CimExporter.ExportString(net);                 // cim16 RDF/XML
            var profiles = CimExporter.ExportProfiles(net);             // EQ, TP, SSH, SV documents

            var back = CimImporter.Import(rdf, new CimImportOptions { BaseMva = net.BaseMva });
            double dLoss = PowerFlowSolver.Solve(back).LossesMw - PowerFlowSolver.Solve(net).LossesMw;
            """),
        new("gis", "data",
            new("GIS grid map", "Peta jaringan GIS"),
            new("Place a synthetic 150 kV grid on a Web Mercator map, colour buses by voltage and lines by loading, and export GeoJSON.",
                "Tempatkan jaringan sintetis 150 kV pada peta Web Mercator, warnai bus menurut tegangan dan saluran menurut pembebanan, lalu ekspor GeoJSON."),
            ["GIS", "PowerFlow", "Core"], """
            using Electrical.Core.Network;
            using Electrical.GIS;
            using Electrical.PowerFlow;

            var net = TestCases.SyntheticGrid(49);                      // buses carry GeoPoint locations
            var pf = PowerFlowSolver.Solve(net);

            string svg = GisSvgRenderer.Render(net, pf, new GisSvgOptions { ContinuousColors = true });
            string geojson = GeoJsonExporter.Export(net, pf);           // FeatureCollection for QGIS / Leaflet
            var nearest = new BusSpatialIndex(net).Nearest(new GeoPoint(-6.0, 107.0));
            """),
    ];

    /// <summary>All framework modules with a one-line description (order of <see cref="ElectricalInfo.Modules"/>).</summary>
    // v1.1 packages not yet listed in ElectricalInfo.Modules are appended (Union keeps this idempotent once they are).
    public static readonly ModuleInfo[] Modules = ElectricalInfo.Modules.Union(["Distribution", "Interop", "Markets"]).Select(m => new ModuleInfo(m, ModuleSummary(m))).ToArray();

    public static DemoInfo? Find(string id) => Demos.FirstOrDefault(d => d.Id == id);
    public static DemoInfo Get(string id) => Find(id) ?? throw new KeyNotFoundException($"No demo '{id}'.");
    public static DemoCategory CategoryOf(DemoInfo d) => Categories.First(c => c.Key == d.Category);

    /// <summary>Demos that showcase a module (empty when none yet).</summary>
    public static IEnumerable<DemoInfo> DemosFor(string module) => Demos.Where(d => d.Modules.Contains(module));

    private static L ModuleSummary(string m) => m switch
    {
        "Core" => new("Sparse LU, per-unit, phasors, network model, Y-bus, MATPOWER I/O", "Sparse LU, per-unit, fasor, model jaringan, Y-bus, I/O MATPOWER"),
        "Circuit" => new("Circuit simulation with modified nodal analysis", "Simulasi rangkaian dengan analisis nodal termodifikasi"),
        "PowerFlow" => new("NR, fast-decoupled, Gauss–Seidel, DC, sweep, N-1, PTDF/LODF", "NR, fast-decoupled, Gauss–Seidel, DC, sweep, N-1, PTDF/LODF"),
        "ShortCircuit" => new("IEC 60909 balanced and unbalanced faults", "Gangguan seimbang dan tak seimbang IEC 60909"),
        "Stability" => new("Transient and voltage stability", "Stabilitas transien dan tegangan"),
        "Harmonic" => new("Harmonic penetration and power quality", "Penetrasi harmonik dan kualitas daya"),
        "Protection" => new("Relay coordination, TCC curves, arc flash", "Koordinasi relai, kurva TCC, arc flash"),
        "Renewable" => new("PV, wind, BESS and hosting capacity", "PV, angin, BESS, dan kapasitas hosting"),
        "SmartGrid" => new("Demand response, EV charging, microgrids, VPP", "Respons permintaan, pengisian EV, mikrogrid, VPP"),
        "Optimization" => new("Economic dispatch, DC/AC-OPF with LMPs, LP/MILP solvers, unit commitment", "Dispatch ekonomis, DC/AC-OPF dengan LMP, solver LP/MILP, unit commitment"),
        "Markets" => new("Security-constrained unit commitment, market clearing, settlement, FTRs", "Unit commitment dengan kendala keamanan, kliring pasar, penyelesaian, FTR"),
        "StateEstimation" => new("Weighted least-squares state estimation", "Estimasi keadaan kuadrat terkecil berbobot"),
        "SCADA" => new("Modbus TCP, tags, ISA-18.2 alarms, historian, IEC 104", "Modbus TCP, tag, alarm ISA-18.2, historian, IEC 104"),
        "IEC61850" => new("SCL parsing, GOOSE and Sampled Values codecs", "Parsing SCL, codec GOOSE dan Sampled Values"),
        "CIM" => new("CGMES RDF/XML export and import", "Ekspor dan impor RDF/XML CGMES"),
        "DigitalTwin" => new("Live grid twin fed by telemetry", "Kembaran digital jaringan dari telemetri"),
        "Forecasting" => new("Load and renewable forecasting", "Peramalan beban dan energi terbarukan"),
        "AI" => new("LLM grid-operator agent with tools", "Agen operator jaringan berbasis LLM"),
        "GIS" => new("GeoJSON, Web Mercator maps, outage areas", "GeoJSON, peta Web Mercator, area padam"),
        "Visualization" => new("Single-line diagrams and SVG charts", "Diagram segaris dan grafik SVG"),
        "Simulation" => new("Quasi-static time series and Monte-Carlo reliability", "Deret waktu kuasi-statis dan keandalan Monte-Carlo"),
        "Cloud" => new("Study runner and ASP.NET Core API", "Penjalan studi dan API ASP.NET Core"),
        "HPC" => new("Parallel batch and probabilistic load flow", "Aliran daya batch paralel dan probabilistik"),
        "Native" => new("Rust kernels with managed fallback", "Kernel Rust dengan cadangan managed"),
        "Distribution" => new("Unbalanced three-phase feeders, Carson line constants, OpenDSS", "Penyulang tiga fasa tak seimbang, konstanta saluran Carson, OpenDSS"),
        "Interop" => new("PSS/E RAW, IEEE CDF and format auto-detection", "PSS/E RAW, IEEE CDF, dan deteksi format otomatis"),
        _ => new(m, m),
    };
}
