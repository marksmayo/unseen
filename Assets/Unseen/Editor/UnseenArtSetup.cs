using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Unseen.Environment;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Turns the imported CC0 texture sets into URP materials, builds the greybox material set the
    /// town generator reads, and sets up the moonlit sky.
    ///
    /// Idempotent: run it again after changing a texture and it updates the existing materials rather
    /// than making duplicates.
    /// </summary>
    public static class UnseenArtSetup
    {
        private const string TextureRoot = "Assets/Unseen/Art/Textures";
        private const string MaterialRoot = "Assets/Unseen/Art/Materials";
        private const string SkyHdri = "Assets/Unseen/Art/Sky/MoonlitNight.hdr";
        private const string SkyMaterial = "Assets/Unseen/Art/Materials/MoonlitSky.mat";
        private const string SetPath = "Assets/Unseen/Resources/GreyboxMaterialSet.asset";

        /// <summary>Per-surface smoothness ceiling, so wet stone and dry paper do not read alike.</summary>
        private static readonly Dictionary<string, float> SmoothnessScale = new Dictionary<string, float>
        {
            { "Stone", 0.55f },
            { "Ground", 0.35f },
            { "Timber", 0.4f },
            { "WoodFloor", 0.5f },
            { "RoofTile", 0.6f },
            { "Tatami", 0.3f },
            { "Paper", 0.25f }
        };

        [MenuItem("Unseen/Art/Build Materials From Textures", priority = 50)]
        public static void BuildMaterials()
        {
            if (!Directory.Exists(TextureRoot))
            {
                Debug.LogError($"[Unseen] no textures at {TextureRoot}. Nothing to build.");
                return;
            }

            Directory.CreateDirectory(MaterialRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));

            ConfigureNormalMapImporters();

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) lit = Shader.Find("Standard");
            if (lit == null)
            {
                Debug.LogError("[Unseen] no usable lit shader found.");
                return;
            }

            var built = new Dictionary<string, Material>();
            foreach (string dir in Directory.GetDirectories(TextureRoot))
            {
                string name = Path.GetFileName(dir);
                Material material = BuildMaterial(lit, name);
                if (material != null) built[name] = material;
            }

            GreyboxMaterialSet set = AssetDatabase.LoadAssetAtPath<GreyboxMaterialSet>(SetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<GreyboxMaterialSet>();
                AssetDatabase.CreateAsset(set, SetPath);
            }

            set.Stone = Pick(built, "Stone");
            set.Timber = Pick(built, "Timber");
            set.WoodFloor = Pick(built, "WoodFloor");
            set.RoofTile = Pick(built, "RoofTile");
            set.Tatami = Pick(built, "Tatami");
            set.Ground = Pick(built, "Ground");
            set.Paper = Pick(built, "Paper");

            // Two tinted variants rather than two more texture sets: the framing only has to read
            // darker than the wall it sits on, and a recolour of an existing surface does that
            // without another 4 MB of maps to import and keep in sync.
            // Derived from Paper, not Stone: lime plaster is a flat, fine surface, and the stone
            // albedo tiled at wall scale read as pebbledash.
            set.Plaster = BuildTint(lit, Pick(built, "Paper"), "Plaster",
                new Color(0.62f, 0.60f, 0.55f), 0.10f, bumpScale: 0.3f);
            set.DarkTimber = BuildTint(lit, Pick(built, "Timber"), "DarkTimber",
                new Color(0.30f, 0.22f, 0.16f), 0.28f);

            set.Water = BuildTint(lit, Pick(built, "Ground"), "Water",
                new Color(0.10f, 0.17f, 0.20f), 0.85f, bumpScale: 0.5f);
            set.Foliage = BuildTint(lit, Pick(built, "Tatami"), "Foliage",
                new Color(0.13f, 0.20f, 0.13f), 0.2f, bumpScale: 0.7f);

            set.LanternGlow = BuildLanternGlow(lit, Pick(built, "Paper"));
            set.ShojiPaper = BuildShojiPaper(Pick(built, "Paper"));
            set.Mist = BuildMistMaterial();

            // Playability: the first pass was atmospheric but unreadable in a street away from a
            // lantern. Lift ambient and thin the fog; relative darkness still drives stealth.
            set.AmbientIntensity = 0.8f;

            // Thinned for the 750 m town. At 0.008 the exponential-squared falloff was total by
            // 400 m, so the far half of the map rendered as flat black from any roof.
            set.FogDensity = 0.0032f;
            set.FogColor = new Color(0.05f, 0.06f, 0.11f);
            set.Sky = BuildSky();
            EditorUtility.SetDirty(set);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Unseen] built {built.Count} materials; material set complete: {set.IsComplete}");
            if (!set.IsComplete)
                Debug.LogWarning("[Unseen] some slots are empty - the generator will fall back to flat colours.");
        }

        /// <summary>
        /// An emissive version of the paper material, so a lit lantern reads as a glowing object and
        /// not just a light source with an unlit box around it.
        /// </summary>
        private static Material BuildLanternGlow(Shader shader, Material paper)
        {
            const string path = MaterialRoot + "/LanternGlow.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;

            // Prefer the purpose-made ribbed paper; fall back to the generic fabric if it is absent.
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureRoot}/Lantern/Lantern_Albedo.jpg");
            var emission = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureRoot}/Lantern/Lantern_Emission.jpg");

            if (albedo != null)
            {
                material.SetTexture("_BaseMap", albedo);
                material.SetTexture("_MainTex", albedo);
            }
            else if (paper != null && paper.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", paper.GetTexture("_BaseMap"));
            }

            material.SetColor("_BaseColor", new Color(1f, 0.92f, 0.75f));
            material.EnableKeyword("_EMISSION");

            if (emission != null) material.SetTexture("_EmissionMap", emission);
            // Kept low deliberately: the lantern also carries a real point light, so a hot
            // emission on top of that blows the paper to flat white and loses the ribs.
            material.SetColor("_EmissionColor", new Color(1f, 0.72f, 0.38f) * 1.1f);
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;

            material.SetFloat("_Smoothness", 0.12f); // paper, not porcelain
            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);

            Debug.Log($"[Unseen] lantern material: albedo={(albedo != null)} emission={(emission != null)}");
            return material;
        }

        /// <summary>
        /// The mist wall material. It has to exist as an asset: a shader reached only through
        /// Shader.Find at runtime is stripped from player builds and renders magenta.
        /// </summary>
        private static Material BuildMistMaterial()
        {
            Shader shader = Shader.Find("Unseen/MistWall");
            if (shader == null)
            {
                Debug.LogWarning("[Unseen] Unseen/MistWall shader not found.");
                return null;
            }

            const string path = MaterialRoot + "/MistWall.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material Pick(Dictionary<string, Material> built, string key)
        {
            return built.TryGetValue(key, out Material m) ? m : null;
        }

        /// <summary>
        /// A recoloured copy of an existing material: same maps, different base colour and
        /// smoothness. Used for the plaster infill and the dark framing timber.
        /// </summary>
        private static Material BuildTint(Shader shader, Material source, string name,
            Color colour, float smoothness, float bumpScale = 1f)
        {
            if (source == null) return null;

            string path = $"{MaterialRoot}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            CopyTexture(source, material, "_BaseMap");
            CopyTexture(source, material, "_MainTex");
            CopyTexture(source, material, "_BumpMap");
            CopyTexture(source, material, "_OcclusionMap");

            if (source.IsKeywordEnabled("_NORMALMAP"))
            {
                material.EnableKeyword("_NORMALMAP");
                // Plaster borrows the paper normal map, which at wall scale reads as insect
                // screen at full strength. Dialled back it is just a surface, not a weave.
                material.SetFloat("_BumpScale", bumpScale);
            }

            material.SetColor("_BaseColor", colour);
            material.SetColor("_Color", colour);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void CopyTexture(Material from, Material to, string property)
        {
            if (!from.HasProperty(property) || !to.HasProperty(property)) return;
            to.SetTexture(property, from.GetTexture(property));
        }

        /// <summary>
        /// The shoji material, on the silhouette shader.
        ///
        /// This is the piece that was missing. The server has always decided who is visible as a
        /// silhouette, and ShojiSilhouetteFeeder has always pushed those contacts into the global
        /// shader properties - but no material used the shader, so all five thousand panels
        /// rendered as plain lit paper and the data went nowhere. The feature was marked done and
        /// could not occur.
        /// </summary>
        private static Material BuildShojiPaper(Material paper)
        {
            Shader shader = Shader.Find("Unseen/ShojiSilhouette");
            if (shader == null)
            {
                Debug.LogError("[Unseen] Unseen/ShojiSilhouette not found; shoji cannot print silhouettes");
                return paper;
            }

            const string path = MaterialRoot + "/ShojiPaper.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;

            if (paper != null && paper.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", paper.GetTexture("_BaseMap"));
                material.SetVector("_BaseMap_ST", paper.GetVector("_BaseMap_ST"));
            }

            material.SetColor("_BaseColor", new Color(0.86f, 0.83f, 0.72f, 0.94f));
            material.SetColor("_SilhouetteColor", new Color(0.05f, 0.045f, 0.07f, 1f));
            material.SetFloat("_Radius", 0.95f);
            material.SetFloat("_Softness", 0.7f);
            material.SetFloat("_Grain", 0.3f);
            EditorUtility.SetDirty(material);

            Debug.Log("[Unseen] shoji paper material built on Unseen/ShojiSilhouette");
            return material;
        }

        private static Material BuildMaterial(Shader shader, string name)
        {
            string folder = $"{TextureRoot}/{name}";
            Texture2D albedo = Load(folder, name, "Albedo");
            if (albedo == null)
            {
                Debug.LogWarning($"[Unseen] {name}: no albedo, skipped.");
                return null;
            }

            Texture2D normal = Load(folder, name, "Normal");
            Texture2D occlusion = Load(folder, name, "Occlusion");
            Texture2D metallic = Load(folder, name, "MetallicSmoothness");

            string path = $"{MaterialRoot}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            material.SetTexture("_BaseMap", albedo);
            material.SetTexture("_MainTex", albedo); // built-in fallback shader

            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.SetFloat("_BumpScale", 1f);
                material.EnableKeyword("_NORMALMAP");
            }

            if (occlusion != null)
            {
                material.SetTexture("_OcclusionMap", occlusion);
                material.SetFloat("_OcclusionStrength", 0.85f);
                material.EnableKeyword("_OCCLUSIONMAP");
            }

            if (metallic != null)
            {
                // Smoothness lives in this texture's alpha; the scalar becomes a ceiling on it.
                material.SetTexture("_MetallicGlossMap", metallic);
                material.EnableKeyword("_METALLICSPECGLOSSMAP");
                material.SetFloat("_Smoothness",
                    SmoothnessScale.TryGetValue(name, out float s) ? s : 0.4f);
                material.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = metallic alpha
            }

            material.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Texture2D Load(string folder, string name, string suffix)
        {
            foreach (string ext in new[] { "png", "jpg" })
            {
                string path = $"{folder}/{name}_{suffix}.{ext}";
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex != null) return tex;
            }

            return null;
        }

        /// <summary>Normal maps must be imported as normal maps, or the lighting comes out wrong.</summary>
        private static void ConfigureNormalMapImporters()
        {
            foreach (string guid in AssetDatabase.FindAssets("_Normal t:Texture2D", new[] { TextureRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("_Normal.jpg") && !path.EndsWith("_Normal.png")) continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.textureType == TextureImporterType.NormalMap) continue;

                importer.textureType = TextureImporterType.NormalMap;
                importer.SaveAndReimport();
            }

            // The packed metallic/smoothness map must keep its alpha and stay linear.
            foreach (string guid in AssetDatabase.FindAssets("_MetallicSmoothness t:Texture2D", new[] { TextureRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                if (!importer.sRGBTexture && importer.alphaSource == TextureImporterAlphaSource.FromInput) continue;

                importer.sRGBTexture = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;
                importer.SaveAndReimport();
            }
        }

        [MenuItem("Unseen/Art/Build Moonlit Sky", priority = 51)]
        public static void BuildSkyMenu()
        {
            BuildSky();
        }

        /// <summary>Creates the panoramic sky material and returns it for the material set.</summary>
        public static Material BuildSky()
        {
            var hdri = AssetDatabase.LoadAssetAtPath<Texture>(SkyHdri);
            if (hdri == null)
            {
                Debug.LogWarning($"[Unseen] no HDRI at {SkyHdri}; leaving the sky alone.");
                return null;
            }

            // Prefer our own sky: it discards the HDRI's landscape below the horizon, which the
            // stock panoramic shader cannot do and fog cannot hide.
            Shader panoramic = Shader.Find("Unseen/NightSky");
            if (panoramic == null) panoramic = Shader.Find("Skybox/Panoramic");
            if (panoramic == null)
            {
                Debug.LogWarning("[Unseen] no skybox shader found.");
                return null;
            }

            Material sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterial);
            if (sky == null)
            {
                sky = new Material(panoramic);
                AssetDatabase.CreateAsset(sky, SkyMaterial);
            }

            sky.shader = panoramic;
            sky.SetTexture("_MainTex", hdri);
            if (sky.HasProperty("_Mapping")) sky.SetFloat("_Mapping", 1f); // lat-long, stock shader
            sky.SetFloat("_Exposure", 1.0f);
            if (sky.HasProperty("_HorizonSoftness")) sky.SetFloat("_HorizonSoftness", 0.20f);
            if (sky.HasProperty("_HorizonLift")) sky.SetFloat("_HorizonLift", 0.26f);
            EditorUtility.SetDirty(sky);

            RenderSettings.skybox = sky;

            // Moonlight is dim and blue, and the stealth model assumes it: AmbientHiddenFloor leaves
            // an unlit ninja 85% hidden rather than invisible, so the sky should light the town
            // faintly rather than not at all.
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 0.45f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.006f;
            RenderSettings.fogColor = new Color(0.06f, 0.07f, 0.12f);

            Debug.Log("[Unseen] moonlit sky, skybox ambient and night fog configured.");
            return sky;
        }
    }
}
