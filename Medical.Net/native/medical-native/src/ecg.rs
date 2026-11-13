//! Pan-Tompkins style QRS (R-peak) detector.
//!
//! 1. five-point derivative  d[i] = (2x[i] + x[i-1] - x[i-3] - 2x[i-4]) / 8
//! 2. squaring
//! 3. moving-window integration over 150 ms
//! 4. threshold at `threshold_ratio * max(integrated)`
//! 5. each supra-threshold region yields one R peak = argmax of the raw signal over
//!    [region_start - window, region_end], with a 200 ms refractory period.
//!
//! All arithmetic is f64 so the managed twin produces identical indices.

use crate::{ERR_ARG, ERR_CAPACITY, ERR_NULL, OK};

pub fn detect_r_peaks(x: &[f32], fs: f64, threshold_ratio: f64) -> Vec<u32> {
    let n = x.len();
    let mut peaks = Vec::new();
    if n < 8 || !(fs > 0.0) {
        return peaks;
    }
    let w = ((0.150 * fs).round() as usize).max(1);
    let refractory = (0.200 * fs).round() as usize;

    let mut sq = vec![0.0f64; n];
    let mut integ = vec![0.0f64; n];
    let mut acc = 0.0f64;
    for i in 0..n {
        let d = if i >= 4 {
            (2.0 * x[i] as f64 + x[i - 1] as f64 - x[i - 3] as f64 - 2.0 * x[i - 4] as f64) / 8.0
        } else {
            0.0
        };
        sq[i] = d * d;
        acc += sq[i];
        if i >= w {
            acc -= sq[i - w];
        }
        integ[i] = acc / w as f64;
    }
    let max = integ.iter().cloned().fold(0.0f64, f64::max);
    if max <= 0.0 {
        return peaks;
    }
    let thr = threshold_ratio * max;

    let mut i = 0;
    let mut last: Option<usize> = None;
    while i < n {
        if integ[i] > thr {
            let start = i;
            while i < n && integ[i] > thr {
                i += 1;
            }
            let end = i - 1;
            let from = start.saturating_sub(w);
            let mut best = from;
            for k in from..=end {
                if x[k] > x[best] {
                    best = k;
                }
            }
            match last {
                Some(l) if best < l + refractory => {}
                _ => {
                    peaks.push(best as u32);
                    last = Some(best);
                }
            }
        } else {
            i += 1;
        }
    }
    peaks
}

#[no_mangle]
pub unsafe extern "C" fn mdn_detect_r_peaks(signal: *const f32, len: usize, fs: f64, threshold_ratio: f64, out: *mut u32, capacity: usize, out_count: *mut usize) -> i32 {
    if out_count.is_null() || (signal.is_null() && len > 0) {
        return ERR_NULL;
    }
    if !(fs > 0.0) || !(threshold_ratio > 0.0 && threshold_ratio < 1.0) {
        return ERR_ARG;
    }
    let sig = if len == 0 { &[][..] } else { std::slice::from_raw_parts(signal, len) };
    let peaks = detect_r_peaks(sig, fs, threshold_ratio);
    *out_count = peaks.len();
    if peaks.len() > capacity {
        return ERR_CAPACITY;
    }
    if !peaks.is_empty() {
        if out.is_null() {
            return ERR_NULL;
        }
        std::ptr::copy_nonoverlapping(peaks.as_ptr(), out, peaks.len());
    }
    OK
}

#[cfg(test)]
mod tests {
    use super::*;

    fn synthetic(fs: f64, seconds: f64, bpm: f64) -> Vec<f32> {
        let n = (fs * seconds) as usize;
        let rr = 60.0 / bpm;
        (0..n)
            .map(|i| {
                let t = i as f64 / fs;
                let phase = (t % rr) / rr;
                let r = (-((phase - 0.3) / 0.012).powi(2)).exp();
                let tw = 0.25 * (-((phase - 0.6) / 0.06).powi(2)).exp();
                (r + tw) as f32
            })
            .collect()
    }

    #[test]
    fn detects_heart_rate() {
        let fs = 250.0;
        let x = synthetic(fs, 10.0, 72.0);
        let p = detect_r_peaks(&x, fs, 0.3);
        assert!((11..=13).contains(&p.len()), "got {}", p.len());
        let rr: Vec<f64> = p.windows(2).map(|w| (w[1] - w[0]) as f64 / fs).collect();
        let mean = rr.iter().sum::<f64>() / rr.len() as f64;
        assert!((60.0 / mean - 72.0).abs() < 1.0);
    }

    #[test]
    fn flat_signal_has_no_peaks() {
        assert!(detect_r_peaks(&vec![0.0f32; 1000], 250.0, 0.3).is_empty());
    }
}
