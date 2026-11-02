//! C ABI over the `truehd` crate (Apache-2.0, github.com/truehdd/truehdd) for Marquee: decodes
//! TrueHD presentation 3 (Atmos: bed channels + objects) one Matroska packet at a time and reports
//! object metadata as timed events.
//!
//! Channel layout of presentation 3, as the truehdd CLI assumes: channels `0..beds` are bed
//! channels in label order, channel `i >= beds` is OAMD object `i`.

use std::panic::{AssertUnwindSafe, catch_unwind};

use truehd::process::decode::{DecodedAccessUnit, Decoder as ThdDecoder};
use truehd::process::extract::Extractor;
use truehd::process::parse::Parser;
use truehd::structs::oamd::ObjectAudioMetadataPayload;
use truehd::utils::errors::ExtractError;

const PRESENTATION: usize = 3;
const PCM_SCALE: f32 = 1.0 / 8_388_608.0;

/// One metadata update for an element (bed channel or object), relative to the decoded audio
/// returned by the same `mq_thd_decode` call.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct MqEvent {
    pub element: u32,
    pub sample_offset: u32,
    pub active: u8,
    pub has_position: u8,
    pub reserved: [u8; 2],
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub gain_db: f32,
    pub ramp: u32,
}

#[derive(Clone, Copy, PartialEq)]
struct Target {
    active: bool,
    pos: [f32; 3],
    gain_db: f32,
}

pub struct MqDecoder {
    extractor: Extractor,
    parser: Parser,
    decoder: ThdDecoder,
    resync: bool,
    channels: usize,
    bed_labels: Vec<String>,
    pcm: Vec<f32>,
    frames: usize,
    events: Vec<MqEvent>,
    targets: Vec<Option<Target>>,
    objects_seen: bool,
}

impl MqDecoder {
    fn new() -> Self {
        let mut parser = Parser::default();
        parser.set_required_presentations(&[false, false, false, true]);
        parser.set_check_fifo(false);
        Self {
            extractor: Extractor::default(),
            parser,
            decoder: ThdDecoder::default(),
            resync: true,
            channels: 0,
            bed_labels: Vec::new(),
            pcm: Vec::new(),
            frames: 0,
            events: Vec::new(),
            targets: Vec::new(),
            objects_seen: false,
        }
    }

    fn reset(&mut self) {
        self.extractor = Extractor::default();
        self.parser.reset_for_next_major_sync();
        self.decoder.reset_for_next_major_sync();
        self.resync = true;
        self.targets.clear();
    }

    fn decode(&mut self, data: &[u8]) {
        self.pcm.clear();
        self.frames = 0;
        self.events.clear();
        self.extractor.push_bytes(data);
        loop {
            let frame = match self.extractor.next() {
                Some(Ok(frame)) => frame,
                Some(Err(ExtractError::InsufficientData)) | None => break,
                Some(Err(_)) => continue,
            };
            if self.resync && !frame.is_major_sync() {
                continue;
            }

            let unit = match self.parser.parse(&frame) {
                Ok(unit) => unit,
                Err(_) => {
                    self.lose_sync();
                    continue;
                }
            };

            let mut decoded = self.decoder.decode_presentation(&unit, PRESENTATION);
            if matches!(&decoded, Ok(d) if d.substream_info_changed) {
                self.decoder.reset_for_next_major_sync();
                decoded = self.decoder.decode_presentation(&unit, PRESENTATION);
            }

            match decoded {
                Ok(d) => {
                    self.resync = false;
                    if !d.is_duplicate {
                        self.append(&d);
                    }
                }
                Err(_) => self.lose_sync(),
            }
        }
    }

    fn lose_sync(&mut self) {
        self.parser.reset_for_next_major_sync();
        self.decoder.reset_for_next_major_sync();
        self.resync = true;
    }

    fn append(&mut self, d: &DecodedAccessUnit) {
        let channels = d.channel_count.min(16);
        let samples = d.sample_length.min(d.pcm_data.len());
        if channels != self.channels {
            // Layout change: drop what this call accumulated under the old layout.
            self.channels = channels;
            self.pcm.clear();
            self.frames = 0;
            self.events.clear();
            self.targets = vec![None; channels];
        }

        self.bed_labels = d.channel_labels.iter().map(|label| format!("{label:?}")).collect();
        let au_start = self.frames;
        for sample in &d.pcm_data[..samples] {
            self.pcm.extend(sample[..channels].iter().map(|&v| v as f32 * PCM_SCALE));
        }
        self.frames += samples;

        for payload in &d.oamd {
            self.objects_seen = true;
            self.add_events(payload, au_start);
        }
    }

    fn add_events(&mut self, payload: &ObjectAudioMetadataPayload, au_start: usize) {
        let Some(element) = &payload.object_element else {
            return;
        };
        let info = &element.md_update_info;
        let block = info.block_update_info.first();
        let ramp = block.map_or(0, |b| b.ramp_duration as u32);
        let block_offset = block.map_or(0, |b| b.block_offset_factor_bits as usize * 32);
        let restatement = info.sample_offset == 0 && info.block_update_info.iter().all(|b| b.block_offset_factor_bits == 0 && b.ramp_duration == 0);
        let offset = au_start + info.sample_offset + block_offset + payload.evo_sample_offset as usize;
        let positions = payload.get_damf_pos();
        let beds = self.bed_labels.len();

        for (index, object) in element.object_data.iter().enumerate() {
            if index >= self.channels {
                break;
            }
            let Some(first) = object.first() else {
                continue;
            };

            let active = !first.b_object_not_active;
            let gain = first.object_basic_info.object_gain;
            let gain_db = if !active || gain <= -128 { f32::NEG_INFINITY } else { gain as f32 };
            let is_object = index >= beds;
            let pos = positions
                .get(index)
                .and_then(|blocks| blocks.first())
                .map_or([0.0; 3], |p| [p[0] as f32, p[1] as f32, p[2] as f32]);
            let target = Target { active, pos: if is_object { pos } else { [0.0; 3] }, gain_db };

            if restatement && self.targets.get(index).copied().flatten() == Some(target) {
                continue;
            }
            if let Some(slot) = self.targets.get_mut(index) {
                *slot = Some(target);
            }

            self.events.push(MqEvent {
                element: index as u32,
                sample_offset: offset as u32,
                active: active as u8,
                has_position: (is_object && active) as u8,
                reserved: [0; 2],
                x: pos[0],
                y: pos[1],
                z: pos[2],
                gain_db,
                ramp,
            });
        }
    }
}

/// Runs `f`, turning a panic inside the decoder into a full reset and an error result.
fn guarded<T>(decoder: *mut MqDecoder, fallback: T, f: impl FnOnce(&mut MqDecoder) -> T) -> T {
    let Some(d) = (unsafe { decoder.as_mut() }) else {
        return fallback;
    };
    match catch_unwind(AssertUnwindSafe(|| f(d))) {
        Ok(value) => value,
        Err(_) => {
            *d = MqDecoder::new();
            fallback
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn mq_thd_create() -> *mut MqDecoder {
    catch_unwind(|| Box::into_raw(Box::new(MqDecoder::new()))).unwrap_or(std::ptr::null_mut())
}

/// # Safety
/// `decoder` must come from `mq_thd_create` and not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mq_thd_destroy(decoder: *mut MqDecoder) {
    if !decoder.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(decoder) })));
    }
}

/// Forgets all stream state; the next bytes are a new, discontinuous stream (after a seek).
#[unsafe(no_mangle)]
pub extern "C" fn mq_thd_reset(decoder: *mut MqDecoder) {
    guarded(decoder, (), |d| d.reset());
}

/// Pushes bytes (one Matroska packet) and decodes every access unit they complete. Returns the
/// number of frames of audio produced (0 while waiting for a major sync), or -1 if the decoder
/// panicked and was reset.
///
/// # Safety
/// `data` must point to `length` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mq_thd_decode(decoder: *mut MqDecoder, data: *const u8, length: usize) -> i32 {
    if data.is_null() {
        return 0;
    }
    let bytes = unsafe { std::slice::from_raw_parts(data, length) };
    guarded(decoder, -1, |d| {
        d.decode(bytes);
        d.frames as i32
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mq_thd_channels(decoder: *mut MqDecoder) -> u32 {
    guarded(decoder, 0, |d| d.channels as u32)
}

#[unsafe(no_mangle)]
pub extern "C" fn mq_thd_bed_count(decoder: *mut MqDecoder) -> u32 {
    guarded(decoder, 0, |d| d.bed_labels.len() as u32)
}

/// True once object metadata has been seen, i.e. this really is object audio.
#[unsafe(no_mangle)]
pub extern "C" fn mq_thd_has_objects(decoder: *mut MqDecoder) -> u8 {
    guarded(decoder, 0, |d| d.objects_seen as u8)
}

/// Writes bed channel `index`'s label (e.g. "LFE") as NUL-terminated ASCII; returns its length.
///
/// # Safety
/// `buffer` must have room for `capacity` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mq_thd_bed_label(decoder: *mut MqDecoder, index: u32, buffer: *mut u8, capacity: u32) -> u32 {
    guarded(decoder, 0, |d| {
        let Some(label) = d.bed_labels.get(index as usize) else {
            return 0;
        };
        if buffer.is_null() || capacity == 0 {
            return 0;
        }
        let n = label.len().min(capacity as usize - 1);
        unsafe {
            std::ptr::copy_nonoverlapping(label.as_ptr(), buffer, n);
            *buffer.add(n) = 0;
        }
        n as u32
    })
}

/// Copies the last decode's audio as float, element-major (`channels` runs of `frames` samples).
/// Returns the number of floats written.
///
/// # Safety
/// `output` must have room for `capacity` floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mq_thd_copy_pcm(decoder: *mut MqDecoder, output: *mut f32, capacity: usize) -> usize {
    guarded(decoder, 0, |d| {
        let total = d.frames * d.channels;
        if output.is_null() || capacity < total {
            return 0;
        }
        let out = unsafe { std::slice::from_raw_parts_mut(output, total) };
        for frame in 0..d.frames {
            for channel in 0..d.channels {
                out[channel * d.frames + frame] = d.pcm[frame * d.channels + channel];
            }
        }
        total
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mq_thd_event_count(decoder: *mut MqDecoder) -> u32 {
    guarded(decoder, 0, |d| d.events.len() as u32)
}

/// Copies the last decode's metadata events; returns how many were written.
///
/// # Safety
/// `output` must have room for `capacity` events.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mq_thd_copy_events(decoder: *mut MqDecoder, output: *mut MqEvent, capacity: u32) -> u32 {
    guarded(decoder, 0, |d| {
        let n = d.events.len().min(capacity as usize);
        if output.is_null() {
            return 0;
        }
        unsafe { std::ptr::copy_nonoverlapping(d.events.as_ptr(), output, n) };
        n as u32
    })
}
