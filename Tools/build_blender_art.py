"""Run with blender --background --python Tools/build_blender_art.py.

Editable source lives outside Assets so Unity does not require Blender to import.
Runtime mesh payloads contain Unity-space positions, normals, UVs and triangles.
"""
import bpy
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'Assets/Unseen/Resources/BlenderArt'
SOURCE = ROOT / 'ArtSource'
OUT.mkdir(parents=True, exist_ok=True)
SOURCE.mkdir(exist_ok=True)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)


def mesh(name, vertices, faces):
    data = bpy.data.meshes.new(name)
    data.from_pydata(vertices, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    return obj


def export(obj):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.uv.smart_project(island_margin=0.02)
    bpy.ops.object.mode_set(mode='OBJECT')
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    data = evaluated.to_mesh()
    data.calc_loop_triangles()
    positions, normals, uvs, triangles = [], [], [], []
    # Per-corner vertices preserve hard edges and UV seams. Unity uses Y-up.
    for tri in data.loop_triangles:
        for index in reversed(tri.loops):
            loop = data.loops[index]
            p = data.vertices[loop.vertex_index].co
            n = data.corner_normals[index].vector
            uv = data.uv_layers.active.data[index].uv
            positions.append(dict(x=p.x, y=p.z, z=p.y))
            normals.append(dict(x=n.x, y=n.z, z=n.y))
            uvs.append(dict(x=uv.x, y=uv.y))
            triangles.append(len(triangles))
    assert all(math.isfinite(v) for p in positions for v in p.values())
    payload = dict(vertices=positions, normals=normals, uv=uvs, triangles=triangles)
    (OUT / (obj.name + '.bytes')).write_text(json.dumps(payload, separators=(',', ':')))
    print(f'ART {obj.name}: {len(triangles)//3} triangles')
    evaluated.to_mesh_clear()


# Layered ceramic crest with a swept, pointed silhouette, in a unit bounding box.
vertices, faces = [], []
profile = [(-.5, 0), (.5, 0), (.47, .26), (.30, .40), (.21, .63),
           (.12, 1), (-.04, .89), (-.13, .60), (-.33, .45), (-.5, .25)]
for depth in [-.5, .5]:
    vertices += [(x, depth, z - .5) for x, z in profile]
n = len(profile)
faces += [tuple(reversed(range(n))), tuple(range(n, 2*n))]
faces += [(i, (i+1)%n, (i+1)%n+n, i+n) for i in range(n)]
crest = mesh('RoofCrest', vertices, faces)
bevel = crest.modifiers.new('Worn ceramic edges', 'BEVEL')
bevel.width = .035
bevel.segments = 3
export(crest)

# Folded, hollow cloth band: nominal diameter one and height one, based at zero.
vertices, faces = [], []
segments, rings = 24, 9
for inner in [False, True]:
    for j in range(rings):
        h = j / (rings-1)
        for i in range(segments):
            a = i * math.tau / segments
            fold = .025 * math.sin(h * math.tau * 3 + a * 2)
            r = (.43 if inner else .48 + fold)
            vertices.append((r * math.cos(a), r * math.sin(a), h))
for shell in range(2):
    base = shell * segments * rings
    for j in range(rings-1):
        for i in range(segments):
            a = base+j*segments+i
            b = base+j*segments+(i+1)%segments
            faces.append((a,b,b+segments,a+segments))
for j in [0,rings-1]:
    for i in range(segments):
        a=j*segments+i
        b=j*segments+(i+1)%segments
        faces.append((a,b,b+segments*rings,a+segments*rings))
wrap = mesh('ClothWrap', vertices, faces)
for p in wrap.data.polygons:
    p.use_smooth = True
export(wrap)

# A regular grid gives the water shader enough vertices to move its silhouette.
vertices, faces = [], []
resolution = 64
for j in range(resolution+1):
    for i in range(resolution+1):
        vertices.append((i/resolution-.5,j/resolution-.5,.5))
for j in range(resolution):
    for i in range(resolution):
        a=j*(resolution+1)+i
        faces.append((a,a+1,a+resolution+2,a+resolution+1))
water = mesh('WaterSurface', vertices, faces)
export(water)

# Jointed bamboo with raised node collars and a tapering, gently curved stem.
vertices, faces = [], []
sides = 8
levels = [(0, .5)]
for node in range(1, 13):
    h = node / 13
    taper = .5 * (1 - h * .28)
    levels += [(h-.006, taper), (h-.003, taper*1.14),
               (h+.003, taper*1.14), (h+.006, taper)]
levels.append((1, .31))
for h, radius in levels:
    for i in range(sides):
        a = i * math.tau / sides
        vertices.append((math.cos(a)*radius + .32*h*h, math.sin(a)*radius, h))
for j in range(len(levels)-1):
    for i in range(sides):
        a=j*sides+i
        b=j*sides+(i+1)%sides
        faces.append((a,b,b+sides,a+sides))
faces += [tuple(reversed(range(sides))), tuple(range((len(levels)-1)*sides,len(levels)*sides))]
culm = mesh('BambooCulm', vertices, faces)
for p in culm.data.polygons:
    p.use_smooth = True
export(culm)

# Three fine twigs and individual lance-shaped leaves, with a raised midrib.
# Leaves have volume, so they stay visible from below without alpha cards.
vertices, faces = [], []
def leaf(start, tip, width):
    from mathutils import Vector
    a, b = Vector(start), Vector(tip)
    direction = b-a
    side = Vector((-direction.y, direction.x, 0)).normalized() * width
    middle = a + direction*.43
    base = len(vertices)
    vertices.extend([tuple(a),tuple(middle+side),tuple(b),tuple(middle-side),
                     tuple(middle+Vector((0,0,width*.26))),tuple(middle-Vector((0,0,.009)))])
    faces.extend(tuple(base+i for i in f) for f in
                 [(0,1,4),(1,2,4),(2,3,4),(3,0,4),(1,0,5),(2,1,5),(3,2,5),(0,3,5)])
for branch in range(3):
    angle = (branch-1)*.6
    direction = (math.cos(angle), math.sin(angle))
    for j in range(7):
        t = .14+j*.12
        x, y = direction[0]*t, direction[1]*t
        z = .32*t-.20*t*t
        for sign in [-1, 1]:
            length = .25 + .12*math.sin(j*1.7+branch)
            leaf((x,y,z), (x+length*.65, y+sign*length, z-.10), .037)
    # A narrow closed twig in the same mesh/material.
    leaf((0,0,0), (direction[0]*1.08,direction[1]*1.08,.1), .008)
frond = mesh('BambooFrond', vertices, faces)
for p in frond.data.polygons:
    p.use_smooth = True
export(frond)

# Katana, standing on the butt of its grip: z from 0 to 1, tip at the top.
#
# Built upright rather than along a horizontal axis because that is how it is reasoned about when
# attached - grip at the origin, blade away from the hand - so the transform that puts it in a fist
# or across a back is a rotation of something already the right way up.
#
# The cross-section is the point of a katana and the reason this is not a box. A ridge (shinogi)
# runs the length on both faces, the back (mune) is flat, and the edge is a single line: light
# catches the ridge and the edge differently as it turns, which is what makes it read as a blade
# rather than a painted plank. The curve (sori) grows toward the tip rather than being a constant
# arc, which is what a real one does and what stops it looking like a banana.
vertices, faces = [], []


def ring(centre_z, half_width, thickness, bend, closed_tip=False):
    """One cross-section of the blade. Six points: edge, two shinogi, two upper, mune."""
    x = bend
    if closed_tip:
        return [(x, 0, centre_z)] * 6
    return [
        (x, -half_width, centre_z),                       # ha - the edge
        (x - thickness, -half_width * 0.25, centre_z),    # shinogi, near face
        (x - thickness * 0.72, half_width * 0.88, centre_z),
        (x, half_width, centre_z),                        # mune - the back
        (x + thickness * 0.72, half_width * 0.88, centre_z),
        (x + thickness, -half_width * 0.25, centre_z),    # shinogi, far face
    ]


GRIP_TOP, GUARD_TOP = 0.26, 0.30
blade_rings = 14
start = len(vertices)

for i in range(blade_rings):
    t = i / (blade_rings - 1)
    z = GUARD_TOP + t * (1.0 - GUARD_TOP)

    # Sori: almost straight at the guard, gathering toward the tip.
    bend = -0.055 * t * t

    # The kissaki - the last eighth narrows hard into the point.
    taper = 1.0 - 0.55 * max(0.0, (t - 0.86) / 0.14) ** 1.5
    vertices += ring(z, 0.052 * taper, 0.011 * taper, bend, closed_tip=(i == blade_rings - 1))

for i in range(blade_rings - 1):
    a, b = start + i * 6, start + (i + 1) * 6
    faces += [(a + k, a + (k + 1) % 6, b + (k + 1) % 6, b + k) for k in range(6)]

# Tsuba: a plate rather than a disc, because a round guard on a low-poly sword reads as a washer.
start = len(vertices)
for z in (GRIP_TOP, GUARD_TOP):
    for i in range(12):
        a = i * math.tau / 12
        vertices.append((0.052 * math.cos(a), 0.078 * math.sin(a), z))
faces.append(tuple(range(start, start + 12)))
faces.append(tuple(reversed(range(start + 12, start + 24))))
faces += [(start + i, start + (i + 1) % 12, start + 12 + (i + 1) % 12, start + 12 + i)
          for i in range(12)]

# Tsuka: the grip, faceted and slightly oval so it has an obvious orientation in the hand.
start = len(vertices)
grip_rings = 5
for j in range(grip_rings):
    z = j / (grip_rings - 1) * GRIP_TOP
    swell = 1.0 + 0.10 * math.sin(j / (grip_rings - 1) * math.pi)
    for i in range(8):
        a = i * math.tau / 8
        vertices.append((0.019 * swell * math.cos(a), 0.027 * swell * math.sin(a), z))
for j in range(grip_rings - 1):
    a, b = start + j * 8, start + (j + 1) * 8
    faces += [(a + k, a + (k + 1) % 8, b + (k + 1) % 8, b + k) for k in range(8)]
faces.append(tuple(range(start, start + 8)))

katana = mesh('Katana', vertices, faces)
export(katana)

# Space the editable models apart for convenient inspection; exports stay normalized.
wrap.location.x = 2
water.location.x = 4
culm.location.x = 6
frond.location.x = 8
katana.location.x = 10
bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE / 'UnseenDetails.blend'))
