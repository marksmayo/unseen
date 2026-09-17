"""Blender --background --python Tools/refine_ninja.py, after UnseenNinjaArtExport.Export.

Keeps vertex order, topology and UVs; Unity retains the original skin weights and bind poses.
"""
import bpy
import json
from pathlib import Path
from mathutils import Vector

root = Path(__file__).resolve().parents[1]
source = json.loads((root/'ArtSource/ninja-mesh-source.json').read_text())
def vec(p): return Vector((p['x'],p['y'],p['z']))
pivot = vec(source['pivot'])
up, right, forward = [vec(source[k]) for k in ('up','right','forward')]
original = [vec(p) for p in source['vertices']]
points = []
for point, weight in zip(original,source['weights']):
    offset = point-pivot
    # Smaller hood and face; smoothly respect mixed neck/head skin weights.
    displacement = right*offset.dot(right)*-.43 + up*offset.dot(up)*-.26 + forward*offset.dot(forward)*-.37
    points.append(point+displacement*weight)
old_height = max(v.dot(up) for v in original)-min(v.dot(up) for v in original)
new_height = max(v.dot(up) for v in points)-min(v.dot(up) for v in points)
scale = old_height/new_height
assert 1 <= scale < 1.25
payload = {'vertices':[dict(x=p.x,y=p.y,z=p.z) for p in points], 'scale':scale}
(root/'Assets/Unseen/Resources/BlenderArt/NinjaBody.bytes').write_text(json.dumps(payload,separators=(',',':')))

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
mesh = bpy.data.meshes.new('NinjaRefined')
# Coordinate conversion for a Z-up Blender inspection scene.
mesh.from_pydata([(p.dot(right),p.dot(forward),p.dot(up)) for p in points],[],
                 [tuple(reversed(source['triangles'][i:i+3])) for i in range(0,len(source['triangles']),3)])
mesh.update()
obj=bpy.data.objects.new('NinjaRefined',mesh)
bpy.context.collection.objects.link(obj)
uv=mesh.uv_layers.new(name='OriginalUV')
for loop in mesh.loops:
    p=source['uv'][loop.vertex_index]
    uv.data[loop.index].uv=(p['x'],p['y'])
for poly in mesh.polygons: poly.use_smooth=True
bpy.context.view_layer.objects.active=obj
obj.select_set(True)
bpy.ops.wm.save_as_mainfile(filepath=str(root/'ArtSource/NinjaRefined.blend'))
print(f'NINJA: {len(points)} vertices, original topology retained, fitted scale {scale:.4f}')
