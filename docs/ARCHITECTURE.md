# Architecture

## The one idea

Every other decision in this codebase falls out of a single rule: **an entity a player has not
perceived does not exist on that player's machine.** Not hidden, not culled at render time, not sent
with a "don't draw this" flag — absent from the packet.

That rule is why the perception pipeline runs before anything else in the tick, why the snapshot
format has no "all players" array, and why bots are fed the same perception products as humans rather
than reading the world directly.

## The loop

`UnseenBootstrap` builds one `ServerSimulation` and steps it from `Update`. The simulation runs a
fixed timestep at the **combat rate** (60 Hz by default) and gates base-rate systems to every third
tick, giving the 20 Hz spatial cadence the plan calls for without two separate loops.

```
Unity Update
   └─ INetworkService.Poll(dt)          transport pumps, input arrives
   └─ ServerSimulation.Advance(dt)      0..4 fixed steps of 1/60 s
          for each step, in SimOrder:

          100  ServerInputSystem        client intent -> agent.Intent (validated, clamped)
          150  WorldBufferSystem        managed agents -> struct-of-arrays mirror
          200  InterestGridSystem  (B)  rebuild the 3D voxel hash            [Burst job]
          250  StealthIndexService (B)  light exposure -> stealth index      [RaycastCommand batch]
          300  InterestManager     (B)  gates + line of sight -> visible set [RaycastCommand batch x2]
          350  AcousticPropagation (B)  sound spheres -> heard sounds        [RaycastCommand batch]
          400  CombatPocketSystem  (B)  who is hot this tick
          500  BotDirector              backfill, pressure job, throttled thinks [Burst job]
          599  DeploymentSystem    (B)  glider descent during infiltration
          600  MotionSystem             parkour motor: hot at 60 Hz, cold at 20 Hz
          700  CombatDirector           clash, parry, takedown, damage, interaction
          750  AgentEffectsSystem  (B)  expire item effects, refresh smoke cover
          800  MatchDirector       (B)  phase flow, placements, next match
          820  MistZoneController  (B)  circle schedule and mist damage
          900  ReplicationSystem        per-observer snapshots out

          (B) = base-rate system, runs on 1 tick in 3
```

Ordering is not incidental. Perception must resolve before AI decides, AI before motion, motion
before combat resolution, and replication last so a client never receives a half-settled frame.

## Why the perception pipeline is shaped like this

Naively, 64 entities means 4 032 visibility pairs per tick, each wanting a raycast. Three gates cut
that down before any physics work happens:

1. **Voxel hash** (`VoxelInterestGrid`) — a Burst job hashes every agent into a 16 m cell. Queries
   walk only the cells a sphere touches, so distant entities cost literally nothing.
2. **Range and frustum** (`LineOfSightService.PassesGate`) — cheap arithmetic, and the range shrinks
   with the *target's* stealth index. A ninja at 0.9 hidden is resolvable at ~12 m instead of 90 m.
   This is the single most important line in the game: darkness is range reduction.
3. **Batched raycasts** — survivors go into `RaycastCommand.ScheduleBatch`, two passes:
   opaque geometry ("is there anything there at all"), then paper ("do I see a person or a shape").
   A per-tick budget caps the batch; over budget, pairs reuse a cached result up to 150 ms old.

Only then does `InterestManager` write each observer's `Visible` list — the single source of truth
for replication, bot decisions and the HUD.

### The shoji silhouette is a real information channel

A paper wall does not hide you, it anonymises you. The second raycast pass classifies a contact as
`Silhouette`, and the snapshot encoder deliberately zeroes the yaw, flags and stance for those
contacts. A silhouette contact reaches the client with a position and nothing else, which is exactly
what the shader draws: a soft blob. There is no gear, health or facing in the packet to leak.

The silhouette also requires the *target* to be lit (`1 - stealthIndex > 0.35`). Standing in an unlit
room behind paper prints nothing.

## Tick LOD: combat pockets

`CombatPocketSystem` marks an agent **hot** when a hostile is within 18 m *and* one of them has seen
the other, has been hurt recently, or is mid-swing. Proximity alone is not enough — two agents on
opposite sides of a wall stay cold.

Hot agents:
- integrate motion every tick (60 Hz) instead of once per base tick,
- get a combat update every tick,
- receive snapshots every tick.

Cold agents integrate once per base tick with a proportionally larger step. That is what makes 64
entities affordable while a 1v1 clash still gets 60 Hz fidelity, and it is measurable in the status
line the server logs every five seconds.

## Data layout

- `AgentEntity` is the authoritative managed record: identity, flags, stance, stealth index, intent,
  perception results. One per ninja, human or bot, with no behavioural difference between them.
- `EntityRegistry` keeps agents in dense slots (swap-back removal) so jobs can address them by index,
  while `AgentId` stays unique for the whole match.
- `WorldBuffers` mirrors the table into `NativeArray`s at the top of every tick. This is the only
  view of the world the Burst jobs touch, which keeps the jobs free of managed references.

## Where the seams are

Two deliberate abstraction boundaries, because both of these are decisions the plan leaves open:

- **Transport** — everything above `INetworkService` is transport-agnostic. `OfflineNetworkService`
  is a loopback that still serialises and still filters, so offline practice exercises the real
  replication path. `Assets/Unseen/Integrations/FishNet` is a drop-in adapter. See
  [NETWORKING.md](NETWORKING.md).
- **Audio** — `SoundEventBus` and `AcousticPropagation` own gameplay audibility with their own
  raycast occlusion model. Steam Audio or FMOD, when added, render what the server already decided
  you heard; they do not decide it. The gameplay layer never depends on the middleware.

## Tuning

`UnseenConfig` is the only place numbers live. Systems read it every tick, so values can be changed
while playing. `UnseenConfig.Default` falls back to `Resources/UnseenConfig`, then to code defaults,
so the game boots with nothing assigned.
