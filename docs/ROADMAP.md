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

## Immediate next steps, in order

1. **Confirm the two suspicious numbers above** — the occlusion check and the takedown check. Both
   are small, targeted experiments, and both concern mechanics the design depends on.
2. **Wire a real transport.** Install Fish-Net, define `UNSEEN_FISHNET`, add a `NetworkManager`, and
   test a second client joining and taking over a bot slot.
3. **Profile a full lobby.** Watch the status line: `sim` milliseconds per tick, `hot` count, `rays`,
   `dropped`. If `dropped` is non-zero, raise `Interest.LosRaycastBudget` or lower the replication
   radius, and re-measure. This is the number that decides how many entities a core can carry.
4. **Bake a NavMesh** over the generated town and compare bot pathing before and after.
5. **Then** animation and audio middleware — both are additive layers over stable contracts, and both
   are much easier to judge once the fight itself feels right at 60 Hz.

## Known gaps and rough edges

- No client-side prediction. Clients send intent and render the authoritative result, so movement has
  one round trip of latency. Acceptable at stealth pacing, and the intent/snapshot split is already
  the right shape to add prediction to the local player later.
- The visibility linger window (0.35 s) trades a sliver of stale information for stable proxies. It is
  a deliberate, configurable compromise, not an oversight — see [NETWORKING.md](NETWORKING.md).
- Melee has no lag compensation by design; only the parry window is latency-adjusted.
- Corpses stay as capsules; there is no death cam or spectate flow.
- `ItemDefinition` assets are generated in code by the greybox generator. Authoring them as real
  assets is a straight lift once the loot table is being tuned by a designer.
- No matchmaking service. `BotDirector` handles in-match backfill; queueing players across servers is
  out of scope here.
