Game Master Plan: Unseen

Executive Summary



Unseen is a 64-player, server-authoritative, stealth-action battle royale built in Unity. Set in a sprawling, multi-level Japanese fortress town, players control rival ninjas hunting each other through shadows, rooftops, subterranean sewers, and traditional paper-walled structures.



The core game loop replaces high-frequency combo spam with high-stakes information asymmetry, environmental manipulation, silent assassinations, and directional melee clashes. Full AI bot integration ensures immediate match backfilling, offline single-player practice, and scalable network testing.

1\. Technical Stack \& Hardware Budget

Engine \& Core Packages



&#x20;   Engine: Unity (Universal Render Pipeline - URP). Built for lightweight, high-performance rendering across modest storage setups (\~10–12 GB footprint).



&#x20;   Networking: Fish-Net or Photon Fusion (Server-Authoritative topology hosted on headless Linux server instances).



&#x20;   Audio Pipeline: Steam Audio or FMOD for real-time geometric raycasted sound propagation and occlusion.



&#x20;   Performance Optimization: Unity C# Job System and Burst Compiler for off-thread AI perception and spatial calculations.



Network Topology \& Server Performance



&#x20;   Target Server Tick Rate: 20 Hz base spatial roaming; dynamic 60 Hz scaling inside active 1v1 combat pockets.



&#x20;   Spatial Interest Management: 3D voxel grid culling. The server strips position packets of hidden or distant entities entirely, rendering memory-reading wallhacks functionally useless.



2\. Core Game Systems \& Mechanics

Stealth \& Perception Framework



&#x20;   Server-Authoritative Line-of-Sight (LoS): Raycast checks validate camera frustums and cover geometry. Fully obscured entities do not exist on the client machine.



&#x20;   Light \& Shadow Engine: Raycast-based "Stealth Index" (0% to 100% hidden) calculated server-side using local light source proximity and light probe values.



&#x20;   Shoji Screen Silhouette Shader: Moving behind translucent paper screens generates a low-fidelity silhouette shader on nearby client screens without revealing gear or health.



&#x20;   Auditory Propagation: Footsteps, jump landings, and environment destruction generate server sound spheres. Surrounding clients receive directional UI pings based on obstacle density.



\[Sound Event Generated] ---> (Server Raycasts Walls/Shoji) ---> \[Muffled Directional Audio Ping]



Movement \& Vertical Traversal



&#x20;   Fluid Parkour System: Smooth wall-climbing, ledge-hanging, roof-running, and rafter-crawling.



&#x20;   Grappling Hook: Fast, silent vertical traversal to roof canopies with a dedicated noise penalty if used near enemies.



Combat \& Environmental Mechanics



&#x20;   Silent Takedown: Attacking an unaware target from behind or above triggers a 1.5-second server-authoritative lockstep assassination animation using Unity Motion Warping.



&#x20;   Directional Clash Mode: Frontal combat shifts to a 3-way guard system (High, Mid, Low) featuring a generous 150ms–200ms parry buffer to mitigate network latency.



&#x20;   Destructible Shoji \& Lanterns: Slice through shoji paper for stealth entry; shoot hanging lanterns to expand local shadow zones.



3\. Battle Royale Structure \& Loop



&#x20;   Infiltration Phase: 64 players/bots deploy onto the map via high-altitude gliders or scatter-spawn across high-canopy trees.



&#x20;   The Curse of the Shadow (Shrinking Zone): A dense, lethal mystical fog rolls in from the map perimeter, forcing players into tight, high-density interior spaces.



&#x20;   Scavenging \& Equipment:



&#x20;       Weapons: Katanas, kusarigama (chain-sickles), and silent shurikens.



&#x20;       Utility: Smoke bombs, distraction noisemakers, and night-vision elixirs.



&#x20;       Gear: Soft-soled tabi boots (reduces footstep noise radius by 50%).



4\. AI Bot System Architecture

Perception without Cheating



Bots do not read memory directly; they rely on the exact same server-authoritative visual cones and auditory spheres as human players.



&#x20;      \[PATROL / CREEP]

&#x20;             |

&#x20;     (Hears Noise / Sees Silhouette)

&#x20;             v

&#x20;      \[INVESTIGATE] ---> (Loses Trail) ---> \[SEARCH AREA]

&#x20;             |                                    |

&#x20;      (Confirms Line-of-Sight)              (Timeout)

&#x20;             v                                    v

&#x20;    \[AMBUSH / COMBAT] -------------------> \[PATROL / CREEP]



Performance Optimization



&#x20;   Hierarchical Task Networks (HTN): Process perception calculations on background CPU threads via the Burst Compiler.



&#x20;   Dynamic Tick Throttling: Bots within active combat range tick at 20–30 Hz. Distant bots in the fog scale down to 2–5 Hz navigation tasks.



5\. Phased Development Roadmap



+-------------------------------------------------------------------------------+

| PHASE 1: Netcode \& Greybox (Months 1-3)                                      |

| - Headless server, 64-capsule stress test, spatial culling prototype.          |

+-------------------------------------------------------------------------------+

&#x20;       |

&#x20;       v

+-------------------------------------------------------------------------------+

| PHASE 2: Core Stealth \& Movement (Months 4-6)                                 |

| - Parkour, Light/Shadow index, Shoji shaders, sound occlusion, AI perception. |

+-------------------------------------------------------------------------------+

&#x20;       |

&#x20;       v

+-------------------------------------------------------------------------------+

| PHASE 3: Combat, Animations \& Destructibles (Months 7-9)                      |

| - Motion warping assassinations, 3-way parry system, destructible shoji.      |

+-------------------------------------------------------------------------------+

&#x20;       |

&#x20;       v

+-------------------------------------------------------------------------------+

| PHASE 4: Level Design \& BR Loop (Months 10-12)                                |

| - Castle Town map, shrinking fog, loot containers, match backfilling.         |

+-------------------------------------------------------------------------------+



Phase 1: Netcode \& Greybox Prototype (Months 1–3)



&#x20;   Establish headless Unity server instances running Fish-Net / Fusion.



&#x20;   Spawn 63 basic AI capsule bots alongside 1 human player to stress-test 64-entity server tick rates.



&#x20;   Implement 3D spatial partitioning and server-side line-of-sight culling.



Phase 2: Core Stealth \& Traversal (Months 4–6)



&#x20;   Build parkour movement: crouch-creeping, ledge hanging, grappling hooks, and rafter locomotion.



&#x20;   Integrate Light/Shadow server calculations and Shoji screen silhouette shaders.



&#x20;   Implement Steam Audio / FMOD raycasted sound propagation.



&#x20;   Hook AI bots into visual/auditory perception loops.



Phase 3: Combat, Animations \& Shoji Interactions (Months 7–9)



&#x20;   Integrate Mecanim animation state machines and Motion Warping for silent takedowns.



&#x20;   Implement 3-way directional melee parries, smoke bombs, and throwable distractions.



&#x20;   Build destructible environment assets (slicing shoji paper, extinguishing light sources).



&#x20;   Program combat AI behavior trees (parrying, retreating, throwing smoke bombs when damaged).



Phase 4: Map Design, BR Loop \& Polish (Months 10–12)



&#x20;   Construct the primary multi-level map: Japanese Castle Town (Rooftops, Corridors, Sewers).



&#x20;   Implement the Shrinking Mist zone controller, loot tables, and match flow logic.



&#x20;   Configure matchmaking logic for seamless AI bot backfilling in online queues and a standalone single-player offline mode.



&#x20;   Perform full 64-entity load-testing on cloud infrastructure (Agones on Kubernetes / AWS GameLift).

