# Roadmap

Status of each phase in [`plan.md`](../plan.md). "Done" means the system exists, is wired into the
live loop, and runs in the playable scene — not that it is shipped-quality or tuned.

---

## Phase 1 — Netcode and greybox (months 1–3)

| Deliverable | Status | Where |
| --- | --- | --- |
| Headless server instances | Done | `LaunchMode.DedicatedServer`, `Server/docker`, `Server/k8s` |
| 64-entity stress test (1 human + 63 bots) | Done | `BotDirector.MaintainPopulation` |
| 3D spatial partitioning | Done | `VoxelInterestGrid` (Burst) |
| Server-side line-of-sight culling | Done | `LineOfSightService`, `InterestManager` |
| Fish-Net / Fusion running the transport | **Adapter only** | `Integrations/FishNet` — needs a licensed install to compile and a real connect/reconnect test |

**Remaining:** stand up a real transport end to end and re-measure. Everything above the transport is
already exercised by the loopback service, so this is adapter and ops work, not redesign.

## Phase 2 — Core stealth and traversal (months 4–6)

| Deliverable | Status | Where |
| --- | --- | --- |
| Crouch-creeping, ledge hang, wall climb, wall run, rafters, slide | Done | `NinjaMotor`, `ParkourProbe` |
| Grappling hook with a noise penalty near enemies | Done | `GrapplingHook`, `NinjaMotor.TryStartGrapple` |
| Light/shadow stealth index, server-side | Done | `StealthIndexService`, `StealthLightSource` |
| Shoji silhouette shader | Done, and now actually renders | `ShojiSilhouette.shader`, `ShojiSilhouetteFeeder`, `GreyboxMaterialSet.ShojiPaper` |
| Raycast sound propagation and occlusion | Done | `AcousticPropagation`, `AcousticMaterial` |
| Bots on the same perception loop | Done | `BotBrain.Perceive` |
| Audible sound rendering | Done | `AudioBank`, `SoundRenderer`, `LocalSoundEmitter`, `AmbientWind` |
| Steam Audio / FMOD integration | **Not started** | Unity's own audio now renders the model; middleware would replace `SoundRenderer` only |

**Remaining:** middleware for the audible result, and real footstep/impact assets. The gameplay
contract (`HeardSound`: intensity, occlusion, direction, apparent position) is stable, so this is an
additive layer.

## Phase 3 — Combat, animation and destructibles (months 7–9)

| Deliverable | Status | Where |
| --- | --- | --- |
| Silent takedown, 1.5 s lockstep, motion warping | Done, and now actually fires | `CombatDirector.TryBeginTakedown`, `NinjaMotor.BeginMotionWarp` |
| Three-zone clash with a 150–200 ms latency-compensated parry | Done | `CombatDirector.UpdateGuard`, `ResolveStrike` |
| Guard break on heavies, stagger on parry | Done | `CombatDirector.ResolveStrike` |
| Smoke bombs, noisemakers, night-vision elixirs, shuriken | Done | `CombatDirector.HandleUtility`, `ThrowShuriken` |
| Sliceable shoji, extinguishable lanterns | Done | `ShojiPanel`, `Lantern` |
| Combat AI: parry, retreat, smoke when hurt | Done | `NinjaDomain` (`fight`, `disengage`) |
| Mecanim state machines and animation clips | Done for combat and stance | `UnseenAnimationSetup` authors the clips; `AgentVisual` drives the layers |

**Takedown bug, found 2026-08-25.** This was marked Done for months while firing exactly zero
times in every smoke run. `LineOfSightService.PassesGate` treated anything within 2.5 m as seen
regardless of facing; a takedown must happen inside 1.6 m; so every possible victim was always
aware of their attacker and no takedown was ever legal. Point-blank now *widens* the awareness cone
to 240 degrees instead of removing it, leaving a 120 degree rear blind arc - deliberately wider than
`CombatSection.TakedownRearArc` (110 degrees), so the two rules cannot contradict each other again.
`Unseen ▸ Probe Takedowns` stages the encounter and prints each gate separately.

The lesson generalises: "the system exists and is wired in" is not the same as "the system can
occur". Two individually reasonable rules can be jointly impossible, and only a test that asserts
the *outcome* catches it.

**Remaining:** the animation layer. Motion warping is implemented as authoritative transform
interpolation onto a target mark, which is precisely the input a warped animation needs; the clips and
the Animator graph are what is missing.

## Phase 4 — Map, BR loop and polish (months 10–12)

| Deliverable | Status | Where |
| --- | --- | --- |
| Multi-level map: rooftops, interiors, rafters, sewers | Done, procedurally | `GreyboxTownGenerator` |
| Shrinking mist zone controller | Done | `MistZoneController`, `MistWall.shader` |
| Loot tables and containers | Done | `LootTable`, `LootContainer` |
| Glider / canopy infiltration | Done | `DeploymentSystem` |
| Match flow, placements, next match | Done | `MatchDirector` |
| Bot backfill in queues, offline single player | Done | `BotDirector`, `LaunchMode.OfflinePractice` |
| Authored art map | **Not started** | The generator is a stand-in; `MapDescriptor` is the contract an authored level implements |
| Cloud load test at 64 entities | **Not started** | Fleet manifests are written; the test needs the real transport first |

---

## Session of 2026-08-25/26

**Two features were marked Done while being incapable of occurring.** Both had every part present
and wired at each end, and a missing middle that nothing would ever have reported.

- *Silent takedowns* fired zero times ever. `LineOfSightService.PassesGate` treated anything within
  2.5 m as seen regardless of facing, and a takedown must happen inside 1.6 m, so every possible
  victim was permanently aware. Point-blank now widens the awareness cone instead of removing it,
  leaving a rear blind arc wider than `TakedownRearArc`.
- *Shoji silhouettes* rendered on nothing. The server computed contacts and `ShojiSilhouetteFeeder`
  pushed them to the GPU every frame, but no material used `Unseen/ShojiSilhouette`, so all 5,136
  panels drew as plain lit paper. Then the first lit version of the shader failed to compile and the
  shader's own `Fallback` silently substituted an unlit one, which still reported the right shader
  name on the material.

The lesson is now a rule for this project: **a test must assert the outcome, not the wiring.**
`Unseen ▸ Test Shoji Silhouettes` measures pixel brightness through a panel; `Unseen ▸ Probe
Takedowns` prints every gate separately; `Unseen ▸ Audit Controls` presses each control and reports
what moved; `Unseen ▸ Test Match Cycle` kills agents and checks the bodies come back. Each one
caught a real fault on its first run.

**Performance.** Per-system timing was added to `ServerSimulation` because "the frame was slow" does
not say which of fourteen systems made it slow. `StealthIndexService` was scanning every light in
the world once per agent per tick: fine at 94 lanterns, and 204 ms per tick at 1,300 - over 90% of
all simulation cost. Lanterns never move, so `StealthLightGrid` indexes them once. **204.13 ms to
1.07 ms.** Next worst are `InterestManager` (20.7 ms) and `ReplicationSystem` (14.3 ms), both
in-editor figures; a real build measured a 4.25 ms median.

**Death.** There was no flow after dying: the camera stayed locked to a corpse that sank and
switched itself off, with the match running on for minutes. It was reported as a crash, and the
player log proved it was an orderly shutdown with no exception anywhere. There is now a collapse
that finds the ground and falls to it, an elimination feed, and spectating that cycles through
living agents. And a bug that had shipped three times: the sink stage disabled the body and nothing
ever called `AgentDeathVisual.Reset`, so every agent that died stayed invisible for the rest of the
session.

**Build environment.** Every long batch run was stalling on `worker timed out connecting with
editor`. Unity's out-of-process asset import workers cannot connect here, and each failure costs a
multi-minute timeout mid-run. `ProjectSettings/EditorSettings.asset` now pins importing to the main
process (`m_DesiredImportWorkerCount: 0`). Builds went from timing out to 148 s.

---

## Verified on 2026-08-24

Unity **6000.5.9f1** installed and the project brought up on it. Actual state, not estimates:

| Check | Result |
| --- | --- |
| Compile (batch mode, all four assemblies) | Clean |
| EditMode tests | 19 / 19 passed |
| Project setup (layers, config asset, scene) | Runs headlessly |
| Headless 64-entity match, 120 simulated seconds | Passed, 0 errors, 0 exceptions |

From the smoke run (`Unseen/Run Headless Smoke Test`):

```
greybox town: 576 shoji, 94 lanterns, 44 containers, radius 132 m
entities 64  alive 39  bots 63          (25 deaths in 120 s, mist still at stage 0)
perception:  pairs 34  visible 34  rays 22  dropped 0
pockets 20  hot 25  motion-hot 25
acoustics:   paths traced 3542  sounds delivered 3542
combat:      swings 398  hits 34  parries 5  takedowns 0  deaths 25
simulated 120 s in 28.0 s wall (7200 ticks, 6.39 ms last tick)
```

### What that run proves, and what it does not

Working end to end: greybox generation, agent spawning, the interest grid and line-of-sight budget
(nothing dropped), combat pockets and the hot/cold tick split, bot HTN planning, the parkour motor,
acoustic propagation, snapshot encoding, and the match state machine reaching the hunt phase.

Two numbers deserve suspicion rather than celebration:

- **`paths traced 3542` equals `sounds delivered 3542` exactly.** Every traced sound path survived to
  a listener, meaning occlusion never once pushed a sound below the audibility floor. That may simply
  be geometry - agents fighting in open streets have no wall between them - but it could equally mean
  the acoustic raycasts are not hitting the geometry they should. Verify before trusting the occlusion
  model: put two agents either side of a wall and confirm the delivered intensity drops.
- **`takedowns 0`.** The silent takedown is the headline mechanic in `plan.md`, and it never fired in
  120 seconds across 63 bots. Plausibly correct - a bot that walks into view alerts its target, which
  is exactly what should disqualify a takedown - but it has never been observed succeeding, so treat
  it as unproven rather than working. Worth a targeted test: park an unaware bot and walk up behind it.

Also unproven by this harness: anything that needs a play session (animation, audio rendering,
client-side rendering and HUD), and anything needing a real transport.

## Session of 2026-08-27

**Both suspicious numbers from the 08-24 smoke run are settled, and the NavMesh is baked.**

*Occlusion is correct.* `paths traced 3542 == sounds delivered 3542` was, as that entry allowed,
simply geometry: agents in that run were fighting in open streets. `Unseen ▸ Probe Sound Occlusion`
stands two agents twelve metres apart on open ground and puts walls between them, holding distance
constant so the only variable is what is in the way. Open ground delivers intensity 0.473 at zero
occlusion; one Occluder wall delivers 0.142, which is 0.30 of it against a configured attenuation of
0.7; one shoji screen delivers 0.416, which is 0.88 against a configured 0.12; three stone walls
saturate occlusion at 1.0 and the sound is dropped below the audibility floor entirely. The model
does what it says to two decimal places.

The first run of that probe reported zero occlusion through three stone walls and looked exactly like
a broken acoustic model. The walls were not in the physics scene: Unity does not auto-sync transforms
in edit mode, so a collider created and then positioned is still at the origin as far as any raycast
is concerned. A plain `Physics.Raycast` on the same mask, added as a control, found zero blockers and
named the culprit immediately.

*The bamboo was never drifting.* `UnseenBoundsProbe` had been failing on "900 of 2,772 renderers
adrift, worst 9.4 m", and it was measuring the wrong thing: each renderer's BOUNDS CENTRE against its
transform position. A culm is a tube standing deliberately ON its origin so it grows out of the
ground instead of being half buried, which puts the centre of a fifteen metre cane seven and a half
metres above its base by construction. Every one of the 900 was exactly where it belonged. The
section now measures what it claimed to: the solid wall against its own colliders (worst gap 0.00 m),
the canes against the damage radius (900 canes between 203.1 m and 204.3 m against an edge at
204.6 m), and that the forest sits on the circle it was given with no margin.

*A NavMesh exists.* `BotNavigator` had been written to path on one since its first commit and every
call had failed silently, because the town is generated at startup and differs by seed - there was
never a level to bake. `NavMeshBaker` builds it in-process after generation in 0.60 s, covering 1312
of 1314 dry ground samples. Bots following a real path went from zero to about fifty of sixty-three.
Physics colliders rather than render meshes, and a coarser voxel than the default third-of-a-radius,
which is the difference between seconds and minutes across seven hundred and fifty metres.

Deep water is carved unwalkable from the registered `WaterVolume`s. The river keeps its shallow
shelves so it can still be forded at the edges; its middle and the whole castle lake are out, and a
bank-to-bank route comes back complete at 246 m against 128 m straight. An off-mesh bot now walks
back onto the mesh before pursuing anything, because marking water unwalkable stops a bot routing
through a lake and does nothing about one already in it.

**What could not be shown.** The claim that this stops bots drowning is unproven. Counting drownings
gave one against one; counting time spent in drownable water gave 992/1044, 1117/1229, 1447/1011 and
955/1243 across four runs - the ordering flips, so the number is dominated by a few bots parked in
one spot rather than by how often anybody walks in. Roughly one bot-second in seven is still spent in
deep water with the bake. It is left in the probe as a labelled diagnostic and deliberately not as a
gate. **Open problem.**

Also corrected: "bots drown in the lake", asserted earlier that session, came from one uninvestigated
agent death in a test warm-up. Measured properly it is nought to one per match, and deaths are
overwhelmingly melee.

**A general bug in the scheduler.** `frame.Dt` was always the combat step, even for systems that run
on one tick in three, so anything integrating it advanced its clock at a third of real time. Critters
lived in slow motion - 289 of them managed 271 outings in four minutes against the fifteen hundred
their configured rest interval implies, and it is 1092 now. The mist collapse and the spirit forest's
damage were already correct because both reach past `frame.Dt` for `BaseTickInterval` by hand, and
three separate workarounds for the same thing is the tell that `Dt` was the bug rather than them.

**Three tests were measuring the wrong thing** and had to be fixed before they could fail honestly.
The shuriken test's target was a bot that walked off the mark it was teleported to, so the blade flew
its whole life hitting nothing and the test blamed the blade. The drowning test grabbed whichever
body of water came first and got the new lake, measuring minus half a metre of depth. The critter
probe counted bodies away from their start, which is a number about the sampling instant - every
stroll target is picked relative to home, so twenty outings look like one - and it read 149 one day
and 68 the next with nothing changed.

## Session of 2026-08-28

A long play-test pass. Most of it was reported from play rather than found by a test, which is
itself worth noting: the probes are good at the things they were written for and blind to
everything else.

**Fixed and measured.** The shuriken went left of the crosshair because the camera sits half a
metre right of the ninja and the blade left the body's centre line - two parallel rays, half a
metre apart, all the way out. It launches from the camera's lateral line now. Shuriken are
unlimited on a five second cooldown, fly flat (they sagged 1.35 m at 24 m, now 0.22), are drawn at
twice life size, and can be heard: every one of their sounds had been credited to the thrower, and
the acoustic model refuses to deliver a sound to its own source, so the one person who could not
hear the blade was the person who threw it. Bots throw them now - there was no throw action in the
HTN domain at all. There is a throw animation, mirrored onto the replicated flags so other players
see it.

Nothing stands on air any more. Verges, hedges, pots and animals were laid out on the street grid
at y = 0 and never asked what was underneath, so anything jittered over the river channel hung at
street level. Each had its own hand-tuned river-exclusion radius and every one of them tested the
GRID position rather than the jittered one. One grounding rule replaced six of them, and the probe
written for it immediately found a second bug: the rule rejected ground far below the intended
height and happily accepted a roof eight metres above, so verges were being laid on rooftops with
their tufts hanging over the road.

Trees no longer grow through walls, nudged along the verge rather than dropped - deleting the ones
that did not fit cost a third of the trees, and trees are cover: the share of bot ticks spent in
combat went from nine per cent to thirty without them.

The castle could not be grappled: its only anchors were on the hip roof, and on a five storey keep
whose lowest eave overhangs twenty-two metres those sit behind the building from anywhere you can
stand. Every storey's eave corners carry a bracket now.

The mist wall read as frosted glass because its noise was four octaves of sin times cos - a smooth
periodic ripple, not turbulence - sampled from mesh UVs, so the tessellation showed straight
through it. Real value noise in world space, domain warped, two layers against each other. Bodies
evaporate into mist ten seconds after death instead of sinking through the floor. Critters look
about, dip, wag and flick their ears while resting, driven on the head and tail rather than the
whole body.

**The interface** has one visual language now rather than default IMGUI, and the runtime log is
quiet in a player build - about thirty-five unconditional Debug.Log calls of generation statistics
are gated behind a flag that defaults on in the editor and off in a player, with `-unseen-verbose`
for a dedicated server. Verified in the shipped build: zero lines by default, twenty-seven with the
flag.

**A performance pass**, measured with a new `Unseen ▸ Probe Cost`. Shadow casters went from 92,610
of 93,445 renderers to 10,222: every renderer-only piece of trim was casting, along with 996 lit
paper lanterns and 2,772 shoji panels casting as TRANSPARENT, which is the most expensive kind.
Trim moved to its own layer and stops being drawn past eighty-five metres - 78,485 renderers, 84%
of the town, averaging a dozen triangles. Ground mist coverage halved, from 978,000 square metres
of alpha over a 562,000 square metre map to 379,000.

That probe's own first run claimed 1.3 billion triangles, because `StaticBatchingUtility.Combine`
repoints every renderer's shared mesh at one combined mesh and asking a renderer for its triangle
count afterwards returns the whole batch. Generation can be told to skip combining now, which is
the only way to measure the real geometry.

**Corrections to things asserted earlier in this project.** "Bake occlusion culling" was wrong: the
town is generated at runtime and there is no scene geometry for a bake to have been computed from.
Per-layer cull distances are the runtime equivalent. And "bots drown in the lake", from the session
before, came from a single uninvestigated death in a test warm-up; measured properly it is nought
to one per match.

**Six tests were measuring the wrong thing** and were fixed before they could fail honestly. The
pattern is now clear enough to name: fixed time windows and fixed map positions, quietly
invalidated as the game around them changed. The river probe's dry control run was pinned a hundred
and twenty metres east of the channel and the castle lake was later built across it, so both its
figures came out at 4.8 m and it reported that wading is no slower than walking. The critter
probe's six second window grew longer than the rest interval it was sampling. The shuriken test
stood its target in front of the body, which stopped being the line the blade travels the moment
the aim was fixed. Two more measured a bot's stance machine rather than the thing under test.

## Immediate next steps, in order

1. **The bot roaming regression.** `UnseenBotRoamProbe` has declined all session - mean path walked
   per bot has gone 184 m, 158, 131, 103 - and now fails both "cover ground" and "not pacing on the
   spot", with some bots at a net-to-path ratio of zero. Bots are sixty-three of the sixty-four
   entities in every match, so this is most of what a player sees. The town has grown a great deal
   denser over the same period, including a hundred and twenty metre unwalkable lake through the
   middle of it, and the guess is that they are winding around obstruction rather than travelling.
   That is a guess. Measure where they actually stop.
2. **Wire a real transport.** Install Fish-Net, define `UNSEEN_FISHNET`, add a `NetworkManager`, and
   test a second client joining and taking over a bot slot. Still the largest structural gap: the
   shipped playtest build is single-player only.
3. **Get a GPU frame capture.** The whole performance pass was counts - renderers, triangles,
   casters, coverage - chosen because they are unambiguous waste. Not one of them is a timing, so
   which of them actually dominated a frame is still unknown, and the next round cannot be aimed
   without one.
4. **Bots in the castle lake.** Sharper than it was: river wading is now zero, because they were
   being SPAWNED in the river and no longer are. What remains is all lake, with ninety-two samples
   of bots stuck in it. One body of water, one cause left to find.
5. **Fix `UnseenZoneCollapseTest`.** It has never observed the final collapse: the match ends at
   t = 20 s, before the final phase produces a single sample. It is currently testing nothing.
6. **Then** animation and audio middleware - both additive layers over stable contracts, and both
   much easier to judge once the fight feels right at 60 Hz.

## Known gaps and rough edges

- No client-side prediction. Clients send intent and render the authoritative result, so movement has
  one round trip of latency. Acceptable at stealth pacing, and the intent/snapshot split is already
  the right shape to add prediction to the local player later.
- The visibility linger window (0.35 s) trades a sliver of stale information for stable proxies. It is
  a deliberate, configurable compromise, not an oversight — see [NETWORKING.md](NETWORKING.md).
- Melee has no lag compensation by design; only the parry window is latency-adjusted.
- ~~Corpses stay as capsules; there is no death cam or spectate flow.~~ Done 2026-08-25:
  there is a collapse that finds the ground, an elimination feed, and spectating that
  cycles through the living.
- `ItemDefinition` assets are generated in code by the greybox generator. Authoring them as real
  assets is a straight lift once the loot table is being tuned by a designer.
- No matchmaking service. `BotDirector` handles in-match backfill; queueing players across servers is
  out of scope here.
