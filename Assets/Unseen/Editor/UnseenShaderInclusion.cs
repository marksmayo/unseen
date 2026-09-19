using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Unseen.EditorTools
{
    /// <summary>
    /// Keeps shaders that are only ever reached by name in the build.
    ///
    /// A shader nothing references is stripped, and `Shader.Find` then returns null in a player
    /// build while working perfectly in the editor - where every shader is loaded whether anything
    /// wants it or not. The lucky version of this renders magenta. The unlucky version is
    /// `new Material(null)`, which throws in Start and takes the rest of that component's setup
    /// with it.
    ///
    /// Both were happening: RiverSurfaceMotion and WeatherAtmosphere each threw an
    /// ArgumentNullException on the first frame of every build, so the river had no surface motion
    /// and there was no rain, and nothing said so unless you read the player log. The project knows
    /// about this trap - GreyboxMaterialSet and MistVisual both carry comments about it - and the
    /// established answer is to reach shaders through a material asset. This is the safety net for
    /// the places that do not.
    ///
    /// Run from the menu, or from a build script before building.
    /// </summary>
    public static class UnseenShaderInclusion
    {
        /// <summary>
        /// Shaders this project reaches by name at runtime.
        ///
        /// Keep it in step with `Shader.Find` calls. A name that no longer resolves is reported
        /// rather than skipped: a typo here would silently restore the exact problem this exists
        /// to prevent.
        /// </summary>
        private static readonly string[] ReachedByName =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Lit",
            "Sprites/Default"
        };

        [MenuItem("Unseen/Build/Include Runtime Shaders", priority = 62)]
        public static void Run()
        {
            int added = Ensure();
            Debug.Log($"[shaders] always-included list checked; {added} added");
        }

        /// <summary>Adds any missing shader to Always Included. Returns how many were added.</summary>
        public static int Ensure()
        {
            var settings = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");

            if (settings == null)
            {
                Debug.LogError("[shaders] could not open GraphicsSettings.asset");
                return 0;
            }

            var serialized = new SerializedObject(settings);
            SerializedProperty list = serialized.FindProperty("m_AlwaysIncludedShaders");

            if (list == null)
            {
                Debug.LogError("[shaders] no m_AlwaysIncludedShaders property; Unity changed the format");
                return 0;
            }

            var already = new HashSet<Shader>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var existing = list.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (existing != null) already.Add(existing);
            }

            int added = 0;

            foreach (string name in ReachedByName)
            {
                Shader shader = Shader.Find(name);

                if (shader == null)
                {
                    // Loud, because the alternative is a list that quietly protects nothing.
                    Debug.LogError($"[shaders] '{name}' does not resolve even in the editor. " +
                                   "Either the name is wrong or the package that provides it is gone.");
                    continue;
                }

                if (!already.Add(shader)) continue;

                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                added++;

                Debug.Log($"[shaders] added '{name}'");
            }

            if (added > 0)
            {
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }

            return added;
        }
    }
}
