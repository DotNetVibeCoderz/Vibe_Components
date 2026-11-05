//! Agritech.Net native kernels.
//!
//! Exposes a small, stable C ABI consumed by the `Agritech.Native` .NET package via `LibraryImport`.
//! All functions are panic-free on valid input, never allocate on the caller's behalf and return
//! a status code (`AGT_OK` = 0). Large inputs are processed in parallel with scoped threads.
//!
//! Made by Gravicode Studios, led by Kang Fadhil.

pub mod et0;
pub mod indices;
pub mod stats;

pub const AGT_OK: i32 = 0;
pub const AGT_ERR_NULL: i32 = -1;
pub const AGT_ERR_LEN: i32 = -2;

/// ABI version: bump the major part on breaking changes.
#[no_mangle]
pub extern "C" fn agt_version() -> u32 {
    1_00_00
}

/// Minimum element count before work is split across threads.
pub(crate) const PARALLEL_THRESHOLD: usize = 1 << 16;

/// Runs `f(chunk_index_start, out_chunk)` over `out` in parallel chunks.
pub(crate) fn par_chunks<F>(out: &mut [f32], f: F)
where
    F: Fn(usize, &mut [f32]) + Sync,
{
    let len = out.len();
    if len < PARALLEL_THRESHOLD {
        f(0, out);
        return;
    }
    let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4).min(16);
    let chunk = len.div_ceil(threads);
    std::thread::scope(|s| {
        for (i, c) in out.chunks_mut(chunk).enumerate() {
            let f = &f;
            s.spawn(move || f(i * chunk, c));
        }
    });
}
