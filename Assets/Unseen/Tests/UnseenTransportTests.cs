using NUnit.Framework;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The seam where a real transport replaces the loopback one.
    ///
    /// UnseenTransport.Create is what every launch goes through, and until now it has returned the
    /// offline service for every mode - including the two that cannot work on it.
    /// </summary>
    public sealed class UnseenTransportTests
    {
        [TearDown]
        public void ResetFactory()
        {
            // These are static seams, so a test that sets one must put it back or it leaks into
            // every test that runs afterwards - and NUnit does not promise an order, so the symptom
            // would be a suite that passes alone and fails in CI.
            UnseenTransport.Factory = null;
            UnseenTransport.ConnectTo = null;
            UnseenTransport.RequestedName = null;
            UnseenTransport.ListenPort = UnseenTransport.DefaultPort;
        }

        [Test]
        public void ADedicatedServerGetsARealSocket()
        {
            UnseenTransport.ListenPort = 0; // let the OS pick, so the test cannot collide

            INetworkService service = UnseenTransport.Create(LaunchMode.DedicatedServer);

            using (service as System.IDisposable)
            {
                Assert.IsInstanceOf<UnseenUdpService>(service,
                    "a dedicated server is the one mode that cannot work on loopback");
                Assert.AreEqual(NetRole.Server, service.Role);
            }
        }

        [Test]
        public void AClientWithNowhereToGoDoesNotQuietlyPlayAlone()
        {
            // Falling back to loopback here would start a single-player game wearing a client's
            // clothes: the player sees a world, nobody else is in it, and nothing says why. The
            // error is the only thing that does say why, so the test insists it is logged - Unity
            // fails a test on an unexpected LogError, which makes declaring it the assertion.
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error,
                "[Unseen] client mode needs -connect <host:port>. Nothing to connect to.");

            INetworkService service = UnseenTransport.Create(LaunchMode.Client);

            using (service as System.IDisposable)
                Assert.IsInstanceOf<OfflineNetworkService>(service,
                    "documents today's fallback - the error log is what tells the player");
        }

        [Test]
        public void OfflineModesStillGetTheLoopbackService()
        {
            // Practice and listen-server run both halves in one process. A socket would be a cost
            // with no benefit, and offline play must keep working with no network at all.
            Assert.IsInstanceOf<OfflineNetworkService>(
                UnseenTransport.Create(LaunchMode.OfflinePractice));
        }

        [Test]
        public void ARegisteredFactoryTakesOverForNetworkedModes()
        {
            using (var service = UnseenUdpService.Host(0))
            {
                UnseenTransport.Factory = mode => service;

                Assert.AreSame(service, UnseenTransport.Create(LaunchMode.DedicatedServer),
                    "a registered transport should be used rather than the loopback fallback");
            }
        }
    }
}
