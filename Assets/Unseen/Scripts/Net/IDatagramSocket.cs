using System;

namespace Unseen.Net
{
    /// <summary>
    /// Somewhere to send datagrams and somewhere they arrive from.
    ///
    /// Extracted so that something other than a real socket can sit here - specifically
    /// <see cref="SimulatedSocket"/>, which drops, delays, duplicates and reorders on the way past.
    /// Everything the transport does about loss - resending a reliable message, acknowledging by
    /// id, replaying unacknowledged inputs, refusing a stale packet - only does anything at all
    /// when packets go missing, and none of it could be tested while the only socket available
    /// delivered everything it was given.
    /// </summary>
    public interface IDatagramSocket : IDisposable
    {
        /// <summary>Where this socket can be reached.</summary>
        NetEndpoint LocalEndpoint { get; }

        /// <summary>Sends a datagram, and says whether it was accepted for sending.</summary>
        bool Send(NetEndpoint to, byte[] payload, int length);

        /// <summary>
        /// Takes in whatever has arrived, and lets anything holding packets move its clock on.
        ///
        /// A real socket has no use for the elapsed time and ignores it. A simulated one is made of
        /// it: latency is a release time, jitter is a spread around it, and reordering is what
        /// happens when two packets are given release times in the opposite order to their sending.
        /// </summary>
        void Poll(float deltaTime);

        /// <summary>Takes the next datagram, if one is waiting.</summary>
        bool TryReceive(out byte[] payload, out NetEndpoint from);
    }
}
