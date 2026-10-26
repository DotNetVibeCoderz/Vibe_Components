//! GDAL.Net raster kernels — data-parallel (rayon) and auto-vectorized hot loops used by the
//! managed library for statistics, histograms, spectral indices and color mapping.
//! All entry points are `extern "C"`, never panic across the FFI boundary and return 0 on success.
//!
//! Created by Gravicode Studios, led by Kang Fadhil.

use rayon::prelude::*;
use std::slice;

const CHUNK: usize = 1 << 16;

/// ABI version, bumped on any signature change.
#[no_mangle]
pub extern "C" fn gnk_version() -> u32 {
    1
}

/// Number of worker threads used by the kernels.
#[no_mangle]
pub extern "C" fn gnk_thread_count() -> u32 {
    rayon::current_num_threads() as u32
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct GnkStats {
    pub min: f64,
    pub max: f64,
    pub mean: f64,
    pub std_dev: f64,
    pub valid_count: u64,
}

#[derive(Clone, Copy)]
struct Acc {
    min: f64,
    max: f64,
    n: u64,
    mean: f64,
    m2: f64,
}

impl Acc {
    const EMPTY: Acc = Acc { min: f64::INFINITY, max: f64::NEG_INFINITY, n: 0, mean: 0.0, m2: 0.0 };

    // Chan et al. parallel variance merge.
    fn merge(a: Acc, b: Acc) -> Acc {
        if a.n == 0 {
            return b;
        }
        if b.n == 0 {
            return a;
        }
        let n = a.n + b.n;
        let delta = b.mean - a.mean;
        let mean = a.mean + delta * (b.n as f64) / (n as f64);
        let m2 = a.m2 + b.m2 + delta * delta * (a.n as f64) * (b.n as f64) / (n as f64);
        Acc { min: a.min.min(b.min), max: a.max.max(b.max), n, mean, m2 }
    }
}

#[inline(always)]
fn is_valid(v: f32, nodata: f32, has_nodata: bool) -> bool {
    !v.is_nan() && !(has_nodata && v == nodata)
}

fn chunk_acc(chunk: &[f32], nodata: f32, has_nodata: bool) -> Acc {
    let mut min = f64::INFINITY;
    let mut max = f64::NEG_INFINITY;
    let mut n = 0u64;
    let mut sum = 0.0f64;
    for &v in chunk {
        if is_valid(v, nodata, has_nodata) {
            let d = v as f64;
            min = min.min(d);
            max = max.max(d);
            sum += d;
            n += 1;
        }
    }
    if n == 0 {
        return Acc::EMPTY;
    }
    let mean = sum / n as f64;
    let mut m2 = 0.0f64;
    for &v in chunk {
        if is_valid(v, nodata, has_nodata) {
            let d = v as f64 - mean;
            m2 += d * d;
        }
    }
    Acc { min, max, n, mean, m2 }
}

/// Computes min/max/mean/population std-dev of `len` floats, skipping NaN and `nodata` (when `has_nodata != 0`).
#[no_mangle]
pub unsafe extern "C" fn gnk_stats_f32(data: *const f32, len: usize, nodata: f32, has_nodata: i32, out: *mut GnkStats) -> i32 {
    if data.is_null() || out.is_null() {
        return -1;
    }
    let values = slice::from_raw_parts(data, len);
    let hn = has_nodata != 0;
    let acc = values
        .par_chunks(CHUNK)
        .map(|c| chunk_acc(c, nodata, hn))
        .reduce(|| Acc::EMPTY, Acc::merge);
    *out = if acc.n == 0 {
        GnkStats { min: f64::NAN, max: f64::NAN, mean: f64::NAN, std_dev: f64::NAN, valid_count: 0 }
    } else {
        GnkStats { min: acc.min, max: acc.max, mean: acc.mean, std_dev: (acc.m2 / acc.n as f64).sqrt(), valid_count: acc.n }
    };
    0
}

/// Histogram of `buckets` equal bins over [min, max]; values outside the range are ignored.
#[no_mangle]
pub unsafe extern "C" fn gnk_histogram_f32(
    data: *const f32,
    len: usize,
    min: f64,
    max: f64,
    buckets: usize,
    nodata: f32,
    has_nodata: i32,
    out: *mut u64,
) -> i32 {
    if data.is_null() || out.is_null() || buckets == 0 || !(max > min) {
        return -1;
    }
    let values = slice::from_raw_parts(data, len);
    let hn = has_nodata != 0;
    let scale = buckets as f64 / (max - min);
    let hist = values
        .par_chunks(CHUNK)
        .fold(
            || vec![0u64; buckets],
            |mut h, chunk| {
                for &v in chunk {
                    if is_valid(v, nodata, hn) {
                        let d = v as f64;
                        if d >= min && d <= max {
                            let mut i = ((d - min) * scale) as usize;
                            if i >= buckets {
                                i = buckets - 1;
                            }
                            h[i] += 1;
                        }
                    }
                }
                h
            },
        )
        .reduce(
            || vec![0u64; buckets],
            |mut a, b| {
                for (x, y) in a.iter_mut().zip(b) {
                    *x += y;
                }
                a
            },
        );
    let dst = slice::from_raw_parts_mut(out, buckets);
    dst.copy_from_slice(&hist);
    0
}

/// Normalized difference index (a - b) / (a + b) — NDVI (NIR, Red), NDWI (Green, NIR), NDBI, NBR, ...
/// Invalid inputs or zero denominators produce `nodata_out`.
#[no_mangle]
pub unsafe extern "C" fn gnk_normalized_difference_f32(
    a: *const f32,
    b: *const f32,
    out: *mut f32,
    len: usize,
    nodata_in: f32,
    has_nodata: i32,
    nodata_out: f32,
) -> i32 {
    if a.is_null() || b.is_null() || out.is_null() {
        return -1;
    }
    let a = slice::from_raw_parts(a, len);
    let b = slice::from_raw_parts(b, len);
    let o = slice::from_raw_parts_mut(out, len);
    let hn = has_nodata != 0;
    o.par_chunks_mut(CHUNK)
        .zip(a.par_chunks(CHUNK).zip(b.par_chunks(CHUNK)))
        .for_each(|(oc, (ac, bc))| {
            for i in 0..oc.len() {
                let x = ac[i];
                let y = bc[i];
                let s = x + y;
                oc[i] = if is_valid(x, nodata_in, hn) && is_valid(y, nodata_in, hn) && s != 0.0 {
                    (x - y) / s
                } else {
                    nodata_out
                };
            }
        });
    0
}

/// Maps floats to RGBA through a 256-entry lookup table (`lut` = 256 * 4 bytes) with a linear stretch
/// between `min` and `max`. Invalid pixels become fully transparent.
#[no_mangle]
pub unsafe extern "C" fn gnk_colorize_f32(
    data: *const f32,
    len: usize,
    min: f32,
    max: f32,
    lut: *const u8,
    nodata: f32,
    has_nodata: i32,
    out_rgba: *mut u8,
) -> i32 {
    if data.is_null() || lut.is_null() || out_rgba.is_null() {
        return -1;
    }
    let values = slice::from_raw_parts(data, len);
    let lut = slice::from_raw_parts(lut as *const [u8; 4], 256);
    let out = slice::from_raw_parts_mut(out_rgba as *mut [u8; 4], len);
    let hn = has_nodata != 0;
    let range = if max > min { max - min } else { 1.0 };
    let k = 255.0 / range;
    out.par_chunks_mut(CHUNK)
        .zip(values.par_chunks(CHUNK))
        .for_each(|(oc, vc)| {
            for i in 0..oc.len() {
                let v = vc[i];
                oc[i] = if is_valid(v, nodata, hn) {
                    let t = ((v - min) * k).clamp(0.0, 255.0) as usize;
                    lut[t]
                } else {
                    [0, 0, 0, 0]
                };
            }
        });
    0
}

/// Linear stretch of 3 float bands into RGBA (true/false color composites).
/// `mins`/`maxs` hold one range per channel.
#[no_mangle]
pub unsafe extern "C" fn gnk_composite_rgb_f32(
    r: *const f32,
    g: *const f32,
    b: *const f32,
    len: usize,
    mins: *const f32,
    maxs: *const f32,
    nodata: f32,
    has_nodata: i32,
    out_rgba: *mut u8,
) -> i32 {
    if r.is_null() || g.is_null() || b.is_null() || mins.is_null() || maxs.is_null() || out_rgba.is_null() {
        return -1;
    }
    let ch = [slice::from_raw_parts(r, len), slice::from_raw_parts(g, len), slice::from_raw_parts(b, len)];
    let mins = slice::from_raw_parts(mins, 3);
    let maxs = slice::from_raw_parts(maxs, 3);
    let k: [f32; 3] = core::array::from_fn(|i| 255.0 / if maxs[i] > mins[i] { maxs[i] - mins[i] } else { 1.0 });
    let out = slice::from_raw_parts_mut(out_rgba as *mut [u8; 4], len);
    let hn = has_nodata != 0;
    out.par_chunks_mut(CHUNK).enumerate().for_each(|(ci, oc)| {
        let base = ci * CHUNK;
        for i in 0..oc.len() {
            let j = base + i;
            let (vr, vg, vb) = (ch[0][j], ch[1][j], ch[2][j]);
            if !is_valid(vr, nodata, hn) || !is_valid(vg, nodata, hn) || !is_valid(vb, nodata, hn) {
                oc[i] = [0, 0, 0, 0];
                continue;
            }
            oc[i] = [
                ((vr - mins[0]) * k[0]).clamp(0.0, 255.0) as u8,
                ((vg - mins[1]) * k[1]).clamp(0.0, 255.0) as u8,
                ((vb - mins[2]) * k[2]).clamp(0.0, 255.0) as u8,
                255,
            ];
        }
    });
    0
}

/// Multiplies an RGBA image by a hillshade intensity (0..255) — terrain shading over a color map.
#[no_mangle]
pub unsafe extern "C" fn gnk_blend_shade(rgba: *mut u8, shade: *const u8, len: usize, strength: f32) -> i32 {
    if rgba.is_null() || shade.is_null() {
        return -1;
    }
    let px = slice::from_raw_parts_mut(rgba as *mut [u8; 4], len);
    let sh = slice::from_raw_parts(shade, len);
    let s = strength.clamp(0.0, 1.0);
    px.par_chunks_mut(CHUNK).zip(sh.par_chunks(CHUNK)).for_each(|(pc, sc)| {
        for i in 0..pc.len() {
            let f = 1.0 - s + s * (sc[i] as f32 / 255.0);
            let p = &mut pc[i];
            p[0] = (p[0] as f32 * f) as u8;
            p[1] = (p[1] as f32 * f) as u8;
            p[2] = (p[2] as f32 * f) as u8;
        }
    });
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stats_match_naive() {
        let v: Vec<f32> = (0..200_000).map(|i| (i % 1000) as f32).collect();
        let mut s = GnkStats { min: 0.0, max: 0.0, mean: 0.0, std_dev: 0.0, valid_count: 0 };
        unsafe { gnk_stats_f32(v.as_ptr(), v.len(), -1.0, 1, &mut s) };
        assert_eq!(s.min, 0.0);
        assert_eq!(s.max, 999.0);
        assert!((s.mean - 499.5).abs() < 1e-9);
        assert_eq!(s.valid_count, 200_000);
    }

    #[test]
    fn ndi() {
        let a = [0.8f32, 0.5, -9999.0];
        let b = [0.2f32, 0.5, 0.1];
        let mut o = [0f32; 3];
        unsafe { gnk_normalized_difference_f32(a.as_ptr(), b.as_ptr(), o.as_mut_ptr(), 3, -9999.0, 1, -2.0) };
        assert!((o[0] - 0.6).abs() < 1e-6);
        assert_eq!(o[1], 0.0);
        assert_eq!(o[2], -2.0);
    }

    #[test]
    fn histogram_counts_all() {
        let v: Vec<f32> = (0..1000).map(|i| i as f32).collect();
        let mut h = vec![0u64; 10];
        unsafe { gnk_histogram_f32(v.as_ptr(), v.len(), 0.0, 999.0, 10, 0.0, 0, h.as_mut_ptr()) };
        assert_eq!(h.iter().sum::<u64>(), 1000);
    }
}
