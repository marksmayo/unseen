"""Author and render the eight-second UNSEEN opening. Blender 5.2, no external assets.
blender --background --python Tools/build_intro.py -- --preview
Omit --preview to render the complete H.264 film and save the editable .blend.
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

cube('Reflecting courtyard',(0,2,-.12),(45,50,.2),stone)
for y in range(-8,10):
    for x in range(-2,3):cube('Path stone',(x*.86,y*.95,0),(.82,.91,.07),stone,.035)
# Receding gates establish a fortress silhouette with depth and parallax.
for y in (4,9,15):
    for x in (-3.6,3.6):
        cube('Torii pillar',(x,y,2.6),(.3,.38,5.2),ink,.05)
        cube('Gold collar',(x,y,4.0),(.36,.43,.10),gold,.015)
    cube('Torii lintel',(0,y,5.4),(8.3,.55,.35),ink,.09)
    cube('Torii cross beam',(0,y,4.55),(7.7,.3,.22),ink,.03)
    for x in (-4,4):
        o=cube('Upturned eave',(x,y,5.52),(1,.57,.23),ink,.07); o.rotation_euler.y=math.copysign(-.18,x)
for y in (-2,2.5,7,11):
    for x in (-3,3):
        cube('Lantern plinth',(x,y,.16),(.6,.6,.3),ink,.05)
        cube('Lantern silk',(x,y,.65),(.35,.35,.65),light,.03)
        cube('Lantern cap',(x,y,1.03),(.65,.65,.12),ink,.04)
        for dx in (-.2,.2):
            for dy in (-.2,.2):cube('Lantern frame',(x+dx,y+dy,.65),(.035,.035,.75),gold)
        bpy.ops.object.light_add(type='POINT',location=(x,y,.9)); bpy.context.object.data.energy=55
        bpy.context.object.data.color=(1,.32,.08); bpy.context.object.data.shadow_soft_size=.8
# Bamboo framing, arranged clear of the central title.
for side in (-1,1):
    for i in range(24):
        x=side*random.uniform(4.5,9); y=random.uniform(-3,17); h=random.uniform(5,10)
        bpy.ops.mesh.primitive_cylinder_add(vertices=10,radius=random.uniform(.045,.09),depth=h,location=(x,y,h/2))
        o=bpy.context.object; o.name='Bamboo silhouette';o.data.materials.append(ink)
        o.rotation_euler.y=random.uniform(-.12,.12)
        for z in (2,3,4,5):
            for j in range(3):
                a=side*(.25+j*.25); dz=.12+j*.14
                m=bpy.data.meshes.new('Lanceolate leaf');m.from_pydata([(x,y,z),(x+a*.55,y-.08,z+dz+.08),(x+a,y,z+dz),(x+a*.45,y+.04,z+dz-.05)],[],[(0,1,2,3)]);m.materials.append(ink)
                o=bpy.data.objects.new('Bamboo leaf',m);s.collection.objects.link(o)
sphere('Moon',(0,18,7.1),1.85,moon)
# A forged four-point blade suspended before the moon.
bpy.ops.object.empty_add(location=(0,1,3.55)); blade=bpy.context.object; blade.name='Animated shuriken'
verts=[(0,-.09,0),(0,.09,0)]
for i in range(8):
    a=i*math.pi/4; r=1.16 if i%2==0 else .26
    verts.append((math.cos(a)*r,0,math.sin(a)*r))
faces=[]
for i in range(8): faces.extend([(0,2+i,2+(i+1)%8),(1,2+(i+1)%8,2+i)])
mesh=bpy.data.meshes.new('Faceted blade');mesh.from_pydata(verts,[],faces);mesh.materials.append(steel)
o=bpy.data.objects.new('Shuriken bevels',mesh);s.collection.objects.link(o);o.parent=blade
b=o.modifiers.new('Blade edges','BEVEL');b.width=.018;b.segments=2;o.modifiers.new('Normals','WEIGHTED_NORMAL')
bpy.ops.mesh.primitive_torus_add(major_radius=.20,minor_radius=.055,major_segments=48,minor_segments=12,rotation=(math.pi/2,0,0))
o=bpy.context.object;o.name='Blade gold eye';o.data.materials.append(gold);o.parent=blade;o.location=(0,-.11,0)
for frame,rotation,scale in [(1,(.4,0,-1.1),.02),(35,(.15,2.5,-.35),1),(112,(0,6.28,0),1),(192,(0,6.5,.1),1)]:
    blade.rotation_euler=rotation;blade.scale=(scale,)*3;blade.keyframe_insert('rotation_euler',frame=frame);blade.keyframe_insert('scale',frame=frame)

def text(body,name,z,size,mat):
    c=bpy.data.curves.new(name,'FONT');c.body=body;c.align_x='CENTER';c.align_y='CENTER';c.size=size;c.space_character=1.3;c.extrude=.008;c.bevel_depth=.002
    o=bpy.data.objects.new(name,c);s.collection.objects.link(o);o.location=(0,-1,z);o.rotation_euler=(math.pi/2,0,0);c.materials.append(mat)
    return o
# Windows ships Georgia; pack it into the blend so the source remains portable.
font=Path('C:/Windows/Fonts/georgia.ttf')
title=text('U N S E E N','UNSEEN title',1.64,.76,type_mat)
if font.exists():title.data.font=bpy.data.fonts.load(str(font))
subtitle=text('BECOME THE SHADOW','Tagline',.92,.14,type_mat)
for o,begin,end in [(title,45,92),(subtitle,83,115)]:
    for f,v in [(1,.001),(begin,.001),(end,1),(192,1)]:o.scale=(v,)*3;o.keyframe_insert('scale',frame=f)
# Slow gold motes cross multiple depth planes.
for i in range(65):
    x=random.uniform(-7,7);y=random.uniform(-2,12);z=random.uniform(.2,7)
    o=sphere('Drifting ember',(x,y,z),random.uniform(.007,.022),light)
    o.keyframe_insert('location',frame=1);o.location.x+=random.uniform(-1.5,.5);o.location.z+=random.uniform(.5,1.8);o.keyframe_insert('location',frame=192)
# Thin world-space atmospheric volume, lit by the warm lamps and cool moon.
fog=bpy.data.materials.new('Blue courtyard haze');fog.use_nodes=True;n=fog.node_tree.nodes;n.clear()
p=n.new('ShaderNodeVolumePrincipled');p.inputs['Density'].default_value=.009;p.inputs['Color'].default_value=(.23,.35,.5,1)
out=n.new('ShaderNodeOutputMaterial');fog.node_tree.links.new(p.outputs['Volume'],out.inputs['Volume'])
cube('Courtyard mist',(0,5,2),(24,32,5),fog)
area('Moon rim',(0,7,10),(.34,.58,1),1400,8,(0,0,2))
area('Blade softbox',(-4,-6,7),(.48,.7,1),950,5,(0,1,3))
area('Amber edge',(4,0,4),(1,.38,.10),700,4,(0,1,3))
bpy.ops.object.camera_add(location=(.65,-18,4.4));cam=bpy.context.object;cam.name='Slow dolly';s.camera=cam;cam.data.lens=43
for f,loc in [(1,(.65,-18,4.4)),(192,(0,-15.8,3.85))]:
    cam.location=loc;cam.rotation_euler=(Vector((0,2,2.85))-cam.location).to_track_quat('-Z','Y').to_euler()
    cam.keyframe_insert('location',frame=f);cam.keyframe_insert('rotation_euler',frame=f)
# Blender 5 uses a compositor node group attached directly to the scene.
g=bpy.data.node_groups.new('Intro finishing','CompositorNodeTree')
g.interface.new_socket(name='Image',in_out='OUTPUT',socket_type='NodeSocketColor')
s.compositing_node_group=g
rl=g.nodes.new('CompositorNodeRLayers');glare=g.nodes.new('CompositorNodeGlare');glare.inputs['Type'].default_value='Fog Glow';glare.inputs['Quality'].default_value='Medium'
g.links.new(rl.outputs['Image'],glare.inputs['Image'])
fade=g.nodes.new('ShaderNodeMix');fade.data_type='RGBA';fade.blend_type='MULTIPLY';fade.inputs[0].default_value=1
for f,v in [(1,0),(20,1),(167,1),(192,0)]:
    fade.inputs[7].default_value=(v,v,v,1);fade.inputs[7].keyframe_insert('default_value',frame=f)
g.links.new(glare.outputs['Image'],fade.inputs[6]);out=g.nodes.new('NodeGroupOutput');g.links.new(fade.outputs[2],out.inputs['Image'])
(R/'Assets/Unseen/Resources/Intro').mkdir(parents=True,exist_ok=True)
(R/'ArtSource/Intro').mkdir(parents=True,exist_ok=True)
s.frame_set(120)
s.render.image_settings.media_type='VIDEO'
s.render.image_settings.file_format='FFMPEG'
s.render.ffmpeg.format='MPEG4'
s.render.ffmpeg.codec='H264'
s.render.ffmpeg.constant_rate_factor='MEDIUM'
s.render.ffmpeg.ffmpeg_preset='GOOD'
s.render.filepath=str(R/'Assets/Unseen/Resources/Intro/UnseenIntro.mp4')
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.file.pack_all()
bpy.ops.wm.save_as_mainfile(filepath=str(R/'ArtSource/Intro/UnseenIntro.blend'))
if '--preview' in sys.argv:
    s.render.image_settings.media_type='IMAGE';s.render.image_settings.file_format='PNG';s.render.filepath=str(R/'ArtSource/Intro/preview.png');bpy.ops.render.render(write_still=True)
else:
    s.render.image_settings.media_type='VIDEO';s.render.image_settings.file_format='FFMPEG';s.render.ffmpeg.format='MPEG4';s.render.ffmpeg.codec='H264';s.render.ffmpeg.constant_rate_factor='MEDIUM';s.render.ffmpeg.ffmpeg_preset='GOOD'
    s.render.filepath=str(R/'Assets/Unseen/Resources/Intro/UnseenIntro.mp4');bpy.ops.render.render(animation=True)
