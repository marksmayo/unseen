using NUnit.Framework;
using Unseen.Core;

namespace Unseen.Tests
{
    /// <summary>
    /// Which build this is.
    ///
    /// Three bugs in one day were "works in the editor, broken in the build" - a mesh imported
    /// without Read/Write, two shaders stripped for being reached only by name. Every one of them
    /// was found by running a binary and reading its log, and not one of those logs said which
    /// binary it was.
    ///
    /// That is fine while the only person running builds is the person who made them, ten minutes
    /// ago. It stops being fine the first time a tester says "it did it again on the build from
    /// Tuesday", and there were four builds on Tuesday.
    ///
    /// The parsing is tested; the git call behind it cannot be. What matters is that a missing or
    /// malformed stamp degrades to something honest rather than to a crash at startup or, worse,
    /// to a confident wrong answer.
    /// </summary>
    public sealed class BuildStampTests
    {
        [Test]
        public void AStampSurvivesBeingWrittenAndRead()
        {
            string written = BuildStamp.Write("0.4.1", "a1b2c3d", "2026-09-20T14:05:00Z", dirty: false);

            BuildStamp stamp = BuildStamp.Read(written);

            Assert.AreEqual("0.4.1", stamp.Version);
            Assert.AreEqual("a1b2c3d", stamp.Commit);
            Assert.IsFalse(stamp.Dirty);
        }

        [Test]
        public void AStampFromUncommittedCodeSaysSo()
        {
            string written = BuildStamp.Write("0.4.1", "a1b2c3d", "2026-09-20T14:05:00Z", dirty: true);

            // The commit alone is a lie for a build made from a working tree with changes in it,
            // and that is how most builds are made. Somebody chasing a bug back to a1b2c3d would
            // be reading code that was never in the binary.
            Assert.IsTrue(BuildStamp.Read(written).Dirty);
            Assert.IsTrue(BuildStamp.Read(written).Describe().Contains("+"),
                "and says it in the one-line description, where anybody will actually see it");
        }

        [Test]
        public void ADescriptionIsShortEnoughToPutOnAScreen()
        {
            BuildStamp stamp = BuildStamp.Read(
                BuildStamp.Write("0.4.1", "a1b2c3d", "2026-09-20T14:05:00Z", dirty: false));

            // It goes in a corner of the main menu and on one line of a log. A description that
            // wraps is one nobody reads and nobody quotes back to you.
            Assert.Less(stamp.Describe().Length, 48);
            Assert.IsTrue(stamp.Describe().Contains("0.4.1"));
            Assert.IsTrue(stamp.Describe().Contains("a1b2c3d"));
        }

        [Test]
        public void NoStampAtAllIsAnHonestAnswerRatherThanACrash()
        {
            // A build made before this existed, a stripped resource, somebody running from the
            // editor. None of those should throw during startup, and none should claim a version.
            BuildStamp missing = BuildStamp.Read(null);

            Assert.IsFalse(string.IsNullOrEmpty(missing.Describe()),
                "it still says something, because a blank corner reads as a bug in the menu");

            Assert.IsTrue(missing.Describe().Contains("unknown"),
                "and what it says is that it does not know");
        }

        [Test]
        public void RubbishIsTreatedAsNoStampRatherThanParsedOptimistically()
        {
            // A half-written file, a text asset that got mangled, a resource that is not this. The
            // wrong answer is to salvage a field or two and present them as the build's identity.
            Assert.IsTrue(BuildStamp.Read("").Describe().Contains("unknown"));
            Assert.IsTrue(BuildStamp.Read("<html>404</html>").Describe().Contains("unknown"));
            Assert.IsTrue(BuildStamp.Read("version=0.4.1").Describe().Contains("unknown"),
                "a version with no commit does not identify anything");
        }
    }
}
