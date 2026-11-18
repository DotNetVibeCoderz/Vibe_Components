//! LAZ (LASzip) decompression through the pure-Rust `laz` crate: all point formats 0-10, including the LAS 1.4
//! layered compressor (v3/v4), whole files and individual COPC chunks.

use laz::record::{LayeredPointRecordDecompressor, RecordDecompressor, SequentialPointRecordDecompressor};
use laz::{LasZipDecompressor, LazVlr};
use std::cell::RefCell;
use std::fs::File;
use std::io::{BufReader, Cursor, Seek, SeekFrom};
use std::slice;

thread_local! {
    static LAST_ERROR: RefCell<String> = RefCell::new(String::new());
}

fn fail(msg: String) -> i32 {
    LAST_ERROR.with(|e| *e.borrow_mut() = msg);
    -1
}

/// Copies the last error message (UTF-8) into `buf`; returns its full length.
///
/// # Safety
/// `buf` must be writable for `len` bytes (may be null when `len` is 0).
#[no_mangle]
pub unsafe extern "C" fn gpn_last_error(buf: *mut u8, len: usize) -> usize {
    LAST_ERROR.with(|e| {
        let s = e.borrow();
        let b = s.as_bytes();
        if !buf.is_null() && len > 0 {
            let n = b.len().min(len);
            std::ptr::copy_nonoverlapping(b.as_ptr(), buf, n);
        }
        b.len()
    })
}

/// Decompresses `point_count` points of a LAZ file into `out` (raw LAS point records).
///
/// # Safety
/// `path` must hold `path_len` UTF-8 bytes, `vlr` `vlr_len` bytes (the "laszip encoded" VLR payload),
/// and `out` must be writable for `out_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn gpn_laz_decompress_file(
    path: *const u8, path_len: usize, offset_to_points: u64,
    vlr: *const u8, vlr_len: usize, point_count: u64, out: *mut u8, out_len: usize,
) -> i32 {
    let path = match std::str::from_utf8(slice::from_raw_parts(path, path_len)) {
        Ok(p) => p,
        Err(e) => return fail(format!("invalid path: {e}")),
    };
    let vlr = match LazVlr::from_buffer(slice::from_raw_parts(vlr, vlr_len)) {
        Ok(v) => v,
        Err(e) => return fail(format!("invalid laszip VLR: {e}")),
    };
    let need = vlr.items_size() as u128 * point_count as u128;
    if need > out_len as u128 {
        return fail(format!("output buffer too small: need {need} bytes"));
    }
    let file = match File::open(path) {
        Ok(f) => f,
        Err(e) => return fail(format!("cannot open {path}: {e}")),
    };
    let mut reader = BufReader::new(file);
    if let Err(e) = reader.seek(SeekFrom::Start(offset_to_points)) {
        return fail(format!("seek failed: {e}"));
    }
    let mut dec = match LasZipDecompressor::new(reader, vlr) {
        Ok(d) => d,
        Err(e) => return fail(format!("decompressor: {e}")),
    };
    let buf = slice::from_raw_parts_mut(out, need as usize);
    match dec.decompress_many(buf) {
        Ok(()) => 0,
        Err(e) => fail(format!("decompression failed: {e}")),
    }
}

/// Decompresses one LAZ chunk (e.g. a COPC hierarchy node) holding `point_count` points.
///
/// # Safety
/// Pointers must be valid for the given lengths.
#[no_mangle]
pub unsafe extern "C" fn gpn_laz_decompress_chunk(
    vlr: *const u8, vlr_len: usize, chunk: *const u8, chunk_len: usize, point_count: u64, out: *mut u8, out_len: usize,
) -> i32 {
    let vlr = match LazVlr::from_buffer(slice::from_raw_parts(vlr, vlr_len)) {
        Ok(v) => v,
        Err(e) => return fail(format!("invalid laszip VLR: {e}")),
    };
    let need = vlr.items_size() as u128 * point_count as u128;
    if need > out_len as u128 {
        return fail(format!("output buffer too small: need {need} bytes"));
    }
    let data = slice::from_raw_parts(chunk, chunk_len);
    let buf = slice::from_raw_parts_mut(out, need as usize);
    let items = vlr.items();
    let layered = items.first().map(|i| i.version() >= 3).unwrap_or(false);
    let result = if layered {
        let mut d = LayeredPointRecordDecompressor::new(Cursor::new(data));
        d.set_fields_from(items).and_then(|_| d.decompress_many(buf).map_err(Into::into))
    } else {
        let mut d = SequentialPointRecordDecompressor::new(Cursor::new(data));
        d.set_fields_from(items).and_then(|_| d.decompress_many(buf).map_err(Into::into))
    };
    match result {
        Ok(()) => 0,
        Err(e) => fail(format!("chunk decompression failed: {e}")),
    }
}
