//! NaN-aware raster statistics in a single pass (Welford), parallel for large rasters.

use crate::{AGT_ERR_NULL, AGT_OK, PARALLEL_THRESHOLD};
use std::slice;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct AgtStats {
    pub count: u64,
    pub min: f64,
    pub max: f64,
    pub mean: f64,
    pub m2: f64,
}

impl AgtStats {
    fn empty() -> Self {
        Self { count: 0, min: f64::INFINITY, max: f64::NEG_INFINITY, mean: 0.0, m2: 0.0 }
    }

    fn push(&mut self, x: f64) {
        self.count += 1;
        let d = x - self.mean;
        self.mean += d / self.count as f64;
        self.m2 += d * (x - self.mean);
        if x < self.min { self.min = x; }
        if x > self.max { self.max = x; }
    }

    /// Chan et al. parallel merge.
    fn merge(a: Self, b: Self) -> Self {
        if a.count == 0 { return b; }
        if b.count == 0 { return a; }
        let n = a.count + b.count;
        let d = b.mean - a.mean;
        let mean = a.mean + d * b.count as f64 / n as f64;
        let m2 = a.m2 + b.m2 + d * d * a.count as f64 * b.count as f64 / n as f64;
        Self { count: n, min: a.min.min(b.min), max: a.max.max(b.max), mean, m2 }
    }
}

fn serial(v: &[f32]) -> AgtStats {
    let mut s = AgtStats::empty();
    for &x in v {
        if !x.is_nan() { s.push(x as f64); }
    }
    s
}

pub fn compute(v: &[f32]) -> AgtStats {
    if v.len() < PARALLEL_THRESHOLD {
        return serial(v);
    }
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4).min(16);
    let chunk = v.len().div_ceil(threads);
    std::thread::scope(|s| {
        let handles: Vec<_> = v.chunks(chunk).map(|c| s.spawn(move || serial(c))).collect();
        handles.into_iter().map(|h| h.join().unwrap_or_else(|_| AgtStats::empty())).fold(AgtStats::empty(), AgtStats::merge)
    })
}

/// Computes count/min/max/mean/M2 (variance = m2 / (count-1)) ignoring NaN.
///
/// # Safety
/// `values` must point to `len` f32 values and `out` to a writable `AgtStats`.
#[no_mangle]
pub unsafe extern "C" fn agt_stats(values: *const f32, len: usize, out: *mut AgtStats) -> i32 {
    if values.is_null() || out.is_null() {
        return AGT_ERR_NULL;
    }
    *out = compute(slice::from_raw_parts(values, len));
    AGT_OK
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stats_with_nan() {
        let s = compute(&[1.0, 2.0, f32::NAN, 3.0]);
        assert_eq!(s.count, 3);
        assert_eq!(s.mean, 2.0);
        assert_eq!(s.m2 / 2.0, 1.0);
    }

    #[test]
    fn parallel_merge_matches() {
        let v: Vec<f32> = (0..200_000).map(|i| (i % 1000) as f32 * 0.001).collect();
        let p = compute(&v);
        let s = serial(&v);
        assert_eq!(p.count, s.count);
        assert!((p.mean - s.mean).abs() < 1e-9);
        assert!((p.m2 - s.m2).abs() / s.m2 < 1e-9);
    }
}
