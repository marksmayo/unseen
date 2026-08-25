using System.Collections.Generic;
using NUnit.Framework;
using Unseen.AI;

namespace Unseen.Tests
{
    public sealed class HtnPlannerTests
    {
        private readonly HtnPlanner _planner = new HtnPlanner();
        private readonly List<PrimitiveTask> _plan = new List<PrimitiveTask>();

        [Test]
        public void FirstValidMethodWins()
        {
            var urgent = new PrimitiveTask("urgent", BotAction.Retreat);
            var normal = new PrimitiveTask("normal", BotAction.PatrolTo);

            var root = new CompoundTask("root")
                .With(new HtnMethod("urgent", f => f.Injured, urgent))
                .With(new HtnMethod("normal", null, normal));

            var facts = new BotFacts { Injured = true };
            Assert.IsTrue(_planner.Plan(root, facts, _plan));
            Assert.AreEqual(BotAction.Retreat, _plan[0].Action);

            facts.Injured = false;
            Assert.IsTrue(_planner.Plan(root, facts, _plan));
            Assert.AreEqual(BotAction.PatrolTo, _plan[0].Action);
        }

        [Test]
        public void MethodWithFailingPrimitiveRollsBackToNextMethod()
        {
            // The first method looks applicable but contains a primitive that cannot run, so the
            // planner must discard its partial plan rather than emit a broken sequence.
            var needsSmoke = new PrimitiveTask("throw-smoke", BotAction.ThrowSmoke, f => f.HasSmoke);
            var setup = new PrimitiveTask("setup", BotAction.HoldAmbush);
            var fallback = new PrimitiveTask("fallback", BotAction.Retreat);

            var root = new CompoundTask("root")
                .With(new HtnMethod("smoke-and-go", null, setup, needsSmoke))
                .With(new HtnMethod("just-go", null, fallback));

            var facts = new BotFacts { HasSmoke = false };
            Assert.IsTrue(_planner.Plan(root, facts, _plan));
            Assert.AreEqual(1, _plan.Count, "partial plan from the failed method should be rolled back");
            Assert.AreEqual(BotAction.Retreat, _plan[0].Action);

            facts.HasSmoke = true;
            Assert.IsTrue(_planner.Plan(root, facts, _plan));
            Assert.AreEqual(2, _plan.Count);
            Assert.AreEqual(BotAction.HoldAmbush, _plan[0].Action);
            Assert.AreEqual(BotAction.ThrowSmoke, _plan[1].Action);
        }

        [Test]
        public void NestedCompoundsDecompose()
        {
            var strike = new PrimitiveTask("strike", BotAction.Strike, f => f.TargetInMeleeRange);
            var approach = new PrimitiveTask("approach", BotAction.Approach);

            var fight = new CompoundTask("fight")
                .With(new HtnMethod("trade", f => f.TargetInMeleeRange, strike))
                .With(new HtnMethod("close", null, approach));

            var root = new CompoundTask("root")
                .With(new HtnMethod("engage", f => f.HasTarget, fight));

            var facts = new BotFacts { HasTarget = true, TargetInMeleeRange = true };
            Assert.IsTrue(_planner.Plan(root, facts, _plan));
            Assert.AreEqual(BotAction.Strike, _plan[0].Action);

            facts.TargetInMeleeRange = false;
            Assert.IsTrue(_planner.Plan(root, facts, _plan));
            Assert.AreEqual(BotAction.Approach, _plan[0].Action);
        }

        [Test]
        public void NoApplicableMethodProducesNoPlan()
        {
            var root = new CompoundTask("root")
                .With(new HtnMethod("impossible", f => f.HasSmoke,
                    new PrimitiveTask("smoke", BotAction.ThrowSmoke)));

            Assert.IsFalse(_planner.Plan(root, new BotFacts(), _plan));
            Assert.AreEqual(0, _plan.Count);
        }

        [Test]
        public void ShippedNinjaDomainAlwaysProducesAPlan()
        {
            // The live domain must have a terminal fallback for every combination of facts, or a bot
            // would stand still for a tick. Sweep the flags that gate the top-level methods.
            CompoundTask domain = NinjaDomain.Build();

            for (int mask = 0; mask < 64; mask++)
            {
                var facts = new BotFacts
                {
                    OutsideZone = (mask & 1) != 0,
                    Injured = (mask & 2) != 0,
                    UnderAttack = (mask & 4) != 0,
                    HasTarget = (mask & 8) != 0,
                    TargetVisible = (mask & 16) != 0,
                    HeardSomething = (mask & 32) != 0
                };

                Assert.IsTrue(_planner.Plan(domain, facts, _plan),
                    $"ninja domain produced no plan for fact mask {mask}");
                Assert.Greater(_plan.Count, 0);
            }
        }
    }
}
