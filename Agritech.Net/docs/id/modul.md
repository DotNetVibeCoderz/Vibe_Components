# Panduan modul

## Agritech.Core
Tipe geografis (`GeoCoordinate`, `GeoPolygon`: luas, keliling, centroid, uji titik), `GeoMath` (haversine, arah, tujuan), `TimeSeries<T>`,
`Stats` (statistik SIMD, persentil, regresi, Otsu), `Matrix`, `Units`, model `Farm`/`Field`, event bus, dan `AddAgritech(...)`.

## Agritech.Crops
Katalog 16 tanaman beserta parameter agronomi (Kc FAO-56, Ky, GDD, RUE, serapan hara), fenologi dan `CropLifecycle`,
estimasi hasil (fungsi produksi air FAO-33, biomassa × indeks panen, NDVI), serta `CropRecommender` dengan kelas kesesuaian lahan FAO (S1/S2/S3/N) dan faktor pembatasnya.

## Agritech.Soil
Klasifikasi tekstur USDA, hidrolika Saxton & Rawls (kapasitas lapang, titik layu, Ksat), indeks kesehatan tanah 0–100 dengan rekomendasi,
kebutuhan kapur, kalkulator pupuk (Urea, ZA, SP-36, TSP, DAP, KCl, NPK Phonska), dan buku besar hara (`NutrientLedger`).

## Agritech.Weather
ET0 Penman–Monteith & Hargreaves (FAO-56), Open-Meteo (prakiraan dan arsip ERA5), generator cuaca stokastik, proyeksi iklim SSP (delta change + CO₂),
serta indeks agroklimat (musim kering, hari panas, awal musim hujan, indeks ariditas).

## Agritech.GIS
Konversi UTM ↔ WGS-84 (galat bolak-balik di bawah 1 mm), Web Mercator, ubin peta, GeoJSON, geohash, indeks spasial quadtree, penyederhanaan Douglas–Peucker, dan convex hull.

## Agritech.RemoteSensing
Tabel pita Sentinel-2/Landsat/MODIS dan konversi DN → reflektansi, indeks NDVI, EVI, SAVI, NDWI, NDMI, NDRE, GNDVI, NBR, MSAVI2,
statistik zonal, zona pengelolaan, deteksi perubahan, fenometrik (awal/puncak/akhir musim), serta pencarian citra STAC (Earth Search).

## Agritech.Drones
Perhitungan GSD dan cakupan kamera, misi survei/penyemprotan pola bolak-balik, ekspor `.plan` QGroundControl,
deteksi gulma (ExG + Otsu + barisan tanaman), analisis keparahan lesi daun, dan ortomosaik georeferensi langsung.

## Agritech.IoT
Model `SensorReading`, transport MQTT (MQTTnet) dan in-memory, codec Cayenne LPP untuk LoRaWAN, parser webhook The Things Network,
`EdgeProcessor` (aturan ambang, deteksi anomali z-score, deadband), dan jaringan sensor simulasi.

## Agritech.AI
Prakiraan hasil (ridge regression + validasi silang), risiko penyakit (blas padi, busuk daun, antraknosa, bulai jagung, busuk buah kakao),
fenologi hama berbasis derajat-hari (ulat grayak, wereng batang cokelat, lalat buah, PBKo), penasihat irigasi FAO-56, penasihat pupuk bertahap, dan `FarmAgent`.

## Agritech.Simulation
Model tanaman harian bergaya APSIM/DSSAT: fenologi waktu termal, LAI, RUE dengan efek CO₂, air tanah 5 lapisan (runoff SCS, perkolasi),
pertumbuhan akar, nitrogen (mineralisasi, pencucian, pengenceran N kritis), dan `ScenarioRunner` untuk skenario pengelolaan × iklim secara paralel.

## Agritech.Genomics
Analisis DNA/RNA (GC, komplemen balik, transkripsi, translasi, ORF, k-mer, Tm), FASTA, alignment global/lokal,
QC penanda SNP (MAF, HWE), matriks kekerabatan genomik VanRaden, prediksi GEBV rrBLUP, koefisien inbreeding dari silsilah, dan rencana persilangan.

## Agritech.Livestock
Registri ternak, peringatan tanda vital, kalender reproduksi (birahi, PKB, perkiraan melahirkan, kering kandang),
THI dan kehilangan susu, bobot dari lingkar dada (rumus Schoorl), konsumsi bahan kering, dan deteksi birahi dari aktivitas.

## Agritech.Fisheries
Spesies budidaya (nila, lele, patin, udang vaname, bandeng, gurame), amonia tak terionisasi, saturasi DO, penilaian kualitas air,
rencana pakan yang disesuaikan dengan suhu, DO, dan amonia, pertumbuhan TGC, serta padat tebar.

## Agritech.Robotics
Grid okupansi dan A*, jalur cakupan traktor dengan headland, model kinematika traktor, pengendali pure-pursuit, rute robot panen (2-opt), dan robot rumah kaca.

## Agritech.SupplyChain
Ketertelusuran lot dengan rantai hash SHA-256, pemecahan dan penggabungan lot, telusur mundur/maju, penarikan produk, kode GS1, umur simpan Q10, dan inventori FEFO.

## Agritech.Marketplace
Buku order berprioritas harga-waktu dengan biaya angkut per km, analitik harga (rata-rata bergerak, volatilitas, proyeksi Holt, indeks musiman), dan harga wajar di tingkat petani.

## Agritech.DigitalTwin
`FarmTwin` mengikat perangkat ke petak, memproses data sensor, memajukan fenologi, dan menghasilkan keputusan beserta alasannya
(irigasi, hentikan irigasi, pemupukan, pemantauan penyakit, perlindungan panas, panen, cek perangkat).

## Agritech.Visualization / .Blazor
Palet warna agronomi, encoder PNG tanpa dependensi, grafik SVG, kebun isometrik 2.5D, dan komponen Blazor (`AgriLineChart`, `RasterImage`, `KpiCard`, dan lainnya).
