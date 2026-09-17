using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// Deciding which server a joining player is sent to.
    ///
    /// Kept as plain logic over a set of known servers, with no HTTP and no Agones: what the fleet
    /// needs from matchmaking is this decision, and a decision that can only be tested by deploying
    /// it is a decision nobody will test.
    /// </summary>
    public sealed class ServerAllocatorTests
    {
        [Test]
        public void PlayersFillOneServerBeforeSpillingToTheNext()
        {
            var allocator = new ServerAllocator(capacity: 4);
            allocator.Register("tokyo-1");
            allocator.Register("tokyo-2");

            string first = allocator.Allocate();

            // The rule that matters for a fleet. Spreading players evenly across servers with room
            // is the obvious implementation and it is the wrong one: a hundred players across a
            // hundred servers is a hundred matches that can never start and a hundred machines
            // being paid for. Packing means a match reaches its player count and begins.
            Assert.AreEqual(first, allocator.Allocate(), "second player joins the same match");
            Assert.AreEqual(first, allocator.Allocate(), "and the third");
            Assert.AreEqual(first, allocator.Allocate(), "and the fourth fills it");

            Assert.AreNotEqual(first, allocator.Allocate(),
                "only once it is full does anybody open the next one");
        }

        [Test]
        public void AFullFleetSaysSoRatherThanOverfilling()
        {
            var allocator = new ServerAllocator(capacity: 2);
            allocator.Register("only-one");

            allocator.Allocate();
            allocator.Allocate();

            // Null, not an exception: a full fleet on a busy evening is an ordinary state and the
            // caller's answer is to start another server. Throwing would dress the normal case as a
            // fault and push it into a catch block, where it gets logged and forgotten.
            Assert.IsNull(allocator.Allocate(), "a full fleet must refuse rather than overfill");
        }

        [Test]
        public void LeavingPlayersGiveTheirSlotBack()
        {
            var allocator = new ServerAllocator(capacity: 1);
            allocator.Register("only-one");

            string server = allocator.Allocate();
            Assert.IsNull(allocator.Allocate(), "full, as expected");

            allocator.Release(server);

            // Without this the occupancy only ever climbs, so a fleet that has been up for a day
            // reports every server full while every match is empty. The same shape as the
            // gatekeeper's ghost connections: state that accumulates because leaving is silent.
            Assert.AreEqual(server, allocator.Allocate(),
                "the freed slot should be handed to the next player");
        }
    }
}
