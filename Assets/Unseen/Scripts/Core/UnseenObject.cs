using UnityEngine;

namespace Unseen.Core
{
    /// <summary>
    /// Object destruction that works in both a play session and the editor.
    ///
    /// <see cref="Object.Destroy"/> is deferred to the end of the frame and does nothing at all
    /// outside play mode, so runtime code that tears down placeholder geometry silently leaves it
    /// behind when driven by a tool or a test. That is not only a cosmetic problem: leftover
    /// colliders would join the physics scene and change line-of-sight and parkour results.
    /// </summary>
    public static class UnseenObject
    {
        public static void Destroy(Object target)
        {
            if (target == null) return;

            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }

        public static void Destroy(Component component)
        {
            if (component != null) Destroy((Object)component);
        }

        public static void DestroyGameObject(GameObject target)
        {
            if (target != null) Destroy((Object)target);
        }
    }
}
