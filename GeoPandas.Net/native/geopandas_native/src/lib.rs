//! GeoPandas.Net native kernels (Rust).
//!
//! Exposes a small C ABI consumed by `GeoPandas.Net.Geometry.Native.NativeKernels` via P/Invoke.
//! All functions are data-parallel over input arrays using Rayon.
//!
//! Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

use rayon::prelude::*;
use std::slice;

pub mod lazcodec;

const PAR_THRESHOLD: usize = 16_384;

/// ABI version; the managed side checks it is > 0.
#[no_mangle]
pub extern "C" fn gpn_version() -> i32 {
    2
}

#[inline(always)]
fn haversine(lon1: f64, lat1: f64, lon2: f64, lat2: f64, r: f64) -> f64 {
    let d = std::f64::consts::PI / 180.0;
    let (p1, p2) = (lat1 * d, lat2 * d);
    let dp = p2 - p1;
    let dl = (lon2 - lon1) * d;
    let a = (dp / 2.0).sin().powi(2) + p1.cos() * p2.cos() * (dl / 2.0).sin().powi(2);
    2.0 * r * a.sqrt().min(1.0).asin()
}

/// # Safety
/// All pointers must reference `n` valid f64 values; `out` must be writable.
#[no_mangle]
pub unsafe extern "C" fn gpn_haversine(
    lon1: *const f64, lat1: *const f64, lon2: *const f64, lat2: *const f64,
    out: *mut f64, n: usize, radius: f64,
) {
    if n == 0 { return; }
    let (a, b, c, d) = (
        slice::from_raw_parts(lon1, n), slice::from_raw_parts(lat1, n),
        slice::from_raw_parts(lon2, n), slice::from_raw_parts(lat2, n),
    );
    let o = slice::from_raw_parts_mut(out, n);
    if n < PAR_THRESHOLD {
        for i in 0..n { o[i] = haversine(a[i], b[i], c[i], d[i], radius); }
    } else {
        o.par_iter_mut().enumerate().for_each(|(i, v)| *v = haversine(a[i], b[i], c[i], d[i], radius));
    }
}

#[inline(always)]
fn inside_ring(x: f64, y: f64, rx: &[f64], ry: &[f64]) -> bool {
    let m = rx.len();
    let mut inside = false;
    let mut j = m - 1;
    for i in 0..m {
        let (yi, yj) = (ry[i], ry[j]);
        if (yi > y) != (yj > y) {
            let (xi, xj) = (rx[i], rx[j]);
            if x < (xj - xi) * (y - yi) / (yj - yi) + xi { inside = !inside; }
        }
        j = i;
    }
    inside
}

/// # Safety
/// `xs`/`ys` hold `n` values, `rx`/`ry` hold `m` ring vertices, `out` holds `n` bytes.
#[no_mangle]
pub unsafe extern "C" fn gpn_point_in_polygon(
    xs: *const f64, ys: *const f64, n: usize,
    rx: *const f64, ry: *const f64, m: usize, out: *mut u8,
) {
    if n == 0 || m < 3 { return; }
    let (x, y) = (slice::from_raw_parts(xs, n), slice::from_raw_parts(ys, n));
    let (rxs, rys) = (slice::from_raw_parts(rx, m), slice::from_raw_parts(ry, m));
    let o = slice::from_raw_parts_mut(out, n);
    // Ring bounding box for a cheap pre-filter.
    let (mut minx, mut miny, mut maxx, mut maxy) = (f64::MAX, f64::MAX, f64::MIN, f64::MIN);
    for i in 0..m {
        minx = minx.min(rxs[i]); maxx = maxx.max(rxs[i]);
        miny = miny.min(rys[i]); maxy = maxy.max(rys[i]);
    }
    let f = |i: usize| -> u8 {
        let (px, py) = (x[i], y[i]);
        if px < minx || px > maxx || py < miny || py > maxy { 0 } else { inside_ring(px, py, rxs, rys) as u8 }
    };
    if n < PAR_THRESHOLD / 8 {
        for i in 0..n { o[i] = f(i); }
    } else {
        o.par_iter_mut().enumerate().for_each(|(i, v)| *v = f(i));
    }
}

/// # Safety
/// `xs`/`ys` hold `n` values, `out` holds `n` bytes.
#[no_mangle]
pub unsafe extern "C" fn gpn_bbox_filter(
    xs: *const f64, ys: *const f64, n: usize,
    minx: f64, miny: f64, maxx: f64, maxy: f64, out: *mut u8,
) {
    if n == 0 { return; }
    let (x, y) = (slice::from_raw_parts(xs, n), slice::from_raw_parts(ys, n));
    let o = slice::from_raw_parts_mut(out, n);
    let f = |i: usize| (x[i] >= minx && x[i] <= maxx && y[i] >= miny && y[i] <= maxy) as u8;
    if n < PAR_THRESHOLD * 4 {
        for i in 0..n { o[i] = f(i); }
    } else {
        o.par_iter_mut().enumerate().for_each(|(i, v)| *v = f(i));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn haversine_jakarta_bandung() {
        let d = haversine(106.8456, -6.2088, 107.6191, -6.9175, 6371008.8);
        assert!((d - 116_000.0).abs() < 3_000.0, "got {d}");
    }

    #[test]
    fn pip_square() {
        let rx = [0.0, 10.0, 10.0, 0.0, 0.0];
        let ry = [0.0, 0.0, 10.0, 10.0, 0.0];
        assert!(inside_ring(5.0, 5.0, &rx, &ry));
        assert!(!inside_ring(15.0, 5.0, &rx, &ry));
    }
}
