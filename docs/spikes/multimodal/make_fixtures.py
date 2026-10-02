#!/usr/bin/env python3
"""Spike #93: synthetic media for the multimodal probes (Python 3, no dependencies).

Writes into <dir>:
  left-red-right-blue.png  400x200, left half red, right half blue
  green-square.png         300x300, white with a green square in the middle
  tone.wav                 2 s, 440 Hz mono PCM; no speech, so it is not used to test transcription

The images carry facts a model can only state by actually seeing the pixels; nothing here is personal data.

Usage: python3 make_fixtures.py <dir>
"""
import math
import struct
import sys
import wave
import zlib
from pathlib import Path


def png(path, width, height, pixel):
    rows = b"".join(b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)) +
                     chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
png(out / "left-red-right-blue.png", 400, 200, lambda x, y: (220, 30, 30) if x < 200 else (30, 60, 220))
png(out / "green-square.png", 300, 300,
    lambda x, y: (20, 170, 60) if 90 <= x < 210 and 90 <= y < 210 else (255, 255, 255))
with wave.open(str(out / "tone.wav"), "wb") as audio:
    audio.setnchannels(1)
    audio.setsampwidth(2)
    audio.setframerate(16000)
    audio.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * 440 * i / 16000)))
                               for i in range(32000)))
print("\n".join(sorted(str(p) for p in out.iterdir())))
