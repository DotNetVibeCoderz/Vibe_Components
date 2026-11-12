//! Explicit finite-volume advection–diffusion–reaction step on a regular 2-D grid.
//! dc/dt = −u·∂c/∂x − v·∂c/∂y + D·∇²c − k·c + s, first-order upwind advection, zero-flux at dry cells and borders.

use crate::{ERR_ARG, ERR_NULL};

#[inline]
fn neighbour(c: &[f32], wet: &[u8], nx: usize, ny: usize, i: isize, j: isize, own: f32) -> f32 {
    if i < 0 || j < 0 || i >= nx as isize || j >= ny as isize {
        return own;
    }
    let k = j as usize * nx + i as usize;
    if wet[k] == 0 {
        own
    } else {
        c[k]
    }
}

#[allow(clippy::too_many_arguments)]
pub fn step(
    c: &[f32],
    out: &mut [f32],
    u: &[f32],
    v: &[f32],
    wet: &[u8],
    source: &[f32],
    nx: usize,
    ny: usize,
    dx: f32,
    dt: f32,
    diff: f32,
    decay: f32,
) {
    let inv_dx = 1.0 / dx;
    let inv_dx2 = inv_dx * inv_dx;
    for j in 0..ny {
        for i in 0..nx {
            let k = j * nx + i;
            if wet[k] == 0 {
                out[k] = c[k];
                continue;
            }
            let ck = c[k];
            let (ii, jj) = (i as isize, j as isize);
            let cw = neighbour(c, wet, nx, ny, ii - 1, jj, ck);
            let ce = neighbour(c, wet, nx, ny, ii + 1, jj, ck);
            let cs = neighbour(c, wet, nx, ny, ii, jj - 1, ck);
            let cn = neighbour(c, wet, nx, ny, ii, jj + 1, ck);
            let uk = u[k];
            let vk = v[k];
            let dcdx = (if uk > 0.0 { ck - cw } else { ce - ck }) * inv_dx;
            let dcdy = (if vk > 0.0 { ck - cs } else { cn - ck }) * inv_dx;
            let lap = (cw + ce + cs + cn - 4.0 * ck) * inv_dx2;
            let rate = -uk * dcdx - vk * dcdy + diff * lap - decay * ck + source[k];
            let next = ck + dt * rate;
            out[k] = if next < 0.0 { 0.0 } else { next };
        }
    }
}

/// # Safety
/// Every pointer must reference `nx * ny` valid elements; `out` must not alias `c`.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn aq_advect_diffuse_step(
    c: *const f32,
    out: *mut f32,
    u: *const f32,
    v: *const f32,
    wet: *const u8,
    source: *const f32,
    nx: usize,
    ny: usize,
    dx: f32,
    dt: f32,
    diff: f32,
    decay: f32,
) -> i32 {
    if c.is_null() || out.is_null() || u.is_null() || v.is_null() || wet.is_null() || source.is_null() {
        return ERR_NULL;
    }
    if nx == 0 || ny == 0 || !(dx > 0.0) || !(dt > 0.0) {
        return ERR_ARG;
    }
    let n = nx * ny;
    step(
        std::slice::from_raw_parts(c, n),
        std::slice::from_raw_parts_mut(out, n),
        std::slice::from_raw_parts(u, n),
        std::slice::from_raw_parts(v, n),
        std::slice::from_raw_parts(wet, n),
        std::slice::from_raw_parts(source, n),
        nx,
        ny,
        dx,
        dt,
        diff,
        decay,
    );
    0
}

#[cfg(test)]
mod tests {
    #[test]
    fn diffusion_conserves_mass_without_decay() {
        let (nx, ny) = (10, 10);
        let mut c = vec![0f32; 100];
        c[55] = 100.0;
        let mut out = vec![0f32; 100];
        let zero = vec![0f32; 100];
        let wet = vec![1u8; 100];
        for _ in 0..50 {
            super::step(&c, &mut out, &zero, &zero, &wet, &zero, nx, ny, 1.0, 0.1, 1.0, 0.0);
            std::mem::swap(&mut c, &mut out);
        }
        let total: f32 = c.iter().sum();
        assert!((total - 100.0).abs() < 1e-3, "{total}");
    }
}
