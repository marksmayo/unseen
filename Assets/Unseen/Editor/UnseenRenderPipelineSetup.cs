using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Creates and assigns the Universal Render Pipeline asset.
    ///
    /// The project was authored for URP - the shoji silhouette and mist shaders include URP's
    /// ShaderLibrary, and every generated material uses URP/Lit - but a hand-assembled project has no
    /// pipeline asset, so Unity silently falls back to the built-in renderer and those shaders break.
    ///
    /// Values are written through SerializedObject rather than the typed API: the serialized names are
    /// stable across URP versions, and each one is checked for existence before being set, so a URP
    /// upgrade degrades to "left at default" instead of failing to compile.
    /// </summary>
    public static class UnseenRenderPipelineSetup
    {
        private const string Folder = "Assets/Unseen/Settings";
        private const string RendererPath = Folder + "/UnseenRenderer.asset";
        private const string PipelinePath = Folder + "/UnseenRenderPipeline.asset";

        [MenuItem("Unseen/Setup/Create And Assign URP Asset", priority = 24)]
        public static void CreateAndAssign()
        {
            Directory.CreateDirectory(Folder);

            UniversalRendererData renderer = LoadOrCreateRenderer();
            UniversalRenderPipelineAsset pipeline = LoadOrCreatePipeline(renderer);

            ConfigurePipeline(pipeline);
            ConfigureRenderer(renderer);
            EnsureAmbientOcclusion(renderer);

            GraphicsSettings.defaultRenderPipeline = pipeline;

            // Clear per-quality-level overrides so every level uses the one pipeline asset.
            int original = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = null;
            }

            QualitySettings.SetQualityLevel(original, false);

            EditorUtility.SetDirty(pipeline);
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Unseen] URP assigned: {AssetDatabase.GetAssetPath(pipeline)} " +
                      $"(active pipeline: {GraphicsSettings.currentRenderPipeline?.name ?? "none"})");
        }

        private static UniversalRendererData LoadOrCreateRenderer()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (data != null) return data;

            data = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(data, RendererPath);
            return data;
        }

        private static UniversalRenderPipelineAsset LoadOrCreatePipeline(UniversalRendererData renderer)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                AssetDatabase.CreateAsset(asset, PipelinePath);
            }

            // The renderer list is not publicly settable, so wire it through serialisation.
            var so = new SerializedObject(asset);
            SerializedProperty list = so.FindProperty("m_RendererDataList");
            if (list != null)
            {
                list.arraySize = 1;
                list.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
                SerializedProperty index = so.FindProperty("m_DefaultRendererIndex");
                if (index != null) index.intValue = 0;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[Unseen] could not find m_RendererDataList; assign the renderer by hand.");
            }

            return asset;
        }

        private static void ConfigurePipeline(UniversalRenderPipelineAsset asset)
        {
            var so = new SerializedObject(asset);

            // A night-time stealth game lives and dies on shadows and many small lights.
            Set(so, "m_SupportsHDR", true);
            Set(so, "m_MainLightShadowsSupported", true);
            Set(so, "m_MainLightShadowmapResolution", 2048);
            Set(so, "m_AdditionalLightsRenderingMode", (int)LightRenderingMode.PerPixel);
            Set(so, "m_AdditionalLightShadowsSupported", true);
            Set(so, "m_AdditionalLightsShadowmapResolution", 1024);
            Set(so, "m_ShadowDistance", 90f);
            Set(so, "m_SoftShadowsSupported", true);
            Set(so, "m_MSAA", 2);

            // The depth texture is what lets transparent surfaces know what is behind them.
            //
            // Without it the mist and smoke shaders cannot soften where a panel cuts through a
            // wall, a street or the river, so every one of them draws a hard straight line at the
            // intersection - which is what reads in motion as a transparent box sliding about the
            // scene. It costs a depth prepass; SSAO already asks for one, so this is close to free
            // now and it is the difference between fog and a stack of visible quads.
            Set(so, "m_RequireDepthTexture", true);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureRenderer(UniversalRendererData renderer)
        {
            var so = new SerializedObject(renderer);

            // Forward+ lifts the per-object additional-light cap, which matters because the town is
            // lit by ~90 lantern point lights and every one of them is a gameplay object: putting a
            // lantern out is supposed to visibly grow the shadow you are standing in.
            SerializedProperty mode = so.FindProperty("m_RenderingMode");
            if (mode != null) mode.intValue = (int)RenderingMode.ForwardPlus;

            Set(so, "m_DepthPrimingMode", 0);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Adds screen-space ambient occlusion to the renderer, unless it is already there.
        ///
        /// Contact darkening is not decoration in this game. The player is asked to read which patch
        /// of ground is dark enough to stand in, and without AO a wall meets the street with no
        /// gradient at all - every surface is a flat tone right up to the join, so nothing reads as
        /// resting on anything.
        ///
        /// Feature assets live as sub-assets of the renderer and are tracked in two parallel
        /// serialized lists: m_RendererFeatures holds the reference, m_RendererFeatureMap holds the
        /// sub-asset's local file id. Both must be grown together - URP's own inspector does exactly
        /// this, and a feature added to only one of them is silently ignored.
        /// </summary>
        private static void EnsureAmbientOcclusion(UniversalRendererData renderer)
        {
            foreach (ScriptableRendererFeature existing in renderer.rendererFeatures)
            {
                if (existing is ScreenSpaceAmbientOcclusion)
                {
                    ConfigureAmbientOcclusion(existing);
                    return;
                }
            }

            var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            ssao.name = nameof(ScreenSpaceAmbientOcclusion);
            ssao.hideFlags |= HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(ssao, renderer);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out _, out long localId);

            var so = new SerializedObject(renderer);
            SerializedProperty features = so.FindProperty("m_RendererFeatures");
            SerializedProperty map = so.FindProperty("m_RendererFeatureMap");

            if (features == null || map == null)
            {
                Debug.LogWarning("[Unseen] renderer feature lists not found; add SSAO by hand.");
                return;
            }

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = ssao;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();

            ConfigureAmbientOcclusion(ssao);
            Debug.Log("[Unseen] SSAO renderer feature added");
        }

        /// <summary>
        /// Tunes SSAO for a town lit almost entirely by lanterns.
        ///
        /// DirectLightingStrength is pushed well above URP's 0.25 default on purpose. AO normally
        /// occludes only the ambient term, and this scene's ambient is 0.14 - there is almost
        /// nothing there to take away, so at the default the effect is invisible exactly where it
        /// matters most, inside a lantern pool. Radius is in world metres: 0.3 m darkens the join
        /// where a wall meets the street without smearing shade across a whole courtyard.
        ///
        /// The settings type is internal to URP, so it is reached through serialisation rather than
        /// the typed API - the same reason, and the same trade, as the pipeline values above.
        /// </summary>
        private static void ConfigureAmbientOcclusion(ScriptableRendererFeature feature)
        {
            var so = new SerializedObject(feature);
            SerializedProperty settings = so.FindProperty("m_Settings");
            if (settings == null)
            {
                Debug.LogWarning("[Unseen] SSAO settings not found; left at defaults.");
                return;
            }

            SetChild(settings, "Intensity", 2.0f);
            SetChild(settings, "Radius", 0.3f);
            SetChild(settings, "DirectLightingStrength", 0.45f);
            SetChild(settings, "Falloff", 60f);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetChild(SerializedProperty parent, string name, float value)
        {
            SerializedProperty p = parent.FindPropertyRelative(name);
            if (p != null) p.floatValue = value;
        }

        private static void Set(SerializedObject so, string path, bool value)
        {
            SerializedProperty p = so.FindProperty(path);
            if (p != null) p.boolValue = value;
        }

        private static void Set(SerializedObject so, string path, int value)
        {
            SerializedProperty p = so.FindProperty(path);
            if (p != null) p.intValue = value;
        }

        private static void Set(SerializedObject so, string path, float value)
        {
            SerializedProperty p = so.FindProperty(path);
            if (p != null) p.floatValue = value;
        }
    }
}
