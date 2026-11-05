//! Spectral vegetation / water indices over reflectance bands (f32, 0..1).
//! Pixels where the denominator is zero produce NaN (treated as no-data downstream).

use crate::{par_chunks, AGT_ERR_NULL, AGT_OK};
use std::slice;

#[inline(always)]
fn nd(a: f32, b: f32) -> f32 {
    let d = a + b;
    if d == 0.0 { f32::NAN } else { (a - b) / d }
}

/// Safe Rust API: normalized difference (a - b) / (a + b).
pub fn normalized_difference(a: &[f32], b: &[f32], out: &mut [f32]) {
    let n = out.len().min(a.len()).min(b.len());
    par_chunks(&mut out[..n], |start, o| {
        let a = &a[start..start + o.len()];
        let b = &b[start..start + o.len()];
        for i in 0..o.len() {
            o[i] = nd(a[i], b[i]);
        }
    });
}

pub fn savi(nir: &[f32], red: &[f32], out: &mut [f32], l: f32) {
    let n = out.len().min(nir.len()).min(red.len());
    par_chunks(&mut out[..n], |start, o| {
        for i in 0..o.len() {
            let (n_, r) = (nir[start + i], red[start + i]);
            let d = n_ + r + l;
            o[i] = if d == 0.0 { f32::NAN } else { (1.0 + l) * (n_ - r) / d };
        }
    });
}

pub fn evi(nir: &[f32], red: &[f32], blue: &[f32], out: &mut [f32], g: f32, c1: f32, c2: f32, l: f32) {
    let n = out.len().min(nir.len()).min(red.len()).min(blue.len());
    par_chunks(&mut out[..n], |start, o| {
        for i in 0..o.len() {
            let j = start + i;
            let d = nir[j] + c1 * red[j] - c2 * blue[j] + l;
            o[i] = if d == 0.0 { f32::NAN } else { g * (nir[j] - red[j]) / d };
        }
    });
}

macro_rules! check {
    ($($p:expr),*) => { $( if $p.is_null() { return AGT_ERR_NULL; } )* };
}

/// # Safety
/// `a`, `b` and `out` must point to at least `len` valid f32 values.
#[no_mangle]
pub unsafe extern "C" fn agt_normalized_difference(a: *const f32, b: *const f32, out: *mut f32, len: usize) -> i32 {
    check!(a, b, out);
    normalized_difference(slice::from_raw_parts(a, len), slice::from_raw_parts(b, len), slice::from_raw_parts_mut(out, len));
    AGT_OK
}

/// # Safety
/// See [`agt_normalized_difference`].
#[no_mangle]
pub unsafe extern "C" fn agt_savi(nir: *const f32, red: *const f32, out: *mut f32, len: usize, l: f32) -> i32 {
    check!(nir, red, out);
    savi(slice::from_raw_parts(nir, len), slice::from_raw_parts(red, len), slice::from_raw_parts_mut(out, len), l);
    AGT_OK
}

/// # Safety
/// See [`agt_normalized_difference`].
#[no_mangle]
pub unsafe extern "C" fn agt_evi(
    nir: *const f32, red: *const f32, blue: *const f32, out: *mut f32, len: usize,
    g: f32, c1: f32, c2: f32, l: f32,
) -> i32 {
    check!(nir, red, blue, out);
    evi(
        slice::from_raw_parts(nir, len), slice::from_raw_parts(red, len), slice::from_raw_parts(blue, len),
        slice::from_raw_parts_mut(out, len), g, c1, c2, l,
    );
    AGT_OK
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ndvi_basic() {
        let nir = [0.5f32, 0.4, 0.0];
        let red = [0.1f32, 0.4, 0.0];
        let mut out = [0f32; 3];
        normalized_difference(&nir, &red, &mut out);
        assert!((out[0] - 0.6666667).abs() < 1e-6);
        assert_eq!(out[1], 0.0);
        assert!(out[2].is_nan());
    }

    #[test]
    fn parallel_matches_serial() {
        let n = 300_000;
        let a: Vec<f32> = (0..n).map(|i| (i % 100) as f32 / 100.0 + 0.01).collect();
        let b: Vec<f32> = (0..n).map(|i| (i % 37) as f32 / 100.0).collect();
        let mut out = vec![0f32; n];
        normalized_difference(&a, &b, &mut out);
        for i in (0..n).step_by(9973) {
            assert!((out[i] - nd(a[i], b[i])).abs() < 1e-7);
        }
    }

    #[test]
    fn evi_known() {
        let mut out = [0f32];
        evi(&[0.5], &[0.1], &[0.05], &mut out, 2.5, 6.0, 7.5, 1.0);
        // 2.5*0.4/(0.5+0.6-0.375+1) = 1.0/1.725
        assert!((out[0] - 0.57971014).abs() < 1e-5);
    }
}
