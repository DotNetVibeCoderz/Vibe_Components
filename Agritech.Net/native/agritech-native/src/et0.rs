//! FAO-56 Penman–Monteith reference evapotranspiration (daily), batch form.

use crate::{AGT_ERR_NULL, AGT_OK};
use std::f64::consts::PI;
use std::slice;

/// One daily weather record (C layout shared with .NET).
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct AgtDailyWeather {
    pub tmin: f64,
    pub tmax: f64,
    /// Mean relative humidity (%).
    pub rh_mean: f64,
    /// Wind speed at 2 m (m/s).
    pub u2: f64,
    /// Incoming shortwave radiation (MJ m-2 day-1).
    pub rs: f64,
    /// Day of year 1..366.
    pub doy: i32,
}

fn svp(t: f64) -> f64 {
    0.6108 * ((17.27 * t) / (t + 237.3)).exp()
}

/// FAO-56 eq. 6 with standard sub-equations. `lat_deg` latitude, `elev` metres.
pub fn penman_monteith(w: &AgtDailyWeather, lat_deg: f64, elev: f64) -> f64 {
    let tmean = (w.tmax + w.tmin) / 2.0;
    let p = 101.3 * ((293.0 - 0.0065 * elev) / 293.0).powf(5.26);
    let gamma = 0.000665 * p;
    let es = (svp(w.tmax) + svp(w.tmin)) / 2.0;
    let ea = w.rh_mean / 100.0 * es;
    let delta = 4098.0 * svp(tmean) / (tmean + 237.3).powi(2);

    let phi = lat_deg.to_radians();
    let j = w.doy as f64;
    let dr = 1.0 + 0.033 * (2.0 * PI / 365.0 * j).cos();
    let dec = 0.409 * (2.0 * PI / 365.0 * j - 1.39).sin();
    let ws = (-phi.tan() * dec.tan()).clamp(-1.0, 1.0).acos();
    let ra = 24.0 * 60.0 / PI * 0.0820 * dr * (ws * phi.sin() * dec.sin() + phi.cos() * dec.cos() * ws.sin());
    let rso = (0.75 + 2e-5 * elev) * ra;
    let rns = 0.77 * w.rs;
    let sigma = 4.903e-9;
    let tk4 = ((w.tmax + 273.16).powi(4) + (w.tmin + 273.16).powi(4)) / 2.0;
    let ratio = if rso > 0.0 { (w.rs / rso).min(1.0) } else { 0.0 };
    let rnl = sigma * tk4 * (0.34 - 0.14 * ea.max(0.0).sqrt()) * (1.35 * ratio - 0.35);
    let rn = rns - rnl;

    let num = 0.408 * delta * rn + gamma * (900.0 / (tmean + 273.0)) * w.u2 * (es - ea);
    let den = delta + gamma * (1.0 + 0.34 * w.u2);
    (num / den).max(0.0)
}

/// # Safety
/// `days` must point to `len` records and `out` to `len` writable f64 values.
#[no_mangle]
pub unsafe extern "C" fn agt_et0_pm_batch(days: *const AgtDailyWeather, len: usize, lat_deg: f64, elev: f64, out: *mut f64) -> i32 {
    if days.is_null() || out.is_null() {
        return AGT_ERR_NULL;
    }
    let d = slice::from_raw_parts(days, len);
    let o = slice::from_raw_parts_mut(out, len);
    for (w, r) in d.iter().zip(o.iter_mut()) {
        *r = penman_monteith(w, lat_deg, elev);
    }
    AGT_OK
}

#[cfg(test)]
mod tests {
    use super::*;

    /// FAO-56 Example 18 (Brussels, 6 July): ET0 ≈ 3.9 mm/day.
    #[test]
    fn fao56_example18() {
        // Tmax 21.5, Tmin 12.3, RH derived ea=1.409 kPa -> RHmean ≈ ea/es
        let es = (svp(21.5) + svp(12.3)) / 2.0;
        let rh = 1.409 / es * 100.0;
        let w = AgtDailyWeather { tmin: 12.3, tmax: 21.5, rh_mean: rh, u2: 2.078, rs: 22.07, doy: 187 };
        let et0 = penman_monteith(&w, 50.8, 100.0);
        assert!((et0 - 3.9).abs() < 0.15, "et0={et0}");
    }
}
