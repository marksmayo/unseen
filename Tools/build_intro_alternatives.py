"""Render two alternative opening films in Blender 5.2.
Usage: blender -b --python Tools/build_intro_alternatives.py -- --variant eclipse|blade [--preview]
Outputs editable .blend, MP4 and optional preview under ArtSource/Intro/<name>.
"""
import bpy, math, random, sys
from pathlib import Path
from mathutils import Vector
R = Path(__file__).resolve().parents[1]
random.seed(17)
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
s = bpy.context.scene
s.render.engine = 'BLENDER_EEVEE'
if hasattr(s.eevee, 'taa_render_samples'): s.eevee.taa_render_samples = 32
s.render.resolution_x = 1280
s.render.resolution_y = 720
s.render.resolution_percentage = 100
s.render.fps = 24
s.frame_start, s.frame_end = 1, 192
s.world.use_nodes = True
s.world.node_tree.nodes['Background'].inputs['Color'].default_value = (.006, .014, .035, 1)
s.world.node_tree.nodes['Background'].inputs['Strength'].default_value = .3
s.view_settings.view_transform = 'AgX'

def material(name, color, metal=0, rough=.45, glow=0):
    m = bpy.data.materials.new(name); m.diffuse_color=(*color,1); m.use_nodes=True
    p=m.node_tree.nodes.get('Principled BSDF')
    p.inputs['Base Color'].default_value=(*color,1)
    p.inputs['Metallic'].default_value=metal; p.inputs['Roughness'].default_value=rough
    p.inputs['Emission Color'].default_value=(*color,1); p.inputs['Emission Strength'].default_value=glow
    return m
ink=material('Midnight lacquer',(.012,.023,.039),.65,.28)
stone=material('Wet slate',(.035,.055,.075),.4,.3)
gold=material('Old gold',(.48,.25,.065),.8,.24)
steel=material('Cold forged steel',(.19,.28,.35),.95,.2)
light=material('Lantern silk', (1,.34,.065),0,.5,6)
moon=material('Ivory moon',(.52,.72,.85),0,.8,2)
type_mat=material('Title silver',(.76,.85,.87),.35,.32, .65)
red=material('Vermilion seal',(.46,.025,.014),.25,.5,.5)

def cube(name, loc, scale, mat, bevel=0):
    bpy.ops.mesh.primitive_cube_add(size=1,location=loc); o=bpy.context.object; o.name=name
    o.scale=scale; bpy.ops.object.transform_apply(location=False,rotation=False,scale=True)
    o.data.materials.append(mat)
    if bevel:
        b=o.modifiers.new('Soft crafted edges','BEVEL'); b.width=bevel; b.segments=3
        o.modifiers.new('Weighted normals','WEIGHTED_NORMAL')
    return o

def sphere(name, loc, radius, mat):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48,ring_count=24,radius=radius,location=loc)
    o=bpy.context.object; o.name=name; o.data.materials.append(mat)
    for p in o.data.polygons:p.use_smooth=True
    return o

def area(name,loc,color,power,size,target):
    bpy.ops.object.light_add(type='AREA',location=loc); o=bpy.context.object; o.name=name
    o.data.energy=power; o.data.color=color; o.data.shape='DISK'; o.data.size=size
    o.rotation_euler=(Vector(target)-o.location).to_track_quat('-Z','Y').to_euler()


# Two independent art directions. Usage: -- --variant eclipse|blade [--preview]
variant = sys.argv[sys.argv.index('--variant')+1] if '--variant' in sys.argv else 'eclipse'
assert variant in ('eclipse','blade')
name = 'CrimsonEclipse' if variant == 'eclipse' else 'TheLastBlade'
folder = R/'ArtSource/Intro'/name
folder.mkdir(parents=True, exist_ok=True)
s.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.001,.002,.006,1)
s.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.1
bpy.context.preferences.filepaths.save_version=0

ivory=material('Warm porcelain',(.8,.73,.59),.25,.4,.8)
crimson=material('Blood orange light',(.75,.012,.004),.2,.45,2.8)
ember=material('Burnished gold light',(1,.32,.025),.6,.3,4)
black=material('Obsidian',(.004,.008,.014),.85,.19)
# Subtle handmade surface variation, resolved by moving light.
for mat in (ink,steel,black):
    nodes=mat.node_tree.nodes;links=mat.node_tree.links
    noise=nodes.new('ShaderNodeTexNoise');noise.inputs['Scale'].default_value=95
    bump=nodes.new('ShaderNodeBump');bump.inputs['Strength'].default_value=.18;bump.inputs['Distance'].default_value=.025
    links.new(noise.outputs['Fac'],bump.inputs['Height']);links.new(bump.outputs['Normal'],nodes.get('Principled BSDF').inputs['Normal'])

def curve(name,points,mat,width=.015):
    c=bpy.data.curves.new(name,'CURVE');c.dimensions='3D';c.bevel_depth=width;c.bevel_resolution=3
    p=c.splines.new('POLY');p.points.add(len(points)-1)
    for a,b in zip(p.points,points):a.co=(*b,1)
    o=bpy.data.objects.new(name,c);s.collection.objects.link(o);c.materials.append(mat)
    return o

def label(body,loc,size,mat,name):
    c=bpy.data.curves.new(name,'FONT');c.body=body;c.align_x='CENTER';c.align_y='CENTER';c.size=size;c.extrude=.009;c.bevel_depth=.002
    o=bpy.data.objects.new(name,c);s.collection.objects.link(o);o.location=loc;c.materials.append(mat)
    font=Path('C:/Windows/Fonts/georgia.ttf')
    if font.exists():c.font=bpy.data.fonts.load(str(font))
    return o

def reveal(o,start,end):
    for f,v in [(1,.0001),(start,.0001),(end,1),(192,1)]:o.scale=(v,)*3;o.keyframe_insert('scale',frame=f)

def ring(name,r,z,mat,width,start=0,end=math.tau):
    return curve(name,[(math.cos(a)*r,math.sin(a)*r,z) for a in [start+(end-start)*i/180 for i in range(181)]],mat,width)

if variant == 'eclipse':
    # Ritual composition: lunar eclipse, ink orbit and vermilion seal.
    sphere('Crimson sun',(0,.55,-2),2.18,crimson)
    matte=material('Lightless lunar surface',(.001,.001,.001),0,1)
    matte.node_tree.nodes.get('Principled BSDF').inputs['Specular IOR Level'].default_value=0
    dark=sphere('Moving lunar shadow',(-.52,.68,-.65),2.08,matte)
    for f,x in [(1,-1.5),(90,-.12),(192,.12)]:
        dark.location.x=x;dark.keyframe_insert('location',frame=f)
    for j in range(4):
        o=ring('Broken ceremonial orbit',2.48+j*.09,-.45,crimson if j==0 else gold,.012 if j==0 else .006,.22+j*.7,4.1+j*.65)
        o.data.bevel_factor_end=0;o.data.keyframe_insert('bevel_factor_end',frame=1)
        o.data.bevel_factor_end=1;o.data.keyframe_insert('bevel_factor_end',frame=78+j*7)
        o.rotation_euler.z=-.2;o.keyframe_insert('rotation_euler',frame=1);o.rotation_euler.z=.17;o.keyframe_insert('rotation_euler',frame=192)
    # Sculpted ink ribbons with irregular edges, visibly moving in depth.
    for j in range(9):
        verts=[]
        for i in range(81):
            a=i/80*4.3+j*.61;r=2.65+.065*j+.06*math.sin(a*7+j)
            w=(.045+.08*math.sin(i/80*math.pi))*(1+.25*math.sin(a*19))
            verts.extend([(math.cos(a)*(r-w), math.sin(a)*(r-w)+.3,-.2),(math.cos(a)*(r+w),math.sin(a)*(r+w)+.3,-.17)])
        m=bpy.data.meshes.new('Brush ribbon');m.from_pydata(verts,[],[(2*i,2*i+1,2*i+3,2*i+2) for i in range(80)]);m.materials.append(ink)
        o=bpy.data.objects.new('Orbiting ink',m);s.collection.objects.link(o)
        for f,a in [(1,-.3),(192,.3)]:o.rotation_euler.z=a;o.keyframe_insert('rotation_euler',frame=f)
    for i,ch in enumerate('UNSEEN'):
        o=label(ch,((i-2.5)*1.02,-.10,2.1),1.12,ivory,'Title '+ch+str(i));reveal(o,52+i*4,82+i*4)
    sub=label('LEAVE NO TRACE',(0,-.95,2.2),.23,ivory,'Whisper');reveal(sub,99,124)
    seal=cube('Vermilion maker seal',(0,-2.03,2.2),(.24,.24,.025),red,.015);seal.rotation_euler.z=math.pi/4;reveal(seal,116,140)
    area('Red rim',(-4,2,5),(1,.06,.018),700,4,(0,0,0))
    area('Blue ink edge',(4,3,4),(.08,.24,.4),450,5,(0,0,0))
    for i in range(90):
        a=random.uniform(0,math.tau);r=random.uniform(2.3,4.7)
        o=sphere('Ember dust',(math.cos(a)*r,math.sin(a)*r,random.uniform(-1,1)),random.uniform(.006,.018),crimson if i%3 else gold)
        o.keyframe_insert('location',frame=1);o.location.y+=.55;o.location.x+=.2;o.keyframe_insert('location',frame=192)
else:
    # A monumental forged blade rotates from darkness into a narrow strip of gold.
    bpy.ops.object.empty_add(); sword=bpy.context.object;sword.name='Katana reveal rig'
    verts=[]
    sections=[(-4.4,.0,.22),(-3.8,.015,.22),(-1,.08,.22),(2,.23,.20),(3.6,.39,.18),(4.35,.56,0)]
    for x,y,w in sections:verts.extend([(x,y-w,0),(x,y,.11),(x,y+w,0)])
    faces=[]
    for i in range(len(sections)-1):
        faces.extend([(i*3,i*3+1,(i+1)*3+1,(i+1)*3),(i*3+1,i*3+2,(i+1)*3+2,(i+1)*3+1)])
    m=bpy.data.meshes.new('Hand forged blade');m.from_pydata(verts,[],faces);m.materials.append(steel);m.materials.append(material('Honed blade bevel',(.43,.53,.64),1,.24))
    for i,p in enumerate(m.polygons):p.material_index=i%2
    o=bpy.data.objects.new('Katana steel',m);s.collection.objects.link(o);o.parent=sword
    b=o.modifiers.new('Solid steel','SOLIDIFY');b.thickness=.035
    edge=curve('Gold cutting edge',[(x,y-w,.02) for x,y,w in sections],ember,.012);edge.parent=sword
    grip=cube('Ray skin grip',(-5.3,0,0),(1.7,.31,.25),ink,.05);grip.parent=sword
    for i in range(10):
        o=cube('Silk binding',(-6+i*.15,0,.14),(.055,.34,.045),gold,.006);o.rotation_euler.z=.5 if i%2 else -.5;o.parent=sword
    guard=cube('Tsuba',(-4.43,0,0),(.10,.94,.52),gold,.08);guard.parent=sword
    for f,r in [(1,(1.4,.04,.30)),(90,(.15,-.06,.30)),(192,(-.15,.06,.33))]:
        sword.rotation_euler=r;sword.keyframe_insert('rotation_euler',frame=f)
    # Angular obsidian fragments form an asymmetrical exploded halo behind the blade.
    for i in range(36):
        a=random.uniform(0,math.tau);r=random.uniform(2.3,4.6)
        o=cube('Suspended obsidian',(math.cos(a)*r,math.sin(a)*r,-1.6),(random.uniform(.07,.2),random.uniform(.3,1.0),.12),black,.015)
        o.rotation_euler=(random.random(),random.random(),a)
        o.keyframe_insert('location',frame=1);o.keyframe_insert('rotation_euler',frame=1)
        o.location.x*=1.12;o.location.y*=1.12;o.rotation_euler.z+=.3;o.keyframe_insert('location',frame=192);o.keyframe_insert('rotation_euler',frame=192)
    # Broken gold arcs and a travelling specular key add continuous motion.
    for j in range(3):
        o=ring('Forged arc',2.45+j*.16,-1.2,gold,.009,j*1.8,j*1.8+1.8)
        o.rotation_euler.z=-.3;o.keyframe_insert('rotation_euler',frame=1);o.rotation_euler.z=.2;o.keyframe_insert('rotation_euler',frame=192)
    title=label('U N S E E N',(0,-1.05,2),.85,ivory,'UNSEEN');reveal(title,57,95)
    sub=label('ONE STRIKE.  NO WITNESSES.',(0,-1.83,2),.20,material('Fine gold lettering',(.5,.32,.12),0,.6,.7),'Tagline');reveal(sub,100,126)
    area('Long cold reflection',(0,4,6),(.18,.46,.8),1600,5,(0,0,0))
    area('Golden travelling key',(-6,-1,4),(1,.48,.13),1700,3,(0,0,0))
    key=bpy.context.object
    key.keyframe_insert('location',frame=1);key.location.x=6;key.keyframe_insert('location',frame=150)
    area('Hilt edge',(-5,1,3),(1,.6,.2),600,3,(-4,0,0))
    for i in range(90):
        x=random.uniform(-7,7);y=random.uniform(-3.7,3.7)
        o=curve('Falling gold spark',[(x,y,-.2),(x+.03,y+.10,-.2)],ember,random.uniform(.004,.009))
        o.keyframe_insert('location',frame=1);o.location.y=-1.5; o.location.x=.5;o.keyframe_insert('location',frame=192)

bpy.ops.object.camera_add(location=(0,0,18));cam=bpy.context.object;s.camera=cam;cam.data.type='ORTHO';cam.data.ortho_scale=13.9
for f,scale in [(1,14.6),(192,13.4)]:cam.data.ortho_scale=scale;cam.data.keyframe_insert('ortho_scale',frame=f)
g=bpy.data.node_groups.new('Cinematic finishing','CompositorNodeTree');g.interface.new_socket(name='Image',in_out='OUTPUT',socket_type='NodeSocketColor');s.compositing_node_group=g
rl=g.nodes.new('CompositorNodeRLayers');gl=g.nodes.new('CompositorNodeGlare');gl.inputs['Type'].default_value='Fog Glow';gl.inputs['Quality'].default_value='Medium';gl.inputs['Strength'].default_value=.5
g.links.new(rl.outputs['Image'],gl.inputs['Image'])
fade=g.nodes.new('ShaderNodeMix');fade.data_type='RGBA';fade.blend_type='MULTIPLY';fade.inputs[0].default_value=1
for f,v in [(1,0),(18,1),(169,1),(192,0)]:fade.inputs[7].default_value=(v,v,v,1);fade.inputs[7].keyframe_insert('default_value',frame=f)
g.links.new(gl.outputs['Image'],fade.inputs[6]);out=g.nodes.new('NodeGroupOutput');g.links.new(fade.outputs[2],out.inputs['Image'])
s.frame_set(132)
s.render.image_settings.media_type='VIDEO';s.render.image_settings.file_format='FFMPEG';s.render.ffmpeg.format='MPEG4';s.render.ffmpeg.codec='H264';s.render.ffmpeg.constant_rate_factor='MEDIUM';s.render.ffmpeg.ffmpeg_preset='GOOD';s.render.filepath=str(folder/(name+'.mp4'))
bpy.ops.file.pack_all();bpy.ops.wm.save_as_mainfile(filepath=str(folder/(name+'.blend')))
if '--preview' in sys.argv:
    s.render.image_settings.media_type='IMAGE';s.render.image_settings.file_format='PNG';s.render.filepath=str(folder/'preview.png');bpy.ops.render.render(write_still=True)
else:bpy.ops.render.render(animation=True)
