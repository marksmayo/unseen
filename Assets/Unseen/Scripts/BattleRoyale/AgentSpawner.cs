using Unity.Mathematics;
using UnityEngine;
using Unseen.AI;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Items;
using Unseen.Movement;

namespace Unseen.BattleRoyale
{
    /// <summary>
    /// Builds agents. A prefab is used when one is supplied; otherwise a fully functional
    /// capsule ninja is assembled in code, which is what lets the 64-entity netcode stress test
    /// run before any art exists.
    /// </summary>
    public sealed class AgentSpawner
    {
        private readonly SimContext _ctx;
        private readonly Transform _root;
        private readonly GameObject _prefab;
        private readonly bool _createVisuals;

        public AgentSpawner(SimContext ctx, Transform root, GameObject prefab, bool createVisuals = true)
        {
            _ctx = ctx;
            _root = root;
            _prefab = prefab;
            _createVisuals = createVisuals;
        }

        public AgentEntity Spawn(AgentKind kind, int connectionId, float3 position, string displayName)
        {
            GameObject go = _prefab != null
                ? Object.Instantiate(_prefab, position, Quaternion.identity, _root)
                : BuildCapsuleAgent(displayName, position);

            go.name = displayName;
            go.transform.SetParent(_root, true);
            go.layer = UnseenLayers.Ninja;

            // The rig has to exist before AgentEntity caches its component references.
            EnsureComponents(go);

            AgentEntity agent = go.GetComponent<AgentEntity>();
            if (agent == null) agent = go.AddComponent<AgentEntity>();

            agent.Kind = kind;
            agent.ConnectionId = connectionId;
            agent.DisplayName = displayName;

            EnsureAnchors(agent, go);
            agent.CacheComponents();

            agent.Vitals.Configure(_ctx.Config.Combat.MaxHealth);
            agent.Motor.Bind(agent);

            if (kind == AgentKind.Bot)
            {
                BotBrain brain = go.GetComponent<BotBrain>();
                if (brain == null) brain = go.AddComponent<BotBrain>();
                brain.Bind(agent);
                agent.Brain = brain;
            }

            AttachVisual(agent);

            if (go.GetComponent<AgentDeathVisual>() == null) go.AddComponent<AgentDeathVisual>();

            _ctx.Entities.Register(agent);
            agent.ResetForMatch();
            GiveStartingKit(agent);
            agent.Motor.Teleport(position);
            return agent;
        }

        /// <summary>
        /// One smoke bomb, for everyone, at spawn.
        ///
        /// A pure loot-from-zero start left the three utility keys doing nothing at all until the
        /// first chest, which reads as broken rather than as an empty inventory. One item is enough
        /// to teach the slot exists without deciding the fight.
        /// </summary>
        private void GiveStartingKit(AgentEntity agent)
        {
            if (agent.Inventory == null) return;
            if (_startingSmoke == null) _startingSmoke = BuildStartingSmoke();
            agent.Inventory.TryAdd(_startingSmoke);
        }

        private static ItemDefinition _startingSmoke;

        private static ItemDefinition BuildStartingSmoke()
        {
            var item = ScriptableObject.CreateInstance<ItemDefinition>();
            item.name = "smoke-issue";
            item.Id = "smoke-issue";
            item.DisplayName = "Smoke Bomb";
            item.Kind = ItemKind.Utility;
            item.Effect = UtilityEffect.SmokeBomb;
            item.EffectRadius = 4.5f;
            item.EffectDuration = 7f;
            item.EffectLoudness = 1.1f;
            item.EffectSoundRadius = 20f;
            item.ThrowSpeed = 14f;
            return item;
        }

        /// <summary>
        /// Adds the cosmetic body. Kept strictly separate from the rig above: the capsule, the eye
        /// anchor and the torso anchor are the gameplay shape, and the mesh is only ever decoration.
        /// </summary>
        private void AttachVisual(AgentEntity agent)
        {
            if (!_createVisuals) return;

            AgentVisualSet set = AgentVisualSet.Load();
            if (set == null || !set.IsUsable) return; // greybox capsule stays

            AgentVisual visual = set.Attach(agent.transform, agent.Id.Value);
            if (visual == null) return;

            visual.Bind(agent);

            // Retire the placeholder capsule now that there is a real body.
            Transform placeholder = agent.transform.Find("Body");
            if (placeholder != null) UnseenObject.DestroyGameObject(placeholder.gameObject);
            Transform nose = agent.transform.Find("Facing");
            if (nose != null) UnseenObject.DestroyGameObject(nose.gameObject);
        }

        public void Despawn(AgentEntity agent)
        {
            if (agent == null) return;
            _ctx.Entities.Unregister(agent);
            UnseenObject.DestroyGameObject(agent.gameObject);
        }

        private void EnsureComponents(GameObject go)
        {
            UnseenConfig.MovementSection move = _ctx.Config.Movement;

            var controller = go.GetComponent<CharacterController>();
            if (controller == null) controller = go.AddComponent<CharacterController>();
            controller.height = move.StandHeight;
            controller.radius = move.Radius;
            controller.center = new Vector3(0f, move.StandHeight * 0.5f, 0f);
            controller.slopeLimit = 50f;
            controller.stepOffset = 0.45f;
            controller.skinWidth = 0.02f;

            if (go.GetComponent<NinjaMotor>() == null) go.AddComponent<NinjaMotor>();
            if (go.GetComponent<GrapplingHook>() == null) go.AddComponent<GrapplingHook>();
            if (go.GetComponent<AgentVitals>() == null) go.AddComponent<AgentVitals>();
            if (go.GetComponent<AgentCombat>() == null) go.AddComponent<AgentCombat>();
            if (go.GetComponent<Inventory>() == null) go.AddComponent<Inventory>();
        }

        private void EnsureAnchors(AgentEntity agent, GameObject go)
        {
            UnseenConfig.MovementSection move = _ctx.Config.Movement;

            if (agent.EyeAnchor == null)
                agent.EyeAnchor = CreateAnchor(go.transform, "Eye", move.StandHeight + move.EyeOffset);

            if (agent.TorsoAnchor == null)
                agent.TorsoAnchor = CreateAnchor(go.transform, "Torso", move.StandHeight * 0.55f);
        }

        private static Transform CreateAnchor(Transform parent, string name, float height)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing;

            var anchor = new GameObject(name).transform;
            anchor.SetParent(parent, false);
            anchor.localPosition = new Vector3(0f, height, 0f);
            return anchor;
        }

        private GameObject BuildCapsuleAgent(string displayName, float3 position)
        {
            var go = new GameObject(displayName);
            go.transform.position = position;

            if (!_createVisuals) return go;

            // Greybox body: a capsule offset so its base sits at the transform origin, matching the
            // character controller.
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = new Vector3(0f, _ctx.Config.Movement.StandHeight * 0.5f, 0f);
            body.transform.localScale = new Vector3(
                _ctx.Config.Movement.Radius * 2f,
                _ctx.Config.Movement.StandHeight * 0.5f,
                _ctx.Config.Movement.Radius * 2f);

            UnseenObject.Destroy(body.GetComponent<Collider>());

            // A nose marker makes facing readable in the greybox, which matters a lot when the
            // whole game is about who is looking where.
            GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Facing";
            nose.transform.SetParent(go.transform, false);
            nose.transform.localPosition = new Vector3(0f, _ctx.Config.Movement.StandHeight * 0.8f, 0.35f);
            nose.transform.localScale = new Vector3(0.12f, 0.12f, 0.3f);
            UnseenObject.Destroy(nose.GetComponent<Collider>());

            return go;
        }
    }
}
