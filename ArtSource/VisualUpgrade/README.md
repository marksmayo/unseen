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

Results and review images are recorded after the final checks. The repeatable editor entry point is `Unseen.EditorTools.UnseenVisualUpgradeValidation.Run`. It validates mesh payloads, contact filtering, skinning, renders the generated town and poses, checks garments and probes 180 camera angles against walls.

For actual play frames, copy `Tools/VisualUpgradePlayValidation.cs` into an isolated validation project's Editor folder and invoke `VisualUpgradePlayValidation.Run` without Unity's `-quit` flag. It exits with a result code after exercising a 64-actor match, wind, visible and occluded effects, deduplication, camera response and particle cleanup.

This is a polish pass on the existing character and procedural town, not a replacement character rig or an entirely new environment. No target-hardware frame-rate benchmark has been claimed.
