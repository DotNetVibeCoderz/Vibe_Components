# Referensi blok

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/blocks.md)

Nama di dalam kurung adalah padanannya di GNU Radio. "f" = `float`, "c" = `Complex32`, "b" = `byte`.

## GnuRadio.Blocks — source, sink, stream, dan matematika

| Blok | Catatan |
|---|---|
| `SignalSource<T>` (`analog.sig_source_f/c`) | sinus, kosinus, kotak, segitiga, gigi gergaji, konstan; `Frequency`, `Amplitude` dapat diubah langsung; port pesan `cmd` |
| `NoiseSource<T>` (`analog.noise_source`) | Gaussian, uniform, Laplacian, impuls; dapat diberi seed |
| `ConstantSource<T>`, `NullSource<T>`, `VectorSource<T>` | `VectorSource` dapat mengulang dan melampirkan tag |
| `NullSink<T>`, `VectorSink<T>`, `ProbeSignal<T>`, `TagDebug<T>` | `VectorSink` menyimpan data dan tag |
| `MessageDebug`, `MessageStrobe` | mencetak/menyimpan pesan, pesan periodik |
| `FileSource<T>`, `FileSink<T>` (`blocks.file_*`) | biner mentah, kompatibel dengan GNU Radio (`.cfile`) |
| `WavFileSource`, `WavFileSink` | PCM 8/16/24/32-bit dan float |
| `AudioSink` (`audio.sink`) | waveOut Windows sudah terpasang; `IAudioBackend` dapat diganti |
| `Head`, `SkipHead`, `Throttle`, `Delay`, `KeepOneInN`, `Copy` | kontrol stream |
| `StreamToVector`, `VectorToStream`, `Interleave`, `Deinterleave` | pembentukan ulang |
| `Add`, `Subtract`, `Multiply`, `MultiplyConst`, `AddConst`, `MultiplyConjugate`, `Conjugate`, `Integrate` | jalur cepat SIMD untuk float/complex |
| `ComplexToMag(Squared)`, `ComplexToArg`, `ComplexToReal/Imag/Float`, `FloatToComplex`, `NLog10`, `Rms`, `Threshold`, `Rotator` | |
| `FloatToShort`, `ShortToFloat`, `CharToFloat`, `UCharToFloat`, `FloatToUChar`, `Interleaved{Short,Char,UChar}ToComplex`, `ComplexToInterleavedShort` | format sampel perangkat keras |
| `PackKBits`, `UnpackKBits`, `RepackBits`, `MapBytes`, `BinarySlicer`, `StreamToPdu`, `PduToStream` | bit dan PDU |

## GnuRadio.Filter

| Item | Catatan |
|---|---|
| `Firdes` (`filter.firdes`) | `LowPass`, `HighPass`, `BandPass`, `BandReject`, `ComplexBandPass`, `RootRaisedCosine`, `Gaussian`, `Hilbert`, `MatchedFilter`, respons magnitudo; jumlah tap mengikuti aturan GNU Radio |
| `Window` | Rectangular, Hamming, Hann, Blackman, Blackman–Harris, Kaiser, flat-top, Bartlett, Gaussian |
| `FirFilterFff`, `FirFilterCcf`, `FirFilterCcc` | FIR dengan desimasi; `SetTaps` saat berjalan |
| `FreqXlatingFirFilter` | geser + filter + desimasi; `CenterFrequency` dapat diubah langsung |
| `InterpFirFilter<T>`, `RationalResampler<T>`, `ArbitraryResampler<T>` | polyphase; rasio sembarang dengan bank 32 cabang |
| `IirFilter`, `BiquadFilter`, `IirDesign` | low/high-pass Butterworth, notch |
| `SinglePoleIir<T>`, `MovingAverage<T>`, `DcBlocker<T>`, `HilbertFilter` | |

## GnuRadio.Fft

`FftPlan` (radix-2, Bluestein untuk ukuran lain), `FftMath`, `FftBlock` (`fft.fft_vcc`), `LogPowerFft` (`logpwrfft`),
`Spectral.WelchPsd`, `Spectral.Spectrogram`, `Spectral.NoiseFloor`.

## GnuRadio.Analog

`QuadratureDemod`, `FrequencyModulator`, `PhaseModulator`, `PhaseDemod`, `AmModulator`, `AmDemod`, `SsbModulator`,
`SsbDemod`, `Agc<T>`, `Pll` (pelacakan pembawa / keluaran referensi), `PllFreqDet`, `ControlLoop`, `Emphasis`
(pre/de-emphasis), `WbfmReceive` (`wfm_rcv`), `NbfmReceive` (`nbfm_rx`), `FmTransmit`, `MorseKeyer`, `MorseDecoder`.

## GnuRadio.Digital

`Constellation` (BPSK, QPSK, M-PSK, QAM persegi, berkode Gray; keputusan keras dan keputusan lunak max-log, EVM),
`ChunksToSymbols`, `ConstellationDecoder`, `ConstellationSoftDecoder`, `DiffEncoder/Decoder`, `Lfsr`,
`AdditiveScrambler`, `Scrambler`/`Descrambler`, `ConstellationModulator` (`generic_mod`), `ConstellationReceiver`,
`BitsToNrz`, `FskModulator` (FSK/GFSK/GMSK), `FskDemodulator`, `OfdmParameters`, `OfdmModulator`, `OfdmDemodulator`
(Schmidl & Cox, ekualisasi LS, pilot), `PacketFormat`, `PacketFramer`, `PacketDeframer`.

Keputusan lunak berupa LLR dengan **nilai positif berarti bit 0**, konvensi yang digunakan oleh setiap dekoder FEC.

## GnuRadio.Fec

`Crc` (CRC-8, CRC-16/CCITT, CRC-16/X.25, CRC-32, kustom), `CrcAppend`, `CrcCheck`, `ReedSolomon` (kode yang dipendekkan,
Berlekamp–Massey + Chien + Forney), `ConvolutionalCode` (K hingga 12, Viterbi lunak, bawaan CCSDS K=7), `LdpcCode`
(konstruksi Gallager, impor/ekspor alist, min-sum ternormalisasi), `PolarCode` (desain Bhattacharyya, dekoding SC),
`RepetitionCode`, `UncodedCode`, `ReedSolomonFec`, `FecEncoderBlock`, `FecDecoderBlock`, `BerCounter`,
`BerMath` (kurva teoretis, `NoiseVoltage`, `Simulate` Monte-Carlo).

## GnuRadio.Synchronization

`CostasLoop` (orde 2/4/8, keluaran frekuensi), `DecisionDirectedCarrierRecovery`, `SymbolSync` (Gardner, loop PI,
interpolasi kubik), `ClockRecoveryMm`, `CorrelateAccessCodeTag`, `CorrelationEstimator` (`corr_est`), `Interpolator`.

## GnuRadio.Channels

`AwgnChannel`, `FrequencyOffset`, `DopplerShift`, `FadingModel` (Rayleigh/Rician, jumlah sinusoida),
`FadingProcess`, `SelectiveFadingModel`, `ChannelModel` (`channels.channel_model`).

## GnuRadio.Network

`TcpSource<T>`, `TcpSink<T>` (klien atau server), `UdpSource<T>`, `UdpSink<T>`.

## GnuRadio.Sinks — visualisasi

`TimeSink<T>` (scope dengan trigger), `FrequencySink` (perataan, max-hold), `WaterfallSink`, `ConstellationSink`,
`VectorFrameSink`, `NumberSink`, ditambah helper `Colormap` dan `Axis`. Sink menyimpan frame terbaru dan sebuah
penghitung versi; UI hanya menyalin data ketika ada perubahan.

## GnuRadio.Extensions

| Area | Blok |
|---|---|
| LoRa (`gr-lora`) | `LoRaParameters`, `LoRaModulator`, `LoRaDemodulator` (hanya PHY CSS dan sinkronisasi frame, bukan LoRaWAN) |
| Radar (`gr-radar`) | `FmcwParameters`, `FmcwRadarSource`, `FmcwRangeProcessor`, `Cfar`, `RangeDoppler` |
| Satelit (`gr-satellites`) | `Hdlc`, `HdlcFramer`, `HdlcDeframer`, `NrziEncoder/Decoder`, `Ax25` |
| Inspector (`gr-inspector`) | `SignalDetector` |
| Machine learning untuk RF | `CumulantFeatures`, `ModulationClassifier` (nearest-centroid yang dapat dilatih) |

## GnuRadio.Kernels

`Volk`: perkalian kompleks, perkalian konjugat, magnitudo, fase, perkalian titik (real, kompleks, kompleks × real),
penskalaan, penjumlahan, rotator, konversi dB — SIMD portabel (SSE/AVX/NEON) dengan opsi pengalihan ke native. Lihat
[performa](performance.md).
