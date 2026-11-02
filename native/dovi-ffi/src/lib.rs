//! C ABI over the `dolby_vision` crate (MIT, github.com/quietvoid/dovi_tool) for Marquee.
//!
//! Windows' Dolby Vision Extensions only handle profiles 5 and 8. UHD Blu-ray uses profile 7: an
//! HDR10 base layer plus an enhancement layer and an RPU. Converting each frame's RPU to profile
//! 8.1 with dovi_tool's mode 2 (luma/chroma mapping set to no-op) and dropping the enhancement
//! layer gives a stream the extensions render as real Dolby Vision.

use std::panic::catch_unwind;

use dolby_vision::rpu::ConversionMode;
use dolby_vision::rpu::dovi_rpu::DoviRpu;

/// What the RPU was before conversion.
#[repr(C)]
#[derive(Default)]
pub struct MqDoviInfo {
    pub profile: u8,
    /// 0: none, 1: MEL, 2: FEL.
    pub el_type: u8,
}

/// Converts one HEVC UNSPEC62 NAL unit (starting with its 0x7C01 header, emulation prevention
/// bytes included) to profile 8.1 and writes the new NAL unit, in the same form, to `output`.
/// Returns its length, 0 if `capacity` is too small, or -1 if the RPU couldn't be parsed or
/// converted (or the converter panicked).
///
/// # Safety
/// `input` must point to `length` readable bytes, `output` to `capacity` writable bytes, and
/// `info` must be null or point to an `MqDoviInfo`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mq_dovi_convert_to_81(
    input: *const u8,
    length: usize,
    output: *mut u8,
    capacity: usize,
    info: *mut MqDoviInfo,
) -> isize {
    if input.is_null() || output.is_null() {
        return -1;
    }

    let data = unsafe { std::slice::from_raw_parts(input, length) };
    let result = catch_unwind(|| -> Option<(Vec<u8>, MqDoviInfo)> {
        let mut rpu = DoviRpu::parse_unspec62_nalu(data).ok()?;
        let before = MqDoviInfo {
            profile: rpu.dovi_profile,
            el_type: match rpu.el_type.as_ref().map(|el| format!("{el:?}")) {
                Some(name) if name.contains("FEL") => 2,
                Some(name) if name.contains("MEL") => 1,
                _ => 0,
            },
        };
        rpu.convert_with_mode(ConversionMode::To81).ok()?;
        Some((rpu.write_hevc_unspec62_nalu().ok()?, before))
    });

    let Ok(Some((nalu, before))) = result else {
        return -1;
    };
    if !info.is_null() {
        unsafe { *info = before };
    }
    if nalu.len() > capacity {
        return 0;
    }

    unsafe { std::ptr::copy_nonoverlapping(nalu.as_ptr(), output, nalu.len()) };
    nalu.len() as isize
}
