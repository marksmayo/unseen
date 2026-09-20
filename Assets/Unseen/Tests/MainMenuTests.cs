using NUnit.Framework;
using Unseen.Client;
using Unseen.Core;

namespace Unseen.Tests
{
    /// <summary>
    /// Whether the game stops to ask.
    ///
    /// The only part of the menu worth testing without a screen, and the part where a mistake is
    /// worst: a dedicated server that waited for somebody to click Start would come up, bind its
    /// port, and never start a match. The fleet would look healthy and be entirely useless, and the
    /// symptom - servers that never fill - is a long way from the cause.
    /// </summary>
    public sealed class MainMenuTests
    {
        [Test]
        public void ADedicatedServerNeverWaitsForAClick()
        {
            Assert.IsFalse(MainMenu.ShouldShow(LaunchMode.DedicatedServer, modeWasChosen: true));

            // Even if nothing chose the mode. A headless build has nobody at the keyboard whatever
            // the command line says, and a menu on it is a server that never starts.
            Assert.IsFalse(MainMenu.ShouldShow(LaunchMode.DedicatedServer, modeWasChosen: false));
        }

        [Test]
        public void ACommandLineThatChoseIsNotSecondGuessed()
        {
            // Somebody who typed -connect 10.0.0.7:7777 has already said what they want. Showing
            // them a menu is ignoring it, and a shortcut made for a playtest would stop working
            // for reasons nobody would guess.
            Assert.IsFalse(MainMenu.ShouldShow(LaunchMode.Client, modeWasChosen: true));
            Assert.IsFalse(MainMenu.ShouldShow(LaunchMode.ListenServer, modeWasChosen: true));
            Assert.IsFalse(MainMenu.ShouldShow(LaunchMode.OfflinePractice, modeWasChosen: true));
        }

        [Test]
        public void ALaunchWithNoOpinionGetsTheMenu()
        {
            // Double-clicking the game. The case the menu exists for, and the one the game did not
            // have: it used to drop straight into a match with no way to choose a name, reach
            // another machine, or leave except Alt-F4.
            Assert.IsTrue(MainMenu.ShouldShow(LaunchMode.OfflinePractice, modeWasChosen: false));
            Assert.IsTrue(MainMenu.ShouldShow(LaunchMode.ListenServer, modeWasChosen: false));
        }
    }
}
