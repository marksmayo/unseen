using NUnit.Framework;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The switches a launcher, a shortcut or a server browser hands the game.
    ///
    /// Untestable until now because the parsing read Environment.GetCommandLineArgs() directly, so
    /// the only way to exercise it was to launch the process - which meant the arguments that
    /// decide whether a build is a server, a client, or neither had no coverage at all.
    /// </summary>
    public sealed class CommandLineTests
    {
        [TearDown]
        public void ResetTransport()
        {
            UnseenTransport.ConnectTo = null;
            UnseenTransport.RequestedName = null;
            UnseenTransport.ListenPort = UnseenTransport.DefaultPort;
        }

        [Test]
        public void ServerSwitchesChooseDedicatedMode()
        {
            var options = LaunchOptions.Parse(new[] { "unseen.exe", "-server" });
            Assert.AreEqual(LaunchMode.DedicatedServer, options.Mode);

            Assert.AreEqual(LaunchMode.ListenServer,
                LaunchOptions.Parse(new[] { "unseen.exe", "-listen" }).Mode);
        }

        [Test]
        public void ConnectingImpliesBeingAClient()
        {
            LaunchOptions options = LaunchOptions.Parse(
                new[] { "unseen.exe", "-connect", "10.0.0.7:7777", "-name", "Mark" });

            // Typing "-connect somewhere" and nothing else is what everybody does, so it has to
            // mean "join that server" rather than requiring a second flag nobody would think of.
            Assert.AreEqual(LaunchMode.Client, options.Mode);
            Assert.IsTrue(options.ConnectTo.HasValue);
            Assert.AreEqual(7777, options.ConnectTo.Value.Port);
            Assert.AreEqual("Mark", options.RequestedName);
        }

        [Test]
        public void AnUnreadableAddressLeavesTheModeAlone()
        {
            // A port left off a shortcut, or a hostname where an address was wanted. Silently
            // starting a single-player game would look like the connection failing for no reason;
            // refusing the switch keeps the launch honest and the error visible.
            LaunchOptions options = LaunchOptions.Parse(
                new[] { "unseen.exe", "-connect", "not-an-address" });

            Assert.AreNotEqual(LaunchMode.Client, options.Mode);
            Assert.IsFalse(options.ConnectTo.HasValue);
            Assert.IsNotNull(options.Error, "and it should say what it could not read");
        }

        [Test]
        public void PortsOutsideTheRangeAreIgnored()
        {
            Assert.AreEqual(UnseenTransport.DefaultPort,
                LaunchOptions.Parse(new[] { "unseen.exe", "-port", "99999" }).ListenPort);

            Assert.AreEqual(7000,
                LaunchOptions.Parse(new[] { "unseen.exe", "-port", "7000" }).ListenPort);
        }

        [Test]
        public void NoSwitchesMeansNoOpinionAboutTheMode()
        {
            LaunchOptions options = LaunchOptions.Parse(new[] { "unseen.exe" });

            // The difference between "offline was asked for" and "nothing was asked for" matters:
            // a caller that cannot tell them apart overwrites whatever a tool or the inspector had
            // already chosen. The screenshot capture sets ListenServer before booting and would
            // have been quietly demoted to offline practice every run.
            Assert.IsFalse(options.HasMode, "an empty command line chooses nothing");

            Assert.IsTrue(LaunchOptions.Parse(new[] { "unseen.exe", "-server" }).HasMode);
            Assert.IsTrue(LaunchOptions.Parse(new[] { "unseen.exe", "-listen" }).HasMode);
        }

        [Test]
        public void ASwitchWithNothingAfterItDoesNotThrow()
        {
            // Trailing switches happen: a shortcut edited in a hurry, an argument the shell ate.
            // Reading past the end of the array during startup kills the process before there is a
            // log to explain it, which is the least debuggable failure a player can report.
            Assert.DoesNotThrow(() => LaunchOptions.Parse(new[] { "unseen.exe", "-connect" }));
            Assert.DoesNotThrow(() => LaunchOptions.Parse(new[] { "unseen.exe", "-port" }));
            Assert.DoesNotThrow(() => LaunchOptions.Parse(new[] { "unseen.exe", "-name" }));
            Assert.DoesNotThrow(() => LaunchOptions.Parse(new[] { "unseen.exe", "-seed" }));
        }
    }
}
