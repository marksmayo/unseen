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
