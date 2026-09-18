"""Author the adult-proportioned Unseen shinobi, including rigged clothing and equipment.
Run: blender --background --python Tools/build_hero_ninja.py
Unity imports the mesh payload into native assets with UnseenHeroNinjaTools.Build.
"""
import bpy, json, math, random
from pathlib import Path
from mathutils import Vector, Matrix
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'ArtSource/HeroNinja';OUT.mkdir(parents=True,exist_ok=True)
rig=json.loads((OUT/'rig.json').read_text())
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
bpy.context.preferences.filepaths.save_version=0
random.seed(19)
# Blender: Z up, forward -Y. Unity: Y up, forward +Z.
def bv(v):return Vector((v['x'],-v['z'],v['y']))
def uv(v):return dict(x=v.x,y=v.z,z=-v.y)
old={b['name']:bv(b['position']) for b in rig['bones']}
bones={k:v.copy() for k,v in old.items()}
for k,z in [('HipsCtrl',1.02),('Hips',.91),('Spine',1.02),('Chest',1.20),('UpperChest',1.38),('Neck',1.52),('Head',1.61)]:bones[k]=Vector((0,0,z))
for side,sign in [('Left',-1),('Right',1)]:
    def b(n,x,y,z):bones[side+n]=Vector((sign*x,y,z))
    b('Shoulder',.065,0,1.438);b('Arm',.205,0,1.426);b('ForeArm',.466,0,1.406);b('Hand',.703,-.005,1.386)
    b('HandIndex1',.758,-.005,1.383);b('HandIndex2',.795,-.006,1.377);b('HandIndex3',.828,-.007,1.367)
    b('HandThumb1',.73,-.04,1.380);b('HandThumb2',.76,-.077,1.36)
    b('UpLeg',.101,0,.91);b('Leg',.108,-.012,.52);b('Foot',.108,.018,.11);b('Toes',.108,-.135,.035)
    for n in ['FootCtrl','FootIK','FootRollCtrl']:bones[side+n]=bones[side+'Foot'].copy()
    b('HeelRoll',.108,.085,.015);bones[side+'ToeRoll']=bones[side+'Toes'].copy();b('KneeCtrl',.108,-.28,.52)
# PBR values are linear, shared by Blender and Unity's single-draw character shader.
specs=[('Indigo cotton',(.016,.027,.047),.88,0,0),('Charcoal cotton',(.008,.012,.019),.90,0,0),('Woven binding',(.025,.034,.046),.78,0,0),('Oiled leather',(.041,.023,.016),.58,0,1),('Blue-black lacquer',(.020,.029,.040),.33,.3,2),('Aged bronze',(.23,.145,.065),.40,.7,2),('Skin',(.24,.125,.077),.64,0,3),('Eye white',(.30,.27,.21),.32,0,4),('Dark iris',(.027,.017,.010),.20,0,4),('Oxblood sash',(.077,.025,.027),.85,0,0),('Stitch',(.105,.095,.075),.85,0,0),('Soft boot sole',(.008,.010,.013),.88,0,1)]
mats=[]
for name,col,rough,metal,kind in specs:
    m=bpy.data.materials.new(name);m.diffuse_color=(*col,1);m.use_nodes=True
    bs=m.node_tree.nodes.get('Principled BSDF');bs.inputs['Base Color'].default_value=(*col,1);bs.inputs['Roughness'].default_value=rough;bs.inputs['Metallic'].default_value=metal
    if kind==0:
        tex=m.node_tree.nodes.new('ShaderNodeTexNoise');tex.inputs['Scale'].default_value=650;tex.inputs['Detail'].default_value=2
        bump=m.node_tree.nodes.new('ShaderNodeBump');bump.inputs['Strength'].default_value=.16;bump.inputs['Distance'].default_value=.00035
        m.node_tree.links.new(tex.outputs['Fac'],bump.inputs['Height']);m.node_tree.links.new(bump.outputs['Normal'],bs.inputs['Normal'])
        bs.inputs['Sheen Weight'].default_value=.12
        grain=m.node_tree.nodes.new('ShaderNodeTexNoise');grain.inputs['Scale'].default_value=85;grain.inputs['Detail'].default_value=3
        ramp=m.node_tree.nodes.new('ShaderNodeValToRGB');ramp.color_ramp.elements[0].color=(*(v*.76 for v in col),1);ramp.color_ramp.elements[1].color=(*(v*1.12 for v in col),1)
        m.node_tree.links.new(grain.outputs['Fac'],ramp.inputs[0]);m.node_tree.links.new(ramp.outputs[0],bs.inputs['Base Color'])
    mats.append(m)
parts=[]
def skin(o,weights):
    if isinstance(weights,str):weights={weights:1}
    if isinstance(weights,dict):weights=[weights]*len(o.data.vertices)
    for i,w in enumerate(weights):
        if isinstance(w,str):w={w:1}
        total=sum(w.values())
        for name,value in w.items():
            if value<=0:continue
            vg=o.vertex_groups.get(name) or o.vertex_groups.new(name=name);vg.add([i],value/total,'REPLACE')
def mesh(name,verts,faces,mat,weights):
    data=bpy.data.meshes.new(name);data.from_pydata(verts,[],faces);data.update();o=bpy.data.objects.new(name,data);bpy.context.collection.objects.link(o)
    o.data.materials.append(mats[mat]);skin(o,weights)
    for p in data.polygons:p.use_smooth=True
    parts.append(o);return o
def zskin(z):
    levels=[(.91,'Hips'),(1.02,'Spine'),(1.20,'Chest'),(1.38,'UpperChest'),(1.52,'Neck')]
    if z<=levels[0][0]:return {levels[0][1]:1}
    for (lo,a),(hi,b) in zip(levels,levels[1:]):
        if z<=hi:
            t=(z-lo)/(hi-lo);return {a:1-t,b:t}
    return {'Neck':1}
def tube(name,centres,radii,mat,weights,segments=32,fold=.0,axis='Z',cap=True):
    vs=[];ws=[];fs=[]
    for j,(c,r) in enumerate(zip(centres,radii)):
        c=Vector(c);a,b=r
        for i in range(segments):
            t=2*math.pi*i/segments
            wrinkle=fold*(.6*math.sin(t*7+j*.8)+.28*math.sin(t*13-j*1.1))
            if 'jacket' in name:
                for zf,amp in [(1.065,.005),(1.135,.006),(1.205,.004)]:
                    wrinkle+=amp*math.exp(-((c.z-zf-.025*math.sin(t*2+zf*9))/.015)**2)*max(0,math.cos(t))
            if 'sleeve' in name:
                for xf in [.415,.46,.50]:wrinkle+=.0045*math.exp(-((abs(c.x)-xf-.011*math.sin(t*2))/.011)**2)
            if 'trouser' in name:
                for zf in [.58,.71,.84]:wrinkle+=.005*math.exp(-((c.z-zf-.025*math.sin(t*2))/.019)**2)
            if axis=='Z':p=c+Vector(((a+wrinkle)*math.sin(t),-(b+wrinkle*.7)*math.cos(t),0))
            else:p=c+Vector((0,-(a+wrinkle)*math.cos(t),(b+wrinkle)*math.sin(t)))
            vs.append(tuple(p));ws.append(weights(p,j) if callable(weights) else weights)
    for j in range(len(centres)-1):
        for i in range(segments):a=j*segments+i;b=j*segments+(i+1)%segments;fs.append((a,b,b+segments,a+segments))
    if cap:fs.extend([tuple(reversed(range(segments))),tuple((len(centres)-1)*segments+i for i in range(segments))])
    o=mesh(name,vs,fs,mat,ws)
    if 'jacket' in name or 'trouser' in name or 'sleeve' in name:
        mod=o.modifiers.new('Soft cloth folds','SUBSURF');mod.levels=1
    return o
def cord(name,points,radius,mat,weight,segments=6):
    pts=[Vector(p) for p in points];vs=[];ws=[];fs=[]
    for j,p in enumerate(pts):
        tangent=(pts[min(j+1,len(pts)-1)]-pts[max(j-1,0)]).normalized();ref=Vector((0,1,0))
        if abs(tangent.dot(ref))>.9:ref=Vector((1,0,0))
        a=tangent.cross(ref).normalized();b=tangent.cross(a).normalized()
        for i in range(segments):vs.append(tuple(p+radius*(a*math.cos(i*math.tau/segments)+b*math.sin(i*math.tau/segments))));ws.append(weight(p,j) if callable(weight) else weight)
    for j in range(len(pts)-1):
        for i in range(segments):a=j*segments+i;b=j*segments+(i+1)%segments;fs.append((a,b,b+segments,a+segments))
    fs += [tuple(reversed(range(segments))),tuple((len(pts)-1)*segments+i for i in range(segments))]
    return mesh(name,vs,fs,mat,ws)
def ellipsoid(name,centre,scale,mat,weight,segments=24,rings=12):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments,ring_count=rings,location=centre);o=bpy.context.object;o.name=name;o.scale=scale
    bpy.ops.object.transform_apply(location=True,rotation=True,scale=True);o.data.materials.append(mats[mat]);skin(o,weight)
    for p in o.data.polygons:p.use_smooth=True
    parts.append(o);return o
def box(name,centre,scale,mat,weight,bevel=.008):
    bpy.ops.mesh.primitive_cube_add(size=1,location=centre);o=bpy.context.object;o.name=name;o.scale=scale;bpy.ops.object.transform_apply(location=True,rotation=True,scale=True)
    o.data.materials.append(mats[mat]);skin(o,weight)
    if bevel:
        mod=o.modifiers.new('Soft worn edges','BEVEL');mod.width=bevel;mod.segments=3;bpy.ops.object.modifier_apply(modifier=mod.name)
    for p in o.data.polygons:p.use_smooth=True
    mod=o.modifiers.new('Face normals','WEIGHTED_NORMAL');bpy.ops.object.modifier_apply(modifier=mod.name)
    parts.append(o);return o
def ribbon(name,points,width,mat,weight):
    pts=[Vector(p) for p in points];vs=[];ws=[]
    for j,p in enumerate(pts):
        d=(pts[min(j+1,len(pts)-1)]-pts[max(0,j-1)]).normalized();side=Vector((d.z,0,-d.x)).normalized()*width/2
        for k in [-1,0,1]:vs.append(tuple(p+side*k+Vector((0,-.003*(1-abs(k)),0))));ws.append(weight(p,j) if callable(weight) else weight)
    fs=[]
    for j in range(len(pts)-1):
        for k in range(2):a=j*3+k;fs.append((a,a+1,a+4,a+3))
    o=mesh(name,vs,fs,mat,ws);mod=o.modifiers.new('Cloth edge','SOLIDIFY');mod.thickness=.002;return o
# A tailored, overlapping jacket. Cloth fold geometry sits over the anatomical volumes.
zs=[.875,.90,.94,.98,1.02,1.06,1.10,1.14,1.18,1.22,1.26,1.30,1.34,1.38,1.42,1.46,1.49]
rx=[.167,.173,.174,.162,.154,.151,.158,.171,.182,.192,.199,.204,.207,.207,.188,.122,.069]
ry=[.104,.109,.112,.110,.101,.096,.099,.103,.109,.115,.119,.119,.116,.106,.090,.073,.064]
tube('Tailored indigo jacket',[(0,0,z) for z in zs],list(zip(rx,ry)),0,lambda p,j:zskin(p.z),40,.0038)
# Diagonal lapels and twin rows of narrow stitching.
for flip in [-1,1]:
    pts=[]
    for j in range(25):
        t=j/24;x=(-.06+.19*t) if flip<0 else (.06-.11*t);z=1.485-(.44 if flip<0 else .24)*t;y=-.074-.043*math.sin(t*math.pi*.8);pts.append((x,y-.008,z))
    ribbon('Overlapping collar lapel',pts,.040,1,lambda p,j:zskin(p.z))
    cord('Lapel piping',[(x+flip*.017,y-.006,z) for x,y,z in pts],.0019,2,lambda p,j:zskin(p.z))
    for j in range(1,24,2):
        a=Vector(pts[j])+Vector((-flip*.014,-.005,0));cord('Lapel stitch',[a,a+Vector((flip*.002,-.0004,-.006))],.00065,10,lambda p,k:zskin(p.z),4)
# Legs: relaxed trousers taper into fitted gaiters.
for side,sgn in [('Left',-1),('Right',1)]:
    legzs=[.94,.92,.88,.84,.80,.76,.72,.68,.64,.60,.56,.52,.48,.44,.40,.36,.32,.28,.24,.20,.16,.12]
    rs=[.089,.101,.110,.111,.107,.105,.103,.102,.097,.090,.083,.078,.073,.067,.064,.061,.057,.053,.048,.045,.043,.041]
    def lw(p,j,side=side):
        t=max(0,min(1,(p.z-.46)/.14));return {side+'UpLeg':t,side+'Leg':1-t} if p.z>.16 else {side+'Leg':.6,side+'Foot':.4}
    tube(side+' gathered trouser',[(sgn*(.101+.007*(.94-z)/.82),-.012*math.sin((.94-z)*4),z) for z in legzs],[(r,r*.86) for r in rs],0,lw,32,.0045)
    # Fitted gaiter with overlapping flat woven strips, rather than rope coils.
    tube(side+' fitted shin gaiter',[(sgn*.108,-.004,z) for z in [.15,.20,.26,.32,.38,.44,.47]],[(r,r*.91) for r in [.045,.049,.055,.061,.067,.073,.075]],1,side+'Leg',32,.001)
    vs=[];fs=[]
    for j in range(241):
        t=j/240;ang=t*math.tau*6
        for edge in [-1,0,1]:
            z=.19+t*.25+edge*.016;r=.050+(z-.19)/.25*.028+.001*(1-abs(edge))
            vs.append((sgn*.108+r*math.sin(ang),-.004-r*.91*math.cos(ang),z))
    for j in range(240):
        for k in range(2):a=j*3+k;fs.append((a,a+1,a+4,a+3))
    o=mesh(side+' flat spiral binding',vs,fs,2,side+'Leg');mod=o.modifiers.new('Thin cotton','SOLIDIFY');mod.thickness=.0012
    # Curved small knee plate, with a flexible textile backing.
    ellipsoid(side+' knee reinforcement',(sgn*.108,-.092,.527),(.060,.009,.053),1,side+'Leg',24,10)
    for x in [-.035,.035]:ellipsoid('Knee stitch rivet',(sgn*.108+x,-.102,.529),(.0025,.0018,.0025),5,side+'Leg',8,6)
    # Continuous soft tabi boot, a shaped outsole and two fitted toe chambers.
    def footloft(name,ys,widths,tops,offset,mat):
        vs=[];fs=[];ws=[];segments=24
        for j,(y,width,top) in enumerate(zip(ys,widths,tops)):
            bottom=.026 if mat!=11 else .009;mid=(top+bottom)/2;rz=(top-bottom)/2
            for i in range(segments):
                ang=i/segments*math.tau
                vs.append((sgn*(.108+offset)+width*math.sin(ang),y,mid+rz*math.cos(ang)))
                t=max(0,min(.7,(-y-.08)/.13));ws.append({side+'Foot':1-t,side+'Toes':t})
        for j in range(len(ys)-1):
            for i in range(segments):a=j*segments+i;b=j*segments+(i+1)%segments;fs.append((a,b,b+segments,a+segments))
        fs.extend([tuple(reversed(range(segments))),tuple((len(ys)-1)*segments+i for i in range(segments))])
        return mesh(name,vs,fs,mat,ws)
    footloft(side+' continuous tabi boot',[.085,.070,.025,-.025,-.070,-.105,-.130],[.026,.041,.048,.050,.052,.050,.040],[.064,.095,.142,.123,.094,.080,.069],0,1)
    footloft(side+' shaped sole',[.093,.075,.015,-.065,-.120,-.166,-.185],[.023,.045,.054,.057,.056,.047,.020],[.032]*7,0,11)
    footloft(side+' inner tabi toe',[-.105,-.140,-.174,-.189],[.020,.023,.021,.008],[.075,.068,.059,.044],-.027,1)
    footloft(side+' outer tabi toes',[-.105,-.136,-.168,-.179],[.026,.029,.025,.010],[.075,.067,.057,.043],.024,1)
    cord(side+' toe seam',[(sgn*(.108-.002),-.182,.049),(sgn*(.108-.002),-.145,.071),(sgn*(.108-.002),-.108,.084)],.0011,2,side+'Toes')
    # Sleeves with creases around elbows and under the arms.
    xx=[.18,.205,.23,.26,.30,.34,.38,.42,.45,.47,.50,.53,.56,.59,.62,.65,.68,.705]
    rr=[.058,.074,.082,.082,.078,.072,.066,.060,.057,.058,.063,.061,.057,.052,.047,.042,.037,.034]
    def aw(p,j,side=side):
        t=max(0,min(1,(abs(p.x)-.415)/.10));return {side+'Arm':1-t,side+'ForeArm':t}
    tube(side+' articulated sleeve',[(sgn*x,-.002,1.426-(x-.205)*.080) for x in xx],[(r*.88,r) for r in rr],0,aw,32,.003,axis='X')
    shoulder=ellipsoid(side+' seamless shoulder cap',(sgn*.204,0,1.420),(.077,.072,.065),0,{side+'Arm':.68,'UpperChest':.32},32,16)
    # Wrist cuffs and two narrow leather binding straps.
    for x in [.56,.64]:
        z=1.426-(x-.205)*.080;r=.058 if x<.6 else .044
        tube(side+' forearm binding',[(sgn*(x+d),-.002,z) for d in [-.012,-.007,.007,.012]],[(r*.91,r)]*4,3,side+'ForeArm',24)
    for j in range(3):
        x=.565+j*.027;z=1.426-(x-.205)*.080
        box(side+' lacquer wrist splint',(sgn*x,-.057,z),(.035,.015,.069),4,side+'ForeArm',.006)
        for dz in [-.023,.023]:ellipsoid('Bronze wrist pin',(sgn*x,-.066,z+dz),(.0021,.0015,.0021),5,side+'ForeArm',8,6)
    # Palm and four separately modelled fingers, following the existing finger chain.
    ellipsoid(side+' fitted glove palm',(sgn*.743,-.005,1.386),(.052,.039,.027),1,side+'Hand')
    for f in range(4):
        yy=-.033+f*.020;length=[.076,.084,.080,.065][f];centres=[];radii=[];weights=[]
        for j in range(9):
            t=j/8;x=.772+length*t;z=1.384-.022*t*t;centres.append((sgn*x,yy,z));radii.append((.0085*(1-.32*t),.010*(1-.4*t)))
        def fw(p,j,side=side):
            t=j/8
            if t<.45:return {side+'HandIndex1':1-t/.45,side+'HandIndex2':t/.45}
            return {side+'HandIndex2':max(0,1-(t-.45)/.55),side+'HandIndex3':min(1,(t-.45)/.55)}
        tube(side+' glove finger '+str(f),centres,radii,1,fw,12,axis='X')
        ellipsoid('Knuckle reinforcement',(sgn*.778,yy,1.400),(.011,.008,.0038),3,side+'HandIndex1',12,8)
    cord(side+' gloved thumb',[(sgn*.727,-.030,1.383),(sgn*.744,-.053,1.375),(sgn*.766,-.073,1.363),(sgn*.784,-.081,1.351)],.014,1,side+'HandThumb1',12)
    cord(side+' glove back seam',[(sgn*.710,-.022,1.405),(sgn*.747,-.023,1.407),(sgn*.767,-.022,1.402)],.0012,2,side+'Hand')
# Three overlapping sash layers and a compact off-centre knot.
for j in range(3):
    z=1.012+j*.025
    tube('Woven obi layer '+str(j),[(0,0,z-.015),(0,0,z),(0,0,z+.014)],[(.168,.117),(.171,.120),(.167,.116)],9,'Spine',48,.0014)
box('Obi knot',(.117,-.106,1.035),(.064,.036,.053),9,'Spine',.012)
for x,zz in [(.112,.795),(.152,.84)]:
    pts=[(x,-.113,1.02),(x+.012,-.135,.96),(x+.009,-.134,.90),(x-.013,-.127,zz)]
    ribbon('Tapered sash end',pts,.040,9,{'Spine':.2,'Hips':.8})
# Leather diagonal harness, following the chest curvature.
pts=[]
for j in range(25):
    t=j/24;x=-.10+.28*t;z=1.07+.355*t
    k=min(len(zs)-2,max(0,next((k for k in range(len(zs)-1) if zs[k]<=z<=zs[k+1]),len(zs)-2)))
    u=(z-zs[k])/(zs[k+1]-zs[k]);a=rx[k]*(1-u)+rx[k+1]*u;b=ry[k]*(1-u)+ry[k+1]*u
    y=-b*math.sqrt(max(.02,1-(x/a)**2))-.022
    pts.append((x,y,z))
ribbon('Diagonal leather harness',pts,.036,3,lambda p,j:zskin(p.z))
cord('Harness edge seam',[(x-.011,y-.003,z+.008) for x,y,z in pts],.00085,10,lambda p,j:zskin(p.z),4)
box('Harness clasp',(.05,-.146,1.26),(.041,.013,.045),5,{'Chest':.65,'UpperChest':.35},.005)
box('Clasp inset',(.05,-.155,1.26),(.025,.005,.029),3,{'Chest':.65,'UpperChest':.35},.003)
# Functional belt pouches at the hip, with flap, strap and a small fastening.
for x,y in [(-.175,.028),(.135,.082)]:
    box('Soft leather belt pouch',(x,y,.972),(.086,.063,.123),3,'Hips',.018)
    box('Pouch flap',(x,y-.035,.999),(.081,.016,.054),3,'Hips',.009)
    ellipsoid('Pouch fastener',(x,y-.046,.982),(.004,.002,.004),5,'Hips',12,8)
# A soft connected neck wrap with shallow compression folds.
tube('Folded neck wrap',[(0,0,z) for z in [1.484,1.496,1.507,1.518,1.530,1.541,1.551,1.560]],[(r,r*.90) for r in [.070,.078,.082,.077,.080,.075,.073,.070]],1,{'Neck':.8,'Head':.2},48,.0009)
# Hood with a real opening rather than painted eyes on a sphere.
hz=[1.535,1.56,1.60,1.64,1.671,1.696,1.735,1.765,1.787,1.798]
hr=[(.057,.060),(.078,.075),(.094,.088),(.103,.096),(.105,.095),(.106,.090),(.099,.083),(.075,.065),(.039,.034),(.008,.008)]
hood=tube('Fitted cloth hood',[(0,0,z) for z in hz],hr,1,'Head',64,.0011)
# Delete only the eye aperture's front-facing quad row.
bpy.context.view_layer.objects.active=hood;hood.select_set(True)
import bmesh
bm=bmesh.new();bm.from_mesh(hood.data)
remove=[]
for face in bm.faces:
    c=face.calc_center_median()
    if 1.671<c.z<1.696 and c.y<-.042:remove.append(face)
bmesh.ops.delete(bm,geom=remove,context='FACES');bm.to_mesh(hood.data);bm.free()
# Reassign rigid hood groups after topology compaction.
hood.vertex_groups.clear();skin(hood,'Head')
vs=[];fs=[]
for j in range(5):
    for i in range(33):
        x=(i/32-.5)*.195;vs.append((x,-.089*math.sqrt(max(.1,1-(x/.108)**2))-.0005,1.666+j*.009))
for j in range(4):
    for i in range(32):a=j*33+i;fs.append((a,a+1,a+34,a+33))
mesh('Skin inside narrow eye opening',vs,fs,6,'Head')
for side,sgn in [('Left',-1),('Right',1)]:
    cx=sgn*.038;cy=-.0865;cz=1.6835
    # Almond outline follows the brow, inset inside the opening.
    vs=[(cx,cy-.001,cz)];fs=[]
    for i in range(25):
        a=i/24*math.tau;vs.append((cx+.0145*math.cos(a),cy+.002*abs(math.cos(a)),cz+.0043*math.sin(a)))
    for i in range(24):fs.append((0,i+1,i+2))
    mesh(side+' almond eye',vs,fs,7,'Head')
    ellipsoid(side+' iris',(cx,cy-.0025,cz),(.0040,.0012,.0040),8,'Head',20,10)
    ellipsoid(side+' pupil',(cx,cy-.004,cz),(.0018,.0007,.0025),1,'Head',12,8)
    cord(side+' upper lid',[(cx+.015*math.cos(i/16*math.pi),cy-.001,cz+.0048*math.sin(i/16*math.pi)) for i in range(17)],.0017,6,'Head')
    cord(side+' stern brow',[(cx-sgn*.018,-.085,1.691),(cx,-.087,1.696),(cx+sgn*.018,-.080,1.697)],.0019,1,'Head')
# The mask is part of the wrapped head surface: no floating rectangular face plate.
hood.data.materials.append(mats[0])
for vertex in hood.data.vertices:
    p=vertex.co
    if 1.555<p.z<1.672 and p.y<-.025:
        fade=max(0,1-(p.x/.11)**2)
        bridge=.014*math.exp(-(p.x/.033)**2)*max(0,min(1,(p.z-1.585)/.085))
        p.y-=bridge+.0025*math.sin((p.z-1.56)*81+p.x*27)*fade
for poly in hood.data.polygons:
    if poly.center.z<1.674 and poly.center.z>1.554 and poly.center.y<-.015:poly.material_index=1
points=[]
for j in range(33):
    t=(j/32-.5)*2.13;x=.105*math.sin(t);y=-.095*math.cos(t)-.014*math.exp(-(x/.033)**2)
    points.append((x,y-.001,1.671))
cord('Mask sewn upper edge',points,.0013,2,'Head')
mod=hood.modifiers.new('Soft fitted hood','SUBSURF');mod.levels=1
# Hood crown and temple seams, with discrete stitches.
for sign in [-1,1]:
    pts=[]
    for j in range(21):
        t=j/20;pts.append((sign*(.090*math.sin(t*math.pi*.85)),.006+.075*math.sin(t*math.pi),1.797-.240*t))
    cord('Hood crown seam',pts,.0015,2,'Head')
    for j in range(1,20,3):
        p=Vector(pts[j]);cord('Hood stitching',[p+Vector((-.002,0,0)),p+Vector((.002,0,-.001))],.00055,10,'Head',4)
# Join the structural jacket surfaces into one continuous sewn volume.
shirt=[o for o in parts if any(k in o.name for k in ['Tailored indigo jacket','articulated sleeve','seamless shoulder cap'])]
bpy.ops.object.select_all(action='DESELECT')
for o in shirt:o.select_set(True)
bpy.context.view_layer.objects.active=shirt[0]
for o in shirt:
    bpy.context.view_layer.objects.active=o
    for mod in list(o.modifiers):bpy.ops.object.modifier_apply(modifier=mod.name)
bpy.context.view_layer.objects.active=shirt[0];bpy.ops.object.join();coat=bpy.context.object;coat.name='Continuous tailored jacket'
parts=[o for o in parts if o not in shirt]+[coat]
mod=coat.modifiers.new('Sewn shoulder union','REMESH');mod.mode='VOXEL';mod.voxel_size=.004;mod.use_smooth_shade=True;bpy.ops.object.modifier_apply(modifier=mod.name)
mod=coat.modifiers.new('Cloth surface relaxation','SMOOTH');mod.factor=.45;mod.iterations=3;bpy.ops.object.modifier_apply(modifier=mod.name)
coat.data.calc_loop_triangles();mod=coat.modifiers.new('Game topology','DECIMATE');mod.ratio=min(1,22000/len(coat.data.loop_triangles));bpy.ops.object.modifier_apply(modifier=mod.name)
coat.vertex_groups.clear();weights=[]
for v in coat.data.vertices:
    p=v.co;arm=max(0,min(1,(abs(p.x)-.12)/.125));w={k:value*(1-arm) for k,value in zskin(p.z).items()}
    side='Left' if p.x<0 else 'Right';elbow=max(0,min(1,(abs(p.x)-.415)/.1));w[side+'Arm']=arm*(1-elbow);w[side+'ForeArm']=arm*elbow;weights.append(w)
skin(coat,weights)
for poly in coat.data.polygons:poly.use_smooth=True
# Unite each soft glove and boot, retaining the closest source skin weights.
from mathutils.kdtree import KDTree
for side in ['Left','Right']:
    for label,keys,voxel,target in [('glove',['fitted glove palm','glove finger','gloved thumb'],.0015,3200),('boot',['continuous tabi boot','inner tabi toe','outer tabi toes'],.0017,2400)]:
        group=[o for o in parts if o.name.startswith(side) and any(k in o.name for k in keys)]
        bpy.ops.object.select_all(action='DESELECT')
        for o in group:o.select_set(True)
        bpy.context.view_layer.objects.active=group[0];bpy.ops.object.join();obj=bpy.context.object;obj.name=side+' tailored '+label
        parts=[o for o in parts if o not in group]+[obj]
        tree=KDTree(len(obj.data.vertices));originalWeights=[]
        for v in obj.data.vertices:
            tree.insert(v.co,v.index);originalWeights.append({obj.vertex_groups[g.group].name:g.weight for g in v.groups})
        tree.balance()
        mod=obj.modifiers.new('Connected fabric','REMESH');mod.mode='VOXEL';mod.voxel_size=voxel;mod.use_smooth_shade=True;bpy.ops.object.modifier_apply(modifier=mod.name)
        mod=obj.modifiers.new('Soft joins','SMOOTH');mod.factor=.38;mod.iterations=2;bpy.ops.object.modifier_apply(modifier=mod.name)
        obj.data.calc_loop_triangles();mod=obj.modifiers.new('Game topology','DECIMATE');mod.ratio=min(1,target/len(obj.data.loop_triangles));bpy.ops.object.modifier_apply(modifier=mod.name)
        weights=[originalWeights[tree.find(v.co)[1]] for v in obj.data.vertices];obj.vertex_groups.clear();skin(obj,weights)
        for p in obj.data.polygons:p.use_smooth=True
# Make normals, UVs and modifiers explicit before export.
for o in parts:
    bpy.context.view_layer.objects.active=o;bpy.ops.object.select_all(action='DESELECT');o.select_set(True)
    for mod in list(o.modifiers):bpy.ops.object.modifier_apply(modifier=mod.name)
    bpy.ops.object.mode_set(mode='EDIT');bpy.ops.mesh.select_all(action='SELECT');bpy.ops.mesh.normals_make_consistent(inside=False);bpy.ops.uv.smart_project(island_margin=.015);bpy.ops.object.mode_set(mode='OBJECT')
# Join geometry but keep material regions and skin groups. One runtime material reads surface IDs.
bpy.ops.object.select_all(action='DESELECT')
for o in parts:o.select_set(True)
bpy.context.view_layer.objects.active=parts[0];bpy.ops.object.join();hero=bpy.context.object;hero.name='Unseen_Hero_Shinobi'
# Export per-corner vertices, with deduplication preserving normals, UV seams and weights.
w2m=Matrix([rig['worldToMesh'][i:i+4] for i in range(0,16,4)])
index={b['name']:i for i,b in enumerate(rig['bones'])}
def export_payload(o,filename):
    data=o.data;data.calc_loop_triangles();positions=[];normals=[];uvs=[];colors=[];surface=[];weights=[];indices=[];dedup={}
    for tri in data.loop_triangles:
        mat=data.materials[tri.material_index];mi=next(i for i,s in enumerate(specs) if s[0]==mat.name)
        for li in tri.loops:
            loop=data.loops[li];v=data.vertices[loop.vertex_index];p=v.co;n=data.corner_normals[li].vector;tex=data.uv_layers.active.data[li].uv
            w=sorted([(index[o.vertex_groups[g.group].name],g.weight) for g in v.groups if o.vertex_groups[g.group].name in index],key=lambda a:-a[1])[:4]
            if not w:raise RuntimeError('Unweighted vertex '+str(v.index))
            total=sum(a[1] for a in w);w=[(a,b/total) for a,b in w]
            key=(v.index,tuple(round(x,5) for x in n),tuple(round(x,5) for x in tex),mi,tuple(w))
            if key not in dedup:
                pu=Vector((p.x,p.z,-p.y));nu=Vector((n.x,n.z,-n.y));pm=w2m@pu;nm=(w2m.to_3x3()@nu).normalized()
                dedup[key]=len(positions);positions.append(dict(x=pm.x,y=pm.y,z=pm.z));normals.append(dict(x=nm.x,y=nm.y,z=nm.z));uvs.append(dict(x=tex.x,y=tex.y))
                col=specs[mi][1];colors.append(dict(r=col[0],g=col[1],b=col[2],a=1));surface.append(dict(x=specs[mi][4],y=1 if mi in [0,9] else 0))
                w += [(0,0)]*(4-len(w));weights.append(dict(boneIndex0=w[0][0],boneIndex1=w[1][0],boneIndex2=w[2][0],boneIndex3=w[3][0],weight0=w[0][1],weight1=w[1][1],weight2=w[2][1],weight3=w[3][1]))
            indices.append(dedup[key])
    payload=dict(vertices=positions,normals=normals,uv=uvs,colors=colors,surface=surface,weights=weights,triangles=indices,visualScale=rig['visualScale'],boneNames=[b['name'] for b in rig['bones']],bonePositions=[uv(bones[b['name']]) for b in rig['bones']])
    (OUT/filename).write_text(json.dumps(payload,separators=(',',':')))
    print('HERO',filename,len(positions),'verts',len(indices)//3,'triangles')
export_payload(hero,'hero-mesh.json')
# A lower-detail skin shares the same rig and material.
lod=hero.copy();lod.data=hero.data.copy();bpy.context.collection.objects.link(lod);lod.name='Unseen_Hero_Shinobi_LOD1';bpy.context.view_layer.objects.active=lod
mod=lod.modifiers.new('Distant silhouette','DECIMATE');mod.ratio=.38;mod.use_collapse_triangulate=True;bpy.ops.object.modifier_apply(modifier=mod.name)
export_payload(lod,'hero-lod1.json');lod.hide_render=True;lod.hide_set(True)
lod2=hero.copy();lod2.data=hero.data.copy();bpy.context.collection.objects.link(lod2);lod2.name='Unseen_Hero_Shinobi_LOD2';bpy.context.view_layer.objects.active=lod2
mod=lod2.modifiers.new('Distant ninja silhouette','DECIMATE');mod.ratio=.10;mod.use_collapse_triangulate=True;bpy.ops.object.modifier_apply(modifier=mod.name)
export_payload(lod2,'hero-lod2.json');lod2.hide_render=True;lod2.hide_set(True)
# Editable armature with the game's names and skinning weights.
bpy.ops.object.armature_add();arm=bpy.context.object;arm.name='Unseen_Hero_Rig';bpy.ops.object.mode_set(mode='EDIT');arm.data.edit_bones.remove(arm.data.edit_bones[0])
for b in rig['bones']:
    eb=arm.data.edit_bones.new(b['name']);eb.head=bones[b['name']]
    children=[c for c in rig['bones'] if c['parent']==index[b['name']]]
    tail=bones[children[0]['name']] if children else eb.head+Vector((0,0,.04))
    if (tail-eb.head).length<.015:tail=eb.head+Vector((0,0,.04))
    eb.tail=tail
for b in rig['bones']:
    if b['parent']>=0:arm.data.edit_bones[b['name']].parent=arm.data.edit_bones[rig['bones'][b['parent']]['name']]
bpy.ops.object.mode_set(mode='OBJECT')
for o in [hero,lod,lod2]:
    mod=o.modifiers.new('Hero skin','ARMATURE');mod.object=arm;o.parent=arm
# Relax the arms for the studio portrait; mesh exports above remain in the bind pose.
for side,sgn in [('Left',-1),('Right',1)]:
    pb=arm.pose.bones[side+'Arm'];pb.rotation_mode='QUATERNION'
    axis=pb.bone.matrix_local.to_3x3().inverted()@Vector((0,1,0))
    from mathutils import Quaternion
    pb.rotation_quaternion=Quaternion(axis,sgn*math.radians(68))
    pb=arm.pose.bones[side+'ForeArm'];pb.rotation_mode='QUATERNION';axis=pb.bone.matrix_local.to_3x3().inverted()@Vector((0,0,1));pb.rotation_quaternion=Quaternion(axis,sgn*math.radians(-9))
# Studio environment: soft warm key, cool rim, charcoal floor.
world=bpy.data.worlds.new('Studio atmosphere');bpy.context.scene.world=world;world.use_nodes=True;world.node_tree.nodes['Background'].inputs[0].default_value=(.065,.080,.11,1);world.node_tree.nodes['Background'].inputs[1].default_value=.45
bpy.ops.mesh.primitive_plane_add(size=200,location=(0,0,-.004));floor=bpy.context.object;floor.name='Studio floor';m=bpy.data.materials.new('Studio charcoal');m.diffuse_color=(.019,.024,.033,1);m.use_nodes=True;m.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value=m.diffuse_color;m.node_tree.nodes['Principled BSDF'].inputs['Roughness'].default_value=.76;floor.data.materials.append(m)
def area(name,pos,power,color,size):
    data=bpy.data.lights.new(name,'AREA');data.energy=power;data.color=color;data.shape='DISK';data.size=size;o=bpy.data.objects.new(name,data);bpy.context.collection.objects.link(o);o.location=pos;o.rotation_euler=(Vector((0,0,1))-o.location).to_track_quat('-Z','Y').to_euler()
area('Large warm key',(-2.3,-3.2,3.4),480,(1,.83,.68),3.2);area('Cool edge',(1.8,1.1,2.8),620,(.48,.67,1),2.0);area('Front fill',(2,-3,1.7),170,(.72,.82,1),2.4)
bpy.ops.object.camera_add(location=(2.3,-4.2,2.0));camera=bpy.context.object;camera.name='Hero portrait camera';camera.rotation_euler=(Vector((0,0,.94))-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.type='ORTHO';camera.data.ortho_scale=2.18;bpy.context.scene.camera=camera
scene=bpy.context.scene;scene.render.engine='CYCLES';scene.cycles.samples=48;scene.cycles.use_denoising=True;scene.render.resolution_x=1100;scene.render.resolution_y=1400;scene.render.resolution_percentage=100
scene.view_settings.view_transform='AgX';scene.render.image_settings.file_format='PNG';scene.render.filepath=str(OUT/'blender-portrait.png')
bpy.ops.wm.save_as_mainfile(filepath=str(OUT/'UnseenHeroNinja.blend'));bpy.ops.render.render(write_still=True)
print('HERO SOURCE COMPLETE')
