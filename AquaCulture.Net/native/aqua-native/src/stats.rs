//! NaN-skipping summary statistics (Welford).

use crate::ERR_NULL;

#[repr(C)]
#[derive(Default, Clone, Copy, Debug)]
pub struct AqStats {
    pub count: u64,
    pub min: f64,
    pub max: f64,
    pub mean: f64,
    pub m2: f64,
}

pub fn summarize(values: &[f32]) -> AqStats {
    let mut s = AqStats { count: 0, min: f64::INFINITY, max: f64::NEG_INFINITY, mean: 0.0, m2: 0.0 };
    for &v in values {
        if v.is_nan() {
            continue;
        }
        let v = v as f64;
        s.count += 1;
        let d = v - s.mean;
        s.mean += d / s.count as f64;
        s.m2 += d * (v - s.mean);
        if v < s.min {
            s.min = v;
        }
        if v > s.max {
            s.max = v;
        }
    }
    if s.count == 0 {
        s.min = f64::NAN;
        s.max = f64::NAN;
        s.mean = f64::NAN;
    }
    s
}

/// # Safety
/// `values` must reference `len` floats and `out` one writable `AqStats`.
#[no_mangle]
pub unsafe extern "C" fn aq_stats(values: *const f32, len: usize, out: *mut AqStats) -> i32 {
    if values.is_null() || out.is_null() {
        return ERR_NULL;
    }
    *out = summarize(std::slice::from_raw_parts(values, len));
    0
}

#[cfg(test)]
mod tests {
    #[test]
    fn skips_nan() {
        let s = super::summarize(&[1.0, f32::NAN, 3.0]);
        assert_eq!(s.count, 2);
        assert!((s.mean - 2.0).abs() < 1e-12);
    }
}
