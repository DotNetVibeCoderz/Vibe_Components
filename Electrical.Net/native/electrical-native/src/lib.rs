//! Electrical.Net native kernels (C ABI, prefix `eln_`).
//! Made by Gravicode Studios, led by Kang Fadhil.
//!
//! Every function returns 0 on success and a negative code on invalid input:
//! -1 null pointer, -2 invalid length/argument.
//! Complex arrays are interleaved `[re0, im0, re1, im1, ...]`.
//! Each kernel has a managed fallback in `Electrical.Native` with identical semantics.

use std::f64::consts::PI;
use std::slice;

const OK: i32 = 0;
const ERR_NULL: i32 = -1;
const ERR_ARG: i32 = -2;

/// Version encoded as major*10000 + minor*100 + patch.
#[no_mangle]
pub extern "C" fn eln_version() -> u32 {
    10000
}

// ---------------------------------------------------------------- FFT

/// In-place complex FFT (radix-2 iterative) for power-of-two lengths. `inverse != 0` computes the
/// unscaled inverse transform. `data` holds `n` interleaved complex values.
pub fn fft_in_place(data: &mut [f64], inverse: bool) -> Result<(), i32> {
    let n = data.len() / 2;
    if n == 0 || n & (n - 1) != 0 {
        return Err(ERR_ARG);
    }
    // bit reversal
    let mut j = 0usize;
    for i in 1..n {
        let mut bit = n >> 1;
        while j & bit != 0 {
            j ^= bit;
            bit >>= 1;
        }
        j |= bit;
        if i < j {
            data.swap(2 * i, 2 * j);
            data.swap(2 * i + 1, 2 * j + 1);
        }
    }
    let sign = if inverse { 1.0 } else { -1.0 };
    let mut len = 2;
    while len <= n {
        let ang = sign * 2.0 * PI / len as f64;
        let (wr, wi) = (ang.cos(), ang.sin());
        let mut start = 0;
        while start < n {
            let (mut cr, mut ci) = (1.0f64, 0.0f64);
            for k in 0..len / 2 {
                let a = start + k;
                let b = a + len / 2;
                let (br, bi) = (data[2 * b], data[2 * b + 1]);
                let (tr, ti) = (br * cr - bi * ci, br * ci + bi * cr);
                let (ar, ai) = (data[2 * a], data[2 * a + 1]);
                data[2 * a] = ar + tr;
                data[2 * a + 1] = ai + ti;
                data[2 * b] = ar - tr;
                data[2 * b + 1] = ai - ti;
                let ncr = cr * wr - ci * wi;
                ci = cr * wi + ci * wr;
                cr = ncr;
            }
            start += len;
        }
        len <<= 1;
    }
    Ok(())
}

#[no_mangle]
pub unsafe extern "C" fn eln_fft(data: *mut f64, n: usize, inverse: i32) -> i32 {
    if data.is_null() {
        return ERR_NULL;
    }
    let d = slice::from_raw_parts_mut(data, 2 * n);
    match fft_in_place(d, inverse != 0) {
        Ok(()) => OK,
        Err(e) => e,
    }
}

// ---------------------------------------------------------------- Harmonics

/// Harmonic amplitudes (peak) for h = 1..=max_h using the Goertzel algorithm on a real signal
/// sampled at `fs` Hz with fundamental `f0` Hz. Exact for coherent sampling (integer number of cycles).
pub fn harmonics(x: &[f64], fs: f64, f0: f64, out: &mut [f64]) {
    let n = x.len() as f64;
    for (idx, o) in out.iter_mut().enumerate() {
        let h = (idx + 1) as f64;
        let w = 2.0 * PI * h * f0 / fs;
        let (c, s) = (w.cos(), w.sin());
        let coeff = 2.0 * c;
        let (mut s1, mut s2) = (0.0f64, 0.0f64);
        for &v in x {
            let s0 = v + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        let re = s1 - s2 * c;
        let im = s2 * s;
        *o = 2.0 * (re * re + im * im).sqrt() / n;
    }
}

#[no_mangle]
pub unsafe extern "C" fn eln_harmonics(x: *const f64, len: usize, fs: f64, f0: f64, out: *mut f64, max_h: usize) -> i32 {
    if x.is_null() || out.is_null() {
        return ERR_NULL;
    }
    if len == 0 || max_h == 0 || fs <= 0.0 || f0 <= 0.0 {
        return ERR_ARG;
    }
    harmonics(slice::from_raw_parts(x, len), fs, f0, slice::from_raw_parts_mut(out, max_h));
    OK
}

/// THD [%] from harmonic amplitudes (index 0 = fundamental).
#[no_mangle]
pub unsafe extern "C" fn eln_thd(amps: *const f64, len: usize) -> f64 {
    if amps.is_null() || len < 2 {
        return 0.0;
    }
    let a = slice::from_raw_parts(amps, len);
    if a[0] == 0.0 {
        return 0.0;
    }
    let s: f64 = a[1..].iter().map(|v| v * v).sum();
    100.0 * s.sqrt() / a[0]
}

/// Root-mean-square of a real signal.
#[no_mangle]
pub unsafe extern "C" fn eln_rms(x: *const f64, len: usize) -> f64 {
    if x.is_null() || len == 0 {
        return 0.0;
    }
    let s = slice::from_raw_parts(x, len);
    (s.iter().map(|v| v * v).sum::<f64>() / len as f64).sqrt()
}

// ---------------------------------------------------------------- Sparse complex power injections

/// S = V ∘ conj(Y·V) for a CSR complex matrix Y (n×n). `vals`, `v`, `s` interleaved complex.
#[no_mangle]
pub unsafe extern "C" fn eln_power_injections(
    n: usize,
    row_ptr: *const i32,
    col_idx: *const i32,
    vals: *const f64,
    v: *const f64,
    s: *mut f64,
) -> i32 {
    if row_ptr.is_null() || col_idx.is_null() || vals.is_null() || v.is_null() || s.is_null() {
        return ERR_NULL;
    }
    let rp = slice::from_raw_parts(row_ptr, n + 1);
    let nnz = rp[n] as usize;
    let ci = slice::from_raw_parts(col_idx, nnz);
    let y = slice::from_raw_parts(vals, 2 * nnz);
    let vv = slice::from_raw_parts(v, 2 * n);
    let out = slice::from_raw_parts_mut(s, 2 * n);
    for i in 0..n {
        let (mut ir, mut ii) = (0.0f64, 0.0f64);
        for p in rp[i] as usize..rp[i + 1] as usize {
            let k = ci[p] as usize;
            if k >= n {
                return ERR_ARG;
            }
            let (yr, yi) = (y[2 * p], y[2 * p + 1]);
            let (vr, vi) = (vv[2 * k], vv[2 * k + 1]);
            ir += yr * vr - yi * vi;
            ii += yr * vi + yi * vr;
        }
        let (vr, vi) = (vv[2 * i], vv[2 * i + 1]);
        // v * conj(i)
        out[2 * i] = vr * ir + vi * ii;
        out[2 * i + 1] = vi * ir - vr * ii;
    }
    OK
}

// ---------------------------------------------------------------- Deterministic random sampling

/// SplitMix64 step (identical to the managed implementation).
#[inline]
fn splitmix64(state: &mut u64) -> u64 {
    *state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut z = *state;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
    z ^ (z >> 31)
}

#[inline]
fn unit(state: &mut u64) -> f64 {
    // 53-bit mantissa in (0, 1]
    ((splitmix64(state) >> 11) as f64 + 1.0) * (1.0 / 9_007_199_254_740_992.0)
}

/// Fills `out` with N(mean, std) samples using SplitMix64 + Box–Muller (pairs).
pub fn normal_samples(seed: u64, mean: f64, std: f64, out: &mut [f64]) {
    let mut st = seed;
    let mut i = 0;
    while i < out.len() {
        let u1 = unit(&mut st);
        let u2 = unit(&mut st);
        let r = (-2.0 * u1.ln()).sqrt();
        let t = 2.0 * PI * u2;
        out[i] = mean + std * r * t.cos();
        if i + 1 < out.len() {
            out[i + 1] = mean + std * r * t.sin();
        }
        i += 2;
    }
}

#[no_mangle]
pub unsafe extern "C" fn eln_normal_samples(seed: u64, mean: f64, std: f64, out: *mut f64, len: usize) -> i32 {
    if out.is_null() {
        return ERR_NULL;
    }
    normal_samples(seed, mean, std, slice::from_raw_parts_mut(out, len));
    OK
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fft_impulse_is_flat() {
        let mut d = vec![0.0; 16];
        d[0] = 1.0;
        fft_in_place(&mut d, false).unwrap();
        for k in 0..8 {
            assert!((d[2 * k] - 1.0).abs() < 1e-12 && d[2 * k + 1].abs() < 1e-12);
        }
    }

    #[test]
    fn fft_roundtrip() {
        let orig: Vec<f64> = (0..64).map(|i| (i as f64 * 0.37).sin()).collect();
        let mut d = orig.clone();
        fft_in_place(&mut d, false).unwrap();
        fft_in_place(&mut d, true).unwrap();
        for i in 0..64 {
            assert!((d[i] / 32.0 - orig[i]).abs() < 1e-12);
        }
    }

    #[test]
    fn harmonics_of_known_wave() {
        let fs = 6400.0;
        let x: Vec<f64> = (0..640)
            .map(|i| {
                let t = i as f64 / fs;
                100.0 * (2.0 * PI * 50.0 * t).sin() + 20.0 * (2.0 * PI * 250.0 * t).sin()
            })
            .collect();
        let mut out = vec![0.0; 7];
        harmonics(&x, fs, 50.0, &mut out);
        assert!((out[0] - 100.0).abs() < 1e-9);
        assert!((out[4] - 20.0).abs() < 1e-9);
        assert!(out[2].abs() < 1e-9);
        let thd = unsafe { eln_thd(out.as_ptr(), out.len()) };
        assert!((thd - 20.0).abs() < 1e-9);
    }

    #[test]
    fn normal_samples_statistics() {
        let mut v = vec![0.0; 100_000];
        normal_samples(42, 5.0, 2.0, &mut v);
        let m = v.iter().sum::<f64>() / v.len() as f64;
        let s = (v.iter().map(|x| (x - m) * (x - m)).sum::<f64>() / v.len() as f64).sqrt();
        assert!((m - 5.0).abs() < 0.03 && (s - 2.0).abs() < 0.03);
    }

    #[test]
    fn injections_two_bus() {
        // Y = [[1-10j, -1+10j], [-1+10j, 1-10j]], V = [1, 0.98∠-2°]
        let rp = [0i32, 2, 4];
        let ci = [0i32, 1, 0, 1];
        let y = [1.0, -10.0, -1.0, 10.0, -1.0, 10.0, 1.0, -10.0];
        let a = -2.0f64.to_radians();
        let v = [1.0, 0.0, 0.98 * a.cos(), 0.98 * a.sin()];
        let mut s = [0.0; 4];
        let r = unsafe { eln_power_injections(2, rp.as_ptr(), ci.as_ptr(), y.as_ptr(), v.as_ptr(), s.as_mut_ptr()) };
        assert_eq!(r, 0);
        // losses = P1 + P2 must be positive and small
        assert!(s[0] + s[2] > 0.0 && s[0] + s[2] < 0.05);
    }
}
