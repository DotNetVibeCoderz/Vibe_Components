# Modul

| Modul | Tipe utama | Dasar ilmiah / catatan |
|---|---|---|
| Core | `SpeciesCatalog`, `Farm`, `Pond`, `CultureCycle`, `Stats`, `TimeSeries`, `Numerics`, `GeoPolygon` | 10 spesies tropis: rentang toleransi, TGC, von Bertalanffy, panjang–bobot, tabel pakan, harga |
| WaterQuality | `WaterChemistry`, `WaterQualityIndex`, `AlertEngine`, `RecommendationEngine` | NH₃ Emerson + koreksi salinitas Bower & Bidwell; saturasi DO Benson & Krause (APHA 4500-O); WQI tertimbang + CCME; dosis NaHCO₃, kapur, garam (Cl⁻:NO₂-N 20:1), molase (C:N) |
| Growth | `GrowthModels`, `GrowthSimulator`, `HarvestPredictor` | TGC harian × pengaruh suhu, DO, NH₃, salinitas, kepadatan, ransum; mortalitas stres; pakan = pertambahan bobot × FCR spesies yang memburuk saat stres |
| Growth (v1.3) | `Bioenergetics`, `DebModel`, `ShrimpMoultModel` | Bioenergetika Wisconsin (FB4: konsumsi, respirasi Kitchell, neraca energi, `FitP`; parameter nila dan vaname bersifat indikatif); DEB standar (parameter Add-my-Pet, setara von Bertalanffy pada pakan konstan); siklus moulting udang (periode antar-moult, tahap Drach A–D3, moulting massal dan faktor pakan) |
| Feeding | `FeedingCalculator`, `FeedOptimizer`, `AutoFeedingAdvisor`, `Fcr` | Tabel pakan, jadwal, biaya per kg pertambahan bobot, metode Pearson, pemberi pakan otomatis berbasis sisa pakan (anco) |
| Disease | `DiseaseKnowledgeBase`, `DiagnosisEngine` | 15 penyakit; naive Bayes + hipotesis "bukan semuanya"; risiko lingkungan noisy-OR; penanda wajib lapor WOAH |
| StockAssessment | `SurplusProduction`, `StockRecruitment`, `YieldPerRecruit`, `LengthBased` | Schaefer/Fox, Beverton–Holt, Ricker, Fmax/F0.1/F40 %, M Pauly/Then, Powell–Wetherall, kurva tangkapan, Kobe |
| Population | `AgeStructuredModel`, `LeslieMatrix`, `PredatorPrey` | Rekrutmen (steepness), musim pemijahan, migrasi, predasi Holling II, aturan panen hockey-stick |
| Environment | `OpenMeteoClient`, `BmkgClient`, `NasaPowerClient`, `MarineForecastClient`, `SyntheticWeather`, `WeatherImpactPredictor`, `ClimateRiskAnalysis` | Suhu air (model jeda), pengenceran hujan, pH turun karena limpasan, indeks fotosintesis, stratifikasi/umbalan, badai |
| Hydrodynamic | `Grid2D`, `FlowFields`, `ScalarTransport`, `PondOxygenBudget`, `RasMassBalance`, `Sediment` | Adveksi–difusi eksplisit (kernel Rust), neraca oksigen Boyd, AOTR kincir, Timmons & Ebeling untuk RAS |
| IoT | `MqttSensorTransport`, `Modbus`, `ModbusRtuClient`, `ChirpStack`, `LoRaPayload`, `EdgeAnalytics`, `SimulatedSensorTransport` | Lihat [IoT dan kembaran digital](iot-kembaran.md) |
| Data | `AquaStore`, `RecordingTransport`, `TimeSeriesExport` | EF Core / SQLite untuk tambak, siklus, pembacaan, keputusan; ekspor CSV dan Parquet. Lihat [Data dan konektor](data-konektor.md) |
| IoT.OpcUa | `OpcUaReader`, `PondOpcUaSimulator` | Klien OPC UA (stack OPC Foundation) dan simulator kolam |
| IoT.IoTCom (preview) | `IoTComModbusTransport`, `AquaSenML`, `NmeaCageDecoder`, `CageZoneWatch`, `SparkplugPondPublisher` | Adaptor IoTCom.Net: Modbus, SenML, NMEA/AIS untuk KJA, Sparkplug B ke SCADA |
| GIS.Gdal | `GdalRaster`, `WaterColour` | GeoTIFF lewat GDAL.Net; NDWI, MNDWI, klorofil-a dari NDCI (Mishra & Mishra 2012), kekeruhan (Dogliotti 2015), status trofik |
| GIS | `GeoJson`, `FarmMapping`, `DrainageAnalysis`, `WaterSourceAnalysis`, `NtsInterop` | IDW, D8, NetTopologySuite + ProjNet (UTM, buffer, union) |
| AI | `GrowthPredictor`, `MortalityPredictor`, `FeedOptimizationPolicy`, `MlNetGrowthModel`, `OnnxTabularModel`, `AquaAgent` | Lihat [AI dan AquaAgent](agen-ai.md) |
| ComputerVision | `OnnxDetector`, `YoloDecoder`, `AnimalAnalyzer`, `OpenCvInterop`, `ModelHub` | YOLO v5/v8 + NMS; penghitungan, ukuran, skrining bintik putih/luka/insang hitam; unduh model dari Hugging Face Hub dengan cek lisensi dan cache |
| ComputerVision.MediaPipe | `FishMeasurer` | Panjang dan bobot ikan/udang dari foto atau video: satu ketukan, segmentasi MagicTouch (MediaPipeNet), sumbu utama, skala papan ukur, relasi panjang–bobot spesies |
| Forecasting | `HoltLinear`, `HoltWinters`, `Autoregressive`, `DoCrashForecaster`, `HarvestForecaster` | DO subuh = gabungan tren malam teredam dan neraca oksigen; panen P10/P50/P90 |
| DigitalTwin | `PondTwin`, `FarmTwin` | Fusi sensor, soft-sensor pertumbuhan, keputusan beralasan, perintah aerator, what-if |
| Analytics | `KpiCalculator` | FCR, SR, ABW, ADG, SGR, produktivitas, struktur biaya (kata kunci dwibahasa), laba, harga impas, B/C, Indeks Kesehatan Air |
| Reporting | `ReportDocument`, `ReportWriters`, `ComplianceAudit`, `Reports` | XLSX, PDF, DOCX, CSV, JSON, HTML, Markdown; GAP, CBIB, ASC, BAP |
| Visualization / Blazor | `ColorRamp`, `PngEncoder`, `SvgCharts`; `PondMap`, `WaterChart`, `FeedScheduler`, `SensorGrid`, `GISViewer`, `DigitalTwinViewer` | Sertakan `_content/AquaCulture.Blazor/aquaculture.css` |
