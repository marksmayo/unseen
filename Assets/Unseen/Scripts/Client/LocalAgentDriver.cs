using Unity.Mathematics;
using UnityEngine;
using Unseen.BattleRoyale;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Net;

namespace Unseen.Client
{
    /// <summary>
    /// The one agent a pure client simulates: its own.
    ///
    /// Everything else on a client is already snapshot-driven - the HUD, the minimap, the mist, the
    /// audio and every other ninja, all of them fed by ClientNetworkView. The local player was the
    /// exception, and it was the exception in the worst possible way: the client ran a complete
    /// second simulation, spawned its own agent in it, and followed that. The ninja on screen was
    /// not the one the server believed in; it merely happened to start in the same place.
    ///
    /// So this owns the local ninja, and only the local ninja. It moves immediately on the player's
    /// own input, because waiting out the round trip is the difference between a game that responds
    /// and one that argues. When a snapshot arrives it rewinds to the position the server states and
    /// replays every input the server has not yet acknowledged, so the two never drift apart for
    /// longer than one round trip.
    ///
    /// The replay uses the real motor. That is the entire discipline here: a prediction written as
    /// a second implementation of movement will disagree with the first, and then every correction
    /// the server sends is a shove.
    /// </summary>
    public sealed class LocalAgentDriver : MonoBehaviour
    {
        public ClientNetworkView View;

        private readonly InputReconciler<MoveIntent> _reconciler = new InputReconciler<MoveIntent>();

        private SimContext _ctx;
        private INetworkService _net;
        private AgentSpawner _spawner;
        private PlayerInputSource _input;
        private AgentEntity _agent;
        private CharacterController _controller;

        /// <summary>The local ninja, once a snapshot has said where it is. Null before that.</summary>
        public AgentEntity Agent => _agent;

        /// <summary>How much of this client's movement the server has not confirmed yet.</summary>
        public int UnconfirmedInputs => _reconciler.PendingCount;

        public void Bind(SimContext ctx, INetworkService net, AgentSpawner spawner,
            PlayerInputSource input, string displayName)
        {
            _ctx = ctx;
            _net = net;
            _spawner = spawner;
            _input = input;
            _displayName = PlayerName.Sanitise(displayName);

            if (View != null) View.SnapshotApplied += OnSnapshot;
        }

        /// <summary>
        /// What this client asked to be called. Only ever a request: the server decides what
        /// everybody including this player is actually shown as, and says so in the standings.
        /// </summary>
        private string _displayName = PlayerName.Fallback;

        private void OnDestroy()
        {
            if (View != null) View.SnapshotApplied -= OnSnapshot;
        }

        /// <summary>
        /// Feeds the local ninja its own input and remembers what was fed.
        ///
        /// Recorded here rather than where the input is sampled, so that what is remembered is
        /// exactly what the motor was stepped with. The replay applies each input for the interval
        /// it was originally applied for, and a recorded interval that does not match the one the
        /// motor actually used puts the replay somewhere the client was never going to be - which
        /// then reads as a correction, every snapshot, for as long as the frame rate disagrees with
        /// the tick rate.
        /// </summary>
        private void Update()
        {
            if (_agent == null || _input == null) return;

            MoveIntent intent = _input.Current;
            _agent.Intent = intent;

            // Nothing classifies combat pockets on a client, so without this the local ninja would
            // be stepped at the cold rate - twenty hertz movement for the one agent whose
            // responsiveness is the entire reason this class exists.
            _agent.IsHot = true;

            _reconciler.Record(intent.Sequence, intent, Time.deltaTime);
        }

        private void OnSnapshot(SnapshotData snapshot)
        {
            if (_ctx == null || _net == null) return;
            if (snapshot.SelfId == AgentId.None) return;

            EnsureAgent(snapshot);
            if (_agent == null) return;

            // Everything the client does not predict comes straight off the wire. Health, stealth
            // and stance are the server's answers and nothing here is entitled to a different one -
            // predicting stealth in particular would be inventing the number the whole game is
            // played against.
            _agent.Yaw = snapshot.SelfYaw;
            _agent.Pitch = snapshot.SelfPitch;
            _agent.StealthIndex = snapshot.SelfStealth;
            _agent.Flags = (AgentFlags)snapshot.SelfFlags;
            _agent.Stance = (Stance)snapshot.SelfStance;
            _agent.Locomotion = (LocomotionState)snapshot.SelfLocomotion;

            float3 corrected = _reconciler.Reconcile(snapshot.AcknowledgedInput, snapshot.SelfPosition, Step);
            Teleport(corrected);
        }

        /// <summary>
        /// One replayed input, through the same motor the server runs.
        ///
        /// Only the first call of a replay actually moves the body: after that the motor's own
        /// output is already where the next step wants to start from, and teleporting a character
        /// controller onto the position it is already at costs a collision resolve for nothing.
        /// </summary>
        private float3 Step(float3 from, MoveIntent intent, float dt)
        {
            if (math.distancesq(_agent.Position, from) > 1e-6f) Teleport(from);

            _agent.Intent = intent;
            _agent.Motor.Simulate(_ctx, dt, _ctx.Tick, _ctx.Time);
            return _agent.Position;
        }

        /// <summary>
        /// Puts the body somewhere without the controller resolving its way out of it.
        ///
        /// A CharacterController keeps its own idea of where it is and reconciles it against the
        /// transform on the next move, so writing the transform alone leaves the two disagreeing -
        /// which surfaces as the body sliding back toward where it was. Switching it off for the
        /// write is the documented way to move one.
        /// </summary>
        private void Teleport(float3 to)
        {
            if (_controller == null)
            {
                _agent.Position = to;
                return;
            }

            _controller.enabled = false;
            _agent.Position = to;
            _controller.enabled = true;
        }

        private void EnsureAgent(SnapshotData snapshot)
        {
            if (_agent != null) return;
            if (_spawner == null) return;

            // Spawned where the server says, not at a guess. This is the first moment a client
            // knows it has a body at all - before the first snapshot there is nothing to place.
            _agent = _spawner.Spawn(AgentKind.Player, _net.LocalConnectionId,
                snapshot.SelfPosition, _displayName);

            _controller = _agent.Controller;
        }
    }
}
