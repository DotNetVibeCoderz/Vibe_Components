//! AquaCulture.Net native kernels exposed through a C ABI (`aq_*`).
//! Every function returns 0 on success and a negative code on invalid input; no function panics across the FFI boundary.

pub mod chemistry;
pub mod hydro;
pub mod rng;
pub mod stats;

pub const ERR_NULL: i32 = -1;
pub const ERR_ARG: i32 = -2;

/// Kernel ABI version (major * 10000 + minor * 100 + patch).
#[no_mangle]
pub extern "C" fn aq_version() -> u32 {
    10000
}
