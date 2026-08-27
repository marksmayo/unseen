using System.Collections.Generic;
using Unity.Mathematics;
using Unseen.Combat;
using Unseen.Core;
using Unseen.Entities;
using Unseen.Environment;

namespace Unseen.Net
{
    public struct VisibleEntity
    {
        public AgentId Id;
        public VisibilityKind Kind;
        public float3 Position;
        public float Yaw;
        public ushort Flags;
        public byte Stance;
        public float Confidence;
    }

    /// <summary>One decoded snapshot. Reused every frame so the client allocates nothing steady-state.</summary>
    public sealed class SnapshotData
    {
        public int Tick;
        public float ServerTime;

        public AgentId SelfId;
        public float3 SelfPosition;
        public float SelfYaw;
        public float SelfPitch;
        public float SelfHealth;
        public float SelfStealth;
        public ushort SelfFlags;
        public byte SelfStance;
        public byte SelfLocomotion;

        /// <summary>Utility slot contents as <see cref="Items.UtilityEffect"/>, 0 for empty.</summary>
        public readonly byte[] SelfUtility = new byte[3];

        /// <summary>What the player could do right now. See <see cref="SelfPrompt"/> bits.</summary>
        public byte SelfPrompts;

        public readonly List<VisibleEntity> Entities = new List<VisibleEntity>(32);
        public readonly List<HeardSound> Sounds = new List<HeardSound>(16);
        public readonly List<CombatEvent> Combat = new List<CombatEvent>(16);
        public readonly List<WorldEvent> World = new List<WorldEvent>(16);

        public float3 ZoneCenter;
        public float ZoneRadius;
        public byte ZoneStage;
        public byte MatchPhase;
        public ushort AliveCount;

        /// <summary>Winner of the match just finished, or None while one is running.</summary>
        public AgentId Winner;

        /// <summary>Seconds until the phase ends. Zero when the phase has no end.</summary>
        public float PhaseSecondsRemaining;

        /// <summary>Where this player finished, and how many they took with them.</summary>
        public ushort SelfPlacement;
        public ushort SelfKills;

        public void Clear()
        {
            Entities.Clear();
            Sounds.Clear();
            Combat.Clear();
            World.Clear();
        }
    }

    /// <summary>
    /// The wire format, encoder and decoder side by side so they cannot drift apart.
    ///
    /// The important property is negative: a snapshot contains only what the interest manager
    /// proved this observer can perceive. There is no "all players" array to scrape, so reading
    /// client memory yields nothing an honest client did not already have on screen.
    /// </summary>
    /// <summary>What the player can act on this instant, sent so the HUD can prompt for it.</summary>
    [System.Flags]
    public enum SelfPrompt : byte
    {
        None = 0,
        Container = 1 << 0,
        Shoji = 1 << 1,
        Lantern = 1 << 2,
        Grapple = 1 << 3,
        Takedown = 1 << 4
    }

    public static class SnapshotProtocol
    {
        public const byte Version = 1;

        /// <summary>
        /// The three utility slots and the set of things in reach.
        ///
        /// Computed on the server rather than probed on the client: the prompt has to agree with
        /// what pressing the key will actually do, and the server is the only thing that knows.
        /// </summary>
        private static void EncodeSelfContext(NetWriter writer, SimContext ctx, AgentEntity self)
        {
            var slots = new byte[3];
            if (self.Inventory != null)
            {
                IReadOnlyList<Items.ItemStack> utility = self.Inventory.Utility;
                for (int i = 0; i < 3 && i < utility.Count; i++)
                    slots[i] = utility[i].Item != null ? (byte)utility[i].Item.Effect : (byte)0;
            }

            writer.WriteByte(slots[0]);
            writer.WriteByte(slots[1]);
            writer.WriteByte(slots[2]);

            SelfPrompt prompts = SelfPrompt.None;

            if (Items.LootContainer.NearestUnlooted(self.TorsoPosition, 2.2f) != null)
                prompts |= SelfPrompt.Container;

            if (Environment.ShojiPanel.NearestIntact(self.TorsoPosition + self.Forward * 0.9f, 1.4f) != null)
                prompts |= SelfPrompt.Shoji;

            if (Environment.Lantern.NearestLit(self.TorsoPosition + self.Forward * 1.2f, 1.8f) != null)
                prompts |= SelfPrompt.Lantern;

            if (self.Hook != null && self.Hook.HasTarget(self.EyePosition, self.ViewDirection,
                    ctx.Config.Movement.GrappleRange))
                prompts |= SelfPrompt.Grapple;

            writer.WriteByte((byte)prompts);
        }

        public static void EncodeSnapshot(
            NetWriter writer,
            SimContext ctx,
            AgentEntity self,
            int tick,
            float time,
            IReadOnlyList<CombatEvent> combatEvents,
            int combatCursor,
            IReadOnlyList<WorldEvent> worldEvents,
            int worldCursor)
        {
            float quantum = ctx.Config.Network.PositionQuantum;

            writer.WriteByte((byte)NetMessage.Snapshot);
            writer.WriteByte(Version);
            writer.WriteInt(tick);
            writer.WriteFloat(time);

            // Own state is complete: you always know exactly where you are and how hidden you are.
            writer.WriteInt(self.Id.Value);
            writer.WritePosition(self.Position, quantum);
            writer.WriteAngle(self.Yaw);
            writer.WriteAngle(self.Pitch);
            writer.WriteNormalised(self.Vitals.Fraction);
            writer.WriteNormalised(self.StealthIndex);
            writer.WriteUShort((ushort)self.Flags);
            writer.WriteByte((byte)self.Stance);
            writer.WriteByte((byte)self.Locomotion);
            EncodeSelfContext(writer, ctx, self);

            // Everyone else, only if earned.
            int visibleCount = math.min(self.Visible.Count, 255);
            writer.WriteByte((byte)visibleCount);
            for (int i = 0; i < visibleCount; i++)
            {
                VisibleTarget v = self.Visible[i];
                AgentEntity target = ctx.Entities.Get(v.Id);

                writer.WriteInt(v.Id.Value);
                writer.WriteByte((byte)v.Kind);
                writer.WritePosition(v.Position, quantum);
                writer.WriteNormalised(v.Confidence);

                // A silhouette carries no identity: no gear, no health, no stance detail.
                bool full = (v.Kind & VisibilityKind.Direct) != 0 && target != null;
                writer.WriteAngle(full ? target.Yaw : 0f);
                writer.WriteUShort(full ? (ushort)target.Flags : (ushort)0);
                writer.WriteByte(full ? (byte)target.Stance : (byte)0);
            }

            int soundCount = math.min(self.Heard.Count, 255);
            writer.WriteByte((byte)soundCount);
            for (int i = 0; i < soundCount; i++)
            {
                HeardSound s = self.Heard[i];
                writer.WriteByte((byte)s.Kind);
                writer.WriteNormalised(s.Intensity);
                writer.WriteNormalised(s.Occlusion);
                writer.WriteDirection(s.Direction);
                writer.WritePosition(s.ApparentPosition, quantum);
            }

            int combatCount = math.min(combatEvents.Count - combatCursor, 255);
            writer.WriteByte((byte)math.max(combatCount, 0));
            for (int i = 0; i < combatCount; i++)
            {
                CombatEvent e = combatEvents[combatCursor + i];
                writer.WriteByte((byte)e.Kind);
                writer.WriteInt(e.Attacker.Value);
                writer.WriteInt(e.Victim.Value);
                writer.WritePosition(e.Position, quantum);
                writer.WriteByte((byte)e.Zone);
            }

            int worldCount = math.min(worldEvents.Count - worldCursor, 255);
            writer.WriteByte((byte)math.max(worldCount, 0));
            for (int i = 0; i < worldCount; i++)
            {
                WorldEvent e = worldEvents[worldCursor + i];
                writer.WriteByte((byte)e.Kind);
                writer.WriteUShort(e.TargetId);
                writer.WritePosition(e.Position, quantum);
                writer.WriteFloat(e.Radius);
                writer.WriteFloat(e.Duration);
            }

            writer.WritePosition(ctx.Mist != null ? ctx.Mist.Center : float3.zero, quantum);
            writer.WriteFloat(ctx.Mist != null ? ctx.Mist.CurrentRadius : 0f);
            writer.WriteByte((byte)(ctx.Mist != null ? ctx.Mist.Stage : 0));
            writer.WriteByte((byte)(ctx.Match != null ? (byte)ctx.Match.Phase : 0));
            writer.WriteUShort((ushort)ctx.Entities.AliveCount);

            // Result state. Sent every snapshot rather than as a one-off event on match end: a
            // client that joins, reconnects or simply drops the packet carrying a single
            // end-of-match message would otherwise never learn how it did.
            writer.WriteInt(ctx.Match != null ? ctx.Match.Winner.Value : AgentId.None.Value);
            writer.WriteFloat(ctx.Match != null ? ctx.Match.SecondsToPhaseEnd : 0f);
            writer.WriteUShort((ushort)math.min(self.Placement, ushort.MaxValue));
            writer.WriteUShort((ushort)math.min(self.Kills, ushort.MaxValue));
        }

        public static bool DecodeSnapshot(NetReader reader, SnapshotData into, float quantum)
        {
            byte message = reader.ReadByte();
            if (message != (byte)NetMessage.Snapshot) return false;
            if (reader.ReadByte() != Version) return false;

            into.Clear();
            into.Tick = reader.ReadInt();
            into.ServerTime = reader.ReadFloat();

            into.SelfId = new AgentId(reader.ReadInt());
            into.SelfPosition = reader.ReadPosition(quantum);
            into.SelfYaw = reader.ReadAngle();
            into.SelfPitch = reader.ReadAngle();
            into.SelfHealth = reader.ReadNormalised();
            into.SelfStealth = reader.ReadNormalised();
            into.SelfFlags = reader.ReadUShort();
            into.SelfStance = reader.ReadByte();
            into.SelfLocomotion = reader.ReadByte();
            into.SelfUtility[0] = reader.ReadByte();
            into.SelfUtility[1] = reader.ReadByte();
            into.SelfUtility[2] = reader.ReadByte();
            into.SelfPrompts = reader.ReadByte();
            if (into.SelfPitch > 180f) into.SelfPitch -= 360f;

            int visible = reader.ReadByte();
            for (int i = 0; i < visible; i++)
            {
                var entity = new VisibleEntity
                {
                    Id = new AgentId(reader.ReadInt()),
                    Kind = (VisibilityKind)reader.ReadByte(),
                    Position = reader.ReadPosition(quantum),
                    Confidence = reader.ReadNormalised(),
                    Yaw = reader.ReadAngle()
                };
                entity.Flags = reader.ReadUShort();
                entity.Stance = reader.ReadByte();
                into.Entities.Add(entity);
            }

            int sounds = reader.ReadByte();
            for (int i = 0; i < sounds; i++)
            {
                into.Sounds.Add(new HeardSound
                {
                    Kind = (SoundKind)reader.ReadByte(),
                    Intensity = reader.ReadNormalised(),
                    Occlusion = reader.ReadNormalised(),
                    Direction = reader.ReadDirection(),
                    ApparentPosition = reader.ReadPosition(quantum),
                    Tick = into.Tick
                });
            }

            int combat = reader.ReadByte();
            for (int i = 0; i < combat; i++)
            {
                into.Combat.Add(new CombatEvent
                {
                    Kind = (CombatEventKind)reader.ReadByte(),
                    Attacker = new AgentId(reader.ReadInt()),
                    Victim = new AgentId(reader.ReadInt()),
                    Position = reader.ReadPosition(quantum),
                    Zone = (GuardZone)reader.ReadByte(),
                    Tick = into.Tick
                });
            }

            int world = reader.ReadByte();
            for (int i = 0; i < world; i++)
            {
                into.World.Add(new WorldEvent
                {
                    Kind = (WorldEventKind)reader.ReadByte(),
                    TargetId = reader.ReadUShort(),
                    Position = reader.ReadPosition(quantum),
                    Radius = reader.ReadFloat(),
                    Duration = reader.ReadFloat(),
                    Tick = into.Tick
                });
            }

            into.ZoneCenter = reader.ReadPosition(quantum);
            into.ZoneRadius = reader.ReadFloat();
            into.ZoneStage = reader.ReadByte();
            into.MatchPhase = reader.ReadByte();
            into.AliveCount = reader.ReadUShort();
            into.Winner = new AgentId(reader.ReadInt());
            into.PhaseSecondsRemaining = reader.ReadFloat();
            into.SelfPlacement = reader.ReadUShort();
            into.SelfKills = reader.ReadUShort();
            return true;
        }

        public static void EncodeInput(NetWriter writer, in MoveIntent intent)
        {
            writer.WriteByte((byte)NetMessage.Input);
            writer.WriteInt(unchecked((int)intent.Sequence));
            writer.WriteByte((byte)math.clamp(math.round((intent.Move.x * 0.5f + 0.5f) * 255f), 0f, 255f));
            writer.WriteByte((byte)math.clamp(math.round((intent.Move.y * 0.5f + 0.5f) * 255f), 0f, 255f));
            writer.WriteAngle(intent.Yaw);
            writer.WriteAngle(intent.Pitch);

            byte buttons = 0;
            if (intent.Sprint) buttons |= 1 << 0;
            if (intent.Crouch) buttons |= 1 << 1;
            if (intent.Jump) buttons |= 1 << 2;
            if (intent.Grapple) buttons |= 1 << 3;
            if (intent.Interact) buttons |= 1 << 4;
            if (intent.AttackLight) buttons |= 1 << 5;
            if (intent.AttackHeavy) buttons |= 1 << 6;
            if (intent.Guard) buttons |= 1 << 7;
            writer.WriteByte(buttons);

            // A second button byte. The first is full - eight buttons, eight bits - and prone had
            // nowhere to go. One byte per input packet is a cheaper price than packing a ninth
            // button into a field that means something else.
            byte extra = 0;
            if (intent.Prone) extra |= 1 << 0;
            if (intent.Throw) extra |= 1 << 1;
            writer.WriteByte(extra);

            writer.WriteByte((byte)intent.Zone);
            writer.WriteByte(intent.UseUtility);
        }

        public static bool DecodeInput(NetReader reader, out MoveIntent intent)
        {
            intent = MoveIntent.Idle;
            if (reader.ReadByte() != (byte)NetMessage.Input) return false;

            intent.Sequence = unchecked((uint)reader.ReadInt());
            intent.Move = new float2(
                reader.ReadByte() / 255f * 2f - 1f,
                reader.ReadByte() / 255f * 2f - 1f);
            intent.Yaw = reader.ReadAngle();
            intent.Pitch = reader.ReadAngle();

            byte buttons = reader.ReadByte();
            intent.Sprint = (buttons & (1 << 0)) != 0;
            intent.Crouch = (buttons & (1 << 1)) != 0;
            intent.Jump = (buttons & (1 << 2)) != 0;
            intent.Grapple = (buttons & (1 << 3)) != 0;
            intent.Interact = (buttons & (1 << 4)) != 0;
            intent.AttackLight = (buttons & (1 << 5)) != 0;
            intent.AttackHeavy = (buttons & (1 << 6)) != 0;
            intent.Guard = (buttons & (1 << 7)) != 0;

            byte extra = reader.ReadByte();
            intent.Prone = (extra & (1 << 0)) != 0;
            intent.Throw = (extra & (1 << 1)) != 0;
            intent.Zone = (GuardZone)reader.ReadByte();
            intent.UseUtility = reader.ReadByte();

            // Pitch travels as an unsigned angle; fold it back into -180..180.
            if (intent.Pitch > 180f) intent.Pitch -= 360f;
            return true;
        }
    }
}
