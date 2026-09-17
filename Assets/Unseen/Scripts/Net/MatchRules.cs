namespace Unseen.Net
{
    /// <summary>
    /// The rules a match is being played under, as the server declares them.
    ///
    /// A deliberately small subset of <see cref="Unseen.Core.UnseenConfig"/>. Most of that asset is
    /// tuning that ships with the build and is identical everywhere; what belongs here is only what
    /// a host actually chooses - how many players, how long the match runs, how hard the mist bites.
    /// Sending all of it would put a large, mostly static payload on the handshake path, which is
    /// the one path worth keeping cheap to serve.
    ///
    /// Written with the project's own NetWriter rather than a second serialisation mechanism, so
    /// the claim that the wire format is auditable by reading one file stays true.
    /// </summary>
    public struct MatchRules
    {
        public int TargetEntityCount;
        public int ZoneStages;
        public float MistDamagePerSecond;
        public float FinalZoneRadius;

        /// <summary>
        /// The rules a client should run under, given what it had locally and what the server said.
        ///
        /// Replace, never merge. The local copy is a file on the player's own disk and is therefore
        /// whatever they want it to be, so it has no standing once the server has spoken - it is a
        /// default for playing alone, not an opinion about a match. Merging is the tempting version
        /// of this ("keep the client's value where the server didn't specify one") and it is how
        /// the hole gets reopened by someone being helpful.
        /// </summary>
        public static MatchRules Adopt(MatchRules local, MatchRules fromServer)
        {
            return fromServer;
        }

        public void Write(NetWriter writer)
        {
            writer.WriteInt(TargetEntityCount);
            writer.WriteInt(ZoneStages);
            writer.WriteFloat(MistDamagePerSecond);
            writer.WriteFloat(FinalZoneRadius);
        }

        public static MatchRules Read(NetReader reader)
        {
            return new MatchRules
            {
                TargetEntityCount = reader.ReadInt(),
                ZoneStages = reader.ReadInt(),
                MistDamagePerSecond = reader.ReadFloat(),
                FinalZoneRadius = reader.ReadFloat()
            };
        }
    }
}
