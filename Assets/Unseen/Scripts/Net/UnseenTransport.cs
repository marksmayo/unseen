using Unseen.Core;

namespace Unseen.Net
{
    /// <summary>
    /// Chooses a transport for a launch mode. This is the seam where Fish-Net or Photon Fusion is
    /// plugged in: install the package, add UNSEEN_FISHNET to the scripting define symbols, and the
    /// adapter in Assets/Unseen/Integrations compiles and takes over. Nothing above this file
    /// changes, because everything above it talks to <see cref="INetworkService"/>.
    /// </summary>
    public static class UnseenTransport
    {
        /// <summary>
        /// Set by an integration assembly at startup (via RuntimeInitializeOnLoadMethod) to take
        /// over transport creation.
        /// </summary>
        public static System.Func<LaunchMode, INetworkService> Factory;

        /// <summary>Port a dedicated server listens on when none is given.</summary>
        public const int DefaultPort = 7777;

        /// <summary>
        /// Where a client should connect, and what it wants to be called. Set from the command line
        /// before the transport is created; left alone, the built-in UDP transport is not used.
        /// </summary>
        public static NetEndpoint? ConnectTo;

        public static string RequestedName;

        /// <summary>Port for a dedicated server. Zero lets the OS choose, which tests want.</summary>
        public static int ListenPort = DefaultPort;

        public static INetworkService Create(LaunchMode mode)
        {
            // An explicitly registered adapter always wins. This is the seam a third-party
            // transport plugs into, and it must be able to override the built-in one.
            INetworkService service = Factory?.Invoke(mode);
            if (service != null) return service;

            // The built-in UDP transport, for the two modes that genuinely need a network. Offline
            // and listen-server run both halves in one process, where a socket would be cost
            // without benefit - and offline play has to keep working with no network at all.
            switch (mode)
            {
                case LaunchMode.DedicatedServer:
                    return UnseenUdpService.Host(ListenPort);

                case LaunchMode.Client when ConnectTo.HasValue:
                    return UnseenUdpService.Join(ConnectTo.Value, RequestedName);

                case LaunchMode.Client:
                    // Asked to be a client with nowhere to go. Falling through to loopback would
                    // start a single-player game wearing a client's clothes, which looks like the
                    // connection silently failing.
                    UnityEngine.Debug.LogError(
                        "[Unseen] client mode needs -connect <host:port>. Nothing to connect to.");
                    break;
            }

            return new OfflineNetworkService();
        }
    }
}
