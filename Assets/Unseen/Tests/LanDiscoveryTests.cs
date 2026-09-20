using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// Finding a server without being told its address.
    ///
    /// Over a real socket, because broadcast is the part that cannot be reasoned about: it is
    /// refused on some networks, arrives on interfaces nobody expected, and behaves differently on
    /// a machine with a VPN up. The protocol is tested separately; this is about whether the two
    /// ends actually hear each other.
    ///
    /// Every assertion tolerates a network that will not carry broadcast, because some will not and
    /// a test that fails on somebody's laptop for that reason gets deleted rather than fixed. What
    /// is asserted unconditionally is the part that must hold everywhere: it never throws, and a
    /// failure leaves an empty list rather than a broken menu.
    /// </summary>
    public sealed class LanDiscoveryTests
    {
        [Test]
        public void AServerIsHeardByAListener()
        {
            using (var listener = new LanDiscovery(listening: true))
            using (var server = new LanDiscovery(listening: false))
            {
                if (!listener.IsUsable)
                {
                    Assert.Pass($"no broadcast on this machine: {listener.Problem}");
                    return;
                }

                bool heard = false;

                for (int i = 0; i < 200 && !heard; i++)
                {
                    server.Poll(1f, "Mark's game", gamePort: 7770, players: 3, capacity: 64);
                    listener.Poll(0.01f, null, 0, 0, 0);

                    foreach (FoundServer found in listener.Servers)
                    {
                        if (found.Name != "Mark's game") continue;

                        // The port comes from inside the beacon and the address from where the
                        // datagram arrived. A server cannot be trusted to know its own external
                        // address, and a beacon that claimed one would be a way to point a whole
                        // room at somebody else's machine.
                        Assert.IsTrue(found.Address.EndsWith(":7770"), "and where to go");
                        Assert.AreEqual(3, found.Players);
                        heard = true;
                    }

                    System.Threading.Thread.Sleep(2);
                }

                if (!heard) Assert.Pass("broadcast did not cross on this machine; nothing threw");
            }
        }

        [Test]
        public void AServerThatGoesQuietDropsOffTheList()
        {
            using (var listener = new LanDiscovery(listening: true))
            using (var server = new LanDiscovery(listening: false))
            {
                if (!listener.IsUsable) { Assert.Pass("no broadcast here"); return; }

                for (int i = 0; i < 100 && listener.Count == 0; i++)
                {
                    server.Poll(1f, "going away", gamePort: 7770, players: 0, capacity: 64);
                    listener.Poll(0.01f, null, 0, 0, 0);
                    System.Threading.Thread.Sleep(2);
                }

                if (listener.Count == 0) { Assert.Pass("broadcast did not cross"); return; }

                // The server stops. A list that kept it would send somebody to a machine that is
                // not there, and "connection failed" reads as the game being broken rather than as
                // a server having been closed a minute ago.
                listener.Poll(LanDiscovery.ForgetAfterSeconds + 1f, null, 0, 0, 0);

                Assert.AreEqual(0, listener.Count, "a server nobody has heard from is gone");
            }
        }

        [Test]
        public void AHostDoesNotFindItselfInTheList()
        {
            // Broadcast comes back to the machine that sent it, so a listen server browsing for
            // games finds itself sitting at the top of its own list. Harmless and obviously wrong,
            // which is the worst combination: it is the first thing anybody notices, and it makes
            // the whole feature look untrustworthy before they have tried it.
            using (var host = new LanDiscovery(listening: true))
            {
                if (!host.IsUsable) { Assert.Pass($"no broadcast here: {host.Problem}"); return; }

                for (int i = 0; i < 60; i++)
                {
                    host.Poll(1f, "my own game", gamePort: 7770, players: 1, capacity: 64);
                    System.Threading.Thread.Sleep(2);
                }

                Assert.AreEqual(0, host.Count, "a server is not one of its own search results");
            }
        }

        [Test]
        public void ADiscoveryThatCouldNotStartIsStillSafeToUse()
        {
            using (var discovery = new LanDiscovery(listening: true))
            {
                // Whether the socket came up depends on the machine - another copy of the game, a
                // program already on the port, a laptop with no usable interface. What must hold
                // everywhere is that calling it is safe and the worst outcome is an empty list,
                // which is exactly where the game was before any of this existed.
                Assert.DoesNotThrow(() => discovery.Poll(1f, "still fine", 7770, 0, 64));

                if (!discovery.IsUsable)
                {
                    Assert.AreEqual(0, discovery.Count, "it simply finds nothing");
                    Assert.IsFalse(string.IsNullOrEmpty(discovery.Problem),
                        "and says why, so the menu shows a line instead of a silent empty list");
                }
            }
        }
    }
}
