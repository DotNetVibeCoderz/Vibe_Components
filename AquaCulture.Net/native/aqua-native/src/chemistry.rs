//! Water chemistry batches: un-ionised ammonia fraction (Emerson et al. 1975, salinity correction of Bower & Bidwell 1978)
//! and oxygen solubility (Benson & Krause 1984, APHA 4500-O, with barometric pressure correction).

use crate::{ERR_ARG, ERR_NULL};

/// Fraction of total ammonia present as un-ionised NH3 (0..1).
#[inline]
pub fn nh3_fraction(temp_c: f64, ph: f64, salinity: f64) -> f64 {
    let tk = temp_c + 273.15;
    let mut pka = 0.09018 + 2729.92 / tk;
    if salinity > 0.0 {
        let ionic = 19.9273 * salinity / (1000.0 - 1.005109 * salinity);
        pka += (0.1552 - 0.000314 * tk) * ionic;
    }
    1.0 / (1.0 + 10f64.powf(pka - ph))
}

/// Dissolved-oxygen saturation (mg/L) at temperature (°C), salinity (ppt) and pressure (kPa).
#[inline]
pub fn do_saturation(temp_c: f64, salinity: f64, pressure_kpa: f64) -> f64 {
    let t = temp_c + 273.15;
    let t2 = t * t;
    let t3 = t2 * t;
    let t4 = t3 * t;
    let ln_c = -139.34411 + 1.575701e5 / t - 6.642308e7 / t2 + 1.2438e10 / t3 - 8.621949e11 / t4
        - salinity * (1.7674e-2 - 10.754 / t + 2140.7 / t2);
    let c = ln_c.exp();
    let p = pressure_kpa / 101.325;
    if (p - 1.0).abs() < 1e-12 {
        return c;
    }
    let pwv = (11.8571 - 3840.70 / t - 216961.0 / t2).exp();
    let theta = 0.000975 - 1.426e-5 * temp_c + 6.436e-8 * temp_c * temp_c;
    c * p * ((1.0 - pwv / p) * (1.0 - theta * p)) / ((1.0 - pwv) * (1.0 - theta))
}

/// # Safety
/// All pointers must reference `len` valid elements.
#[no_mangle]
pub unsafe extern "C" fn aq_nh3_fraction_batch(temp: *const f64, ph: *const f64, sal: *const f64, out: *mut f64, len: usize) -> i32 {
    if temp.is_null() || ph.is_null() || sal.is_null() || out.is_null() {
        return ERR_NULL;
    }
    let t = std::slice::from_raw_parts(temp, len);
    let p = std::slice::from_raw_parts(ph, len);
    let s = std::slice::from_raw_parts(sal, len);
    let o = std::slice::from_raw_parts_mut(out, len);
    for i in 0..len {
        o[i] = nh3_fraction(t[i], p[i], s[i]);
    }
    0
}

/// # Safety
/// All pointers must reference `len` valid elements.
#[no_mangle]
pub unsafe extern "C" fn aq_do_saturation_batch(temp: *const f64, sal: *const f64, pressure_kpa: f64, out: *mut f64, len: usize) -> i32 {
    if temp.is_null() || sal.is_null() || out.is_null() {
        return ERR_NULL;
    }
    if !(pressure_kpa > 0.0) {
        return ERR_ARG;
    }
    let t = std::slice::from_raw_parts(temp, len);
    let s = std::slice::from_raw_parts(sal, len);
    let o = std::slice::from_raw_parts_mut(out, len);
    for i in 0..len {
        o[i] = do_saturation(t[i], s[i], pressure_kpa);
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn do_saturation_freshwater_25c() {
        let c = do_saturation(25.0, 0.0, 101.325);
        assert!((c - 8.26).abs() < 0.02, "{c}");
    }

    #[test]
    fn do_saturation_seawater_lower() {
        assert!(do_saturation(28.0, 35.0, 101.325) < do_saturation(28.0, 0.0, 101.325));
    }

    #[test]
    fn nh3_fraction_known_value() {
        // about 5.4 % at 25 °C and pH 8.0 in freshwater
        let f = nh3_fraction(25.0, 8.0, 0.0);
        assert!((f - 0.0538).abs() < 0.002, "{f}");
    }
}
