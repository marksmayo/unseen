namespace Unseen.AI
{
    /// <summary>
    /// The ninja HTN domain. Read it top to bottom as a priority list: survive the mist, break off
    /// when badly hurt, execute an unaware target, fight one that has seen you, stalk a silhouette,
    /// investigate a noise, otherwise scavenge and creep.
    ///
    /// Every precondition below is a fact the bot legitimately perceived. There is no branch that
    /// reads the true position of a target the bot has not resolved.
    /// </summary>
    public static class NinjaDomain
    {
        public static CompoundTask Build()
        {
            // Leaves.
            var moveIntoZone = new PrimitiveTask("move-into-zone", BotAction.MoveIntoZone, duration: 1.5f);
            var throwSmoke = new PrimitiveTask("throw-smoke", BotAction.ThrowSmoke,
                f => f.HasSmoke, 0.4f);
            var retreat = new PrimitiveTask("retreat", BotAction.Retreat, duration: 1.2f);
            var takedown = new PrimitiveTask("takedown", BotAction.TakeDownTarget,
                f => f.TargetInMeleeRange && f.TargetUnaware, 0.6f);
            var strike = new PrimitiveTask("strike", BotAction.Strike,
                f => f.TargetInMeleeRange, 0.35f);
            var parry = new PrimitiveTask("parry", BotAction.Parry,
                f => f.EnemyIsSwinging, 0.3f);
            var approach = new PrimitiveTask("approach", BotAction.Approach,
                f => f.TargetVisible || f.TargetIsSilhouette, 0.6f);
            var creepToTarget = new PrimitiveTask("creep-to-target", BotAction.CreepTo,
                f => f.HasTarget, 0.8f);
            var holdAmbush = new PrimitiveTask("hold-ambush", BotAction.HoldAmbush, duration: 1.6f);
            var breakLantern = new PrimitiveTask("break-lantern", BotAction.BreakLantern,
                f => f.LanternNearby && !f.Concealed, 0.5f);
            var moveToNoise = new PrimitiveTask("move-to-noise", BotAction.MoveToNoise,
                f => f.HeardSomething, 1f);
            var searchNearby = new PrimitiveTask("search-nearby", BotAction.SearchNearby, duration: 1.5f);
            var loot = new PrimitiveTask("loot", BotAction.LootContainer,
                f => f.LootNearby, 0.8f);
            var patrol = new PrimitiveTask("patrol", BotAction.PatrolTo, duration: 2f);
            var creep = new PrimitiveTask("creep", BotAction.CreepTo, duration: 2f);

            // Fight: the three-zone clash, with a parry attempt when the enemy commits.
            var fight = new CompoundTask("fight")
                .With(new HtnMethod("parry-the-swing", f => f.EnemyIsSwinging, parry))
                .With(new HtnMethod("execute", f => f.TargetInMeleeRange && f.TargetUnaware, takedown))
                .With(new HtnMethod("trade", f => f.TargetInMeleeRange, strike))
                .With(new HtnMethod("close-the-gap", f => f.TargetInApproachRange, approach))
                .With(new HtnMethod("stalk", null, creepToTarget));

            // Disengage: smoke first if we have it, then break line of sight.
            var disengage = new CompoundTask("disengage")
                .With(new HtnMethod("smoke-and-go", f => f.HasSmoke, throwSmoke, retreat))
                .With(new HtnMethod("just-go", null, retreat));

            // Hunt a contact we cannot currently see.
            var hunt = new CompoundTask("hunt")
                .With(new HtnMethod("kill-the-light", f => f.LanternNearby && !f.Concealed, breakLantern))
                .With(new HtnMethod("set-an-ambush", f => f.Concealed, holdAmbush))
                .With(new HtnMethod("stalk-last-seen", f => f.HasTarget, creepToTarget))
                .With(new HtnMethod("chase-the-noise", f => f.HeardSomething, moveToNoise))
                .With(new HtnMethod("sweep", null, searchNearby));

            // Idle behaviour: gear up, then keep moving through cover.
            var prowl = new CompoundTask("prowl")
                .With(new HtnMethod("gear-up", f => f.LootNearby, loot))
                .With(new HtnMethod("creep-in-the-open", f => !f.Concealed, creep))
                .With(new HtnMethod("patrol", null, patrol));

            return new CompoundTask("be-a-ninja")
                .With(new HtnMethod("escape-the-mist", f => f.OutsideZone, moveIntoZone))
                .With(new HtnMethod("break-off", f => f.Injured && (f.UnderAttack || f.TargetVisible), disengage))
                .With(new HtnMethod("engage", f => f.HasTarget && (f.TargetVisible || f.TargetInMeleeRange), fight))
                .With(new HtnMethod("hunt", f => f.HasTarget || f.HeardSomething, hunt))
                .With(new HtnMethod("prowl", null, prowl));
        }
    }
}
