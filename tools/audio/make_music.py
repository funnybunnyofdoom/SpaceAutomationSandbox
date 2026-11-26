"""Makes the generated music layers (#129): tension and combat, seamless loops as 16-bit mono WAV.

Project-owned, CC0. Run from the repository root: python tools/audio/make_music.py
Every frequency is a whole number of cycles per loop, so each loop joins without a click.
"""
import math
import os
import random
import struct
import wave

RATE = 22050
OUT = os.path.join("assets", "audio", "music")


def cycles(freq, seconds):
    """The nearest frequency with a whole number of cycles in the loop."""
    return round(freq * seconds) / seconds


def write(name, samples):
    os.makedirs(OUT, exist_ok=True)
    peak = max(1e-9, max(abs(s) for s in samples))
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s / peak * 0.8)) * 32767)) for s in samples))


def tension(seconds=48.0):
    """A low drone that breathes, a slow rising shimmer, and a quiet heartbeat."""
    n = int(RATE * seconds)
    rng = random.Random(129)
    drone = [cycles(f, seconds) for f in (55.0, 82.5, 110.0, 116.5)]  # a fifth and a rub
    shimmer = [cycles(f, seconds) for f in (660.0, 990.0, 1046.5)]
    breath = cycles(1 / 8, seconds)
    beat = 60 / 54  # heartbeat, about 54 a minute, fitted to the loop
    beats = round(seconds / beat)
    beat = seconds / beats
    out = []
    noise = 0.0
    for i in range(n):
        t = i / RATE
        swell = 0.6 + 0.4 * math.sin(2 * math.pi * breath * t)
        s = sum(math.sin(2 * math.pi * f * t + k) / (k + 1) for k, f in enumerate(drone)) * swell
        s += 0.08 * sum(math.sin(2 * math.pi * f * t) for f in shimmer) * (0.5 + 0.5 * math.sin(2 * math.pi * cycles(1 / 16, seconds) * t))
        phase = (t % beat) / beat
        for hit in (0.0, 0.18):  # lub-dub
            x = phase - hit
            if 0 <= x < 0.12:
                s += 0.7 * math.sin(2 * math.pi * 48 * x * beat) * math.exp(-x * beat * 30)
        noise = 0.995 * noise + 0.005 * rng.uniform(-1, 1)
        s += noise * 0.6
        out.append(s)
    return out


def combat(seconds=32.0):
    """A driving pulse at 120 a minute: bass on the beat, a tick between, a low brass-like stab every bar."""
    n = int(RATE * seconds)
    rng = random.Random(1291)
    beat = 0.5
    bass = [cycles(f, seconds) for f in (55.0, 65.4, 49.0, 58.3)]  # a bar each
    out = []
    for i in range(n):
        t = i / RATE
        bar = int(t / (4 * beat)) % 4
        root = bass[bar]
        b = (t % beat) / beat
        s = 0.9 * math.sin(2 * math.pi * root * t) * math.exp(-b * 3) + 0.3 * math.sin(2 * math.pi * root * 2 * t) * math.exp(-b * 6)
        e = ((t + beat / 2) % beat) / beat  # off-beat tick
        if e < 0.08:
            s += 0.35 * rng.uniform(-1, 1) * (1 - e / 0.08)
        bt = (t % (4 * beat)) / (4 * beat)
        if bt < 0.25:
            env = math.exp(-bt * 12)
            s += 0.4 * env * sum(math.sin(2 * math.pi * root * h * t) / h for h in (2, 3, 4, 5))
        s += 0.15 * math.sin(2 * math.pi * cycles(220, seconds) * t) * (0.5 + 0.5 * math.sin(2 * math.pi * cycles(0.25, seconds) * t))
        out.append(s)
    return out


if __name__ == "__main__":
    write("tension.wav", tension())
    write("combat.wav", combat())
    print("tension.wav and combat.wav written to", OUT)
