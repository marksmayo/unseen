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

        public static INetworkService Create(LaunchMode mode)
        {
            INetworkService service = Factory?.Invoke(mode);
            if (service != null) return service;

            // No real transport installed. Offline and listen-server modes are fully playable on the
            // loopback service; a pure client build needs an adapter and will say so.
            if (mode == LaunchMode.Client || mode == LaunchMode.DedicatedServer)
            {
                UnityEngine.Debug.LogWarning(
                    $"[Unseen] {mode} requested but no network transport is registered. " +
                    "Falling back to the loopback service - see docs/NETWORKING.md.");
            }

            return new OfflineNetworkService();
        }
    }
}
