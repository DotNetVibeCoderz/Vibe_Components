//! xoshiro256** seeded through SplitMix64 and Marsaglia-polar Gaussians: bit-identical to the managed
//! `AquaCulture.Core.Data.DeterministicRandom`, so native and managed Monte-Carlo runs agree exactly.

use crate::{ERR_ARG, ERR_NULL};

pub struct Rng {
    s: [u64; 4],
    spare: Option<f64>,
}

#[inline]
fn split_mix(x: &mut u64) -> u64 {
    *x = x.wrapping_add(0x9E3779B97F4A7C15);
    let mut z = *x;
    z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9);
    z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EB);
    z ^ (z >> 31)
}

impl Rng {
    pub fn new(seed: u64) -> Self {
        let mut x = seed;
        let s = [split_mix(&mut x), split_mix(&mut x), split_mix(&mut x), split_mix(&mut x)];
        Rng { s, spare: None }
    }

    #[inline]
    pub fn next_u64(&mut self) -> u64 {
        let s = &mut self.s;
        let result = s[1].wrapping_mul(5).rotate_left(7).wrapping_mul(9);
        let t = s[1] << 17;
        s[2] ^= s[0];
        s[3] ^= s[1];
        s[1] ^= s[2];
        s[0] ^= s[3];
        s[2] ^= t;
        s[3] = s[3].rotate_left(45);
        result
    }

    #[inline]
    pub fn next_f64(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 * (1.0 / (1u64 << 53) as f64)
    }

    pub fn gaussian(&mut self) -> f64 {
        if let Some(s) = self.spare.take() {
            return s;
        }
        loop {
            let u = 2.0 * self.next_f64() - 1.0;
            let v = 2.0 * self.next_f64() - 1.0;
            let q = u * u + v * v;
            if q < 1.0 && q != 0.0 {
                let f = (-2.0 * q.ln() / q).sqrt();
                self.spare = Some(v * f);
                return u * f;
            }
        }
    }
}

/// Monte-Carlo TGC growth: for each simulation, W^(1/3) grows by TGC/1000 · T each day with T = temps[d] + temp_sd · N(0,1).
/// Simulation k uses seed `seed + k`. Writes the final weight (g) of every simulation to `out`.
///
/// # Safety
/// `temps` must reference `ndays` doubles and `out` `nsims` writable doubles.
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub unsafe extern "C" fn aq_tgc_monte_carlo(
    w0: f64,
    tgc: f64,
    temps: *const f64,
    ndays: usize,
    nsims: usize,
    temp_sd: f64,
    seed: u64,
    out: *mut f64,
) -> i32 {
    if temps.is_null() || out.is_null() {
        return ERR_NULL;
    }
    if !(w0 > 0.0) || !(tgc >= 0.0) {
        return ERR_ARG;
    }
    let temps = std::slice::from_raw_parts(temps, ndays);
    let out = std::slice::from_raw_parts_mut(out, nsims);
    for (k, o) in out.iter_mut().enumerate() {
        let mut rng = Rng::new(seed.wrapping_add(k as u64));
        let mut cube = w0.cbrt();
        for &t in temps {
            let tt = t + temp_sd * rng.gaussian();
            if tt > 0.0 {
                cube += tgc / 1000.0 * tt;
            }
        }
        *o = cube * cube * cube;
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deterministic() {
        let mut a = Rng::new(42);
        let mut b = Rng::new(42);
        for _ in 0..100 {
            assert_eq!(a.next_u64(), b.next_u64());
        }
    }

    #[test]
    fn zero_sd_matches_closed_form() {
        let temps = vec![29.0; 100];
        let mut out = vec![0.0; 3];
        let rc = unsafe { aq_tgc_monte_carlo(5.0, 1.6, temps.as_ptr(), 100, 3, 0.0, 1, out.as_mut_ptr()) };
        assert_eq!(rc, 0);
        let expected = (5f64.cbrt() + 1.6 / 1000.0 * 29.0 * 100.0).powi(3);
        assert!((out[0] - expected).abs() < 1e-9);
    }
}
