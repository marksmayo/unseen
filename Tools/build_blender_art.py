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

# Space the editable models apart for convenient inspection; exports stay normalized.
wrap.location.x = 2
water.location.x = 4
culm.location.x = 6
frond.location.x = 8
bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE / 'UnseenDetails.blend'))
