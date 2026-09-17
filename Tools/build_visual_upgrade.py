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



# Reusable, metre-scaled art kit. Authored geometry only; Unity owns gameplay collision.
import random
random.seed(91)
bpy.context.preferences.filepaths.save_version=0
assets=[]
def finish(o):
    export(o);assets.append(o);return o

def join(name,objects):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:o.select_set(True)
    bpy.context.view_layer.objects.active=objects[0];bpy.ops.object.join()
    o=bpy.context.object;o.name=name
    bpy.context.scene.cursor.location=(0,0,0);bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    bpy.ops.object.transform_apply(location=False,rotation=True,scale=True)
    return finish(o)

def box(loc,scale,bevel=.02):
    bpy.ops.mesh.primitive_cube_add(size=1,location=loc);o=bpy.context.object;o.scale=scale
    bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    if bevel:
        m=o.modifiers.new('Rounded crafted edges','BEVEL');m.width=bevel;m.segments=2
        bpy.context.view_layer.objects.active=o;bpy.ops.object.modifier_apply(modifier=m.name)
    return o

def lathe(name,profile,sides=16):
    verts=[];faces=[]
    for z,r in profile:
        for i in range(sides):
            a=i*math.tau/sides;verts.append((r*math.cos(a),r*math.sin(a),z))
    for j in range(len(profile)-1):
        for i in range(sides):a=j*sides+i;b=j*sides+(i+1)%sides;faces.append((a,b,b+sides,a+sides))
    o=mesh(name,verts,faces)
    for p in o.data.polygons:p.use_smooth=True
    return finish(o)

lathe('UpgradeStorageJar',[(0,.18),(.04,.24),(.15,.29),(.42,.28),(.56,.20),(.59,.16),(.64,.16),(.64,.12),(.58,.12),(.46,.17)],20)
lathe('UpgradeBasket',[(0,.19),(.035,.21),(.12,.23),(.38,.28),(.42,.28),(.44,.30),(.46,.30),(.46,.25),(.42,.25),(.10,.19),(.07,0)],20)
# A coarse weave in the silhouette is cheap enough to retain at distance.
parts=[]
for i in range(9):
    x=(i-4)*.09
    parts.append(box((x,0,.08),(.07,.66,.15),.009))
for y in [-.23,.23]:parts.append(box((0,y,.015),(.85,.07,.06),.008))
join('UpgradeTimberStack',parts)
# Folded satchel and overlapping segmented shin/forearm armour.
join('UpgradePouch',[box((0,0,.11),(.19,.09,.22),.035),box((0,-.055,.18),(.20,.03,.10),.015),box((0,-.077,.14),(.045,.012,.06),.004)])
parts=[]
for i in range(3):parts.append(box((0,-.012*i,.06+i*.065),(.115-i*.012,.045,.08),.012))
join('UpgradeBracer',parts)
# Split-toe shoe cap with sole and instep, fitted to the existing foot bone.
join('UpgradeTabi',[box((-.036,-.07,.037),(.065,.23,.065),.025),box((.027,-.06,.039),(.050,.21,.067),.020),box((0,.01,.075),(.13,.14,.13),.025)])
# Curved corbel: normalized for attachment beneath the existing eave.
verts=[];faces=[]
profile=[(-.5,0),(.5,0),(.5,.2),(.32,.24),(.18,.36),(.12,.65),(-.15,.75),(-.5,.75)]
for y in [-.16,.16]:verts += [(x,y,z) for x,z in profile]
n=len(profile);faces=[tuple(reversed(range(n))),tuple(range(n,n*2))]+[(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
o=mesh('UpgradeCorbel',verts,faces);b=o.modifiers.new('Worn corbel','BEVEL');b.width=.025;b.segments=2;finish(o)
# Four-metre tile edge, corrugated and upturned at its front lip.
verts=[];faces=[]
for tile in range(8):
    base=len(verts)
    for j in range(5):
        y=j*.13
        for i in range(9):
            x=tile*.5+i*.0625-2
            z=.045*math.cos(i/8*math.tau)+.06*(1-j/4)**3
            verts.append((x,y,z))
    for j in range(4):
        for i in range(8):a=base+j*9+i;faces.append((a,a+1,a+10,a+9))
o=mesh('UpgradeTileEdge',verts,faces);m=o.modifiers.new('Ceramic thickness','SOLIDIFY');m.thickness=.035;finish(o)
# Framed shoji insert: dimensions 1 x 1, surface lies in X/Z.
parts=[box((x,0,.5),(.045,.065,1.05),.006) for x in [-.5,.5]]
parts += [box((0,0,z),(1.04,.065,.045),.006) for z in [0,1]]
parts += [box((x,-.015,.5),(.023,.045,.96),.003) for x in [-.25,0,.25]]
parts += [box((0,-.015,z),(.98,.045,.023),.003) for z in [.25,.5,.75]]
join('UpgradeShojiFrame',parts)
# Noren cloth: a tessellated hanging panel, pinned along the top in Unity's wind shader.
verts=[];faces=[]
for j in range(9):
    z=j/8
    for i in range(9):
        x=i/8-.5;verts.append((x,.035*math.sin(x*math.pi*6)*(1-z),z))
for j in range(8):
    for i in range(8):a=j*9+i;faces.append((a,a+1,a+10,a+9))
finish(mesh('UpgradeBanner',verts,faces))
# Low-relief leaf litter, never tall enough to resemble traversable cover.
verts=[];faces=[]
for i in range(24):
    x=random.uniform(-.55,.55);y=random.uniform(-.4,.4);a=random.random()*math.tau
    base=len(verts);dx=.055*math.cos(a);dy=.055*math.sin(a)
    verts.extend([(x-dx,y-dy,.005),(x-dy*.45,y+dx*.45,.022),(x+dx,y+dy,.008),(x+dy*.45,y-dx*.45,.022)])
    faces.append((base,base+1,base+2,base+3))
finish(mesh('UpgradeLeafLitter',verts,faces))
# Blender-authored presentation envelopes, editable as keyframes on control objects.
controls={'Landing':[(0,0),(.10,1),(.30,.28),(.58,0)],'Breathing':[(0,0),(.8,1),(1.6,0),(2.4,-.45),(3.2,0)],'Attack':[(0,0),(.18,-.35),(.30,1),(.44,.30),(.65,0)]}
payload={}
for name,keys in controls.items():
    bpy.ops.object.empty_add();o=bpy.context.object;o.name='Motion_'+name
    for seconds,value in keys:o.location.z=value;o.keyframe_insert('location',frame=1+seconds*60)
    values=[]
    for i in range(65):
        frame=1+keys[-1][0]*60*i/64
        bpy.context.scene.frame_set(int(frame),subframe=frame-int(frame));values.append(o.location.z)
    payload[name.lower()]=values
(OUT/'UpgradeMotion.bytes').write_text(json.dumps(payload,separators=(',',':')))
# Editable contact sheet arrangement and a simple studio preview.
for i,o in enumerate(assets):o.location=(i%4*2.2,i//4*2.1,0)
s=bpy.context.scene;s.render.engine='BLENDER_EEVEE';s.world.color=(.12,.12,.12)
bpy.ops.object.camera_add(location=(10,-12,13));s.camera=bpy.context.object
from mathutils import Vector
s.camera.rotation_euler=(Vector((3,2,0))-s.camera.location).to_track_quat('-Z','Y').to_euler();s.camera.data.type='ORTHO';s.camera.data.ortho_scale=12
bpy.ops.object.light_add(type='AREA',location=(3,0,9));bpy.context.object.data.energy=2000;bpy.context.object.data.shape='DISK';bpy.context.object.data.size=8
s.render.resolution_x=1280;s.render.resolution_y=900;s.render.resolution_percentage=100
bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE/'VisualUpgrade/VisualUpgradeKit.blend'))
s.render.filepath=str(SOURCE/'VisualUpgrade/kit-preview.png');bpy.ops.render.render(write_still=True)
