# Third-party assets

Every asset in this repository is **CC0 / public domain**. No attribution is legally required for
any of it; this file exists so provenance is recoverable, and so nobody has to re-derive whether a
texture is safe to ship.

## Textures — ambientCG (CC0 1.0 Universal)

Source: <https://ambientcg.com> · Licence: <https://docs.ambientcg.com/license/>
Downloaded at 1K-JPG. Each set was repacked for URP (see below) and lives in
`Assets/Unseen/Art/Textures/<Name>/`.

| In project | Source asset | Used for |
| --- | --- | --- |
| `Timber` | [WoodSiding008](https://ambientcg.com/a/WoodSiding008) | Compound walls, shoji frames |
| `WoodFloor` | [WoodFloor043](https://ambientcg.com/a/WoodFloor043) | Upper storeys, rafters, chests |
| `Tatami` | [Wicker013](https://ambientcg.com/a/Wicker013) | Interior floors — woven matting reads as tatami |
| `Stone` | [PavingStones151](https://ambientcg.com/a/PavingStones151) | Keep, sewers, access shafts |
| `RoofTile` | [RoofingTiles013A](https://ambientcg.com/a/RoofingTiles013A) | Roofs and eaves |
| `Ground` | [Ground110](https://ambientcg.com/a/Ground110) | Streets, courtyards, ground plane |
| `Paper` | [Fabric061](https://ambientcg.com/a/Fabric061) | Shoji panels, lantern shells |

## Sky — Poly Haven (CC0)

Source: <https://polyhaven.com/a/moonlit_golf> · Author: Greg Zaal · Licence: CC0
Stored as `Assets/Unseen/Art/Sky/MoonlitNight.hdr` (2K HDR).

Chosen because the stealth model already assumes moonlight: `Stealth.AmbientHiddenFloor` is 0.85, so
an unlit ninja is 85% hidden rather than invisible. The lighting the player sees should match the
arithmetic the server is doing.

## How the textures were repacked

ambientCG ships separate `Color`, `NormalGL`, `Roughness`, `AmbientOcclusion` and sometimes
`Metalness` maps. URP's Lit shader has no roughness input — it reads **smoothness from the alpha of
the metallic map** — so each set was repacked once, offline:

- `<Name>_Albedo.jpg` ← `Color`
- `<Name>_Normal.jpg` ← `NormalGL` (OpenGL convention, which is what Unity expects)
- `<Name>_Occlusion.jpg` ← `AmbientOcclusion`
- `<Name>_MetallicSmoothness.png` ← RGB from `Metalness` (black when absent), **alpha = 255 −
  Roughness**

`Unseen/Art/Build Materials From Textures` then builds the URP materials, sets the normal-map and
linear/alpha import settings, and fills in `Resources/GreyboxMaterialSet.asset`.

## Regenerating

```powershell
# after adding or replacing a texture set
& $UNITY -batchmode -nographics -quit -projectPath . `
         -executeMethod Unseen.EditorTools.UnseenArtSetup.BuildMaterials
```

The generator falls back to flat greybox colours whenever the material set is missing or incomplete,
so deleting `Assets/Unseen/Art` breaks nothing — the project just goes back to looking like a
greybox.
