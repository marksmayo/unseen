# Unseen

A 64-player, server-authoritative stealth-action battle royale in Unity, built to the design in
[`plan.md`](plan.md). Rival ninjas hunt each other through a multi-level Japanese fortress town:
rooftops, paper-walled interiors, rafters and sewers.

The pitch in one sentence: **the server decides what you are allowed to know, and everything else in
the game is built on top of that decision.**

---

## Current state

This repository is a working vertical slice of the systems in the plan, not a finished game. It has
no art, no animation clips and no audio assets — it has the *systems* those assets will hang off,
plus a procedural greybox town so all of it is playable today.

| Area | State |
| --- | --- |
| Fixed-step authoritative simulation, 20 Hz base / 60 Hz combat pockets | Implemented |
| 3D voxel interest management + server-authoritative line of sight | Implemented |
| Stealth index (light/shadow), shoji silhouettes | Implemented |
| Raycast sound propagation with occlusion-degraded directional pings | Implemented |
| Parkour motor: wall climb, wall run, ledge hang, rafters, slide, grapple | Implemented |
| Silent takedown with motion warping, 3-zone clash with latency-compensated parry | Implemented |
| Destructible shoji and lanterns, smoke, loot | Implemented |
| Battle royale loop: glider drop, mist stages, placement, bot backfill | Implemented |
| HTN bot brains sharing the players' perception, with tick LOD | Implemented |
| Snapshot protocol, loopback transport, Fish-Net adapter | Implemented (adapter is opt-in) |
| Procedural greybox castle town (compounds, keep, sewers) | Implemented |
| Real transport wired up end to end, animation, audio assets, authored art | **Not done** — see [docs/ROADMAP.md](docs/ROADMAP.md) |

Verified on Unity 6000.5.9f1: compiles clean, 19/19 EditMode tests pass, and a headless 64-entity
match runs 120 simulated seconds with zero errors. See
[docs/ROADMAP.md](docs/ROADMAP.md#verified-on-2026-08-24) for the numbers and, more importantly, for
what those numbers do *not* prove.

---

## Getting started

1. Install **Unity 6000.5.9f1** (Unity 6, Supported stream). `ProjectSettings/ProjectVersion.txt` pins it; a
   newer editor will offer to upgrade, which is fine. To install it without clicking through the
   Hub UI:

   ```powershell
   winget install --id Unity.UnityHub --exact
   $hub = (Get-AppxPackage UnityTechnologies.UnityHub).InstallLocation + "\app\Unity Hub.exe"
   & $hub -- --headless install --version 6000.5.9f1 --changeset b57deb96f08d `
             -m linux-server linux-il2cpp --childModules
   ```

   The `linux-server` and `linux-il2cpp` modules are what `UnseenBuild.BuildLinuxServer` needs.
   Opening the editor the first time requires signing in once to activate a licence.
2. Open this folder as a Unity project. The Package Manager resolves URP, Burst, Collections,
   Mathematics and AI Navigation from `Packages/manifest.json`.
3. Run **`Unseen ▸ Setup ▸ Run All Setup Steps`** from the editor menu bar. It validates the custom
   layers, creates `Assets/Unseen/Resources/UnseenConfig.asset`, and creates and registers
   `Assets/Unseen/Scenes/Unseen_Game.unity`.
4. Press **Play**. The greybox town builds itself, 63 bots fill the lobby, and the match starts.

There is nothing to author first: with no prefabs assigned the spawner assembles capsule ninjas in
code and the level generator builds the town procedurally.

### Controls

| Input | Action |
| --- | --- |
| `WASD` / mouse | Move, look |
| `Shift` | Sprint (louder, and worse for your stealth index) |
| `Ctrl` | Crouch — quieter, harder to see. Hold while sprinting to slide |
| `Space` | Jump, mantle, vault, grab a rafter |
| `F` | Grapple to a roof anchor |
| `E` | Interact: loot a chest, cut a shoji panel, put out a lantern |
| `LMB` | Attack (hold `Alt` for a heavy that breaks guards) |
| `RMB` | Guard. The zone follows your aim: look up to cover High, down for Low |
| `1` `2` `3` | Use utility slot (smoke bomb, noisemaker, night-vision elixir) |
| `F3` | Debug overlay |
| `Esc` | Release the mouse cursor |

Attacking an unaware enemy from behind or above triggers the silent takedown instead of a swing.

---

## Layout

```
Assets/Unseen/
  Scripts/
    Core/          Types, config, the fixed-step loop and system ordering, bootstrap
    Entities/      Agent records, the dense slot registry, the struct-of-arrays mirror
    Perception/    Voxel interest grid, line of sight, stealth index, interest manager
    Audio/         Sound bus, raycast propagation, per-surface acoustics
    Movement/      Parkour motor, geometry probes, grappling hook
    Combat/        Vitals, melee state, combat pockets, the clash and the takedown
    Items/         Item definitions, inventory, loot tables and containers
    Environment/   Shoji, lanterns, smoke, destructible index, greybox generator
    BattleRoyale/  Match flow, mist zone, glider deployment, agent spawning
    AI/            HTN planner, ninja domain, blackboard, navigation, bot director
    Net/           Transport abstraction, byte protocol, replication, loopback service
    Client/        Input, camera, snapshot view, HUD, silhouette and mist visuals
  Editor/          Project setup, greybox menu, batch-mode build entry points
  Shaders/         Shoji silhouette, mist wall
  Tests/           Edit-mode tests: HTN planner, wire protocol, interest grid, loot
  Integrations/    Fish-Net transport adapter (compiles only with UNSEEN_FISHNET defined)
Server/            Dockerfile, compose harness, Agones fleet, build scripts
docs/              Architecture, networking, AI, roadmap
```

## Docs

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the simulation loop, system order, and why the
  perception pipeline is shaped the way it is.
- [docs/NETWORKING.md](docs/NETWORKING.md) — topology, interest management, the wire format, and how
  to plug in Fish-Net or Photon Fusion.
- [docs/AI.md](docs/AI.md) — the HTN domain, the behaviour states, and the tick LOD.
- [docs/ROADMAP.md](docs/ROADMAP.md) — what is done against each phase of `plan.md`, and what is next.

## Running a headless server

```bash
export UNITY="$HOME/Unity/Hub/Editor/6000.5.9f1/Editor/Unity"
./Server/build-server.sh --docker
docker run --rm -p 7770:7770/udp -e UNSEEN_ENTITIES=64 unseen/server:dev
```

On Windows: `./Server/build-server.ps1 -Unity "$env:USERPROFILE\Unity\Hub\Editor\6000.5.9f1\Editor\Unity.exe" -Docker`

The server binary takes `-server -entities 64 -seed 1234`. `Server/k8s/agones-fleet.yaml` runs it as
an Agones fleet with a buffer autoscaler — one pod per match.

## Compile and test

`Tools/verify.ps1` compiles the project and runs the EditMode tests in batch mode, then prints any
`error CS####` lines and the test tally. It is the quickest health check and does not need the editor
open:

```powershell
.\Tools\verify.ps1                 # compile + tests
.\Tools\verify.ps1 -SkipTests      # compile only
```

Logs and results land in `Server/out/` (`compile.log`, `tests.log`, `tests.xml`).

In the editor, the same tests are under **Window > General > Test Runner > EditMode > Run All**.

The unit tests only cover pure logic. To exercise the actual simulation - every system, the greybox
generator, spawning, perception, bots, motion, combat, replication - run the smoke test, which boots a
real 64-entity match and pumps the authoritative loop for 120 simulated seconds:

```powershell
& $UNITY -batchmode -nographics -quit -projectPath . `
         -executeMethod Unseen.EditorTools.UnseenSmokeTest.RunHeadlessMatch -logFile smoke.log
```

It exits non-zero on any error or exception, and prints entity, perception, acoustic, combat and
replication tallies. In the editor it is **Unseen > Run Headless Smoke Test**.

## Gotchas worth knowing

**Unity Hub from winget is an MSIX package.** Everything it writes is redirected into
`%LOCALAPPDATA%\Packages\UnityTechnologies.UnityHub_*\LocalCache\`, including your licence. The editor
is a normal Win32 app and reads `%LOCALAPPDATA%\Unity\licenses\`, so it cannot see it, and every
editor launch fails with *"No valid Unity Editor license found"* despite the Hub being signed in. Fix:

```powershell
$src = "$env:LOCALAPPDATA\Packages\UnityTechnologies.UnityHub_2vrhnee42bhxm\LocalCache\Local\Unity\licenses\UnityEntitlementLicense.xml"
Copy-Item $src "$env:LOCALAPPDATA\Unity\licenses\" -Force
```

Re-activating the licence refreshes the Hub's sandboxed copy only, so this may need repeating.

**`System` type names collide with this project's namespaces.** `Unseen.Environment` shadows
`System.Environment`, and `using System.Diagnostics` makes `Debug` ambiguous with `UnityEngine.Debug`.
Inside the `Unseen.*` tree, qualify `System` types whose short names are common words - C# searches
enclosing namespaces before `using` directives, so the shadowing wins silently.
