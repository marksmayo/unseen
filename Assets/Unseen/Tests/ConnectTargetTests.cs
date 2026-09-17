using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// Turning "-connect 10.0.0.4:7777" into somewhere to send a packet.
    ///
    /// Small, but it is the one piece of attacker-adjacent parsing that runs before anything else:
    /// a command line can come from a shortcut, a server browser or a launcher, and the failure has
    /// to be a refusal rather than a crash on startup.
    /// </summary>
    public sealed class ConnectTargetTests
    {
        [Test]
        public void AnOrdinaryAddressParses()
        {
            Assert.IsTrue(ConnectTarget.TryParse("192.168.1.50:7777", out NetEndpoint endpoint));
            Assert.AreEqual(7777, endpoint.Port);
            Assert.AreEqual("192.168.1.50:7777", endpoint.ToString());
        }

        [Test]
        public void RubbishIsRefusedRatherThanCrashing()
        {
            // Every one of these is something a person or a launcher can actually produce, and a
            // game that throws on startup because a port was left off is a game nobody can report
            // a useful bug about.
            Assert.IsFalse(ConnectTarget.TryParse(null, out _));
            Assert.IsFalse(ConnectTarget.TryParse("", out _));
            Assert.IsFalse(ConnectTarget.TryParse("192.168.1.50", out _), "no port");
            Assert.IsFalse(ConnectTarget.TryParse("192.168.1.50:", out _), "empty port");
            Assert.IsFalse(ConnectTarget.TryParse("not-an-address:7777", out _));
            Assert.IsFalse(ConnectTarget.TryParse("192.168.1.50:99999", out _), "port out of range");
            Assert.IsFalse(ConnectTarget.TryParse("192.168.1.50:-1", out _), "negative port");
            Assert.IsFalse(ConnectTarget.TryParse("192.168.1.50:0", out _), "port zero is not a target");
        }
    }
}
