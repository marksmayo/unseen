using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// The materials the greybox generator paints the town with. When this asset is absent the
    /// generator falls back to flat colours, so the project still runs with no art at all - which is
    /// how it was built in the first place.
    ///
    /// Create or refresh it with <c>Unseen ▸ Art ▸ Build Materials From Textures</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Unseen/Greybox Material Set", fileName = "GreyboxMaterialSet")]
    public sealed class GreyboxMaterialSet : ScriptableObject
    {
        public const string ResourcePath = "GreyboxMaterialSet";

        [Header("Structure")]
        [Tooltip("Keep walls, sewer walls and access shafts.")]
        public Material Stone;

        [Tooltip("Compound walls and shoji frames.")]
        public Material Timber;

        [Tooltip("Upper storeys, chests, rafters.")]
        public Material WoodFloor;

        [Tooltip("Lime-plaster wall infill between the timber framing. Falls back to Stone.")]
        public Material Plaster;

        [Tooltip("Posts, rails and eave boards. Deliberately darker than Timber so the framing " +
                 "reads against the plaster. Falls back to Timber.")]
        public Material DarkTimber;

        [Tooltip("Roofs and eaves.")]
        public Material RoofTile;

        [Header("Surfaces")]
        [Tooltip("Interior floors. Woven matting reads as tatami.")]
        public Material Tatami;

        [Tooltip("Streets, courtyards and the ground plane.")]
        public Material Ground;

        [Tooltip("River water. Falls back to Stone.")]
        public Material Water;

        [Tooltip("Tree canopies and shrubs. Falls back to Timber.")]
        public Material Foliage;

        [Tooltip("Shoji panels and lantern shells.")]
        public Material Paper;

        [Tooltip("Emissive lantern shell. Falls back to Paper when empty.")]
        public Material LanternGlow;

        [Tooltip("Shoji paper that can print silhouettes. Uses Unseen/ShojiSilhouette. Referenced " +
                 "here so the shader survives a player build - one reached only through " +
                 "Shader.Find is stripped and renders magenta. Falls back to Paper.")]
        public Material ShojiPaper;

        [Tooltip("Mist wall material. Referenced here so the shader survives player builds - a " +
                 "shader only reached via Shader.Find is stripped and renders magenta.")]
        public Material Mist;

        [Header("Sky")]
        [Tooltip("Panoramic skybox material. Applied at runtime along with ambient and fog.")]
        public Material Sky;

        [Tooltip("Ambient light from the sky. Moonlight should read as faint, not absent.")]
        [Range(0f, 2f)] public float AmbientIntensity = 0.45f;

        [Tooltip("Night fog. Also hides the HDRI's own horizon scenery beyond the map edge.")]
        [Range(0f, 0.05f)] public float FogDensity = 0.012f;

        public Color FogColor = new Color(0.06f, 0.07f, 0.12f);

        [Header("Scale")]
        [Tooltip("World metres covered by one texture repeat. Lower means finer detail.")]
        [Range(0.25f, 12f)] public float TextureMetres = 2.5f;

        /// <summary>True when every slot is filled and the set can be used.</summary>
        public bool IsComplete =>
            Stone != null && Timber != null && WoodFloor != null && RoofTile != null &&
            Tatami != null && Ground != null && Paper != null;

        /// <summary>Loads the set from Resources, or returns null when no art has been imported.</summary>
        public static GreyboxMaterialSet Load()
        {
            return Resources.Load<GreyboxMaterialSet>(ResourcePath);
        }
    }
}
