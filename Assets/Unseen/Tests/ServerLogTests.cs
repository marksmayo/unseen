using NUnit.Framework;
using Unseen.Core;

namespace Unseen.Tests
{
    /// <summary>
    /// What a server writes about itself while nobody is watching.
    ///
    /// A dedicated server's log is read in two situations and both are unhappy: something went
    /// wrong in a match hours ago, or a soak has been running overnight and somebody wants to know
    /// whether it is still well. Both need the same things - a timestamp that means something
    /// across machines, a level that can be filtered, and a line that a script can read without
    /// guessing.
    ///
    /// And repetition has to be survivable. One system throwing on every tick is sixty lines a
    /// second, which is three hundred thousand lines in an hour and a disk full by morning - and
    /// the one interesting line, the first, is buried a quarter of a million lines up.
    /// </summary>
    public sealed class ServerLogTests
    {
        [Test]
        public void ALineCarriesWhenWhatAndHowBad()
        {
            string line = ServerLog.Format(LogLevel.Warn, "net", "a peer went quiet",
                atUtc: new System.DateTime(2026, 9, 20, 14, 5, 9, System.DateTimeKind.Utc));

            // Sortable, unambiguous across time zones, and the same shape every time. A log from a
            // machine in another region is compared against this one by hand often enough that
            // local time would be a genuine obstacle.
            Assert.IsTrue(line.Contains("2026-09-20T14:05:09Z"), "when, in UTC");
            Assert.IsTrue(line.Contains("WARN"), "how bad");
            Assert.IsTrue(line.Contains("net"), "which part of the game");
            Assert.IsTrue(line.Contains("a peer went quiet"), "and what happened");
        }

        [Test]
        public void TheLevelComesEarlyEnoughToGrepFor()
        {
            string line = ServerLog.Format(LogLevel.Error, "sim", "system threw on tick",
                atUtc: System.DateTime.UtcNow);

            // Somebody looking for trouble in a two-hundred-megabyte log will reach for grep before
            // anything else. A level buried after a free-text message cannot be matched without
            // also matching every message that happens to contain the word.
            int level = line.IndexOf("ERROR", System.StringComparison.Ordinal);
            int message = line.IndexOf("system threw", System.StringComparison.Ordinal);

            Assert.Greater(level, 0);
            Assert.Less(level, message, "level before message, so a prefix match is enough");
        }

        [Test]
        public void ErrorsAndWarningsAreCountedSoASoakCanBeAsked()
        {
            var log = new ServerLog();

            log.Write(LogLevel.Info, "sim", "match started");
            log.Write(LogLevel.Warn, "net", "a peer went quiet");
            log.Write(LogLevel.Error, "sim", "system threw on tick");
            log.Write(LogLevel.Error, "sim", "another thing threw");

            // The question after an overnight run is "was it healthy", and the honest answer is a
            // count. Grepping a log to find out is fine once and useless as something a probe or a
            // status line can do every few seconds.
            Assert.AreEqual(2, log.Errors);
            Assert.AreEqual(1, log.Warnings);
        }

        [Test]
        public void TheSameMessageOverAndOverIsSaidOnceAndThenCounted()
        {
            var log = new ServerLog();
            int written = 0;
            log.Sink = _ => written++;

            for (int i = 0; i < 1000; i++)
                log.Write(LogLevel.Error, "sim", "system threw on tick");

            // One system throwing every tick is sixty lines a second and a full disk by morning,
            // with the first and only interesting line a quarter of a million lines up. Saying it
            // once and counting the rest keeps both the disk and the signal.
            Assert.Less(written, 10, "a thousand identical errors is not a thousand lines");
            Assert.GreaterOrEqual(written, 1, "but it is certainly not none");

            Assert.AreEqual(1000, log.Errors, "and every one of them still counts");
        }

        [Test]
        public void ADifferentMessageIsNotSwallowedByTheOneBeforeIt()
        {
            var log = new ServerLog();
            int written = 0;
            log.Sink = _ => written++;

            log.Write(LogLevel.Error, "sim", "the first thing");
            log.Write(LogLevel.Error, "sim", "a different thing");
            log.Write(LogLevel.Error, "net", "the first thing");

            // Suppression that is too eager is worse than none: the second distinct failure in a
            // cascade is usually the one that explains the first, and a log that hides it leaves
            // somebody debugging the symptom.
            Assert.AreEqual(3, written, "three distinct things are three lines");
        }

        [Test]
        public void ASuppressedBurstIsSummarisedEvenIfItStops()
        {
            var log = new ServerLog();
            string last = null;
            log.Sink = line => last = line;

            var start = new System.DateTime(2026, 9, 20, 14, 0, 0, System.DateTimeKind.Utc);

            for (int i = 0; i < 50; i++)
                log.Write(LogLevel.Warn, "net", "a peer went quiet", start);

            // And then it stops. This is the case that matters and the one a naive fold misses
            // entirely: the count is only reported when the next identical line escapes, so a
            // system that threw fifty times and recovered would leave a single line claiming it
            // happened once. That is the shape of every incident closed as "could not reproduce".
            log.Flush(start.AddSeconds(ServerLog.RepeatWindowSeconds + 1));

            Assert.IsTrue(last.Contains("x50"),
                "the tally is written when the burst ends, not only when it repeats");

            Assert.AreEqual(50, log.Warnings, "and every one of them was counted all along");
        }
    }
}
