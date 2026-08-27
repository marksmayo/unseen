"""Generates the drowning sounds as 22.05 kHz mono WAVs.

Synthesised rather than sourced, for the same reason the textures are: no licences to track, no
downloads to keep in sync, and every clip can be regenerated from the recipe that made it.

Lives in the repository rather than in a scratch directory. The rest of the audio bank was written
by a generator that sat outside version control and has since been lost with the temp folder it
lived in - which meant that adding one new sound to a bank of thirty-five required rebuilding the
primitives from scratch. Anything else added to the bank belongs beside this.

Pure standard library on purpose (wave/struct/math/random), so it runs anywhere Python does.

    python Tools/make_choking.py
"""
import math
import os
import random
import struct
import wave

RATE = 22050
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   'Assets', 'Unseen', 'Art', 'Audio')


# ---------------------------------------------------------------- primitives

def white(n, rng):
    return [rng.uniform(-1.0, 1.0) for _ in range(n)]


def lowpass(buf, cutoff):
    """One-pole low pass. cutoff in Hz."""
    a = 1.0 - math.exp(-2.0 * math.pi * cutoff / RATE)
    out = []
    y = 0.0
    for x in buf:
        y += a * (x - y)
        out.append(y)
    return out


def lowpass2(buf, cutoff):
    return lowpass(lowpass(buf, cutoff), cutoff)


def lowpass4(buf, cutoff):
    return lowpass(lowpass(lowpass(lowpass(buf, cutoff), cutoff), cutoff), cutoff)


def highpass(buf, cutoff):
    a = 1.0 - math.exp(-2.0 * math.pi * cutoff / RATE)
    out = []
    y = 0.0
    for x in buf:
        y += a * (x - y)
        out.append(x - y)
    return out


def envelope(buf, attack, decay, curve=2.0):
    n = len(buf)
    atk = max(1, int(RATE * attack))
    dec = max(1, int(RATE * decay))
    out = []
    for i, x in enumerate(buf):
        if i < atk:
            g = i / atk
        else:
            t = min(1.0, (i - atk) / dec)
            g = (1.0 - t) ** curve
        out.append(x * g)
    return out


def sine(freq, seconds, sweep=0.0):
    n = int(RATE * seconds)
    out = []
    phase = 0.0
    for i in range(n):
        f = freq + sweep * (i / max(1, n))
        phase += 2.0 * math.pi * f / RATE
        out.append(math.sin(phase))
    return out


def bubble(rng, seconds, low, high):
    """One burble: a short sine sweeping upward, which is what a bubble surfacing sounds like."""
    n = int(RATE * seconds)
    f0 = rng.uniform(low, high)
    f1 = f0 * rng.uniform(1.4, 2.6)

    out = []
    phase = 0.0
    for i in range(n):
        t = i / max(1, n)
        f = f0 + (f1 - f0) * t
        phase += 2.0 * math.pi * f / RATE
        out.append(math.sin(phase) * math.exp(-4.5 * t))
    return out


def mix(*layers):
    n = max(len(l) for l in layers)
    out = [0.0] * n
    for layer in layers:
        for i, x in enumerate(layer):
            out[i] += x
    return out


def gain(buf, g):
    return [x * g for x in buf]


def normalise(buf, peak=0.85):
    m = max(abs(x) for x in buf) or 1.0
    return [x * peak / m for x in buf]


def write(name, buf):
    buf = normalise(buf)
    path = os.path.join(OUT, name + '.wav')
    with wave.open(path, 'wb') as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        frames = b''.join(struct.pack('<h', int(max(-1.0, min(1.0, x)) * 32000)) for x in buf)
        f.writeframes(frames)
    print('%-28s %5.2f s' % (name, len(buf) / RATE))


# ---------------------------------------------------------------- the sound itself

def choking(seed):
    """Somebody out of air and still under.

    Three things, and the muffling is what makes it read as underwater rather than as a cough:

      - A constricted rasp. Band-passed noise with a slow resonant sweep, which is the throat.
      - Bubbles leaving. Upward chirps, densest at the start of each spasm.
      - A dull body thump underneath, heavily filtered, for the weight of it.

    The whole thing is low-passed hard at the end. Above about 1.2 kHz almost nothing survives a
    metre of water, and a bright choke sounds like a man on a sofa rather than a man under a river.
    """
    rng = random.Random(seed)
    length = 1.6
    n = int(RATE * length)

    # Two or three spasms, each a rasp with bubbles escaping.
    rasp = [0.0] * n
    bubbles = [0.0] * n

    at = 0.02
    for _ in range(rng.randint(2, 3)):
        start = int(at * RATE)
        span = int(RATE * rng.uniform(0.28, 0.45))

        # The throat: noise through a wandering resonance.
        noisy = highpass(white(span, rng), rng.uniform(180, 320))
        noisy = lowpass2(noisy, rng.uniform(900, 1400))
        shaped = envelope(noisy, 0.03, span / RATE, curve=1.6)

        # A pitched edge to it, so it is a voice and not a hiss.
        voice = envelope(sine(rng.uniform(95, 145), span / RATE, sweep=rng.uniform(-40, 60)),
                         0.04, span / RATE, curve=1.5)

        for i in range(span):
            if start + i < n:
                rasp[start + i] += shaped[i] * 0.9 + voice[i] * 0.35

        # Air leaving, thickest as the spasm begins.
        for _ in range(rng.randint(7, 12)):
            b_at = start + int(rng.uniform(0.0, 0.22) * RATE)
            b = bubble(rng, rng.uniform(0.03, 0.09), 180, 900)
            for i, x in enumerate(b):
                if b_at + i < n:
                    bubbles[b_at + i] += x * rng.uniform(0.3, 0.75)

        at += rng.uniform(0.42, 0.62)

    body = envelope(lowpass4(white(n, rng), 260), 0.05, 1.2, curve=1.8)

    out = mix(gain(rasp, 0.8), gain(bubbles, 0.5), gain(body, 0.35))

    # Under a metre of water. This is the line between a choke and a drowning.
    return lowpass2(out, 1100)


if __name__ == '__main__':
    for i in range(3):
        write('choking_%d' % (i + 1), choking(881 + i))
