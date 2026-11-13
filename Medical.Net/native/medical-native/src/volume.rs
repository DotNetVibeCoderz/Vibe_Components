//! Volume resampling: multiplanar reformation (MPR) and slab projections (MIP / MinIP / AvgIP).
//!
//! One kernel covers every case. Output pixel (i, j) samples the volume at
//! `origin + i * u + j * v + k * n` for `k` in `0..steps` (voxel coordinates, x fastest),
//! with trilinear interpolation, and reduces the samples with the slab mode.
//! A single step is a plain (oblique) MPR. The managed twin in `MedicalKernels.Slab`
//! performs the same f64 operations in the same order, so both backends are bit-identical.

use crate::{ERR_ARG, ERR_NULL, OK};

/// Slab reduction. `First` and `Mean` with one step are both a plain MPR.
pub const MODE_MEAN: i32 = 0;
pub const MODE_MAX: i32 = 1;
pub const MODE_MIN: i32 = 2;
/// Border tolerance in voxels.
pub const EPS: f64 = 1e-6;

#[derive(Clone, Copy)]
pub struct Dims {
    pub nx: usize,
    pub ny: usize,
    pub nz: usize,
}

/// Trilinear sample at voxel coordinates, or `None` outside `[0, n-1]` on any axis.
#[inline(always)]
pub fn sample(vol: &[i16], d: Dims, x: f64, y: f64, z: f64) -> Option<f64> {
    // A tolerance absorbs round-off at the volume border (e.g. 127.00000000000001 on a 128-voxel axis).
    let (mx, my, mz) = ((d.nx - 1) as f64, (d.ny - 1) as f64, (d.nz - 1) as f64);
    if !(x >= -EPS && y >= -EPS && z >= -EPS && x <= mx + EPS && y <= my + EPS && z <= mz + EPS) {
        return None;
    }
    let x = x.max(0.0).min(mx);
    let y = y.max(0.0).min(my);
    let z = z.max(0.0).min(mz);
    let x0 = x.floor() as usize;
    let y0 = y.floor() as usize;
    let z0 = z.floor() as usize;
    let x1 = if x0 + 1 < d.nx { x0 + 1 } else { x0 };
    let y1 = if y0 + 1 < d.ny { y0 + 1 } else { y0 };
    let z1 = if z0 + 1 < d.nz { z0 + 1 } else { z0 };
    let fx = x - x0 as f64;
    let fy = y - y0 as f64;
    let fz = z - z0 as f64;
    let plane = d.nx * d.ny;
    let at = |xi: usize, yi: usize, zi: usize| vol[zi * plane + yi * d.nx + xi] as f64;
    let c00 = at(x0, y0, z0) * (1.0 - fx) + at(x1, y0, z0) * fx;
    let c10 = at(x0, y1, z0) * (1.0 - fx) + at(x1, y1, z0) * fx;
    let c01 = at(x0, y0, z1) * (1.0 - fx) + at(x1, y0, z1) * fx;
    let c11 = at(x0, y1, z1) * (1.0 - fx) + at(x1, y1, z1) * fx;
    let c0 = c00 * (1.0 - fy) + c10 * fy;
    let c1 = c01 * (1.0 - fy) + c11 * fy;
    Some(c0 * (1.0 - fz) + c1 * fz)
}

/// Rounds half away from zero and clamps to the i16 range.
#[inline(always)]
pub fn to_i16(v: f64) -> i16 {
    let r = v.round();
    if r < i16::MIN as f64 {
        i16::MIN
    } else if r > i16::MAX as f64 {
        i16::MAX
    } else {
        r as i16
    }
}

#[allow(clippy::too_many_arguments)]
pub fn slab(vol: &[i16], d: Dims, origin: [f64; 3], u: [f64; 3], v: [f64; 3], n: [f64; 3], steps: usize, mode: i32, fill: i16, out: &mut [i16], out_w: usize, out_h: usize) {
    for j in 0..out_h {
        for i in 0..out_w {
            let (fi, fj) = (i as f64, j as f64);
            let bx = origin[0] + fi * u[0] + fj * v[0];
            let by = origin[1] + fi * u[1] + fj * v[1];
            let bz = origin[2] + fi * u[2] + fj * v[2];
            let mut acc = 0.0f64;
            let mut count = 0usize;
            for k in 0..steps {
                let fk = k as f64;
                if let Some(s) = sample(vol, d, bx + fk * n[0], by + fk * n[1], bz + fk * n[2]) {
                    acc = if count == 0 {
                        s
                    } else {
                        match mode {
                            MODE_MAX => {
                                if s > acc {
                                    s
                                } else {
                                    acc
                                }
                            }
                            MODE_MIN => {
                                if s < acc {
                                    s
                                } else {
                                    acc
                                }
                            }
                            _ => acc + s,
                        }
                    };
                    count += 1;
                }
            }
            out[j * out_w + i] = if count == 0 {
                fill
            } else if mode == MODE_MEAN {
                to_i16(acc / count as f64)
            } else {
                to_i16(acc)
            };
        }
    }
}

/// Oblique slab resampling of a signed 16-bit volume (`nx * ny * nz`, x fastest).
/// `origin`, `u`, `v`, `n` are 3-element f64 arrays in voxel coordinates.
/// `mode`: 0 = mean (AvgIP, or MPR with one step), 1 = maximum (MIP), 2 = minimum (MinIP).
#[no_mangle]
pub unsafe extern "C" fn mdn_slab_i16(
    vol: *const i16,
    nx: usize,
    ny: usize,
    nz: usize,
    origin: *const f64,
    u: *const f64,
    v: *const f64,
    n: *const f64,
    steps: u32,
    mode: i32,
    fill: i16,
    out: *mut i16,
    out_w: usize,
    out_h: usize,
) -> i32 {
    if vol.is_null() || origin.is_null() || u.is_null() || v.is_null() || n.is_null() || out.is_null() {
        return ERR_NULL;
    }
    if nx == 0 || ny == 0 || nz == 0 || steps == 0 || !(MODE_MEAN..=MODE_MIN).contains(&mode) {
        return ERR_ARG;
    }
    let rd = |p: *const f64| [*p, *p.add(1), *p.add(2)];
    let d = Dims { nx, ny, nz };
    let volume = std::slice::from_raw_parts(vol, nx * ny * nz);
    let output = std::slice::from_raw_parts_mut(out, out_w * out_h);
    slab(volume, d, rd(origin), rd(u), rd(v), rd(n), steps as usize, mode, fill, output, out_w, out_h);
    OK
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ramp() -> (Vec<i16>, Dims) {
        // value = x + 10 y + 100 z on a 4 x 3 x 2 grid
        let d = Dims { nx: 4, ny: 3, nz: 2 };
        let mut v = Vec::new();
        for z in 0..2 {
            for y in 0..3 {
                for x in 0..4 {
                    v.push((x + 10 * y + 100 * z) as i16);
                }
            }
        }
        (v, d)
    }

    #[test]
    fn trilinear_is_exact_on_linear_field() {
        let (v, d) = ramp();
        assert_eq!(sample(&v, d, 1.5, 0.5, 0.5), Some(1.5 + 5.0 + 50.0));
        assert_eq!(sample(&v, d, 3.0, 2.0, 1.0), Some(123.0));
        assert_eq!(sample(&v, d, 3.01, 0.0, 0.0), None);
        assert_eq!(sample(&v, d, 3.0 + 1e-9, 2.0, 1.0), Some(123.0));
    }

    #[test]
    fn axial_mpr_and_projections() {
        let (v, d) = ramp();
        let mut out = vec![0i16; 12];
        // z = 1 axial plane
        slab(&v, d, [0.0, 0.0, 1.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0], 1, MODE_MEAN, -1, &mut out, 4, 3);
        assert_eq!(out[0], 100);
        assert_eq!(out[11], 123);
        // MIP / MinIP / mean through z
        slab(&v, d, [0.0; 3], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0], 2, MODE_MAX, -1, &mut out, 4, 3);
        assert_eq!(out[5], 111);
        slab(&v, d, [0.0; 3], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0], 2, MODE_MIN, -1, &mut out, 4, 3);
        assert_eq!(out[5], 11);
        slab(&v, d, [0.0; 3], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0], 2, MODE_MEAN, -1, &mut out, 4, 3);
        assert_eq!(out[5], 61);
    }

    #[test]
    fn outside_samples_use_fill() {
        let (v, d) = ramp();
        let mut out = vec![0i16; 2];
        slab(&v, d, [-5.0, 0.0, 0.0], [10.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0], 1, MODE_MAX, -1024, &mut out, 2, 1);
        assert_eq!(out, vec![-1024, -1024]);
    }

    #[test]
    fn rounding_half_away_from_zero() {
        assert_eq!(to_i16(2.5), 3);
        assert_eq!(to_i16(-2.5), -3);
        assert_eq!(to_i16(1e9), i16::MAX);
    }
}
