# Arsitektur

`Agritech.Core` adalah fondasi bersama. Di atasnya terdapat modul domain (Crops, Soil, Weather, GIS, IoT, Genomics, Livestock, Fisheries, SupplyChain, Marketplace).
`AI` dan `Simulation` menggabungkan Crops, Soil, dan Weather. `Drones` dan `Robotics` memakai GIS. `RemoteSensing` memakai kernel `Native` (Rust).
`DigitalTwin` menyatukan IoT, AI, dan modul agronomi. `Visualization` bersifat netral UI, sedangkan `Visualization.Blazor` menyediakan komponen web.

## Prinsip desain

- **Inti minim dependensi.** Aljabar linear, statistik, PNG/SVG, GeoJSON, dan proyeksi diimplementasikan sendiri.
- **Performa dan efisiensi memori.** Memakai `Span`, `Vector<T>`, `stackalloc`, kamus beku (*frozen dictionary*), kernel Rust multithread, dan `Parallel.For`; pustaka ditandai kompatibel AOT.
- **Data sintetis deterministik.** Pengujian dan tangkapan layar dapat direproduksi.
- **Berbasis literatur ilmiah.** FAO-56, FAO-33, Saxton–Rawls, Magarey, Emerson, THI NRC, VanRaden, dan pure-pursuit Coulter; pengujian memverifikasi contoh yang telah dipublikasikan.
- **Dwibahasa.** Nama tanaman dan patogen, agen, galeri, serta dokumentasi tersedia dalam Bahasa Indonesia dan Inggris.

## Pustaka pendukung
Dapat dipadukan dengan GravicodeScience, Gravicode.RLNet, ThreeNet, HF.Net, MediaPipeNet, IoTComNet, Biotech.Net, TorchSharp, OpenCvSharp, dan ML.NET
melalui antarmuka `IPlantDiseaseClassifier`, `IWeatherProvider`, `ISensorTransport`, dan `IChatModel`.
