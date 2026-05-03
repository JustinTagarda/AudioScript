import json
import sys
import wave

import torch
from pyannote.audio import Pipeline


def load_waveform(audio_file: str) -> dict[str, object]:
    with wave.open(audio_file, "rb") as wav_file:
        channel_count = wav_file.getnchannels()
        sample_width = wav_file.getsampwidth()
        sample_rate = wav_file.getframerate()
        frame_count = wav_file.getnframes()
        audio_bytes = wav_file.readframes(frame_count)

    if sample_width == 1:
        waveform = torch.frombuffer(memoryview(audio_bytes), dtype=torch.uint8).to(torch.float32)
        waveform = (waveform - 128.0) / 128.0
    elif sample_width == 2:
        waveform = torch.frombuffer(memoryview(audio_bytes), dtype=torch.int16).to(torch.float32)
        waveform = waveform / 32768.0
    elif sample_width == 4:
        waveform = torch.frombuffer(memoryview(audio_bytes), dtype=torch.int32).to(torch.float32)
        waveform = waveform / 2147483648.0
    else:
        raise ValueError(f"Unsupported WAV sample width: {sample_width} byte(s)")

    waveform = waveform.reshape(-1, channel_count).transpose(0, 1).contiguous()
    return {"waveform": waveform, "sample_rate": sample_rate}


def main() -> int:
    if len(sys.argv) != 3:
        print(
            "Usage: run_community_diarization.py <model-directory> <audio-file>",
            file=sys.stderr,
        )
        return 2

    model_directory = sys.argv[1]
    audio_file = sys.argv[2]

    pipeline = Pipeline.from_pretrained(model_directory)
    output = pipeline(load_waveform(audio_file))
    diarization = getattr(output, "exclusive_speaker_diarization", None)
    if diarization is None:
        diarization = output.speaker_diarization

    turns = [
        {
            "speaker": str(speaker),
            "start": float(segment.start),
            "end": float(segment.end),
        }
        for segment, _, speaker in diarization.itertracks(yield_label=True)
    ]
    json.dump(turns, sys.stdout, separators=(",", ":"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
