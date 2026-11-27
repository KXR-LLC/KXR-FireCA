"""Builds the Plant Models and the Leaf Atlas of the Vegetation (docs/decisions.md, decision D34).

The script runs in Blender 5.1 with no window:

    blender -b --python tools/blender/plant_models.py -- --out unity/Assets/FireCA/Models

The script writes two files in the output folder:

    PlantModels.bytes   11 Plant Models at three levels of detail, as lists of triangles
    LeafAtlas.png       four leaf pictures in a 2 x 2 atlas, rendered by Cycles

With --preview <folder>, the script also renders one picture of the Plant Models at each level of detail and saves
the scene as PlantModels.blend in that folder.

A Plant Model has a height of 1 and stands on the origin. The axes are the axes of Unity: Y is up.
Each vertex has 12 floats: position (3), part, normal (3), shade, atlas uv (2), flutter, leaf seed.
The part is 0 for bark, 1 for foliage, and 2 for dead wood. A uv below 0 means that the triangle has no texture.
"""
import argparse
import math
import os
import random
import struct
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

VERTEX_FLOATS = 12
PART_BARK, PART_FOLIAGE, PART_DEAD = 0.0, 1.0, 2.0
TILE_FIR, TILE_PINE, TILE_LEAF, TILE_SHRUB = 0, 1, 2, 3
ATLAS_PIXELS = 2048
UP = Vector((0.0, 1.0, 0.0))
GOLDEN = 2.399963

# The order matches PlantModelId in VegetationRules.cs.
MODEL_NAMES = ["ConiferA", "ConiferB", "ConiferC", "HardwoodA", "HardwoodB", "ShrubA", "ShrubB", "ShrubC",
               "LogA", "LogB", "Snag"]
# The half width of each Plant Model as a share of its height. The values match Vegetation_HalfWidth in Vegetation.hlsl.
HALF_WIDTH = [0.17, 0.26, 0.27, 0.48, 0.32, 0.85, 1.25, 0.60, 2.4, 2.6, 0.12]


# ---------------------------------------------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------------------------------------------

class Model:
    """A list of triangles with 12 floats per vertex."""

    def __init__(self):
        self.data = []

    def vert(self, p, n, part, shade, uv=(-1.0, -1.0), flutter=0.0, seed=0.0):
        self.data.extend((p.x, p.y, p.z, part, n.x, n.y, n.z, shade, uv[0], uv[1], flutter, seed))

    def tri(self, a, b, c):
        self.vert(*a)
        self.vert(*b)
        self.vert(*c)

    def quad(self, a, b, c, d):
        self.tri(a, b, c)
        self.tri(a, c, d)

    @property
    def vertices(self):
        return len(self.data) // VERTEX_FLOATS

    @property
    def triangles(self):
        return self.vertices // 3


def lerp(a, b, t):
    return a + (b - a) * t


def perp(d):
    a = UP if abs(d.y) < 0.9 else Vector((1.0, 0.0, 0.0))
    return d.cross(a).normalized()


def rot(v, axis, angle):
    return Matrix.Rotation(angle, 3, axis) @ v


def rand_unit(rng):
    while True:
        v = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)))
        if 0.05 < v.length <= 1.0:
            return v.normalized()


def tile_rect(tile):
    col, row = tile % 2, tile // 2
    m = 4.0 / ATLAS_PIXELS
    return col * 0.5 + m, row * 0.5 + m, col * 0.5 + 0.5 - m, row * 0.5 + 0.5 - m


def tube(m, pts, radii, sides, part, shade=1.0, cap_start=False, cap_end=False, rough=0.0, rng=None):
    """A tube along a line of points. The shade is one value or one value per point."""
    pts = [Vector(p) for p in pts]
    n = len(pts)
    shades = shade if isinstance(shade, (list, tuple)) else [shade] * n
    side = None
    rings = []
    for i in range(n):
        t = (pts[min(i + 1, n - 1)] - pts[max(i - 1, 0)]).normalized()
        side = perp(t) if side is None else (side - t * side.dot(t)).normalized()
        other = t.cross(side)
        ring = []
        for s in range(sides):
            a = 2.0 * math.pi * s / sides
            d = side * math.cos(a) + other * math.sin(a)
            r = radii[i] * (1.0 + (rough * (rng.random() - 0.5) if rough else 0.0))
            ring.append((pts[i] + d * r, d, part, shades[i]))
        rings.append((ring, t))
    for i in range(n - 1):
        r0, r1 = rings[i][0], rings[i + 1][0]
        for s in range(sides):
            s1 = (s + 1) % sides
            m.quad(r0[s], r0[s1], r1[s1], r1[s])
    for end, wanted in ((0, cap_start), (n - 1, cap_end)):
        if not wanted:
            continue
        ring, t = rings[end]
        normal = -t if end == 0 else t
        center = (pts[end], normal, part, shades[end] * 0.8)
        for s in range(sides):
            s1 = (s + 1) % sides
            m.tri(center, (ring[s][0], normal, part, shades[end]), (ring[s1][0], normal, part, shades[end]))


def card(m, base, direction, side, length, width, tile, outward, shade, seed, segments=1, droop=0.0):
    """A strip of leaf texture. The bottom edge of the atlas tile is at the base."""
    u0, v0, u1, v1 = tile_rect(tile)
    face = side.cross(direction).normalized()
    rows = []
    for k in range(segments + 1):
        t = k / segments
        c = base + direction * (length * t) - UP * (droop * length * t * t)
        row = []
        for p, u in ((c - side * (width * 0.5), u0), (c + side * (width * 0.5), u1)):
            p.y = max(p.y, 0.004)
            o = outward(p)
            f = face if face.dot(o) >= 0.0 else -face
            row.append((p, (f * 0.35 + o * 0.65).normalized(), PART_FOLIAGE, shade(p), (u, lerp(v0, v1, t)), t, seed))
        rows.append(row)
    for k in range(segments):
        a, b = rows[k]
        d, c = rows[k + 1]
        m.quad(a, b, c, d)


def jitter(seed, index):
    h = ((seed * 73856093) ^ (index * 19349663)) & 0xFFFFFFFF
    h ^= h >> 13
    h = (h * 1274126177) & 0xFFFFFFFF
    h ^= h >> 16
    return (h & 0xFFFFFF) / 16777216.0


def blob(m, center, radii, lon, lat, rough, seed):
    """A rough ball of foliage with no texture. The triangle count is 2 x lon x (lat - 1)."""
    def point(i, j):
        polar = j / lat * math.pi
        azimuth = (i % lon) * 2.0 * math.pi / lon + (seed % 5) * 0.4
        d = Vector((math.sin(polar) * math.cos(azimuth), math.cos(polar), math.sin(polar) * math.sin(azimuth)))
        pole = j == 0 or j == lat
        r = 1.0 + rough * (jitter(seed, j * 131 if pole else j * 131 + i % lon + 1) - 0.5)
        normal = Vector((d.x / radii.x, d.y / radii.y, d.z / radii.z)).normalized()
        p = center + Vector((d.x * radii.x, d.y * radii.y, d.z * radii.z)) * r
        return (p, normal, PART_FOLIAGE, 0.55 + 0.45 * min(max(normal.y * 0.5 + 0.5, 0.0), 1.0), (-1.0, -1.0), 0.5, 0.5)

    for j in range(lat):
        for i in range(lon):
            p00, p10, p01, p11 = point(i, j), point(i + 1, j), point(i, j + 1), point(i + 1, j + 1)
            if j > 0:
                m.tri(p00, p10, p01)
            if j < lat - 1:
                m.tri(p10, p11, p01)


def cone(m, y0, y1, r0, sides, seed):
    """A cone of foliage with no texture."""
    turn = (seed % 7) * 0.37
    for s in range(sides):
        a0, a1 = turn + s * 2.0 * math.pi / sides, turn + (s + 1) * 2.0 * math.pi / sides
        d0, d1 = Vector((math.cos(a0), 0.0, math.sin(a0))), Vector((math.cos(a1), 0.0, math.sin(a1)))
        rise, run = y1 - y0, max(r0, 1.0e-4)
        n0, n1 = (d0 * rise + UP * run).normalized(), (d1 * rise + UP * run).normalized()
        top = ((n0 + n1) * 0.5 + UP * 0.5).normalized()
        m.tri((d0 * r0 + UP * y0, n0, PART_FOLIAGE, 0.9, (-1.0, -1.0), 0.3, 0.5),
              (d1 * r0 + UP * y0, n1, PART_FOLIAGE, 0.9, (-1.0, -1.0), 0.3, 0.5),
              (UP * y1, top, PART_FOLIAGE, 1.0, (-1.0, -1.0), 0.6, 0.5))


# ---------------------------------------------------------------------------------------------------------------
# Plant Models
# ---------------------------------------------------------------------------------------------------------------

def conifer(lod, crown_base, crown_radius, whorls, per_whorl, trunk_r, seed, slope=(-0.60, 0.25)):
    """A fir: whorls of boughs that slope down at the bottom of the crown and up at the top."""
    rng = random.Random(seed)
    m = Model()

    def profile(y):
        t = min(max((y - crown_base) / (1.0 - crown_base), 0.0), 1.0)
        return crown_radius * max(1.0 - 0.93 * t, 0.03) ** 0.9

    def outward(p):
        h = Vector((p.x, 0.0, p.z))
        h = h.normalized() if h.length > 1.0e-5 else Vector((1.0, 0.0, 0.0))
        return (h * 0.8 + UP * 0.6).normalized()

    def shade(p):
        d = min(math.hypot(p.x, p.z) / profile(p.y), 1.0)
        return 0.40 + 0.60 * d ** 1.3

    sides, segs = (8, 5, 3)[lod], (5, 2, 1)[lod]
    pts = [Vector((0.0, 0.98 * k / segs, 0.0)) for k in range(segs + 1)]
    tube(m, pts, [trunk_r * (1.0 - 0.9 * k / segs) + 0.002 for k in range(segs + 1)], sides, PART_BARK,
         [1.0 if p.y <= crown_base else 0.6 for p in pts])

    branches = []
    # The boughs stand on a spiral, so that no two boughs have the same height.
    count = whorls * per_whorl
    for i in range(count):
        t = (i + rng.random()) / count * 0.95
        y = crown_base + t * (1.0 - crown_base)
        az = i * GOLDEN + rng.uniform(-0.25, 0.25)
        branches.append((y, az, profile(y) * rng.uniform(0.90, 1.15), lerp(slope[0], slope[1], t) + rng.uniform(-0.10, 0.10),
                         rng.random()))
    if lod == 2:
        cone(m, crown_base, 1.0, crown_radius * 0.9, 6, seed)
        return m
    if lod == 1:
        branches = branches[::2]
    for y, az, length, lift, leaf in branches:
        d = Vector((math.cos(az), lift, math.sin(az))).normalized()
        base = Vector((0.0, y, 0.0))
        side0 = d.cross(UP).normalized()
        grow = 1.0 if lod == 0 else 1.15
        for tilt in (0.95, -0.95):
            card(m, base, d, rot(side0, d, tilt), length * grow, length * 0.70 * (1.0 if lod == 0 else 1.3), TILE_FIR,
                 outward, shade, leaf, segments=2 if lod == 0 else 1, droop=0.30)
        if lod == 0:
            tube(m, [base, base + d * (length * 0.7) - UP * (0.1 * length)], [trunk_r * 0.24, trunk_r * 0.06], 3, PART_BARK, 0.6)
    # The top of the tree.
    for a in (0.0, 0.5 * math.pi):
        card(m, Vector((0.0, 0.84, 0.0)), UP, Vector((math.cos(a), 0.0, math.sin(a))), 0.16, 0.11, TILE_FIR, outward,
             lambda p: 1.0, 0.5)
    return m


def pine(lod, seed):
    """A pine: a bare trunk and a round crown of needle tufts on branches that sweep up."""
    rng = random.Random(seed)
    m = Model()
    radius, crown0 = 0.24, 0.47
    c, e = Vector((0.0, 0.74, 0.0)), Vector((0.25, 0.27, 0.25))

    def outward(p):
        q = p - c
        o = Vector((q.x / e.x, q.y / e.y, q.z / e.z))
        o = o.normalized() if o.length > 1.0e-5 else UP
        return (o + UP * 0.35).normalized()

    def shade(p):
        q = p - c
        d = min(Vector((q.x / e.x, q.y / e.y, q.z / e.z)).length, 1.0)
        return 0.42 + 0.58 * d ** 1.3

    trunk = [Vector((0.0, 0.0, 0.0)), Vector((0.010, 0.30, 0.0)), Vector((-0.008, 0.60, 0.010)),
             Vector((0.005, 0.80, 0.0)), Vector((0.0, 0.93, 0.0))]
    radii = [0.026, 0.022, 0.017, 0.011, 0.005]
    shades = [1.0, 1.0, 0.75, 0.6, 0.6]
    if lod == 0:
        tube(m, trunk, radii, 8, PART_BARK, shades)
    else:
        keep = (0, 2, 4) if lod == 1 else (0, 4)
        tube(m, [trunk[i] for i in keep], [radii[i] for i in keep], 5 if lod == 1 else 3, PART_BARK, [shades[i] for i in keep])

    def tuft(pos, d, s, leaf):
        base = pos - d * (s * 0.35)
        side = perp(d)
        card(m, base, d, side, s, s, TILE_PINE, outward, shade, leaf)
        card(m, base, d, rot(side, d, 0.5 * math.pi), s, s, TILE_PINE, outward, shade, leaf)

    count = 24
    for i in range(count):
        t = (i + 0.5) / count
        y = crown0 + t * (0.93 - crown0)
        az = i * GOLDEN + rng.uniform(-0.3, 0.3)
        length = radius * (0.5 + 0.5 * math.sin(math.pi * (0.12 + 0.8 * t))) * rng.uniform(0.85, 1.1)
        lift = 0.1 + 0.9 * t
        offsets = [rand_unit(rng) for _ in range(2)]
        leaf = rng.random()
        if lod == 2 or (lod == 1 and i % 2 != 0):
            continue
        h = Vector((math.cos(az), 0.0, math.sin(az)))
        p0 = Vector((0.0, y, 0.0))
        p1 = p0 + h * (length * 0.55) + UP * (length * (0.15 + 0.35 * lift))
        p2 = p0 + h * length + UP * (length * (0.3 + 0.7 * lift))
        # A branch that leaves the crown is made shorter.
        for _ in range(8):
            q = p2 - c
            if Vector((q.x / e.x, q.y / e.y, q.z / e.z)).length <= 0.74:
                break
            p1 = p0 + (p1 - p0) * 0.9
            p2 = p0 + (p2 - p0) * 0.9
        d = (h * 0.7 + UP * (0.4 + 0.6 * lift)).normalized()
        if lod == 0:
            tube(m, [p0, p1, p2], [0.007, 0.005, 0.002], 4, PART_BARK, 0.7)
            s = 0.125
            tuft(p2, d, s, leaf)
            tuft(p1, (d + h * 0.3).normalized(), s * 0.9, leaf)
            for o in offsets:
                tuft(p2 + o * (s * 0.45) - h * (s * 0.3), (d + o * 0.5).normalized(), s * 0.85, leaf)
        else:
            tube(m, [p0, p2], [0.008, 0.003], 3, PART_BARK, 0.6)
            tuft(p2, d, 0.21, leaf)
            tuft(p1, (d + h * 0.3).normalized(), 0.19, leaf)
    if lod == 2:
        blob(m, c, e, 4, 3, 0.1, seed)
    else:
        tuft(Vector((0.0, 0.92, 0.0)), UP, 0.11 if lod == 0 else 0.12, 0.5)
    return m


def hardwood(lod, trunk_top, trunk_r, c, e, seed, per_twig=11, grow=1.0):
    """A tree with a trunk, limbs, and a crown of leaf clusters."""
    rng = random.Random(seed)
    m = Model()

    def scaled(q):
        return Vector(((q.x - c.x) / e.x, (q.y - c.y) / e.y, (q.z - c.z) / e.z))

    def outward(p):
        o = scaled(p)
        o = o.normalized() if o.length > 1.0e-5 else UP
        return (o + UP * 0.3).normalized()

    def shade(p):
        return 0.40 + 0.60 * min(scaled(p).length, 1.0) ** 1.4

    def inside(q, limit):
        s = scaled(q)
        if s.length > limit:
            s = s * (limit / s.length)
        return c + Vector((s.x * e.x, s.y * e.y, s.z * e.z))

    sides = (8, 5, 3)[lod]
    tube(m, [Vector((0.0, 0.0, 0.0)), Vector((0.006, trunk_top * 0.5, 0.0)), Vector((0.0, trunk_top + 0.02, 0.0))],
         [trunk_r, trunk_r * 0.8, trunk_r * 0.6], sides, PART_BARK, [1.0, 1.0, 0.7])

    limbs = 5
    cards = []
    for k in range(limbs):
        if k == 0:
            target = c + UP * (e.y * 0.40)
        else:
            az = k * GOLDEN + rng.uniform(-0.3, 0.3)
            target = c + Vector((math.cos(az) * e.x * 0.5, rng.uniform(-0.30, 0.20) * e.y, math.sin(az) * e.z * 0.5))
        fork = Vector((0.0, trunk_top - 0.02 * k, 0.0))
        mid = fork.lerp(target, 0.5) + UP * 0.03
        if lod == 0:
            tube(m, [fork, mid, target], [trunk_r * 0.5, trunk_r * 0.32, trunk_r * 0.14], 5, PART_BARK, 0.6)
        elif lod == 1:
            tube(m, [fork, target], [trunk_r * 0.45, trunk_r * 0.14], 3, PART_BARK, 0.6)
        for j in range(4):
            r = rand_unit(rng)
            pos = inside(target + Vector((r.x * e.x, r.y * e.y, r.z * e.z)) * 0.45, 0.82)
            if lod == 0:
                tube(m, [mid.lerp(target, 0.6), pos], [trunk_r * 0.16, trunk_r * 0.05], 3, PART_BARK, 0.55)
            for n in range(per_twig):
                r = rand_unit(rng)
                q = pos + Vector((r.x * e.x, r.y * e.y, r.z * e.z)) * (0.34 * rng.random() ** 0.5)
                q = inside(c + (q - c) * 1.12, 1.0)
                size = 0.44 * 0.5 * (e.x + e.y) * rng.uniform(0.8, 1.15) * grow
                face = (outward(q) + rand_unit(rng) * 0.9).normalized()
                r = rand_unit(rng) + UP * 0.5
                d = (r - face * r.dot(face)).normalized()
                cards.append((q, d, face.cross(d).normalized(), size, rng.random()))
    if lod == 2:
        blob(m, c, e, 4, 3, 0.1, seed)
        return m
    if lod == 1:
        cards = cards[::max(len(cards) // 85, 1)]
    for q, d, side, size, leaf in cards:
        s = size * (1.0 if lod == 0 else 1.7)
        card(m, q - d * (s * 0.5), d, side, s, s, TILE_LEAF, outward, shade, leaf)
    return m


def shrub(lod, half_width, seed):
    """A shrub with many stems from one base and a dome of leaf sprays."""
    rng = random.Random(seed)
    m = Model()
    r_dome = half_width * 0.92

    def outward(p):
        o = Vector((p.x / r_dome, p.y, p.z / r_dome))
        o = o.normalized() if o.length > 1.0e-5 else UP
        return (o + UP * 0.4).normalized()

    def shade(p):
        d = min(Vector((p.x / r_dome, p.y, p.z / r_dome)).length, 1.0)
        return 0.40 + 0.60 * d ** 1.3

    stems = 9 if half_width < 1.0 else 13
    anchors = []
    for i in range(stems):
        az = i * GOLDEN + rng.uniform(-0.3, 0.3)
        h = Vector((math.cos(az), 0.0, math.sin(az)))
        ring = (0.15 if i == 0 else rng.uniform(0.35, 0.80)) * r_dome
        top = math.sqrt(max(1.0 - (ring / r_dome) ** 2, 0.05)) * 0.80 * rng.uniform(0.75, 1.0)
        tip = h * ring + UP * top
        foot = h * 0.04
        mid = foot.lerp(tip, 0.5) + UP * 0.08 - h * (0.05 * ring)
        tip2 = mid + rot(tip - mid, UP, rng.choice((-1.0, 1.0)) * rng.uniform(0.5, 0.9)) * 0.9
        seeds = [rng.random() for _ in range(4)]
        turns = [rand_unit(rng) for _ in range(12)]
        if lod == 2 or (lod == 1 and i % 2 == 1):
            continue
        if lod == 0:
            tube(m, [foot, mid, tip], [0.022, 0.014, 0.006], 3, PART_BARK, [0.8, 0.6, 0.6])
            tube(m, [mid, tip2], [0.012, 0.005], 3, PART_BARK, 0.6)
        else:
            tube(m, [foot, tip], [0.022, 0.006], 3, PART_BARK, 0.6)
        for k, a in enumerate((tip, tip2, mid.lerp(tip, 0.5), mid.lerp(tip2, 0.5))):
            anchors.append((a, seeds[k], turns[k * 3:k * 3 + 3]))
    if lod == 2:
        blob(m, Vector((0.0, 0.05, 0.0)), Vector((r_dome, 0.9, r_dome)), 5, 4, 0.15, seed)
        return m
    for a, leaf, turns in anchors:
        for n in range(3 if lod == 0 else 1):
            d = ((a - Vector((0.0, 0.15, 0.0))).normalized() + turns[n] * 0.55 + UP * 0.25).normalized()
            s = (0.40 if lod == 0 else 0.66) * (0.85 + 0.3 * leaf)
            base = a - d * (s * 0.55)
            side = rot(perp(d), d, turns[n].x * math.pi)
            card(m, base, d, side, s, s * 0.9, TILE_SHRUB, outward, shade, leaf)
    return m


def tufts(lod, seed):
    """A shrub of upright sprays."""
    rng = random.Random(seed)
    m = Model()

    def outward(p):
        return (Vector((p.x, 0.0, p.z)) * 2.0 + UP).normalized()

    def shade(p):
        return 0.5 + 0.5 * min(p.y * 1.4, 1.0)

    count = 14
    for i in range(count):
        az = i * GOLDEN
        h = Vector((math.cos(az), 0.0, math.sin(az)))
        lean = 0.05 if i == 0 else rng.uniform(0.12, 0.42)
        d = (h * lean + UP).normalized()
        foot = h * (0.0 if i == 0 else rng.uniform(0.03, 0.14))
        length = (0.97 if i == 0 else rng.uniform(0.60, 0.92)) / max(d.y, 0.5) * (1.0 if i == 0 else 0.9)
        leaf = rng.random()
        if lod == 2 or (lod == 1 and i % 3 != 0):
            continue
        grow = 1.0 if lod == 0 else 1.3
        side = rot(perp(d), d, az)
        card(m, foot, d, side, length, length * 0.5 * grow, TILE_SHRUB, outward, shade, leaf)
        card(m, foot, d, rot(side, d, 0.5 * math.pi), length, length * 0.5 * grow, TILE_SHRUB, outward, shade, leaf)
        if lod == 0:
            tube(m, [foot, foot + d * (length * 0.6)], [0.012, 0.004], 3, PART_BARK, 0.7)
    if lod == 2:
        cone(m, 0.02, 0.95, 0.5, 4, seed)
    return m


def logs(lod, count, stump, seed):
    """Dead logs that lie across each other."""
    rng = random.Random(seed)
    m = Model()
    sides, segs = (10, 6, 3)[lod], (3, 1, 1)[lod]
    for k in range(count):
        angle = rng.uniform(0.0, math.pi)
        radius = rng.uniform(0.16, 0.26)
        length = rng.uniform(1.6, 2.5)
        lift = radius + (0.34 if k >= 2 else 0.0) + (0.2 if k >= 4 else 0.0)
        offset = Vector((rng.uniform(-0.5, 0.5), 0.0, rng.uniform(-0.5, 0.5)))
        d = Vector((math.cos(angle), rng.uniform(-0.02, 0.03), math.sin(angle))).normalized()
        bend = rng.uniform(-0.10, 0.10)
        stub_at = [rng.uniform(-0.6, 0.6) for _ in range(2)]
        stub_turn = [rng.uniform(0.0, 2.0 * math.pi) for _ in range(2)]
        if lod == 1 and k >= 3:
            continue
        mid = offset + UP * min(lift, 0.95 - radius)
        across = d.cross(UP).normalized()
        pts, radii = [], []
        for j in range(segs + 1):
            f = j / segs * 2.0 - 1.0
            pts.append(mid + d * (length * f) + across * (bend * (1.0 - f * f)))
            radii.append(radius * (1.0 - 0.09 * (f + 1.0)))
        tube(m, pts, radii, sides, PART_DEAD, 1.0, cap_start=lod < 2, cap_end=lod < 2, rough=0.07 if lod == 0 else 0.0, rng=rng)
        if lod == 0:
            for f, turn in zip(stub_at, stub_turn):
                root = mid + d * (length * f) + across * (bend * (1.0 - f * f))
                out = rot(UP, d, turn * 0.5 - 0.5 * math.pi * 0.5)
                tube(m, [root, root + out * (radius * 1.5) + d * (radius * 0.4)], [radius * 0.22, radius * 0.14], 4, PART_DEAD,
                     0.9, cap_end=True)
    if stump and lod < 2:
        at = Vector((1.1, 0.0, 0.9))
        tube(m, [at, at + UP * 0.35, at + UP * 0.7], [0.34, 0.28, 0.25], sides, PART_DEAD, [0.8, 1.0, 1.0], cap_end=True,
             rough=0.1 if lod == 0 else 0.0, rng=rng)
    return m


def snag(lod, seed):
    """A dead tree with a broken top and the stubs of branches."""
    rng = random.Random(seed)
    m = Model()
    if lod == 0:
        pts = [Vector((0.0, 0.0, 0.0)), Vector((0.006, 0.22, 0.004)), Vector((0.016, 0.48, -0.004)),
               Vector((0.020, 0.72, 0.006)), Vector((0.024, 0.90, 0.008)), Vector((0.030, 1.0, 0.012))]
        radii = [0.042, 0.036, 0.030, 0.025, 0.021, 0.008]
        tube(m, pts, radii, 8, PART_DEAD, 1.0, cap_end=True, rough=0.10, rng=rng)
    elif lod == 1:
        tube(m, [Vector((0.0, 0.0, 0.0)), Vector((0.016, 0.5, 0.0)), Vector((0.028, 1.0, 0.01))], [0.042, 0.03, 0.012], 5,
             PART_DEAD, 1.0, cap_end=True)
    else:
        tube(m, [Vector((0.0, 0.0, 0.0)), Vector((0.028, 1.0, 0.01))], [0.045, 0.016], 3, PART_DEAD, 1.0, cap_end=True)
    stubs = (7, 3, 0)[lod]
    for k in range(stubs):
        y = 0.36 + 0.085 * k if lod == 0 else 0.4 + 0.2 * k
        az = k * 2.1 + (seed % 5)
        reach = (0.17 - 0.015 * k) * (1.0 if lod == 0 else 1.1)
        h = Vector((math.cos(az), 0.0, math.sin(az)))
        root = Vector((0.02 * y, y, 0.0))
        if lod == 0:
            tube(m, [root, root + h * (reach * 0.6) + UP * (reach * 0.15), root + h * reach + UP * (reach * 0.5)],
                 [0.012, 0.008, 0.003], 4, PART_DEAD, 0.9)
        else:
            tube(m, [root, root + h * reach + UP * (reach * 0.5)], [0.012, 0.004], 3, PART_DEAD, 0.9)
    return m


def build_model(index, lod):
    seed = index * 101 + 7
    if index == 0:
        return conifer(lod, 0.10, 0.16, 19, 5, 0.022, seed)
    if index == 1:
        return pine(lod, seed)
    if index == 2:
        return conifer(lod, 0.07, 0.24, 13, 5, 0.030, seed, slope=(-0.25, 0.40))
    if index == 3:
        return hardwood(lod, 0.34, 0.040, Vector((0.0, 0.66, 0.0)), Vector((0.42, 0.31, 0.42)), seed, per_twig=17, grow=1.1)
    if index == 4:
        return hardwood(lod, 0.28, 0.032, Vector((0.0, 0.64, 0.0)), Vector((0.27, 0.33, 0.27)), seed)
    if index == 5:
        return shrub(lod, 0.85, seed)
    if index == 6:
        return shrub(lod, 1.25, seed)
    if index == 7:
        return tufts(lod, seed)
    if index == 8:
        return logs(lod, 3, False, seed)
    if index == 9:
        return logs(lod, 5, True, seed)
    return snag(lod, seed)


def write_models(path):
    """Writes PlantModels.bytes and returns the models as a list per level of detail."""
    all_models = [[build_model(i, lod) for i in range(len(MODEL_NAMES))] for lod in range(3)]
    with open(path, "wb") as f:
        f.write(b"FCPM")
        f.write(struct.pack("<4i", 1, len(MODEL_NAMES), 3, VERTEX_FLOATS))
        for lod in range(3):
            for model in all_models[lod]:
                f.write(struct.pack("<i", model.vertices))
                f.write(np.asarray(model.data, dtype="<f4").tobytes())
    for i, name in enumerate(MODEL_NAMES):
        a = np.asarray(all_models[0][i].data, dtype=np.float32).reshape(-1, VERTEX_FLOATS)
        reach = float(np.max(np.hypot(a[:, 0], a[:, 2])))
        print(f"[plant_models] {name}: {all_models[0][i].triangles}, {all_models[1][i].triangles}, "
              f"{all_models[2][i].triangles} triangles. Height {a[:, 1].min():.3f} to {a[:, 1].max():.3f}, "
              f"reach {reach:.3f} (half width {HALF_WIDTH[i]}).")
    return all_models


# ---------------------------------------------------------------------------------------------------------------
# Leaf Atlas: four pictures of twigs with leaves, drawn as flat meshes and rendered from above by Cycles.
# The red channel is the brightness, the green channel is 1 on wood, and the blue channel is a random value per leaf.
# ---------------------------------------------------------------------------------------------------------------

class Sheet:
    def __init__(self):
        self.verts, self.faces, self.colors = [], [], []
        self.origin = (0.0, 0.0)
        self.z = 0.0

    def poly(self, pts, color, wood=False):
        self.z += 1.0e-6
        z = self.z * (0.1 if wood else 1.0) + (0.0 if wood else 0.01)
        start = len(self.verts)
        for x, y in pts:
            self.verts.append((self.origin[0] + 0.02 + 0.95 * x, self.origin[1] + 0.02 + 0.95 * y, z))
            self.colors.append(color)
        self.faces.append(list(range(start, start + len(pts))))

    def twig(self, a, b, w0, w1):
        dx, dy = b[0] - a[0], b[1] - a[1]
        n = math.hypot(dx, dy) or 1.0
        px, py = -dy / n, dx / n
        self.poly([(a[0] - px * w0, a[1] - py * w0), (a[0] + px * w0, a[1] + py * w0),
                   (b[0] + px * w1, b[1] + py * w1), (b[0] - px * w1, b[1] - py * w1)], (0.5, 1.0, 0.0, 1.0), wood=True)

    def needle(self, a, angle, length, width, lum, hue):
        b = (a[0] + math.cos(angle) * length, a[1] + math.sin(angle) * length)
        px, py = -math.sin(angle) * width * 0.5, math.cos(angle) * width * 0.5
        self.poly([(a[0] - px, a[1] - py), (a[0] + px, a[1] + py), (b[0] + px * 0.3, b[1] + py * 0.3),
                   (b[0] - px * 0.3, b[1] - py * 0.3)], (lum, 0.0, hue, 1.0))

    def leaf(self, a, angle, length, width, lum, hue):
        c, s = math.cos(angle), math.sin(angle)
        left, right = [], []
        steps = 6
        for k in range(steps + 1):
            t = k / steps
            w = 0.5 * width * math.sin(math.pi * t ** 0.75) ** 0.8 if 0 < k < steps else 0.0
            x, y = a[0] + c * length * t, a[1] + s * length * t
            left.append((x - s * w, y + c * w))
            right.append((x + s * w, y - c * w))
        self.poly(left + right[-2:0:-1], (lum, 0.0, hue, 1.0))
        # The midrib.
        w = width * 0.035
        self.poly([(a[0] - s * w, a[1] + c * w), (a[0] + s * w, a[1] - c * w),
                   (a[0] + c * length * 0.92, a[1] + s * length * 0.92)], (lum * 0.72, 0.0, hue, 1.0))


def needles_along(sheet, rng, a, b, spread, length, width, step):
    dx, dy = b[0] - a[0], b[1] - a[1]
    n = math.hypot(dx, dy)
    base = math.atan2(dy, dx)
    for k in range(int(n / step)):
        t = (k + rng.random()) * step / n
        p = (a[0] + dx * t, a[1] + dy * t)
        for sgn in (-1.0, 1.0):
            sheet.needle(p, base + sgn * (spread + rng.uniform(-0.2, 0.2)), length * rng.uniform(0.75, 1.1), width,
                         rng.uniform(0.34, 0.66) * (0.75 + 0.25 * t), rng.random())


def draw_fir(sheet, rng):
    twigs = [((0.5, 0.0), (0.5, 0.96), 0.011, 0.003)]
    for k in range(13):
        t = 0.07 + 0.84 * k / 12
        length = 0.40 * (1.0 - t) ** 0.75 + 0.05
        for sgn in (-1.0, 1.0):
            angle = math.radians(90.0 - sgn * (57.0 - 10.0 * t) + rng.uniform(-4.0, 4.0))
            a = (0.5, 0.96 * t + rng.uniform(-0.012, 0.012))
            b = (a[0] + math.cos(angle) * length, a[1] + math.sin(angle) * length)
            twigs.append((a, b, 0.006, 0.002))
            if length > 0.16:
                for j in range(int(length / 0.085)):
                    f = 0.25 + 0.6 * j / max(int(length / 0.085) - 1, 1)
                    for s2 in (-1.0, 1.0):
                        sub = angle + s2 * math.radians(42.0)
                        root = (a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f)
                        reach = 0.30 * length * (1.0 - 0.6 * f)
                        twigs.append((root, (root[0] + math.cos(sub) * reach, root[1] + math.sin(sub) * reach), 0.003, 0.0015))
    for a, b, w0, w1 in twigs:
        needles_along(sheet, rng, a, b, math.radians(52.0), 0.030, 0.0048, 0.0058)
    for a, b, w0, w1 in twigs:
        sheet.twig(a, b, w0, w1)


def draw_pine(sheet, rng):
    sheet.twig((0.5, 0.0), (0.5, 0.52), 0.016, 0.008)
    sheet.twig((0.5, 0.14), (0.29, 0.36), 0.009, 0.005)
    sheet.twig((0.5, 0.20), (0.71, 0.40), 0.009, 0.005)
    for center, reach in (((0.5, 0.54), 0.40), ((0.29, 0.37), 0.25), ((0.71, 0.41), 0.25)):
        for _ in range(130):
            angle = math.radians(90.0 + max(min(rng.gauss(0.0, 62.0), 150.0), -150.0))
            sheet.needle((center[0] + rng.uniform(-0.01, 0.01), center[1] + rng.uniform(-0.05, 0.01)), angle,
                         reach * rng.uniform(0.7, 1.0), 0.0075, rng.uniform(0.34, 0.66), rng.random())


def draw_leaf(sheet, rng):
    twigs = [((0.5, 0.0), (0.5, 0.78), 0.010, 0.004)]
    for k, t in enumerate((0.16, 0.30, 0.44, 0.58, 0.70)):
        sgn = -1.0 if k % 2 else 1.0
        angle = math.radians(90.0 - sgn * rng.uniform(42.0, 55.0))
        length = 0.30 * (1.0 - 0.45 * t)
        a = (0.5, 0.78 * t)
        twigs.append((a, (a[0] + math.cos(angle) * length, a[1] + math.sin(angle) * length), 0.006, 0.003))
    for a, b, w0, w1 in twigs:
        sheet.twig(a, b, w0, w1)
    for a, b, w0, w1 in twigs:
        dx, dy = b[0] - a[0], b[1] - a[1]
        n = math.hypot(dx, dy)
        base = math.atan2(dy, dx)
        count = max(int(n / 0.06), 2)
        for k in range(count + 1):
            t = min(0.15 + 0.85 * k / count, 1.0)
            p = (a[0] + dx * t, a[1] + dy * t)
            turn = 0.0 if k == count else (1.0 if k % 2 else -1.0) * math.radians(rng.uniform(38.0, 70.0))
            length = 0.145 * rng.uniform(0.8, 1.12)
            sheet.leaf(p, base + turn, length, length * rng.uniform(0.48, 0.60), rng.uniform(0.36, 0.64), rng.random())


def draw_shrub(sheet, rng):
    def branch(a, angle, length, depth):
        b = (a[0] + math.cos(angle) * length, a[1] + math.sin(angle) * length)
        sheet.twig(a, b, 0.003 + 0.0025 * depth, 0.002 + 0.002 * depth)
        count = int(length / 0.026)
        for k in range(count + 1):
            t = (k + 0.5) / (count + 1)
            if depth >= 2 and t < 0.45:
                continue
            p = (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
            turn = (1.0 if k % 2 else -1.0) * math.radians(rng.uniform(35.0, 75.0))
            size = 0.058 * rng.uniform(0.75, 1.15)
            sheet.leaf(p, angle + turn, size, size * 0.5, rng.uniform(0.36, 0.64), rng.random())
        if depth > 0:
            for k in range(3 if depth > 1 else 2):
                f = rng.uniform(0.35, 0.95) if k else 1.0
                root = (a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f)
                sgn = (1.0 if k % 2 else -1.0) if k else rng.choice((-0.3, 0.3))
                branch(root, angle + sgn * math.radians(rng.uniform(24.0, 44.0)), length * 0.66, depth - 1)

    branch((0.5, 0.0), math.radians(90.0), 0.33, 3)


def render_atlas(path, work):
    """Renders the four leaf pictures and writes LeafAtlas.png."""
    sheet = Sheet()
    for tile, draw in ((TILE_FIR, draw_fir), (TILE_PINE, draw_pine), (TILE_LEAF, draw_leaf), (TILE_SHRUB, draw_shrub)):
        sheet.origin = (float(tile % 2), float(tile // 2))
        first = len(sheet.verts)
        draw(sheet, random.Random(1000 + tile))
        xs = [v[0] - sheet.origin[0] for v in sheet.verts[first:]]
        ys = [v[1] - sheet.origin[1] for v in sheet.verts[first:]]
        print(f"[plant_models] atlas tile {tile}: x {min(xs):.3f} to {max(xs):.3f}, y {min(ys):.3f} to {max(ys):.3f}, "
              f"{len(sheet.verts) - first} vertices.")
        if min(xs) < 0.008 or max(xs) > 0.992 or min(ys) < 0.008 or max(ys) > 0.992:
            raise RuntimeError(f"The picture of atlas tile {tile} leaves the tile.")

    scene = bpy.data.scenes.new("LeafAtlas")
    bpy.context.window.scene = scene
    mesh = bpy.data.meshes.new("LeafSheet")
    mesh.from_pydata(sheet.verts, [], sheet.faces)
    attribute = mesh.color_attributes.new("Col", "FLOAT_COLOR", "POINT")
    attribute.data.foreach_set("color", np.asarray(sheet.colors, dtype=np.float32).ravel())
    material = bpy.data.materials.new("LeafSheet")
    material.use_nodes = True
    nodes, links = material.node_tree.nodes, material.node_tree.links
    nodes.clear()
    color = nodes.new("ShaderNodeAttribute")
    color.attribute_name = "Col"
    emission = nodes.new("ShaderNodeEmission")
    output = nodes.new("ShaderNodeOutputMaterial")
    links.new(color.outputs["Color"], emission.inputs["Color"])
    links.new(emission.outputs["Emission"], output.inputs["Surface"])
    mesh.materials.append(material)
    scene.collection.objects.link(bpy.data.objects.new("LeafSheet", mesh))

    camera = bpy.data.cameras.new("AtlasCamera")
    camera.type = "ORTHO"
    camera.ortho_scale = 2.0
    camera_object = bpy.data.objects.new("AtlasCamera", camera)
    camera_object.location = (1.0, 1.0, 5.0)
    scene.collection.objects.link(camera_object)
    scene.camera = camera_object
    scene.world = bpy.data.worlds.new("Black")
    scene.world.color = (0.0, 0.0, 0.0)

    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 24
    scene.cycles.use_denoising = False
    scene.cycles.max_bounces = 0
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = ATLAS_PIXELS
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "OPEN_EXR"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "32"
    exr = os.path.join(work, "leaf_atlas.exr")
    scene.render.filepath = exr
    bpy.ops.render.render(write_still=True, scene=scene.name)

    image = bpy.data.images.load(exr)
    image.colorspace_settings.name = "Non-Color"
    pixels = np.empty(ATLAS_PIXELS * ATLAS_PIXELS * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    pixels = pixels.reshape(ATLAS_PIXELS, ATLAS_PIXELS, 4)
    alpha = pixels[:, :, 3].copy()
    rgb = pixels[:, :, :3] / np.maximum(alpha, 1.0e-4)[:, :, None]

    # Spread the colors into the empty pixels, so that the edge of a leaf has no dark fringe.
    filled = alpha > 0.5
    rgb[~filled] = 0.0
    for _ in range(24):
        total = np.zeros_like(rgb)
        count = np.zeros(alpha.shape, dtype=np.float32)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
            total += np.roll(rgb * filled[:, :, None], (dy, dx), axis=(0, 1))
            count += np.roll(filled.astype(np.float32), (dy, dx), axis=(0, 1))
        grow = (~filled) & (count > 0.0)
        rgb[grow] = total[grow] / count[grow][:, None]
        filled = filled | grow
    # The leaves of each tile have a mean brightness of 0.5. The shader multiplies the brightness by 2.
    half = ATLAS_PIXELS // 2
    for row in range(2):
        for col in range(2):
            part = (slice(row * half, (row + 1) * half), slice(col * half, (col + 1) * half))
            weight = alpha[part] * (rgb[part][:, :, 1] < 0.5)
            mean = float((rgb[part][:, :, 0] * weight).sum() / max(weight.sum(), 1.0))
            coverage = float(alpha[part].mean())
            print(f"[plant_models] atlas tile {row * 2 + col}: mean brightness {mean:.3f}, coverage {coverage:.3f}.")
            rgb[part][:, :, 0] *= 0.5 / max(mean, 1.0e-3)
    out = np.clip(np.dstack([rgb, alpha]), 0.0, 1.0).astype(np.float32)
    result = bpy.data.images.new("LeafAtlas", ATLAS_PIXELS, ATLAS_PIXELS, alpha=True, is_data=True)
    result.alpha_mode = "STRAIGHT"
    result.pixels.foreach_set(out.ravel())
    result.filepath_raw = path
    result.file_format = "PNG"
    result.save()
    return result


# ---------------------------------------------------------------------------------------------------------------
# Preview: the Plant Models as Blender objects, with one picture per level of detail.
# ---------------------------------------------------------------------------------------------------------------

def preview(folder, all_models, atlas_path):
    scene = bpy.data.scenes.new("PlantModels")
    bpy.context.window.scene = scene
    atlas = bpy.data.images.load(atlas_path)
    atlas.colorspace_settings.name = "Non-Color"
    atlas.alpha_mode = "STRAIGHT"
    # A view of the Leaf Atlas on a gray card, with the colors that the shader gives it.
    px = np.empty(ATLAS_PIXELS * ATLAS_PIXELS * 4, dtype=np.float32)
    atlas.pixels.foreach_get(px)
    px = px.reshape(ATLAS_PIXELS, ATLAS_PIXELS, 4)
    leaf = px[:, :, 0:1] * 2.0 * np.array([0.25, 0.42, 0.14]) * (0.8 + 0.4 * px[:, :, 2:3])
    wood = px[:, :, 0:1] * 2.0 * np.array([0.30, 0.21, 0.14])
    shown = leaf * (1.0 - px[:, :, 1:2]) + wood * px[:, :, 1:2]
    shown = shown * px[:, :, 3:4] + 0.75 * (1.0 - px[:, :, 3:4])
    view = bpy.data.images.new("LeafAtlasView", ATLAS_PIXELS, ATLAS_PIXELS, alpha=False, is_data=True)
    view.pixels.foreach_set(np.dstack([shown, np.ones((ATLAS_PIXELS, ATLAS_PIXELS, 1))]).astype(np.float32).ravel())
    view.filepath_raw = os.path.join(folder, "leaf_atlas_view.png")
    view.file_format = "PNG"
    view.save()

    def material(name, kind):
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = nodes["Principled BSDF"]
        bsdf.inputs["Roughness"].default_value = 0.9
        if kind == "leaf":
            tex = nodes.new("ShaderNodeTexImage")
            tex.image = atlas
            split = nodes.new("ShaderNodeSeparateColor")
            mix = nodes.new("ShaderNodeMix")
            mix.data_type = "RGBA"
            mix.inputs["A"].default_value = (0.0, 0.0, 0.0, 1.0)
            mix.inputs["B"].default_value = (0.16, 0.30, 0.08, 1.0)
            links.new(tex.outputs["Color"], split.inputs["Color"])
            links.new(split.outputs["Red"], mix.inputs["Factor"])
            links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
            links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
        else:
            bsdf.inputs["Base Color"].default_value = {"shell": (0.08, 0.15, 0.04, 1.0), "bark": (0.10, 0.07, 0.05, 1.0),
                                                       "dead": (0.23, 0.20, 0.17, 1.0)}[kind]
        return mat

    materials = {k: material(k, k) for k in ("leaf", "shell", "bark", "dead")}
    # The wide Plant Models are drawn smaller, so that each Plant Model has the same room in the picture.
    scales = [min(1.0, 0.55 / w) for w in HALF_WIDTH]
    centers = [0.65 + 1.3 * i for i in range(len(MODEL_NAMES))]
    x = 1.3 * len(MODEL_NAMES)
    for lod in range(3):
        collection = bpy.data.collections.new(f"LOD{lod}")
        scene.collection.children.link(collection)
        for i, name in enumerate(MODEL_NAMES):
            a = np.asarray(all_models[lod][i].data, dtype=np.float32).reshape(-1, 3, VERTEX_FLOATS)
            groups = {"leaf": (a[:, 0, 3] == 1.0) & (a[:, 0, 8] >= 0.0), "shell": (a[:, 0, 3] == 1.0) & (a[:, 0, 8] < 0.0),
                      "bark": a[:, 0, 3] == 0.0, "dead": a[:, 0, 3] == 2.0}
            for kind, mask in groups.items():
                t = a[mask]
                if len(t) == 0:
                    continue
                flat = t.reshape(-1, VERTEX_FLOATS)
                # Unity (x, y, z) to Blender (x, z, y).
                verts = [(float(v[0]) * scales[i] + centers[i], float(v[2]) * scales[i] + lod * 4.0, float(v[1]) * scales[i]) for v in flat]
                mesh = bpy.data.meshes.new(f"{name}_LOD{lod}_{kind}")
                mesh.from_pydata(verts, [], [(k * 3, k * 3 + 1, k * 3 + 2) for k in range(len(t))])
                uv = mesh.uv_layers.new(name="UVMap")
                uv.data.foreach_set("uv", flat[:, 8:10].astype(np.float32).ravel())
                mesh.materials.append(materials[kind])
                collection.objects.link(bpy.data.objects.new(mesh.name, mesh))

    sun = bpy.data.lights.new("Sun", "SUN")
    sun.energy = 4.0
    sun_object = bpy.data.objects.new("Sun", sun)
    sun_object.rotation_euler = (math.radians(50.0), 0.0, math.radians(35.0))
    scene.collection.objects.link(sun_object)
    scene.world = bpy.data.worlds.new("Sky")
    scene.world.color = (0.55, 0.68, 0.85)
    camera = bpy.data.cameras.new("PreviewCamera")
    camera.type = "ORTHO"
    camera_object = bpy.data.objects.new("PreviewCamera", camera)
    scene.collection.objects.link(camera_object)
    scene.camera = camera_object
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 24
    scene.cycles.transparent_max_bounces = 48
    scene.render.resolution_x, scene.render.resolution_y = 2000, 640
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "Standard"
    camera.ortho_scale = 1.3 * 4
    for lod in range(3):
        for other in range(3):
            bpy.data.collections[f"LOD{other}"].hide_render = other != lod
        for group in range(3):
            camera_object.location = (1.3 * (2 + 4 * group) - (0.65 if group == 2 else 0.0), lod * 4.0 - 20.0,
                                      0.55 + 20.0 * math.tan(math.radians(12.0)))
            camera_object.rotation_euler = (math.radians(90.0 - 12.0), 0.0, 0.0)
            scene.render.filepath = os.path.join(folder, f"plant_models_lod{lod}_{group}.png")
            bpy.ops.render.render(write_still=True, scene=scene.name)
    for other in range(3):
        bpy.data.collections[f"LOD{other}"].hide_render = False
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, "PlantModels.blend"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="The folder for PlantModels.bytes and LeafAtlas.png")
    parser.add_argument("--preview", help="A folder for the preview pictures and PlantModels.blend")
    parser.add_argument("--no-atlas", action="store_true", help="Keep the LeafAtlas.png of the output folder")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    work = os.path.abspath(args.preview) if args.preview else bpy.app.tempdir
    os.makedirs(work, exist_ok=True)
    all_models = write_models(os.path.join(out, "PlantModels.bytes"))
    atlas_path = os.path.join(out, "LeafAtlas.png")
    if not args.no_atlas:
        render_atlas(atlas_path, work)
    if args.preview:
        preview(work, all_models, atlas_path)
    print("[plant_models] done")


main()
