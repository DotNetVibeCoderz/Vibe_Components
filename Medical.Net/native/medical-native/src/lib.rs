//! Medical.Net native kernels — C ABI (`mdn_*`), consumed from .NET through `LibraryImport`.
//!
//! Every kernel has a managed twin in `Medical.Native.MedicalKernels` with identical semantics,
//! so results never depend on whether this library could be loaded.
//! Return codes: 0 = ok, -1 = null pointer, -2 = invalid argument, -3 = output buffer too small.
//!
//! Made by Gravicode Studios, led by Kang Fadhil.

#![allow(clippy::missing_safety_doc)]

pub mod ecg;
pub mod voi;

pub const OK: i32 = 0;
pub const ERR_NULL: i32 = -1;
pub const ERR_ARG: i32 = -2;
pub const ERR_CAPACITY: i32 = -3;

/// Library version encoded as `major * 10000 + minor * 100 + patch`.
#[no_mangle]
pub extern "C" fn mdn_version() -> u32 {
    1_00_00
}

/// Streaming summary statistics (Welford). Layout shared with C# `MdnStats`.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct MdnStats {
    pub count: u64,
    pub min: f64,
    pub max: f64,
    pub mean: f64,
    pub m2: f64,
}

pub fn stats_i16(values: &[i16], slope: f64, intercept: f64) -> MdnStats {
    let mut s = MdnStats { count: 0, min: f64::INFINITY, max: f64::NEG_INFINITY, mean: 0.0, m2: 0.0 };
    for &raw in values {
        let v = raw as f64 * slope + intercept;
        s.count += 1;
        if v < s.min {
            s.min = v;
        }
        if v > s.max {
            s.max = v;
        }
        let d = v - s.mean;
        s.mean += d / s.count as f64;
        s.m2 += d * (v - s.mean);
    }
    if s.count == 0 {
        s.min = f64::NAN;
        s.max = f64::NAN;
        s.mean = f64::NAN;
    }
    s
}

/// Statistics of rescaled pixel values (`raw * slope + intercept`, e.g. Hounsfield units for CT).
#[no_mangle]
pub unsafe extern "C" fn mdn_stats_i16(values: *const i16, len: usize, slope: f64, intercept: f64, out: *mut MdnStats) -> i32 {
    if out.is_null() || (values.is_null() && len > 0) {
        return ERR_NULL;
    }
    let v = if len == 0 { &[][..] } else { std::slice::from_raw_parts(values, len) };
    *out = stats_i16(v, slope, intercept);
    OK
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stats_with_rescale() {
        let s = stats_i16(&[0, 10, 20], 1.0, -1024.0);
        assert_eq!(s.count, 3);
        assert_eq!(s.min, -1024.0);
        assert_eq!(s.max, -1004.0);
        assert!((s.mean + 1014.0).abs() < 1e-12);
        assert!((s.m2 / 2.0 - 100.0).abs() < 1e-9);
    }
}
