using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Unseen.Environment
{
    /// <summary>
    /// A volume of smoke. Inside it agents gain a large stealth bonus, which in turn shortens the
    /// range at which anyone can resolve them - the same mechanism as standing in a dark room.
    /// </summary>
    public sealed class SmokeCloud : MonoBehaviour
    {
        private static readonly List<SmokeCloud> Clouds = new List<SmokeCloud>(32);

        public float Radius = 6f;
        public float Duration = 8f;

        [Tooltip("Seconds spent expanding to full radius.")]
        public float GrowDuration = 0.6f;

        private float _age;

        public static IReadOnlyList<SmokeCloud> All => Clouds;
        public float CurrentRadius => Radius * Mathf.Clamp01(GrowDuration <= 0f ? 1f : _age / GrowDuration);

        private void OnEnable()
        {
            Clouds.Add(this);
        }

        private void OnDisable()
        {
            Clouds.Remove(this);
        }

        private void Update()
        {
            _age += Time.deltaTime;
            if (_age >= Duration) Destroy(gameObject);
        }

        public static bool Covers(float3 point)
        {
            for (int i = 0; i < Clouds.Count; i++)
            {
                SmokeCloud c = Clouds[i];
                if (c == null) continue;
                float r = c.CurrentRadius;
                if (math.distancesq(point, (float3)c.transform.position) <= r * r) return true;
            }

            return false;
        }

        public static SmokeCloud Spawn(GameObject prefab, float3 position, float radius, float duration)
        {
            GameObject go = prefab != null
                ? Instantiate(prefab, position, Quaternion.identity)
                : new GameObject("SmokeCloud");

            go.transform.position = position;
            SmokeCloud cloud = go.GetComponent<SmokeCloud>();
            if (cloud == null) cloud = go.AddComponent<SmokeCloud>();
            cloud.Radius = radius;
            cloud.Duration = duration;
            return cloud;
        }
    }
}
