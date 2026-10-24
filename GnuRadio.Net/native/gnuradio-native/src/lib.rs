//! Optional native kernels for GnuRadio.Net.
//!
//! Every entry point takes raw pointers and lengths owned by the managed side and never keeps them.
//! Hot loops are written with independent accumulator lanes so LLVM vectorises them; on x86-64 the
//! dot products are compiled a second time with AVX2+FMA and selected at runtime.
//!
//! Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.

use rustfft::num_complex::Complex32;
use rustfft::{Fft, FftPlanner};
use std::cell::RefCell;
use std::collections::HashMap;
use std::slice;
use std::sync::Arc;

/// ABI version; bump on any signature change. Must match `NativeKernels.ExpectedAbiVersion`.
#[no_mangle]
pub extern "C" fn gr_native_abi_version() -> u32 {
    1
}

/// dst[i] = a[i] * b[i] for interleaved complex float arrays of `n` samples.
///
/// # Safety
/// `a`, `b`, `dst` must point to `n` complex samples (2·n floats).
#[no_mangle]
pub unsafe extern "C" fn gr_complex_multiply(a: *const f32, b: *const f32, dst: *mut f32, n: usize) {
    let a = slice::from_raw_parts(a as *const Complex32, n);
    let b = slice::from_raw_parts(b as *const Complex32, n);
    let d = slice::from_raw_parts_mut(dst as *mut Complex32, n);
    for i in 0..n {
        d[i] = a[i] * b[i];
    }
}

/// dst[i] = |x[i]|².
///
/// # Safety
/// `x` must hold `n` complex samples, `dst` `n` floats.
#[no_mangle]
pub unsafe extern "C" fn gr_magnitude_squared(x: *const f32, dst: *mut f32, n: usize) {
    let x = slice::from_raw_parts(x, 2 * n);
    let d = slice::from_raw_parts_mut(dst, n);
    for i in 0..n {
        let re = x[2 * i];
        let im = x[2 * i + 1];
        d[i] = re * re + im * im;
    }
}

/// dst[i] = x[i]·phase·inc^i; `phase` (two floats) is updated in place.
///
/// # Safety
/// `x`/`dst` hold `n` complex samples, `phase` points to one complex sample.
#[no_mangle]
pub unsafe extern "C" fn gr_rotate(x: *const f32, inc_re: f32, inc_im: f32, phase: *mut f32, dst: *mut f32, n: usize) {
    let x = slice::from_raw_parts(x as *const Complex32, n);
    let d = slice::from_raw_parts_mut(dst as *mut Complex32, n);
    let p = &mut *(phase as *mut Complex32);
    let inc = Complex32::new(inc_re, inc_im);
    let mut cur = *p;
    for (i, (o, s)) in d.iter_mut().zip(x.iter()).enumerate() {
        *o = *s * cur;
        cur *= inc;
        if i & 511 == 511 {
            cur /= cur.norm();
        }
    }
    *p = cur / cur.norm();
}

/// Portable dot product with 16 independent lanes; returns (Σ even lanes, Σ odd lanes) so interleaved
/// complex · real-tap products come out as (re, im). x86-64 with AVX2 uses `dot_even_odd_avx2` instead.
#[inline(always)]
fn dot_even_odd<const FMA: bool>(a: &[f32], b: &[f32]) -> (f32, f32) {
    let n = a.len().min(b.len());
    let (a, b) = (&a[..n], &b[..n]);
    let mut acc = [0.0f32; 16];
    let mut ca = a.chunks_exact(16);
    let mut cb = b.chunks_exact(16);
    for (x, y) in (&mut ca).zip(&mut cb) {
        for l in 0..16 {
            acc[l] = if FMA { x[l].mul_add(y[l], acc[l]) } else { acc[l] + x[l] * y[l] };
        }
    }
    let (mut even, mut odd) = (0.0f32, 0.0f32);
    for (i, (x, y)) in ca.remainder().iter().zip(cb.remainder()).enumerate() {
        if i & 1 == 0 { even += x * y } else { odd += x * y }
    }
    for l in (0..16).step_by(2) {
        even += acc[l];
        odd += acc[l + 1];
    }
    (even, odd)
}

#[inline(always)]
fn fir_fff_loop<const FMA: bool>(inp: &[f32], taps: &[f32], decim: usize, out: &mut [f32]) {
    let n = taps.len();
    for (k, o) in out.iter_mut().enumerate() {
        let (e, od) = dot_even_odd::<FMA>(&inp[k * decim..k * decim + n], taps);
        *o = e + od;
    }
}

#[inline(always)]
fn fir_ccf_loop<const FMA: bool>(inp: &[f32], taps: &[f32], decim: usize, out: &mut [f32]) {
    let n = taps.len();
    for k in 0..out.len() / 2 {
        let start = 2 * k * decim;
        let (re, im) = dot_even_odd::<FMA>(&inp[start..start + n], taps);
        out[2 * k] = re;
        out[2 * k + 1] = im;
    }
}

/// AVX2/FMA dot product returning (Σ even lanes, Σ odd lanes), two 8-wide accumulators.
#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2,fma")]
unsafe fn dot_even_odd_avx2(a: &[f32], b: &[f32]) -> (f32, f32) {
    use std::arch::x86_64::*;
    let n = a.len().min(b.len());
    let pa = a.as_ptr();
    let pb = b.as_ptr();
    let mut acc0 = _mm256_setzero_ps();
    let mut acc1 = _mm256_setzero_ps();
    let mut i = 0;
    while i + 16 <= n {
        acc0 = _mm256_fmadd_ps(_mm256_loadu_ps(pa.add(i)), _mm256_loadu_ps(pb.add(i)), acc0);
        acc1 = _mm256_fmadd_ps(_mm256_loadu_ps(pa.add(i + 8)), _mm256_loadu_ps(pb.add(i + 8)), acc1);
        i += 16;
    }
    if i + 8 <= n {
        acc0 = _mm256_fmadd_ps(_mm256_loadu_ps(pa.add(i)), _mm256_loadu_ps(pb.add(i)), acc0);
        i += 8;
    }
    let mut lanes = [0.0f32; 8];
    _mm256_storeu_ps(lanes.as_mut_ptr(), _mm256_add_ps(acc0, acc1));
    let mut even = lanes[0] + lanes[2] + lanes[4] + lanes[6];
    let mut odd = lanes[1] + lanes[3] + lanes[5] + lanes[7];
    // i is a multiple of 8 here, so parity of the tail index matches the lane parity.
    while i < n {
        let p = *pa.add(i) * *pb.add(i);
        if i & 1 == 0 { even += p } else { odd += p }
        i += 1;
    }
    (even, odd)
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2,fma")]
unsafe fn fir_fff_avx2(inp: &[f32], taps: &[f32], decim: usize, out: &mut [f32]) {
    let n = taps.len();
    for (k, o) in out.iter_mut().enumerate() {
        let (e, od) = dot_even_odd_avx2(&inp[k * decim..k * decim + n], taps);
        *o = e + od;
    }
}

#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "avx2,fma")]
unsafe fn fir_ccf_avx2(inp: &[f32], taps: &[f32], decim: usize, out: &mut [f32]) {
    let n = taps.len();
    for k in 0..out.len() / 2 {
        let start = 2 * k * decim;
        let (re, im) = dot_even_odd_avx2(&inp[start..start + n], taps);
        out[2 * k] = re;
        out[2 * k + 1] = im;
    }
}

fn has_avx2() -> bool {
    #[cfg(target_arch = "x86_64")]
    {
        std::is_x86_feature_detected!("avx2") && std::is_x86_feature_detected!("fma")
    }
    #[cfg(not(target_arch = "x86_64"))]
    {
        false
    }
}

/// Real FIR: output[k] = Σ_j input[k·decim + j]·taps[j] (taps reversed by the caller).
///
/// # Safety
/// `input` must hold `(outputs − 1)·decim + ntaps` floats.
#[no_mangle]
pub unsafe extern "C" fn gr_fir_fff(input: *const f32, outputs: usize, taps: *const f32, ntaps: usize, decim: usize, output: *mut f32) {
    if outputs == 0 {
        return;
    }
    let inp = slice::from_raw_parts(input, (outputs - 1) * decim + ntaps);
    let t = slice::from_raw_parts(taps, ntaps);
    let out = slice::from_raw_parts_mut(output, outputs);
    #[cfg(target_arch = "x86_64")]
    if has_avx2() {
        return fir_fff_avx2(inp, t, decim, out);
    }
    fir_fff_loop::<false>(inp, t, decim, out);
}

/// Complex-sample, real-tap FIR. `taps` holds 2·ntaps floats: the reversed taps, each duplicated (t, t),
/// so every output is one contiguous float dot product.
///
/// # Safety
/// `input` must hold `(outputs − 1)·decim + ntaps` complex samples; `output` `outputs` complex samples.
#[no_mangle]
pub unsafe extern "C" fn gr_fir_ccf(input: *const f32, outputs: usize, taps: *const f32, ntaps: usize, decim: usize, output: *mut f32) {
    if outputs == 0 {
        return;
    }
    let inp = slice::from_raw_parts(input, 2 * ((outputs - 1) * decim + ntaps));
    let t = slice::from_raw_parts(taps, 2 * ntaps);
    let out = slice::from_raw_parts_mut(output, 2 * outputs);
    #[cfg(target_arch = "x86_64")]
    if has_avx2() {
        return fir_ccf_avx2(inp, t, decim, out);
    }
    fir_ccf_loop::<false>(inp, t, decim, out);
}

thread_local! {
    static PLANS: RefCell<(FftPlanner<f32>, HashMap<(usize, bool), Arc<dyn Fft<f32>>>, Vec<Complex32>)> =
        RefCell::new((FftPlanner::new(), HashMap::new(), Vec::new()));
}

/// In-place complex FFT of `n` samples, unnormalised. Returns 0 on success.
///
/// # Safety
/// `data` must hold `n` complex samples.
#[no_mangle]
pub unsafe extern "C" fn gr_fft_c2c(data: *mut f32, n: usize, inverse: i32) -> i32 {
    if n == 0 {
        return 0;
    }
    let buf = slice::from_raw_parts_mut(data as *mut Complex32, n);
    PLANS.with(|cell| {
        let mut guard = cell.borrow_mut();
        let (planner, plans, scratch) = &mut *guard;
        let fft = plans
            .entry((n, inverse != 0))
            .or_insert_with(|| if inverse != 0 { planner.plan_fft_inverse(n) } else { planner.plan_fft_forward(n) })
            .clone();
        let need = fft.get_inplace_scratch_len();
        if scratch.len() < need {
            scratch.resize(need, Complex32::new(0.0, 0.0));
        }
        fft.process_with_scratch(buf, &mut scratch[..need]);
    });
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fir_matches_naive() {
        let input: Vec<f32> = (0..100).map(|i| (i as f32 * 0.37).sin()).collect();
        let taps: Vec<f32> = (0..21).map(|i| 1.0 / (1.0 + i as f32)).collect();
        let outputs = 80;
        let mut out = vec![0.0f32; outputs];
        unsafe { gr_fir_fff(input.as_ptr(), outputs, taps.as_ptr(), taps.len(), 1, out.as_mut_ptr()) };
        for k in 0..outputs {
            let e: f32 = (0..taps.len()).map(|j| input[k + j] * taps[j]).sum();
            assert!((out[k] - e).abs() < 1e-4);
        }
    }

    #[test]
    fn fft_inverse_round_trips() {
        let mut x: Vec<f32> = (0..128).map(|i| (i as f32).cos()).collect();
        let orig = x.clone();
        unsafe {
            gr_fft_c2c(x.as_mut_ptr(), 64, 0);
            gr_fft_c2c(x.as_mut_ptr(), 64, 1);
        }
        for i in 0..128 {
            assert!((x[i] / 64.0 - orig[i]).abs() < 1e-4);
        }
    }
}
