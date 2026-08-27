"""Generates the organic textures the downloaded set does not cover.

The photographic material in Assets/Unseen/Art/Textures came from ambientCG and covers the built
world - timber, plaster, paving, roof tile, matting, ground. What it never covered is the living
half: grass, moss, leaves, bamboo, wet river stone, cloth. Those were all being faked by tinting an
existing map, which is why every plant in the town looked like green wicker and the riverbed looked
like paving stones somebody had painted.

So they are synthesised. Procedural is the right answer for these specifically: organic surfaces are
noise with structure imposed on it, which is exactly what a few octaves and a directional smear
produce, and generating them means no new licences to track and a recipe anybody can re-run.

Each material gets an albedo and a normal map derived from the same height field, so the lighting
agrees with the colour. Written at 1024 square to match the downloaded set.

    python Tools/make_textures.py

Requires numpy and Pillow, both of which the project already has for the character work.
"""
import os

import numpy as np
from PIL import Image

SIZE = 1024
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   'Assets', 'Unseen', 'Art', 'Textures')

RNG = np.random.default_rng(20260827)


# ---------------------------------------------------------------- noise

def value_noise(size, cells, rng):
    """Smooth noise from a jittered lattice, bilinearly interpolated. Tiles seamlessly."""
    lattice = rng.random((cells + 1, cells + 1))

    # Wrap the far edge onto the near one so the result tiles.
    lattice[-1, :] = lattice[0, :]
    lattice[:, -1] = lattice[:, 0]

    # Bilinear upsample with a smoothstep on the interpolant, which is what turns visible
    # diamond-shaped lattice artefacts into something that reads as cloud.
    ys = np.linspace(0, cells, size, endpoint=False)
    xs = np.linspace(0, cells, size, endpoint=False)

    y0 = ys.astype(int)
    x0 = xs.astype(int)
    fy = (ys - y0)[:, None]
    fx = (xs - x0)[None, :]

    fy = fy * fy * (3 - 2 * fy)
    fx = fx * fx * (3 - 2 * fx)

    a = lattice[np.ix_(y0, x0)]
    b = lattice[np.ix_(y0, x0 + 1)]
    c = lattice[np.ix_(y0 + 1, x0)]
    d = lattice[np.ix_(y0 + 1, x0 + 1)]

    return (a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) +
            c * (1 - fx) * fy + d * fx * fy)


def fbm(size, base_cells, octaves, rng, gain=0.5):
    """Fractal noise: octaves of value noise at doubling frequency and halving amplitude."""
    total = np.zeros((size, size))
    amplitude = 1.0
    weight = 0.0
    cells = base_cells

    for _ in range(octaves):
        total += value_noise(size, cells, rng) * amplitude
        weight += amplitude
        amplitude *= gain
        cells *= 2

    return total / weight


def ridged(size, base_cells, octaves, rng):
    """Ridged noise - sharp creases rather than soft blobs. Cracks, veins, stone edges."""
    return 1.0 - np.abs(fbm(size, base_cells, octaves, rng) * 2.0 - 1.0)


def smear(field, angle_deg, length):
    """Directional blur, which is what turns isotropic noise into fibres or blades."""
    out = np.zeros_like(field)
    rad = np.deg2rad(angle_deg)
    dy = np.sin(rad)
    dx = np.cos(rad)

    for i in range(length):
        out += np.roll(np.roll(field, int(round(dy * i)), axis=0), int(round(dx * i)), axis=1)

    return out / length


def normalise01(field):
    lo = field.min()
    hi = field.max()
    return (field - lo) / max(1e-6, hi - lo)


# ---------------------------------------------------------------- output

def normal_from_height(height, strength=2.0):
    """Tangent-space normal map from a height field, by central difference."""
    # Wrapped gradients, so the normal map tiles with the albedo.
    gx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * strength
    gy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * strength

    nz = np.ones_like(height)
    length = np.sqrt(gx * gx + gy * gy + nz * nz)

    # Unity samples normal maps as (x, y, z) in 0..1 with +y up the texture.
    r = (-gx / length) * 0.5 + 0.5
    g = (-gy / length) * 0.5 + 0.5
    b = (nz / length) * 0.5 + 0.5

    return np.stack([r, g, b], axis=-1)


def save(name, albedo, height, strength=2.0):
    folder = os.path.join(OUT, name)
    os.makedirs(folder, exist_ok=True)

    rgb = np.clip(albedo * 255.0, 0, 255).astype(np.uint8)
    Image.fromarray(rgb).save(os.path.join(folder, f'{name}_Albedo.png'))

    nrm = np.clip(normal_from_height(height, strength) * 255.0, 0, 255).astype(np.uint8)
    Image.fromarray(nrm).save(os.path.join(folder, f'{name}_Normal.png'))

    print(f'{name:<14} albedo + normal at {SIZE}x{SIZE}')


def tint(mask, dark, light):
    """Blends two colours by a 0..1 mask. Returns an HxWx3 array."""
    dark = np.array(dark, dtype=float)
    light = np.array(light, dtype=float)
    return dark[None, None, :] + (light - dark)[None, None, :] * mask[:, :, None]


# ---------------------------------------------------------------- the materials

def grass():
    """Blades, in clumps, with soil showing between them.

    The blades are directional smears of high-frequency noise rather than drawn strokes: at this
    resolution a blade is two or three pixels wide, and smearing noise along a slowly varying angle
    gives thousands of them for the price of a handful of array shifts.
    """
    fine = fbm(SIZE, 96, 3, RNG)

    # Three sheafs at different angles, taken at their maximum, so blades cross rather than all
    # lying the same way like brushed fur.
    blades = np.maximum.reduce([
        smear(fine, 88, 9),
        smear(fine, 74, 7),
        smear(fine, 101, 7),
    ])
    blades = normalise01(blades)

    # Clumping, and bare patches where the soil shows.
    clump = fbm(SIZE, 7, 3, RNG)

    # Nearly full cover. The first pass left so much soil showing that it read as muddy scrub
    # rather than as grass, and the blades were lost in it.
    cover = np.clip(clump * 2.4 - 0.35, 0, 1)

    density = np.clip(blades * 1.9 - 0.24, 0, 1) * cover

    soil = tint(fbm(SIZE, 24, 4, RNG), (0.16, 0.12, 0.08), (0.30, 0.24, 0.17))

    # Greens vary per clump, not per pixel: a lawn is patches of slightly different grass, and
    # per-pixel hue variation reads as static.
    hue = fbm(SIZE, 11, 2, RNG)
    dark = tint(hue, (0.10, 0.17, 0.06), (0.15, 0.24, 0.08))
    bright = tint(hue, (0.30, 0.47, 0.16), (0.42, 0.58, 0.22))

    green = dark + (bright - dark) * (blades ** 1.4)[:, :, None]
    albedo = soil + (green - soil) * density[:, :, None]

    height = density * 0.75 + clump * 0.25
    save('Grass', albedo, height, strength=3.0)


def moss():
    """Fine clumpy growth with dark crevices. Wetter and flatter than grass."""
    fine = fbm(SIZE, 140, 3, RNG)
    clump = fbm(SIZE, 16, 4, RNG)

    surface = normalise01(fine * 0.55 + clump * 0.45)
    crevice = np.clip(ridged(SIZE, 20, 3, RNG) - 0.55, 0, 1) * 2.2

    hue = fbm(SIZE, 9, 2, RNG)
    albedo = tint(np.clip(surface - crevice, 0, 1),
                  (0.05, 0.08, 0.04), (0.20, 0.31, 0.12))
    albedo *= (0.85 + 0.3 * hue)[:, :, None]

    save('Moss', albedo, surface - crevice * 0.6, strength=2.2)


def dirt():
    """Bare earth: fine tilth, scattered pebbles, and dried cracks."""
    tilth = fbm(SIZE, 64, 4, RNG)
    coarse = fbm(SIZE, 12, 3, RNG)

    # Pebbles: the bright peaks of a mid-frequency field, kept sparse.
    stones = fbm(SIZE, 44, 2, RNG)
    pebbles = np.clip((stones - 0.72) * 5.0, 0, 1)

    # Hairline. At a 0.72 threshold with a 4x gain these came out as thick dark veins and the
    # whole texture read as marbled chocolate.
    cracks = np.clip((ridged(SIZE, 22, 4, RNG) - 0.88) * 9.0, 0, 1)

    base = tint(np.clip(tilth * 0.6 + coarse * 0.4, 0, 1),
                (0.17, 0.12, 0.08), (0.42, 0.33, 0.23))

    stone_colour = tint(stones, (0.38, 0.35, 0.31), (0.62, 0.59, 0.54))
    albedo = base + (stone_colour - base) * pebbles[:, :, None]

    # Cracks are darker than anything around them, and they are the deepest part of the height.
    albedo *= (1.0 - cracks * 0.45)[:, :, None]

    height = tilth * 0.4 + coarse * 0.3 + pebbles * 0.5 - cracks * 0.8
    save('Dirt', albedo, height, strength=2.6)


def leaf():
    """Overlapping foliage, for canopies and hedges.

    Built from a lot of small soft ellipses at random angles. Leaves are the one thing here that
    genuinely is made of discrete shapes rather than filtered noise, and faking them with noise
    gives the green fog that every procedural tree suffers from.
    """
    height = np.zeros((SIZE, SIZE))
    shade = np.zeros((SIZE, SIZE))

    ys, xs = np.mgrid[0:SIZE, 0:SIZE]

    for _ in range(1100):
        cy = RNG.integers(0, SIZE)
        cx = RNG.integers(0, SIZE)
        a = RNG.uniform(14, 34)
        b = a * RNG.uniform(0.30, 0.55)
        angle = RNG.uniform(0, np.pi)

        # Only the neighbourhood of the leaf, or this is 420 full-image evaluations.
        r = int(a) + 4
        y0, y1 = max(0, cy - r), min(SIZE, cy + r)
        x0, x1 = max(0, cx - r), min(SIZE, cx + r)

        dy = ys[y0:y1, x0:x1] - cy
        dx = xs[y0:y1, x0:x1] - cx

        u = dx * np.cos(angle) + dy * np.sin(angle)
        v = -dx * np.sin(angle) + dy * np.cos(angle)

        d = (u / a) ** 2 + (v / b) ** 2
        blade = np.clip(1.0 - d, 0, 1) ** 0.6

        # A central vein, which is most of what makes a shape read as a leaf.
        vein = np.exp(-((v / (b * 0.16)) ** 2)) * np.clip(1.0 - (u / a) ** 2, 0, 1)

        lift = RNG.uniform(0.35, 1.0)
        height[y0:y1, x0:x1] = np.maximum(height[y0:y1, x0:x1], blade * lift)
        shade[y0:y1, x0:x1] = np.maximum(shade[y0:y1, x0:x1], blade * lift - vein * 0.35 * lift)

    gaps = fbm(SIZE, 6, 3, RNG)
    hue = fbm(SIZE, 13, 2, RNG)

    dark = tint(hue, (0.03, 0.05, 0.02), (0.06, 0.10, 0.04))
    bright = tint(hue, (0.16, 0.30, 0.10), (0.28, 0.44, 0.15))

    lit = np.clip(shade * (0.75 + 0.6 * gaps), 0, 1)
    albedo = dark + (bright - dark) * lit[:, :, None]

    save('Leaf', albedo, height, strength=2.4)


def bamboo():
    """A cane: vertical fibres, a waxy sheen, and a node every so often."""
    y = np.linspace(0, 1, SIZE, endpoint=False)[:, None]

    # Fibres running up the cane.
    fibre = normalise01(smear(fbm(SIZE, 200, 2, RNG), 90, 24))

    # Nodes: six rings up the texture, each a raised collar with a dark line under it.
    rings = np.sin(y * np.pi * 2 * 6)
    # Wider and softer than the first pass, which drew them as single dark scan lines.
    collar = np.clip((np.abs(rings) - 0.80) * 5.0, 0, 1)
    under = np.clip((np.abs(rings + 0.14) - 0.90) * 7.0, 0, 1)

    # Cylindrical shading baked into the albedo. A cane is round and the UV on a box is flat, so
    # without this it reads as a painted plank.
    x = np.linspace(0, 1, SIZE, endpoint=False)[None, :]
    round_shade = np.sin(x * np.pi) ** 0.45

    hue = fbm(SIZE, 8, 2, RNG)
    base = tint(np.clip(fibre * 0.7 + hue * 0.3, 0, 1),
                (0.24, 0.27, 0.13), (0.44, 0.47, 0.25))

    albedo = base * round_shade[:, :, None]
    albedo *= (1.0 - under * 0.55)[:, :, None]
    albedo += collar[:, :, None] * 0.06

    height = fibre * 0.25 + collar * 0.9 - under * 0.4
    save('Bamboo', albedo, height, strength=2.0)


def river_stone():
    """Wet cobbles on a riverbed: rounded, dark, close-packed."""
    cells = 26
    py = RNG.random(cells * cells) * SIZE
    px = RNG.random(cells * cells) * SIZE

    # Distance to the nearest and second-nearest seed. Their difference is the classic cellular
    # pattern, and it gives rounded stones with tight joints between them.
    step = 4
    ys, xs = np.mgrid[0:SIZE:step, 0:SIZE:step]
    flat_y = ys.reshape(-1, 1)
    flat_x = xs.reshape(-1, 1)

    # Wrapped distance, so the stones tile.
    dy = np.abs(flat_y - py[None, :])
    dx = np.abs(flat_x - px[None, :])
    dy = np.minimum(dy, SIZE - dy)
    dx = np.minimum(dx, SIZE - dx)
    dist = np.sqrt(dy * dy + dx * dx)

    nearest = np.partition(dist, 1, axis=1)[:, :2]
    edge = (nearest[:, 1] - nearest[:, 0]).reshape(ys.shape)

    small = normalise01(edge)
    full = np.array(Image.fromarray((small * 255).astype(np.uint8)).resize(
        (SIZE, SIZE), Image.BICUBIC), dtype=float) / 255.0

    dome = np.clip(full * 1.6, 0, 1) ** 0.65
    grit = fbm(SIZE, 128, 3, RNG)
    wet = fbm(SIZE, 15, 3, RNG)

    albedo = tint(np.clip(dome * 0.75 + grit * 0.25, 0, 1),
                  (0.035, 0.045, 0.045), (0.24, 0.27, 0.26))

    # Wet stone is darker and shinier in patches; dry crowns are paler.
    albedo *= (0.7 + 0.5 * wet)[:, :, None]

    height = dome * 0.85 + grit * 0.15
    save('RiverStone', albedo, height, strength=3.2)


def cloth():
    """Twill weave with wrap bands, for the ninja."""
    y = np.linspace(0, 1, SIZE, endpoint=False)[:, None]
    x = np.linspace(0, 1, SIZE, endpoint=False)[None, :]

    # A twill runs diagonally. Two out-of-phase gratings crossed give the over-under.
    warp = np.sin((x * SIZE / 3.0) * np.pi)
    weft = np.sin((y * SIZE / 3.0) * np.pi)
    diagonal = np.sin(((x + y) * SIZE / 4.5) * np.pi)

    weave = normalise01(warp * 0.35 + weft * 0.35 + diagonal * 0.5)

    slub = fbm(SIZE, 60, 3, RNG)
    wear = fbm(SIZE, 9, 3, RNG)

    surface = np.clip(weave * 0.78 + slub * 0.22, 0, 1)

    # More contrast than the first pass, where the twill was invisible against a flat navy and the
    # ninja might as well have been wearing paint.
    albedo = tint(surface, (0.035, 0.038, 0.048), (0.26, 0.27, 0.31))
    albedo *= (0.8 + 0.4 * wear)[:, :, None]

    height = surface * 0.6 + slub * 0.4
    save('Cloth', albedo, height, strength=1.6)


if __name__ == '__main__':
    grass()
    moss()
    dirt()
    leaf()
    bamboo()
    river_stone()
    cloth()
