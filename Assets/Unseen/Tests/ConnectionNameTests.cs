using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The name a connection is known by, from the client asking to the server deciding.
    ///
    /// The transport already sanitises and de-duplicates what a client asks for, but nothing was
    /// reading the answer: a real two-process run showed a client that sent "Mark" being labelled
    /// player-1, because the name reached the transport and stopped there.
    /// </summary>
    public sealed class ConnectionNameTests
    {
        private static bool PumpUntil(UnseenUdpService a, UnseenUdpService b,
            System.Func<bool> condition, int attempts = 400)
        {
            for (int i = 0; i < attempts; i++)
            {
                a.Poll(1f / 60f);
                b.Poll(1f / 60f);
                if (condition()) return true;
                System.Threading.Thread.Sleep(1);
            }

            return false;
        }

        [Test]
        public void TheServerCanBeAskedWhatAConnectionIsCalled()
        {
            using (var server = UnseenUdpService.Host(0))
            using (var client = UnseenUdpService.Join(server.LocalEndpoint, "Mark"))
            {
                int id = -1;
                server.ClientConnected += c => id = c;
                Assert.IsTrue(PumpUntil(server, client, () => id >= 0), "connected");

                // Asked through INetworkService, not through the concrete transport. The game has
                // exactly one type it talks to, and a name that can only be read off UnseenUdpService
                // is a name the simulation cannot use.
                INetworkService asInterface = server;
                Assert.AreEqual("Mark", asInterface.NameOf(id),
                    "the server should report the name it granted this connection");
            }
        }

        [Test]
        public void ATransportWithNoNamesSaysSoRatherThanGuessing()
        {
            // Offline practice has a connection but nobody ever asked to be called anything. Null
            // rather than an invented label, so the caller decides the fallback - the alternative is
            // two different places inventing two different defaults for the same player.
            INetworkService offline = new OfflineNetworkService();
            Assert.IsNull(offline.NameOf(0));
        }
    }
}
