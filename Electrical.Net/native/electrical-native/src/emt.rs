//! Electromagnetic-transients (EMT) hot-loop kernels.
//!
//! * `eln_emt_branch_update` — history-current recurrence of many lumped series R-L-C branches
//!   (trapezoidal or backward-Euler companion models) and accumulation of the history sources into the
//!   nodal right-hand side.
//! * `eln_emt_lu_solve` — forward/backward substitution with a pre-factored sparse LU (P·G·Pᵀ = L·U) in CSR form.
//!
//! Both kernels perform exactly the same floating-point operations, in the same order, as the managed
//! fallbacks in `Electrical.Native.EmtKernels`, so results are bit-for-bit identical.

use std::slice;

use crate::{ERR_ARG, ERR_NULL, OK};

/// Number of coefficients per branch in a coefficient set: `[g, kc_new, kc_old, cv, ci, cc]`.
pub const COEF_STRIDE: usize = 6;
/// Number of state values per branch: `[i, vc, ih]`.
pub const STATE_STRIDE: usize = 3;

/// Safe core of [`eln_emt_branch_update`]. Node index `< 0` means ground (0 V, not accumulated).
///
/// For every branch k with branch voltage `vb = v[from] - v[to]`:
/// ```text
/// i    = g_cur·vb + ih
/// vc  += kcNew_cur·i + kcOld_cur·i_prev
/// ih   = g_nxt·(cv_nxt·vb + ci_nxt·i + cc_nxt·vc)
/// rhs[from] -= ih;  rhs[to] += ih
/// ```
pub fn branch_update(
    from: &[i32],
    to: &[i32],
    v: &[f64],
    cur: &[f64],
    nxt: &[f64],
    state: &mut [f64],
    rhs: &mut [f64],
) -> Result<(), i32> {
    let n = from.len();
    if to.len() != n || cur.len() < COEF_STRIDE * n || nxt.len() < COEF_STRIDE * n || state.len() < STATE_STRIDE * n {
        return Err(ERR_ARG);
    }
    let nv = v.len();
    if rhs.len() < nv {
        return Err(ERR_ARG);
    }
    for k in 0..n {
        let f = from[k];
        let t = to[k];
        if f >= nv as i32 || t >= nv as i32 {
            return Err(ERR_ARG);
        }
        let vf = if f >= 0 { v[f as usize] } else { 0.0 };
        let vt = if t >= 0 { v[t as usize] } else { 0.0 };
        let vb = vf - vt;
        let c = &cur[COEF_STRIDE * k..COEF_STRIDE * k + COEF_STRIDE];
        let x = &nxt[COEF_STRIDE * k..COEF_STRIDE * k + COEF_STRIDE];
        let s = &mut state[STATE_STRIDE * k..STATE_STRIDE * k + STATE_STRIDE];
        let i_prev = s[0];
        let i = c[0] * vb + s[2];
        let vc = s[1] + c[1] * i + c[2] * i_prev;
        let ih = x[0] * (x[3] * vb + x[4] * i + x[5] * vc);
        s[0] = i;
        s[1] = vc;
        s[2] = ih;
        if f >= 0 {
            rhs[f as usize] -= ih;
        }
        if t >= 0 {
            rhs[t as usize] += ih;
        }
    }
    Ok(())
}

/// History update of `n` series R-L-C branches. `from`/`to` index the node-voltage vector `v` (length `nv`, `-1` = ground),
/// `cur`/`nxt` are coefficient sets (6 doubles per branch) of the step just solved and of the next step, `state` holds
/// `[i, vc, ih]` per branch (updated in place) and the new history currents are accumulated into `rhs` (length `nv`).
#[no_mangle]
pub unsafe extern "C" fn eln_emt_branch_update(
    n: usize,
    from: *const i32,
    to: *const i32,
    nv: usize,
    v: *const f64,
    cur: *const f64,
    nxt: *const f64,
    state: *mut f64,
    rhs: *mut f64,
) -> i32 {
    if n == 0 {
        return OK;
    }
    if from.is_null() || to.is_null() || v.is_null() || cur.is_null() || nxt.is_null() || state.is_null() || rhs.is_null() {
        return ERR_NULL;
    }
    let r = branch_update(
        slice::from_raw_parts(from, n),
        slice::from_raw_parts(to, n),
        slice::from_raw_parts(v, nv),
        slice::from_raw_parts(cur, COEF_STRIDE * n),
        slice::from_raw_parts(nxt, COEF_STRIDE * n),
        slice::from_raw_parts_mut(state, STATE_STRIDE * n),
        slice::from_raw_parts_mut(rhs, nv),
    );
    match r {
        Ok(()) => OK,
        Err(e) => e,
    }
}

/// Pre-factored sparse LU in CSR form (row-wise strictly-lower `L` with unit diagonal, strictly-upper `U`, pivots `diag`,
/// symmetric permutation `perm`: position k → original index).
pub struct LuFactors<'a> {
    pub perm: &'a [i32],
    pub l_ptr: &'a [i32],
    pub l_idx: &'a [i32],
    pub l_val: &'a [f64],
    pub u_ptr: &'a [i32],
    pub u_idx: &'a [i32],
    pub u_val: &'a [f64],
    pub diag: &'a [f64],
}

/// Safe core of [`eln_emt_lu_solve`]: x = A⁻¹·b using `work` (length n) as scratch.
pub fn lu_solve(f: &LuFactors, b: &[f64], x: &mut [f64], work: &mut [f64]) -> Result<(), i32> {
    let n = f.diag.len();
    if f.perm.len() != n || f.l_ptr.len() != n + 1 || f.u_ptr.len() != n + 1 || b.len() < n || x.len() < n || work.len() < n {
        return Err(ERR_ARG);
    }
    let y = work;
    for k in 0..n {
        let p = f.perm[k];
        if p < 0 || p as usize >= n {
            return Err(ERR_ARG);
        }
        y[k] = b[p as usize];
    }
    for i in 0..n {
        let mut s = y[i];
        for q in f.l_ptr[i] as usize..f.l_ptr[i + 1] as usize {
            s -= f.l_val[q] * y[f.l_idx[q] as usize];
        }
        y[i] = s;
    }
    for i in (0..n).rev() {
        let mut s = y[i];
        for q in f.u_ptr[i] as usize..f.u_ptr[i + 1] as usize {
            s -= f.u_val[q] * y[f.u_idx[q] as usize];
        }
        y[i] = s / f.diag[i];
    }
    for k in 0..n {
        x[f.perm[k] as usize] = y[k];
    }
    Ok(())
}

/// Forward/backward substitution with a pre-factored sparse LU (see [`LuFactors`]). `nnz_l`/`nnz_u` are the lengths of
/// the index/value arrays. Returns 0 on success.
#[no_mangle]
pub unsafe extern "C" fn eln_emt_lu_solve(
    n: usize,
    perm: *const i32,
    l_ptr: *const i32,
    l_idx: *const i32,
    l_val: *const f64,
    nnz_l: usize,
    u_ptr: *const i32,
    u_idx: *const i32,
    u_val: *const f64,
    nnz_u: usize,
    diag: *const f64,
    b: *const f64,
    x: *mut f64,
    work: *mut f64,
) -> i32 {
    if n == 0 {
        return OK;
    }
    if perm.is_null() || l_ptr.is_null() || u_ptr.is_null() || diag.is_null() || b.is_null() || x.is_null() || work.is_null() {
        return ERR_NULL;
    }
    if (nnz_l > 0 && (l_idx.is_null() || l_val.is_null())) || (nnz_u > 0 && (u_idx.is_null() || u_val.is_null())) {
        return ERR_NULL;
    }
    let lp = slice::from_raw_parts(l_ptr, n + 1);
    let up = slice::from_raw_parts(u_ptr, n + 1);
    if lp[n] as usize != nnz_l || up[n] as usize != nnz_u {
        return ERR_ARG;
    }
    let empty_i: [i32; 0] = [];
    let empty_f: [f64; 0] = [];
    let f = LuFactors {
        perm: slice::from_raw_parts(perm, n),
        l_ptr: lp,
        l_idx: if nnz_l > 0 { slice::from_raw_parts(l_idx, nnz_l) } else { &empty_i },
        l_val: if nnz_l > 0 { slice::from_raw_parts(l_val, nnz_l) } else { &empty_f },
        u_ptr: up,
        u_idx: if nnz_u > 0 { slice::from_raw_parts(u_idx, nnz_u) } else { &empty_i },
        u_val: if nnz_u > 0 { slice::from_raw_parts(u_val, nnz_u) } else { &empty_f },
        diag: slice::from_raw_parts(diag, n),
    };
    // Validate index ranges once (cheap compared to the substitution itself).
    if f.l_idx.iter().chain(f.u_idx.iter()).any(|&j| j < 0 || j as usize >= n) {
        return ERR_ARG;
    }
    match lu_solve(&f, slice::from_raw_parts(b, n), slice::from_raw_parts_mut(x, n), slice::from_raw_parts_mut(work, n)) {
        Ok(()) => OK,
        Err(e) => e,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Trapezoidal coefficients `[g, kcNew, kcOld, cv, ci, cc]` of a series R-L-C branch (c = ∞ when `c_f` is 0).
    fn trap(r: f64, l: f64, c_f: f64, dt: f64) -> [f64; 6] {
        let beta = if c_f > 0.0 { dt / (2.0 * c_f) } else { 0.0 };
        let req = r + 2.0 * l / dt + beta;
        [1.0 / req, beta, beta, 1.0, -(r - 2.0 * l / dt + beta), -2.0]
    }

    #[test]
    fn rl_dc_step_matches_exponential() {
        // Voltage step V across series RL: node 0 driven to V (treated as given), branch 0 -> ground.
        let (r, l, dt, vdc) = (10.0, 0.1, 1e-5, 100.0);
        let cf = trap(r, l, 0.0, dt);
        let mut state = [0.0; 3];
        let v = [vdc];
        let mut t = 0.0;
        for _ in 0..2000 {
            let mut rhs = [0.0];
            branch_update(&[0], &[-1], &v, &cf, &cf, &mut state, &mut rhs).unwrap();
            t += dt;
        }
        // first step starts from i(0)=0 with v(0)=V treated as the first solved point -> t shifted by one step
        let tau = l / r;
        let exact = vdc / r * (1.0 - (-(t - dt) / tau).exp());
        assert!((state[0] - exact).abs() / (vdc / r) < 1e-3, "{} vs {}", state[0], exact);
    }

    #[test]
    fn rlc_branch_capacitor_voltage_integrates_current() {
        let (r, l, c, dt) = (1.0, 1e-3, 1e-6, 1e-7);
        let cf = trap(r, l, c, dt);
        let mut state = [0.0; 3];
        let v = [10.0];
        let mut charge = 0.0;
        let mut i_prev = 0.0;
        for _ in 0..5000 {
            let mut rhs = [0.0];
            branch_update(&[0], &[-1], &v, &cf, &cf, &mut state, &mut rhs).unwrap();
            charge += 0.5 * dt * (state[0] + i_prev);
            i_prev = state[0];
            assert!((rhs[0] + state[2]).abs() < 1e-15);
        }
        assert!((state[1] - charge / c).abs() < 1e-9);
    }

    #[test]
    fn lu_solve_small_system() {
        // A = [[4,1,0],[1,3,1],[0,1,2]], identity ordering. L (strict lower) and U (strict upper) computed by hand.
        let d0 = 4.0;
        let l10 = 1.0 / d0;
        let d1 = 3.0 - l10 * 1.0;
        let l21 = 1.0 / d1;
        let d2 = 2.0 - l21 * 1.0;
        let perm = [0, 1, 2];
        let lp = [0, 0, 1, 2];
        let li = [0, 1];
        let lv = [l10, l21];
        let upp = [0, 1, 2, 2];
        let ui = [1, 2];
        let uv = [1.0, 1.0];
        let diag = [d0, d1, d2];
        let b = [1.0, 2.0, 3.0];
        let mut x = [0.0; 3];
        let mut w = [0.0; 3];
        let r = unsafe {
            eln_emt_lu_solve(3, perm.as_ptr(), lp.as_ptr(), li.as_ptr(), lv.as_ptr(), 2, upp.as_ptr(), ui.as_ptr(), uv.as_ptr(), 2,
                diag.as_ptr(), b.as_ptr(), x.as_mut_ptr(), w.as_mut_ptr())
        };
        assert_eq!(r, 0);
        let res = [4.0 * x[0] + x[1] - 1.0, x[0] + 3.0 * x[1] + x[2] - 2.0, x[1] + 2.0 * x[2] - 3.0];
        assert!(res.iter().all(|e| e.abs() < 1e-14), "{:?}", res);
    }

    #[test]
    fn invalid_arguments_rejected() {
        let mut s = [0.0; 3];
        let mut rhs = [0.0];
        assert_eq!(branch_update(&[5], &[-1], &[1.0], &[0.0; 6], &[0.0; 6], &mut s, &mut rhs), Err(ERR_ARG));
        let r = unsafe { eln_emt_branch_update(1, std::ptr::null(), std::ptr::null(), 0, std::ptr::null(), std::ptr::null(), std::ptr::null(), std::ptr::null_mut(), std::ptr::null_mut()) };
        assert_eq!(r, ERR_NULL);
    }
}
