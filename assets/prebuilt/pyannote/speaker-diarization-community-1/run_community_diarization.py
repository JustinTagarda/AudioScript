import json
import sys
import wave

import numpy as np
import torch
from pyannote.audio import Pipeline

print("runner_started", file=sys.stderr, flush=True)

if len(sys.argv) != 3:
    print("[]")
    sys.exit(2)

model_dir = sys.argv[1]
audio_path = sys.argv[2]

print("model_loading", file=sys.stderr, flush=True)
pipeline = Pipeline.from_pretrained(model_dir)
if torch.cuda.is_available():
    pipeline = pipeline.to(torch.device("cuda"))
print("model_loaded", file=sys.stderr, flush=True)

print("waveform_loading", file=sys.stderr, flush=True)
# Prefer a direct WAV loader to avoid runtime torchcodec/ffmpeg dependency
# issues inside torchaudio on Windows embedded runtimes.
with wave.open(audio_path, "rb") as wav_file:
    channels = wav_file.getnchannels()
    sample_rate = wav_file.getframerate()
    sample_width = wav_file.getsampwidth()
    frame_count = wav_file.getnframes()
    raw = wav_file.readframes(frame_count)

if sample_width == 1:
    data = np.frombuffer(raw, dtype=np.uint8).astype(np.float32)
    data = (data - 128.0) / 128.0
elif sample_width == 2:
    data = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
elif sample_width == 4:
    data = np.frombuffer(raw, dtype=np.int32).astype(np.float32) / 2147483648.0
else:
    raise RuntimeError(f"Unsupported WAV sample width: {sample_width} byte(s).")

if channels <= 0:
    raise RuntimeError("Invalid WAV channel count.")

data = data.reshape(-1, channels).T
waveform = torch.from_numpy(data)
print("waveform_loaded", file=sys.stderr, flush=True)

print("inference_started", file=sys.stderr, flush=True)
diarization = pipeline({"waveform": waveform, "sample_rate": sample_rate})
print("inference_finished", file=sys.stderr, flush=True)

print("serializing_turns", file=sys.stderr, flush=True)
turns = []
annotation = diarization
if hasattr(diarization, "speaker_diarization"):
    annotation = diarization.speaker_diarization

for segment, _, speaker in annotation.itertracks(yield_label=True):
    turns.append({
        "speaker": str(speaker),
        "start": float(segment.start),
        "end": float(segment.end),
    })

print(json.dumps(turns))
print("completed", file=sys.stderr, flush=True)