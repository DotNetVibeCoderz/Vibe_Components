# Block reference

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/blocks.md)

Names in brackets are the GNU Radio equivalents. "f" = `float`, "c" = `Complex32`, "b" = `byte`.

## GnuRadio.Blocks — sources, sinks, stream and math

| Block | Notes |
|---|---|
| `SignalSource<T>` (`analog.sig_source_f/c`) | sine, cosine, square, triangle, sawtooth, constant; live `Frequency`, `Amplitude`; `cmd` message port |
| `NoiseSource<T>` (`analog.noise_source`) | Gaussian, uniform, Laplacian, impulse; seedable |
| `ConstantSource<T>`, `NullSource<T>`, `VectorSource<T>` | `VectorSource` can repeat and attach tags |
| `NullSink<T>`, `VectorSink<T>`, `ProbeSignal<T>`, `TagDebug<T>` | `VectorSink` keeps data and tags |
| `MessageDebug`, `MessageStrobe` | message print/store, periodic message |
| `FileSource<T>`, `FileSink<T>` (`blocks.file_*`) | raw binary, GNU Radio-compatible (`.cfile`) |
| `WavFileSource`, `WavFileSink` | PCM 8/16/24/32-bit and float |
| `AudioSink` (`audio.sink`) | Windows waveOut built in; pluggable `IAudioBackend` |
| `Head`, `SkipHead`, `Throttle`, `Delay`, `KeepOneInN`, `Copy` | stream control |
| `StreamToVector`, `VectorToStream`, `Interleave`, `Deinterleave` | reshaping |
| `Add`, `Subtract`, `Multiply`, `MultiplyConst`, `AddConst`, `MultiplyConjugate`, `Conjugate`, `Integrate` | SIMD fast paths for float/complex |
| `ComplexToMag(Squared)`, `ComplexToArg`, `ComplexToReal/Imag/Float`, `FloatToComplex`, `NLog10`, `Rms`, `Threshold`, `Rotator` | |
| `FloatToShort`, `ShortToFloat`, `CharToFloat`, `UCharToFloat`, `FloatToUChar`, `Interleaved{Short,Char,UChar}ToComplex`, `ComplexToInterleavedShort` | hardware sample formats |
| `PackKBits`, `UnpackKBits`, `RepackBits`, `MapBytes`, `BinarySlicer`, `StreamToPdu`, `PduToStream` | bits and PDUs |

## GnuRadio.Filter

| Item | Notes |
|---|---|
| `Firdes` (`filter.firdes`) | `LowPass`, `HighPass`, `BandPass`, `BandReject`, `ComplexBandPass`, `RootRaisedCosine`, `Gaussian`, `Hilbert`, `MatchedFilter`, magnitude responses; tap counts follow GNU Radio's rules |
| `Window` | Rectangular, Hamming, Hann, Blackman, Blackman–Harris, Kaiser, flat-top, Bartlett, Gaussian |
| `FirFilterFff`, `FirFilterCcf`, `FirFilterCcc` | decimating FIR; `SetTaps` while running |
| `FreqXlatingFirFilter` | shift + filter + decimate; live `CenterFrequency` |
| `InterpFirFilter<T>`, `RationalResampler<T>`, `ArbitraryResampler<T>` | polyphase; arbitrary ratio with a 32-branch bank |
| `IirFilter`, `BiquadFilter`, `IirDesign` | Butterworth low/high-pass, notch |
| `SinglePoleIir<T>`, `MovingAverage<T>`, `DcBlocker<T>`, `HilbertFilter` | |

## GnuRadio.Fft

`FftPlan` (radix-2, Bluestein for other sizes), `FftMath`, `FftBlock` (`fft.fft_vcc`), `LogPowerFft` (`logpwrfft`),
`Spectral.WelchPsd`, `Spectral.Spectrogram`, `Spectral.NoiseFloor`.

## GnuRadio.Analog

`QuadratureDemod`, `FrequencyModulator`, `PhaseModulator`, `PhaseDemod`, `AmModulator`, `AmDemod`, `SsbModulator`,
`SsbDemod`, `Agc<T>`, `Pll` (carrier tracking / reference output), `PllFreqDet`, `ControlLoop`, `Emphasis`
(pre/de-emphasis), `WbfmReceive` (`wfm_rcv`), `NbfmReceive` (`nbfm_rx`), `FmTransmit`, `MorseKeyer`, `MorseDecoder`.

## GnuRadio.Digital

`Constellation` (BPSK, QPSK, M-PSK, square QAM, Gray coded; hard and max-log soft decisions, EVM),
`ChunksToSymbols`, `ConstellationDecoder`, `ConstellationSoftDecoder`, `DiffEncoder/Decoder`, `Lfsr`,
`AdditiveScrambler`, `Scrambler`/`Descrambler`, `ConstellationModulator` (`generic_mod`), `ConstellationReceiver`,
`BitsToNrz`, `FskModulator` (FSK/GFSK/GMSK), `FskDemodulator`, `OfdmParameters`, `OfdmModulator`, `OfdmDemodulator`
(Schmidl & Cox, LS equalisation, pilots), `PacketFormat`, `PacketFramer`, `PacketDeframer`.

Soft decisions are LLRs with **positive meaning bit 0**, the convention used by every FEC decoder.

## GnuRadio.Fec

`Crc` (CRC-8, CRC-16/CCITT, CRC-16/X.25, CRC-32, custom), `CrcAppend`, `CrcCheck`, `ReedSolomon` (shortened codes,
Berlekamp–Massey + Chien + Forney), `ConvolutionalCode` (K up to 12, soft Viterbi, CCSDS K=7 default), `LdpcCode`
(Gallager construction, alist import/export, normalised min-sum), `PolarCode` (Bhattacharyya design, SC decoding),
`RepetitionCode`, `UncodedCode`, `ReedSolomonFec`, `FecEncoderBlock`, `FecDecoderBlock`, `BerCounter`,
`BerMath` (theory curves, `NoiseVoltage`, Monte-Carlo `Simulate`).

## GnuRadio.Synchronization

`CostasLoop` (order 2/4/8, frequency output), `DecisionDirectedCarrierRecovery`, `SymbolSync` (Gardner, PI loop, cubic
interpolation), `ClockRecoveryMm`, `CorrelateAccessCodeTag`, `CorrelationEstimator` (`corr_est`), `Interpolator`.

## GnuRadio.Channels

`AwgnChannel`, `FrequencyOffset`, `DopplerShift`, `FadingModel` (Rayleigh/Rician, sum of sinusoids),
`FadingProcess`, `SelectiveFadingModel`, `ChannelModel` (`channels.channel_model`).

## GnuRadio.Network

`TcpSource<T>`, `TcpSink<T>` (client or server), `UdpSource<T>`, `UdpSink<T>`.

## GnuRadio.Sinks — visualisation

`TimeSink<T>` (triggered scope), `FrequencySink` (averaging, max-hold), `WaterfallSink`, `ConstellationSink`,
`VectorFrameSink`, `NumberSink`, plus `Colormap` and `Axis` helpers. Sinks keep the latest frame and a version counter;
UIs copy only when it changed.

## GnuRadio.Extensions

| Area | Blocks |
|---|---|
| LoRa (`gr-lora`) | `LoRaParameters`, `LoRaModulator`, `LoRaDemodulator` (CSS PHY and frame sync only, not LoRaWAN) |
| Radar (`gr-radar`) | `FmcwParameters`, `FmcwRadarSource`, `FmcwRangeProcessor`, `Cfar`, `RangeDoppler` |
| Satellites (`gr-satellites`) | `Hdlc`, `HdlcFramer`, `HdlcDeframer`, `NrziEncoder/Decoder`, `Ax25` |
| Inspector (`gr-inspector`) | `SignalDetector` |
| Machine learning for RF | `CumulantFeatures`, `ModulationClassifier` (trainable nearest-centroid) |

## GnuRadio.Kernels

`Volk`: complex multiply, conjugate multiply, magnitude, phase, dot products (real, complex, complex × real), scale,
add, rotator, dB conversion — portable SIMD (SSE/AVX/NEON) with optional native routing. See [performance](performance.md).
