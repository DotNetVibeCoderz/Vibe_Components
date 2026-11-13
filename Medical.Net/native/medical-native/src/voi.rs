//! DICOM PS3.3 C.11.2.1.2 linear VOI LUT ("window/level") to 8-bit grayscale.

use crate::{ERR_ARG, ERR_NULL, OK};

#[inline(always)]
pub fn window_value(value: f64, center: f64, width: f64, invert: bool) -> u8 {
    let lo = center - 0.5 - (width - 1.0) / 2.0;
    let hi = center - 0.5 + (width - 1.0) / 2.0;
    let y = if value <= lo {
        0.0
    } else if value > hi {
        255.0
    } else {
        ((value - (center - 0.5)) / (width - 1.0) + 0.5) * 255.0
    };
    let b = (y + 0.5) as u8;
    if invert {
        255 - b
    } else {
        b
    }
}

pub fn window_level_i16(src: &[i16], dst: &mut [u8], slope: f64, intercept: f64, center: f64, width: f64, invert: bool) {
    for (o, &raw) in dst.iter_mut().zip(src) {
        *o = window_value(raw as f64 * slope + intercept, center, width, invert);
    }
}

pub fn window_level_u16(src: &[u16], dst: &mut [u8], slope: f64, intercept: f64, center: f64, width: f64, invert: bool) {
    for (o, &raw) in dst.iter_mut().zip(src) {
        *o = window_value(raw as f64 * slope + intercept, center, width, invert);
    }
}

#[no_mangle]
pub unsafe extern "C" fn mdn_window_level_i16(src: *const i16, dst: *mut u8, len: usize, slope: f64, intercept: f64, center: f64, width: f64, invert: i32) -> i32 {
    if !(width >= 1.0) {
        return ERR_ARG;
    }
    if len == 0 {
        return OK;
    }
    if src.is_null() || dst.is_null() {
        return ERR_NULL;
    }
    window_level_i16(std::slice::from_raw_parts(src, len), std::slice::from_raw_parts_mut(dst, len), slope, intercept, center, width, invert != 0);
    OK
}

#[no_mangle]
pub unsafe extern "C" fn mdn_window_level_u16(src: *const u16, dst: *mut u8, len: usize, slope: f64, intercept: f64, center: f64, width: f64, invert: i32) -> i32 {
    if !(width >= 1.0) {
        return ERR_ARG;
    }
    if len == 0 {
        return OK;
    }
    if src.is_null() || dst.is_null() {
        return ERR_NULL;
    }
    window_level_u16(std::slice::from_raw_parts(src, len), std::slice::from_raw_parts_mut(dst, len), slope, intercept, center, width, invert != 0);
    OK
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn soft_tissue_window() {
        // CT soft-tissue window C=40 W=400: far below is black, far above is white, centre is mid grey.
        assert_eq!(window_value(-1000.0, 40.0, 400.0, false), 0);
        assert_eq!(window_value(1000.0, 40.0, 400.0, false), 255);
        let mid = window_value(40.0, 40.0, 400.0, false);
        assert!((127..=128).contains(&mid));
        assert_eq!(window_value(-1000.0, 40.0, 400.0, true), 255);
    }

    #[test]
    fn rescale_applied() {
        let src = [0i16, 1024, 2048];
        let mut dst = [0u8; 3];
        window_level_i16(&src, &mut dst, 1.0, -1024.0, 40.0, 400.0, false);
        assert_eq!(dst[0], 0); // -1024 HU (air)
        assert_eq!(dst[2], 255); // +1024 HU (bone)
    }
}
