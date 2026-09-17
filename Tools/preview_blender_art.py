"""Render the exported crest and cloth source for geometry review."""
import bpy
from mathutils import Vector
from pathlib import Path

root = Path(__file__).resolve().parents[1]
bpy.ops.wm.open_mainfile(filepath=str(root / 'ArtSource/UnseenDetails.blend'))
bpy.data.objects['WaterSurface'].hide_render = True
for name, color in [('RoofCrest', (.12, .17, .20, 1)), ('ClothWrap', (.08, .10, .18, 1))]:
    mat = bpy.data.materials.new(name + ' Preview')
    mat.diffuse_color = color
    bpy.data.objects[name].data.materials.append(mat)
bpy.data.objects['RoofCrest'].location = (-.85, 0, .5)
bpy.data.objects['ClothWrap'].location = (.85, 0, 0)
bpy.ops.object.camera_add(location=(3, -5, 2.6))
camera = bpy.context.object
camera.rotation_euler = (Vector((0, 0, .5)) - camera.location).to_track_quat('-Z', 'Y').to_euler()
camera.data.type = 'ORTHO'
camera.data.ortho_scale = 3.6
scene = bpy.context.scene
scene.camera = camera
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'MATERIAL'
scene.display.shading.show_shadows = True
scene.display.shading.show_cavity = True
scene.display.shading.cavity_type = 'BOTH'
scene.display.shading.background_type = 'WORLD'
scene.world.color = (.12, .12, .12)
scene.render.resolution_x = 1000
scene.render.resolution_y = 650
scene.render.resolution_percentage = 100
scene.render.filepath = str(root / 'ArtSource/details-preview.png')
bpy.ops.render.render(write_still=True)
