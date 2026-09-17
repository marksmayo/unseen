# Visual upgrade: items 1–9

This pass uses Blender-authored meshes and motion envelopes with Unity runtime presentation. Gameplay collision, stealth light radii, and hit resolution remain authoritative.

| Item | Implemented |
| --- | --- |
| 1. Night lighting | Cool diffuse sky fill, warmer controlled lanterns, softer moon shadows, restrained exposure/bloom, 32 nearby lantern lights and two shadow casters. |
| 2. Ninja model | Smaller hood proportions on the original skinning topology; fitted 1.8 m body; Blender bracers, pouch and split-toe footwear. |
| 3. Animation | Blender landing, breathing and attack envelopes; acceleration lean, lateral weight shift, creeping leg motion; corrected crouch/prone floor contact. |
| 4. Architecture | Ceramic eave strips, timber corbels and lattice window frames, with a bounded budget prioritizing the central landmark. |
| 5. Materials | Reduced uniform weathering, subtler fabric normals, damp surface gloss and restrained moss. |
| 6. Street dressing | 100 ground-supported groups of pottery, baskets, stacked timber and leaf litter, checked for clearance from existing geometry. |
| 7. Environmental motion | A shared wind clock animates bamboo leaves and hanging cloth; nearby lantern intensity varies subtly, and extinguished shells lose their glow. |
| 8. Combat feedback | Six pooled blade arcs and a 160-particle impact pool, visible hit/parry accents and local swing audio. Hidden contacts, duplicates and events behind geometry are suppressed. |
| 9. Camera | Small speed and impact FOV responses with adjustable MotionFeel. Existing shoulder alignment and wall collision remain intact. |

## Editable sources

- `VisualUpgradeKit.blend`: props, architecture pieces, equipment and keyed motion controls.
- `../NinjaRefined.blend`: body refinement with original vertex order and UVs.
- `../../Tools/build_visual_upgrade.py`: regenerate the kit, exported meshes, motion samples and studio preview using Blender.
- `../../Tools/refine_ninja.py`: regenerate the character refinement from the exported Unity source.
- Unity assets: `Assets/Unseen/Resources/BlenderArt/Upgrade*.bytes` and `NinjaBody.bytes`.

## Validation

[Open the before/after review](review.html). Final validation completed on 18 September 2026.

- Eleven exported mesh payloads passed scale, finite-geometry and collider checks.
- Refined body: 1,029 vertices, original skin weights/bind poses retained, measured height 1.800 m.
- Garment and animation-pose captures completed. Crouch sole samples range approximately −5 to +11 mm; prone samples −22 to +20 mm through its breathing cycle.
- Camera collision: all 180 tested view angles stayed on the correct side of walls; the old-camera negative control failed 78 angles.
- Real play frames: a 64-actor match, wind clock, direct/hidden/occluded combat, duplicate suppression, impact FOV and particle expiry passed.
- Final representative scene renders completed with the gameplay lantern budget and sky-bounce lighting.

Logs: `Logs/visual-upgrade-final.log`, `Logs/visual-upgrade-play.log`, and `Logs/visual-upgrade-highlights.log`. The isolated editor emitted a Unity Search indexing exception; the play harness excludes that specific editor-only stack from its gameplay error count. The checks above are targeted visual/runtime validation, not a claim that the entire project test suite was run.

 The repeatable editor entry point is `Unseen.EditorTools.UnseenVisualUpgradeValidation.Run`. It validates mesh payloads, contact filtering, skinning, renders the generated town and poses, checks garments and probes 180 camera angles against walls.

For actual play frames, copy `Tools/VisualUpgradePlayValidation.cs` into an isolated validation project's Editor folder and invoke `VisualUpgradePlayValidation.Run` without Unity's `-quit` flag. It exits with a result code after exercising a 64-actor match, wind, visible and occluded effects, deduplication, camera response and particle cleanup.

This is a polish pass on the existing character and procedural town, not a replacement character rig or an entirely new environment. No target-hardware frame-rate benchmark has been claimed.
