//! osgnet_fast — native hot paths for OpenSceneGraph.Net.
//!
//! Every function has a managed twin in `OpenSceneGraph.Net.Interop.FastPath` callers, so this
//! library is optional. All pointers are caller-owned; lengths are element counts.
//! Vec3 is `[f32; 3]` (matches System.Numerics.Vector3), Vec4 is `[f32; 4]`.

use rayon::prelude::*;
use std::slice;

type V3 = [f32; 3];
type V4 = [f32; 4];


#[inline(always)]
fn sub(a: V3, b: V3) -> V3 { [a[0] - b[0], a[1] - b[1], a[2] - b[2]] }
#[inline(always)]
fn cross(a: V3, b: V3) -> V3 { [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]] }
#[inline(always)]
fn normalize_or_z(v: V3) -> V3 {
    let l = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if l > 1e-20 { [v[0] / l, v[1] / l, v[2] / l] } else { [0.0, 0.0, 1.0] }
}

/// ABI version checked by the managed side. Bump on any signature change.
#[no_mangle]
pub extern "C" fn osgnet_fast_abi_version() -> u32 { 1 }

/// Area-weighted smooth normals for an indexed triangle list. Returns 0 on success.
///
/// # Safety
/// `positions`/`normals_out` must hold `vertex_count` Vec3s; `indices` must hold `index_count` u32s.
#[no_mangle]
pub unsafe extern "C" fn osgnet_fast_compute_normals(
    positions: *const V3, vertex_count: usize, indices: *const u32, index_count: usize, normals_out: *mut V3,
) -> i32 {
    if positions.is_null() || indices.is_null() || normals_out.is_null() { return -1; }
    let p = slice::from_raw_parts(positions, vertex_count);
    let idx = slice::from_raw_parts(indices, index_count);
    let out = slice::from_raw_parts_mut(normals_out, vertex_count);

    // Face normals in parallel, then a serial scatter (scatter is memory-bound and race-free this way).
    let faces: Vec<(u32, u32, u32, V3)> = idx
        .par_chunks_exact(3)
        .filter_map(|t| {
            let (a, b, c) = (t[0], t[1], t[2]);
            if a as usize >= vertex_count || b as usize >= vertex_count || c as usize >= vertex_count { return None; }
            let pa = p[a as usize];
            Some((a, b, c, cross(sub(p[b as usize], pa), sub(p[c as usize], pa))))
        })
        .collect();

    out.iter_mut().for_each(|n| *n = [0.0; 3]);
    for (a, b, c, f) in faces {
        for v in [a, b, c] {
            let n = &mut out[v as usize];
            n[0] += f[0]; n[1] += f[1]; n[2] += f[2];
        }
    }
    out.par_iter_mut().for_each(|n| *n = normalize_or_z(*n));
    0
}

/// Builds grid positions (row-major, centred on the origin, Z = height) and normals for a heightfield.
///
/// # Safety
/// `heights` holds `cols*rows` floats; outputs hold `cols*rows` Vec3s.
#[no_mangle]
pub unsafe extern "C" fn osgnet_fast_heightfield_mesh(
    heights: *const f32, cols: u32, rows: u32, dx: f32, dy: f32, positions_out: *mut V3, normals_out: *mut V3,
) -> i32 {
    if heights.is_null() || positions_out.is_null() || normals_out.is_null() || cols < 2 || rows < 2 { return -1; }
    let (c, r) = (cols as usize, rows as usize);
    let h = slice::from_raw_parts(heights, c * r);
    let pos = slice::from_raw_parts_mut(positions_out, c * r);
    let nor = slice::from_raw_parts_mut(normals_out, c * r);
    let ox = -(c as f32 - 1.0) * dx * 0.5;
    let oy = -(r as f32 - 1.0) * dy * 0.5;

    pos.par_chunks_mut(c).zip(nor.par_chunks_mut(c)).enumerate().for_each(|(y, (prow, nrow))| {
        for x in 0..c {
            let at = |xx: usize, yy: usize| h[yy * c + xx];
            prow[x] = [ox + x as f32 * dx, oy + y as f32 * dy, at(x, y)];
            let hl = at(x.saturating_sub(1), y);
            let hr = at((x + 1).min(c - 1), y);
            let hd = at(x, y.saturating_sub(1));
            let hu = at(x, (y + 1).min(r - 1));
            let sx = ((x + 1).min(c - 1) - x.saturating_sub(1)) as f32 * dx;
            let sy = ((y + 1).min(r - 1) - y.saturating_sub(1)) as f32 * dy;
            nrow[x] = normalize_or_z([-(hr - hl) / sx, -(hu - hd) / sy, 1.0]);
        }
    });
    0
}

/// Tests spheres (xyz = centre, w = radius) against 6 planes (xyz = normal, w = d). Writes 1/0 per sphere,
/// returns the number visible.
///
/// # Safety
/// `planes` holds 6 Vec4s; `spheres` and `visible_out` hold `count` elements.
#[no_mangle]
pub unsafe extern "C" fn osgnet_fast_cull_spheres(planes: *const V4, spheres: *const V4, count: usize, visible_out: *mut u8) -> usize {
    if planes.is_null() || spheres.is_null() || visible_out.is_null() { return 0; }
    let pl = slice::from_raw_parts(planes, 6);
    let s = slice::from_raw_parts(spheres, count);
    let v = slice::from_raw_parts_mut(visible_out, count);
    v.par_iter_mut()
        .zip(s.par_iter())
        .map(|(out, sp)| {
            let inside = sp[3] >= 0.0
                && pl.iter().all(|p| p[0] * sp[0] + p[1] * sp[1] + p[2] * sp[2] + p[3] >= -sp[3]);
            *out = inside as u8;
            inside as usize
        })
        .sum()
}

/// Semi-implicit Euler step for particles: v += g*dt; v *= (1 - drag*dt); p += v*dt; age += dt.
///
/// # Safety
/// All arrays hold `count` elements.
#[no_mangle]
pub unsafe extern "C" fn osgnet_fast_particles_step(
    pos: *mut V3, vel: *mut V3, age: *mut f32, count: usize, gx: f32, gy: f32, gz: f32, drag: f32, dt: f32,
) {
    if pos.is_null() || vel.is_null() || age.is_null() { return; }
    let p = slice::from_raw_parts_mut(pos, count);
    let v = slice::from_raw_parts_mut(vel, count);
    let a = slice::from_raw_parts_mut(age, count);
    let damp = (1.0 - drag * dt).max(0.0);
    let g = [gx, gy, gz];
    p.par_iter_mut().zip(v.par_iter_mut()).zip(a.par_iter_mut()).for_each(|((p, v), a)| {
        for k in 0..3 {
            v[k] = (v[k] + g[k] * dt) * damp;
            p[k] += v[k] * dt;
        }
        *a += dt;
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flat_triangle_normal_is_up() {
        let p = [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]];
        let i = [0u32, 1, 2];
        let mut n = [[0.0f32; 3]; 3];
        unsafe { assert_eq!(osgnet_fast_compute_normals(p.as_ptr(), 3, i.as_ptr(), 3, n.as_mut_ptr()), 0) };
        for v in n { assert!((v[2] - 1.0).abs() < 1e-6); }
    }

    #[test]
    fn cull_counts_inside() {
        // a box [-1,1]^3 expressed as 6 planes
        let pl = [[1.0, 0.0, 0.0, 1.0], [-1.0, 0.0, 0.0, 1.0], [0.0, 1.0, 0.0, 1.0], [0.0, -1.0, 0.0, 1.0], [0.0, 0.0, 1.0, 1.0], [0.0, 0.0, -1.0, 1.0]];
        let s = [[0.0, 0.0, 0.0, 0.5], [5.0, 0.0, 0.0, 0.5], [1.2, 0.0, 0.0, 0.5]];
        let mut v = [0u8; 3];
        let n = unsafe { osgnet_fast_cull_spheres(pl.as_ptr(), s.as_ptr(), 3, v.as_mut_ptr()) };
        assert_eq!(n, 2);
        assert_eq!(v, [1, 0, 1]);
    }
}
