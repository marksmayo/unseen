using System;
using System.Collections.Generic;

namespace Unseen.Net
{
    public enum NetRole : byte
    {
        /// <summary>Single process, no transport. Offline practice and the bot stress test.</summary>
        Offline = 0,

        /// <summary>Headless authoritative server.</summary>
        Server = 1,

        /// <summary>Connected client with no authority.</summary>
        Client = 2,

        /// <summary>Listen server: authoritative and playing.</summary>
        Host = 3
    }

    /// <summary>
    /// Transport abstraction. The whole simulation is written against this interface so the choice
    /// of Fish-Net, Photon Fusion or a loopback stub is a boot-time decision, not an architectural
    /// one. Nothing above this layer knows which transport is live.
    /// </summary>
    public interface INetworkService
    {
        NetRole Role { get; }
        bool IsServer { get; }
        bool IsClient { get; }

        /// <summary>Connection id of the local player, or -1 on a headless server.</summary>
        int LocalConnectionId { get; }

        IReadOnlyList<int> Connections { get; }

        /// <summary>Measured round trip in seconds. Feeds the latency-compensated parry window.</summary>
        float RoundTripTime(int connectionId);

        void SendToClient(int connectionId, byte[] payload, int length, bool reliable);
        void SendToServer(byte[] payload, int length, bool reliable);

        /// <summary>Pumps the transport. Called once per Unity frame, before the simulation steps.</summary>
        void Poll(float deltaTime);

        event Action<int> ClientConnected;
        event Action<int> ClientDisconnected;

        /// <summary>Server-side receive: connection id, buffer, length.</summary>
        event Action<int, byte[], int> ServerReceived;

        /// <summary>Client-side receive: buffer, length.</summary>
        event Action<byte[], int> ClientReceived;

        void Shutdown();
    }
}
