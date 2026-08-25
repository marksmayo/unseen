# Bots

## The constraint

A bot reads exactly two things: its own `AgentEntity.Visible` list and its own `Heard` queue — the
same two products the replication system packs into a human's snapshot. It never queries another
agent's transform, health or intent.

That is not a courtesy, it is what makes the whole design testable. If a bot could see through a
shoji panel, the panel would stop being a gameplay object and become a rendering effect. It also
means a bug in the perception pipeline shows up as bots behaving oddly in single player, long before
it shows up as a player complaint about a wallhack.

Two places where a bot is allowed to use information a player also has, documented so they don't look
like cheating:

- **Reading a wind-up.** `BotFacts.EnemyIsSwinging` checks a *visible* target's attack phase. The
  telegraph is on screen for a human too. Whether the bot then guards the correct zone is a dice roll
  against `Bots.ParryAptitude` — a bot that fails the read guards wrong and eats the hit.
- **Awareness.** `BotFacts.TargetUnaware` uses the same `IsUnawareOf` test the combat director will
  apply when the takedown is attempted, so a bot never commits to an execution the server will refuse.

## Behaviour states

The states from `plan.md`, driven by timers in `BotBrain.UpdateState`:

```
        [PATROL / CREEP]
               |
      (hears noise / sees a silhouette)
               v
        [INVESTIGATE] ---(trail goes cold)---> [SEARCH AREA]
               |                                     |
      (confirms line of sight)                   (timeout)
               v                                     v
   [AMBUSH] --> [COMBAT] ------------------> [PATROL / CREEP]
               |
      (injured and under attack)
               v
            [FLEE]
```

`Ambush` rather than `Combat` is chosen when the bot is concealed (stealth index above the concealed
threshold), the target is more than 6 m away, and it is not already being hit. A concealed bot waits
in the dark instead of charging — which is the behaviour that makes a shadow feel dangerous.

## The HTN domain

State selects the situation; the **Hierarchical Task Network** in `NinjaDomain.cs` selects the action.
Methods are ordered, and the first one whose condition holds wins, so the domain reads as a priority
list:

```
be-a-ninja
├── escape-the-mist      outside the circle              -> move-into-zone
├── break-off            injured and engaged             -> disengage
│                                                            ├── smoke-and-go (has smoke)
│                                                            └── just-go
├── engage               has a visible target            -> fight
│                                                            ├── parry-the-swing
│                                                            ├── execute        (in range, unaware)
│                                                            ├── trade          (in range)
│                                                            ├── close-the-gap
│                                                            └── stalk
├── hunt                 has a contact or heard something
│                                                            ├── kill-the-light (exposed, lantern near)
│                                                            ├── set-an-ambush  (concealed)
│                                                            ├── stalk-last-seen
│                                                            ├── chase-the-noise
│                                                            └── sweep
└── prowl                otherwise
                                                             ├── gear-up (loot nearby)
                                                             ├── creep-in-the-open (exposed)
                                                             └── patrol
```

The planner (`HtnPlanner`) is a depth-first decomposition with rollback: if a method's subtask turns
out not to apply, the partial plan is discarded and the next method is tried. It allocates nothing per
plan, which is what makes replanning 63 bots inside one tick affordable. `HtnPlannerTests` covers
priority order, rollback, nesting, and sweeps 64 fact combinations to prove the shipped domain always
terminates in an action.

Each chosen primitive carries a **commitment duration**, jittered per bot, so a lobby does not replan
in lockstep and bots don't twitch between actions frame to frame.

## Tick LOD

The plan calls for dynamic throttling; `BotDirector.RateFor` implements it:

| Tier | Condition | Rate |
| --- | --- | --- |
| Combat | in a combat pocket, or in the `Combat` state | 30 Hz |
| Alert | perceived pressure > 0.1, or investigating / ambushing / fleeing / in the mist | 12 Hz |
| Idle | anything else | 4 Hz |

"Perceived pressure" is computed off the main thread. `PressureJob` (Burst, `IJobParallelFor`) scores
every (bot, visible contact) pair by confidence over distance; the main thread reduces per bot. So the
cost of 63 bots watching each other stays off the critical path, and the reduction decides who is
worth thinking about this tick.

## Navigation

`BotNavigator` uses `NavMesh.CalculatePath` when the level has a bake, and falls back to direct
steering with whisker avoidance when it does not. The fallback matters: the greybox town is generated
at runtime, so there is no bake on first run and bots still have to function. Bake a NavMesh over the
generated town (or an authored one) and pathing gets noticeably better through doorways and around
compound walls.

Vault detection is a two-ray test in `MoveTowards`: blocked at chest height, clear above head height
means jump. That is how bots get onto roofs and over compound walls without authored links.

## Backfill

`BotDirector` keeps the entity count topped up to `Match.TargetEntityCount` (64), spawning at most 4
per tick so filling a lobby never hitches.

- **A human joins** → takes over an existing bot body, preferring one that is alive and not in a
  fight. Position, inventory and placement carry over; the brain is disabled. Nobody inherits a
  losing clash they didn't start.
- **A human leaves** → the same body reverts to a bot, brain re-enabled and reset.

Because a bot is mechanically identical to a player, the handover has no discontinuity to hide.

## Tuning

Everything lives in `UnseenConfig.Bots`: the three tick rates, reaction time, investigate and search
timeouts, noise interest threshold, flee health fraction, parry aptitude and sloppiness. `SkillOffset`
is per bot, derived from its entity id, and scales reaction time, commitment duration and parry
aptitude so a lobby has a spread of competence rather than 63 identical opponents.
