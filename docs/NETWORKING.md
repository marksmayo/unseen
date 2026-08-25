# Networking

## Topology

Server-authoritative, with no client authority over anything that matters. Clients send intent;
the server simulates and replies with a per-observer snapshot.

```
client                                  server
  |  MoveIntent (unreliable, 60/s)       |
  |------------------------------------->|  ServerInputSystem: clamp, drop out-of-order
  |                                      |  ... simulate ...
  |                                      |  InterestManager: what may this observer know?
  |  Snapshot (unreliable, 20 or 60/s)   |
  |<-------------------------------------|  only that observer's earned contacts
```

Four launch modes, chosen by `UnseenBootstrap.Mode` or the command line:

| Mode | Server | Client | Notes |
| --- | --- | --- | --- |
| `OfflinePractice` | yes | yes | Loopback transport. 1 human + 63 bots |
| `ListenServer` | yes | yes | Authoritative and playing |
| `DedicatedServer` | yes | no | `-server`, headless, no local player |
| `Client` | no | yes | Needs a real transport adapter |

## Interest management

Replication is driven entirely by `AgentEntity.Visible`, produced by the perception pipeline
(see [ARCHITECTURE.md](ARCHITECTURE.md)). Consequences worth being explicit about:

- There is no "all entities" array anywhere in the client. A memory scraper finds the contacts the
  player can already see on screen, and nothing else. This is the anti-wallhack claim in `plan.md`,
  and it holds because of the packet layout, not because of an obfuscation trick.
- A contact that is lost keeps its **last resolved position** for a 0.35 s linger window so a target
  ducking behind a pillar does not pop out of existence. The frozen value is never refreshed, so the
  linger cannot be farmed for live positions — it costs an attacker at most one third of a second of
  stale information, which is the price of not having proxies flicker.
- Silhouette contacts are stripped of identity at encode time, not at draw time.

## Wire format

Byte-oriented and hand-rolled (`NetStream.cs`, `SnapshotProtocol.cs`) so the layout is auditable and
the "no hidden entities" property is provable by reading one file. Quantisation:

| Quantity | Encoding | Cost |
| --- | --- | --- |
| Position | 3 × int32, 1 cm steps | 12 B |
| Angle (yaw, pitch) | 1 byte over 360° (~1.4° steps) | 1 B |
| Normalised (health, stealth, confidence, intensity) | 1 byte | 1 B |
| Direction | yaw byte + elevation byte | 2 B |
| Flags | uint16 | 2 B |

Snapshot layout:

```
byte    message id (1 = snapshot)
byte    protocol version
int32   tick
float   server time
--- self (always complete: you always know your own state) ---
int32   entity id
pos     position
angle   yaw, angle pitch
norm    health fraction, norm stealth index
uint16  flags
byte    stance, byte locomotion state
--- contacts (only what this observer earned) ---
byte    count
  per:  int32 id, byte visibility kind, pos position, norm confidence,
        angle yaw*, uint16 flags*, byte stance*      (* zeroed for silhouettes)
--- heard sounds (one-shot, cleared by the send) ---
byte    count
  per:  byte kind, norm intensity, norm occlusion, direction, pos apparent position
--- combat events (per-connection cursor, so no duplicates and no gaps) ---
byte    count
  per:  byte kind, int32 attacker, int32 victim, pos position, byte guard zone
--- world events (shoji sliced/broken, lantern out, smoke, container opened) ---
byte    count
  per:  byte kind, uint16 target id, pos position, float radius, float duration
--- zone and match ---
pos     mist centre, float mist radius, byte stage, byte match phase, uint16 alive
```

Input is 13 bytes: sequence, two quantised axes, yaw, pitch, a button bitfield, guard zone and the
utility slot.

Destructible ids are assigned by sorting scene objects on quantised position (`DestructibleRegistry`),
so server and client agree on ids without a handshake — including for the procedural greybox, which
regenerates identically from the same seed.

## Event delivery

Combat and world events are queued per tick and fanned out with a **per-connection cursor**. A hot
client receiving 60 snapshots a second never sees the same clash twice; a cold client on 20 Hz never
misses one. The queues retire at the end of each base tick, which is the point where every connection
is guaranteed to have been served.

## Latency compensation

The only place the server bends for latency is the parry window, and it bends explicitly:

```
window = clamp(ParryWindowBase + rtt * ParryLatencyCompensation, ParryWindowBase, ParryWindowMax)
       = clamp(150 ms + rtt/2, 150 ms, 200 ms)
```

A 100 ms player gets the full 200 ms; a LAN player gets 150 ms. The cap is what stops a deliberately
laggy client from becoming unparryable. Nothing else is rewound: there is no lag compensation on
melee hits, because at this range rewinding the world would let an attacker kill someone who had
already broken line of sight — which in a stealth game is a worse failure than a lost trade.

## Plugging in a real transport

`INetworkService` is the whole surface: role, connections, RTT, send, poll, four events. To add
Fish-Net:

1. Install Fish-Net.
2. Add `UNSEEN_FISHNET` to **Project Settings ▸ Player ▸ Scripting Define Symbols**.
3. Put a `NetworkManager` in the scene.

`Assets/Unseen/Integrations/FishNet` then compiles (its assembly definition carries the same define
constraint) and registers itself with `UnseenTransport.Factory` before the first scene loads. It
wraps our snapshots in a single opaque broadcast — Fish-Net moves bytes, and the game keeps its own
interest management rather than handing that job to the library's observer system. That matters: a
built-in observer system would replicate NetworkObjects on distance, which is exactly the leak this
design exists to avoid.

Photon Fusion, a raw transport, or a custom UDP layer plug in the same way: implement the interface,
assign the factory. The adapter is the only file that changes.

> The Fish-Net adapter is written against 4.x broadcasts and has not been compiled against a live
> install in this repository. If your version renames a member, that one file is the only thing to
> fix.

## Bandwidth

At 20 Hz with a typical 6–10 visible contacts, a snapshot is roughly 250–400 bytes, so ~50–65 kbit/s
down per player, spiking to ~3× that while hot at 60 Hz. `ReplicationSystem.KilobitsPerSecond` reports
the live figure and the server logs it every five seconds.
