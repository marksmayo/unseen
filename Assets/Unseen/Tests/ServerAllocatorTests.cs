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

        [Test]
        public void ADoubleReleaseDoesNotInventCapacity()
        {
            var allocator = new ServerAllocator(capacity: 2);
            allocator.Register("only-one");

            string server = allocator.Allocate();

            // Released twice: one player left, and something upstream counted it twice. That is a
            // bookkeeping mistake and it will happen - a disconnect races a match ending, an
            // orchestrator retries a call it thought had failed.
            //
            // The wrong response is to let the count go negative, because a negative occupancy is
            // capacity that does not exist. A small accounting error then becomes players being
            // routed to a server with no room for them, which presents as a failed join nobody can
            // reproduce rather than as the counting bug it is.
            allocator.Release(server);
            allocator.Release(server);

            Assert.AreEqual(server, allocator.Allocate(), "the real slot is genuinely free");
            Assert.AreEqual(server, allocator.Allocate(), "and so is the second");

            Assert.IsNull(allocator.Allocate(),
                "but the third does not exist, however many times the second was given back");
        }

        [Test]
        public void AServerTakenOutOfTheFleetStopsReceivingPlayers()
        {
            var allocator = new ServerAllocator(capacity: 4);
            allocator.Register("doomed");
            allocator.Register("healthy");

            // A pod evicted, a node lost, a server drained for a deploy. Leaving it in the fleet is
            // worse than having one fewer machine: the packing prefers the fullest server with room,
            // so a dead one keeps being chosen, and every player sent there bounces.
            allocator.Remove("doomed");

            for (int i = 0; i < 4; i++)
                Assert.AreEqual("healthy", allocator.Allocate(), "nobody goes to a server that is gone");

            Assert.IsNull(allocator.Allocate(), "and the fleet is a machine smaller, honestly");
        }

        [Test]
        public void RemovingAServerTwiceIsHarmless()
        {
            var allocator = new ServerAllocator(capacity: 1);
            allocator.Register("a");

            // Kubernetes will happily tell you about the same deletion more than once, and an
            // orchestrator retrying is not a reason to throw inside a matchmaker.
            Assert.DoesNotThrow(() => allocator.Remove("a"));
            Assert.DoesNotThrow(() => allocator.Remove("a"));
            Assert.DoesNotThrow(() => allocator.Remove("never-existed"));

            Assert.IsNull(allocator.Allocate(), "an empty fleet has nothing to give");
        }

        [Test]
        public void ReleasingAServerNobodyRegisteredDoesNotPoisonTheName()
        {
            var allocator = new ServerAllocator(capacity: 1);

            // A disconnect arriving for a server the allocator has not been told about: an
            // orchestrator retrying, or a machine restarting with the name it had before. Agones
            // fleets reuse names, so this is ordinary rather than exotic.
            Assert.DoesNotThrow(() => allocator.Release("tokyo-1"));

            // The trap is not the release, it is what comes after. Releasing an unknown id without
            // a guard writes an entry for it, and Register returns early for any id it already has
            // an entry for - so the server would be silently dropped on the floor: never added to
            // the fleet, never allocated to, and nothing anywhere saying why. A server that boots,
            // reports healthy and receives no players is close to undiagnosable.
            allocator.Register("tokyo-1");

            Assert.AreEqual("tokyo-1", allocator.Allocate(),
                "a name released before it was registered must still be usable afterwards");
        }
    }
}
