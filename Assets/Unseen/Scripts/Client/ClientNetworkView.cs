using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// The client half of the netcode. It sends intent up, decodes snapshots coming down, and keeps
    /// exactly one proxy alive per entity the server says this player can perceive. When a contact
    /// drops out of the snapshot its proxy is destroyed, because on this machine that ninja really
    /// has ceased to exist.
    /// </summary>
    public sealed class ClientNetworkView : MonoBehaviour
    {
        [Tooltip("Optional proxy prefab. A greybox capsule is generated when this is null.")]
        public GameObject ProxyPrefab;

        [Tooltip("Optional smoke prefab used when replaying a smoke bomb event.")]
        public GameObject SmokePrefab;

        [Tooltip("Seconds a proxy survives after it stops appearing in snapshots.")]
        public float ProxyLinger = 0.4f;

        [Tooltip("Input sends per second.")]
        public int InputSendRate = 60;

        private readonly NetReader _reader = new NetReader();
        private readonly NetWriter _writer = new NetWriter(256);
        private readonly SnapshotData _snapshot = new SnapshotData();
        private readonly Dictionary<int, EntityProxy> _proxies = new Dictionary<int, EntityProxy>(64);
        private readonly List<int> _stale = new List<int>(16);

        private INetworkService _net;
        private EntityRegistry _localAgents;
        private UnseenConfig _config;
        private DestructibleRegistry _destructibles;
        private PlayerInputSource _input;
        private Transform _proxyRoot;
        private float _nextInputSendAt;

        public SnapshotData Latest => _snapshot;
        public int ProxyCount => _proxies.Count;
        public int SnapshotsReceived { get; private set; }
        public long BytesReceived { get; private set; }

        /// <summary>Raised after each snapshot is applied. The HUD and audio layers hang off this.</summary>
        public event Action<SnapshotData> SnapshotApplied;

        /// <summary>
        /// <paramref name="localAgents"/> is the server's own registry when this process is also the
        /// server. Proxies are then skipped for agents that already exist locally: rendering both a
        /// collided server agent and a smoothed, collider-less proxy of the same ninja produces a
        /// ghost that visibly drifts through walls beside the real one.
        /// </summary>
        public void Bind(INetworkService net, UnseenConfig config, DestructibleRegistry destructibles,
            PlayerInputSource input, EntityRegistry localAgents = null)
        {
            _net = net;
            _localAgents = localAgents;
            _config = config;
            _destructibles = destructibles;
            _input = input;

            _proxyRoot = new GameObject("Proxies").transform;
            _proxyRoot.SetParent(transform, false);

            _net.ClientReceived += OnClientReceived;
        }

        private void OnDestroy()
        {
            if (_net != null) _net.ClientReceived -= OnClientReceived;
        }

        private void Update()
        {
            if (_net == null || _input == null) return;

            float interval = 1f / Mathf.Max(1, InputSendRate);
            if (Time.unscaledTime < _nextInputSendAt) return;
            _nextInputSendAt = Time.unscaledTime + interval;

            _writer.Reset();
            SnapshotProtocol.EncodeInput(_writer, _input.Current);
            _net.SendToServer(_writer.Buffer, _writer.Length, false);
        }

        private void OnClientReceived(byte[] payload, int length)
        {
            _reader.Attach(payload, length);
            if (!SnapshotProtocol.DecodeSnapshot(_reader, _snapshot, _config.Network.PositionQuantum)) return;

            SnapshotsReceived++;
            BytesReceived += length;

            ApplyEntities(_snapshot);
            ApplyWorldEvents(_snapshot);
            ApplyCombatEvents(_snapshot);
            SnapshotApplied?.Invoke(_snapshot);
        }

        /// <summary>
        /// Reacts to replicated combat beats. Only death matters here so far: a proxy that dies has
        /// to play the death scene, because a pure client has no server agent to hang it off.
        ///
        /// The listen-server path in UnseenBootstrap handles the agents this process owns. Both
        /// exist because both are real cases, and the visual has an internal guard against being
        /// started twice.
        /// </summary>
        private void ApplyCombatEvents(SnapshotData snapshot)
        {
            for (int i = 0; i < snapshot.Combat.Count; i++)
            {
                CombatEvent e = snapshot.Combat[i];
                if (e.Kind != CombatEventKind.Death) continue;

                if (!_proxies.TryGetValue(e.Victim.Value, out EntityProxy proxy) || proxy == null)
                    continue;

                var death = proxy.GetComponent<AgentDeathVisual>();
                if (death == null) death = proxy.gameObject.AddComponent<AgentDeathVisual>();

                // Fall away from wherever the blow came from; the event carries the attacker.
                Vector3 from = Vector3.zero;
                if (_proxies.TryGetValue(e.Attacker.Value, out EntityProxy attacker) && attacker != null)
                    from = attacker.transform.position - proxy.transform.position;

                death.Play(from);
            }
        }

        private void ApplyEntities(SnapshotData snapshot)
        {
            float now = Time.time;

            for (int i = 0; i < snapshot.Entities.Count; i++)
            {
                VisibleEntity e = snapshot.Entities[i];

                // Already present locally (host or offline): the real agent is being rendered, so a
                // proxy would only add a drifting duplicate.
                if (_localAgents != null && _localAgents.Get(e.Id) != null) continue;

                if (!_proxies.TryGetValue(e.Id.Value, out EntityProxy proxy) || proxy == null)
                {
                    proxy = CreateProxy(e);
                    _proxies[e.Id.Value] = proxy;
                    proxy.Apply(e.Position, e.Yaw, e.Kind, e.Flags, now, snap: true);
                    continue;
                }

                proxy.Apply(e.Position, e.Yaw, e.Kind, e.Flags, now);
            }

            // Anything that stopped being reported is genuinely gone from this client world.
            _stale.Clear();
            foreach (KeyValuePair<int, EntityProxy> kv in _proxies)
            {
                if (kv.Value == null)
                {
                    _stale.Add(kv.Key);
                    continue;
                }

                if (now - kv.Value.LastUpdateTime > ProxyLinger) _stale.Add(kv.Key);
            }

            for (int i = 0; i < _stale.Count; i++)
            {
                if (_proxies.TryGetValue(_stale[i], out EntityProxy doomed) && doomed != null)
                    Destroy(doomed.gameObject);
                _proxies.Remove(_stale[i]);
            }
        }

        private void ApplyWorldEvents(SnapshotData snapshot)
        {
            if (_destructibles == null) return;

            for (int i = 0; i < snapshot.World.Count; i++)
            {
                WorldEvent e = snapshot.World[i];
                _destructibles.ApplyEvent(in e, SmokePrefab);
            }
        }

        private EntityProxy CreateProxy(VisibleEntity entity)
        {
            GameObject go;
            if (ProxyPrefab != null)
            {
                go = Instantiate(ProxyPrefab, entity.Position, Quaternion.identity, _proxyRoot);
            }
            else
            {
                go = new GameObject($"proxy-{entity.Id.Value}");
                go.transform.SetParent(_proxyRoot, false);
                go.transform.position = entity.Position;

                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                body.transform.SetParent(go.transform, false);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                body.transform.localScale = new Vector3(0.64f, 0.9f, 0.64f);
                UnseenObject.Destroy(body.GetComponent<Collider>());

                GameObject facing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                facing.name = "Facing";
                facing.transform.SetParent(go.transform, false);
                facing.transform.localPosition = new Vector3(0f, 1.45f, 0.35f);
                facing.transform.localScale = new Vector3(0.12f, 0.12f, 0.3f);
                UnseenObject.Destroy(facing.GetComponent<Collider>());
            }

            EntityProxy proxy = go.GetComponent<EntityProxy>();
            if (proxy == null) proxy = go.AddComponent<EntityProxy>();
            proxy.Id = entity.Id;

            // Swap the placeholder capsule for the ninja body when one exists. A silhouette contact
            // keeps its anonymity through EntityProxy's appearance handling, not through the mesh.
            AgentVisualSet set = AgentVisualSet.Load();
            if (set != null && set.IsUsable && ProxyPrefab == null)
            {
                Transform placeholder = go.transform.Find("Body");
                if (placeholder != null) UnseenObject.DestroyGameObject(placeholder.gameObject);
                Transform nose = go.transform.Find("Facing");
                if (nose != null) UnseenObject.DestroyGameObject(nose.gameObject);
                set.Attach(go.transform, entity.Id.Value);
            }

            return proxy;
        }

        /// <summary>Clears every proxy, e.g. between matches.</summary>
        public void ClearProxies()
        {
            foreach (KeyValuePair<int, EntityProxy> kv in _proxies)
                if (kv.Value != null) Destroy(kv.Value.gameObject);
            _proxies.Clear();
        }
    }
}
