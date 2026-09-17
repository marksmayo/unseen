# Blender detail pass

`UnseenDetails.blend` contains editable meshes built with Blender 5.2.1:

| Mesh | Triangles | Use |
| --- | ---: | --- |
| RoofCrest | 380 | Bevelled ceramic ornaments on compound, storehouse and pagoda roof ridges |
| ClothWrap | 864 | Folded hollow bands fitted to the existing ninja bones |
| WaterSurface | 8,192 | Subdivided river, lake and garden-pool surface |

This is an initial detail pass, not a replacement building kit or a new character body.
The existing character rig, cloth-tail animation, colliders, grapple anchors and water-depth
queries are retained. Water now has gentle world-space vertex movement in `RiverWater.shader`.

Rebuild from the repository root:

```powershell
& 'C:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background --python Tools/build_blender_art.py
& 'C:/Program Files/Blender Foundation/Blender 5.2/blender.exe' --background --python Tools/preview_blender_art.py
```

The build script recreates the source file. Preserve hand edits in a separate `.blend` before
running it. Runtime `.bytes` files are JSON mesh payloads in Unity coordinates, including normals,
UV seams and triangle winding. `BlenderArt` loads and caches each mesh once. Blender is not
required on a machine that imports or runs the Unity project. Missing payloads retain the
procedural fallback meshes.

`details-preview.png` is a Blender geometry preview, not an in-game screenshot.

The current visual direction is deliberately weathered and realistic: building surfaces receive
rain staining, damp lower-wall moss, mineral plaster grain and rougher material response; ninja
fabric uses a worn, desaturated cloth shader; bamboo has joint collars, tapered culms and individual
pointed leaves. The wall-clearance pass relocates or omits finished plants after generation so their
render bounds cannot penetrate building walls or roofs.

Validation: Blender generation and preview rendering succeeded. All three payloads passed
finite-value, index-range, nondegenerate-triangle and normal/winding checks. Unity 6000.6.0f1
subsequently recognized its Personal license, compiled the isolated project copy and rendered
17 game views successfully, with no C# or shader errors reported. The project itself still pins
6000.5.10f1. Captures are in `Server/out/blender-game-renders`; the log is
`Server/out/game-render.log`. The editor capture shows characters in their bind pose and does
not validate live animation. The dedicated garment probe has not yet been run successfully.
