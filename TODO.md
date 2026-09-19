# Unseen — road to a shippable multiplayer game

What stands between the current build and something a stranger can buy on Steam and play
against sixty-three other people without noticing it was ever a prototype.

Written to be honest rather than encouraging. Items are marked by what has actually been
observed, not by what is believed to work:

- **Done** — built, tested, and seen working in a player build or across two processes.
- **Partial** — exists and is tested, but is not wired into the running game.
- **Not started** — no code.
- **Unknown** — code exists, nobody has measured whether it works at scale.

---

## Honest state, 2026-09-17

The engineering underneath is unusually strong for a project with no art. The netcode is
server-authoritative with real interest management, the stealth model is computed server-side,
and the wire format is hand-rolled and auditable. That is the hard half and it is largely done.

What is missing is nearly everything between "the simulation is correct" and "a product".

| | |
| --- | --- |
| Simulation, AI, stealth, combat | Largely built, some of it unverified at scale |
| Netcode transport | Works across two processes; prediction and sequencing not on the wire |
| Art | Greybox. The town is procedural placeholder geometry |
| Performance at 64 players | **Never measured.** Biggest unknown in the project |
| Steam | Nothing. No SDK, no app ID, no store presence |
| Infrastructure | Manifests written, never deployed |
| CI, telemetry, crash reporting | None |
| Legal, ratings, compliance | Not begun |

**The single largest technical risk** is that the generated town reports **158,394 renderers and
25,385 colliders**, with one static-batching call and no LOD groups anywhere in the project. That
is a single-player-sized scene being asked to host sixty-four networked players. Until that is
measured on target hardware, every schedule below is a guess.

---

# Roadmap

Six phases. The ordering is driven by risk: measure the things that could invalidate the design
before building content on top of them.

| Phase | Theme | Rough duration | Gate to exit |
| --- | --- | --- | --- |
| 0 | Measure the unknowns | 2–4 weeks | Frame budget and bandwidth known at 64 players |
| 1 | Finish the netcode | 6–10 weeks | Sixty-four real clients in one match, stable for a full round |
| 2 | Make it a game | 3–6 months | A stranger can install, find a match, play, and leave |
| 3 | Make it look like a product | 4–8 months | Art, audio and UI at a standard someone would pay for |
| 4 | Ship it | 6–10 weeks | Steam page live, build certified, launch rehearsed |
| 5 | Operate it | Ongoing | Live service that survives its own players |

Phases 2 and 3 overlap heavily. Phase 0 must not overlap with anything.

---

## Phase 0 — Measure the unknowns

Nothing here builds a feature. All of it exists to find out whether the current design survives
contact with sixty-four players, because the answers change what gets built afterwards.

- [x] **Load test at 64 entities — first numbers.** *(2026-09-18, 4 min, 45 samples, dedicated
      server, this machine.)* Sim time: mean 6.35 ms, p50 4.95, p95 13.01, **p99 15.38**, max 15.38.
      Against the 50 ms budget of a 20 Hz base tick that is 31%; against a 60 Hz combat tick's
      16.7 ms it is **92%**, which is tight.
      **Read as a floor, not a capacity figure.** 64 bots exercise the planner a human does not,
      but do *not* exercise 64 snapshot encodes through interest management, which is the cost that
      grows with real clients. And a development machine is not a server.
- [x] **Real clients attached.** *(Done 2026-09-18, `Tools/multiplayer-test.ps1`.)* A dedicated
      server and N client processes, separate binaries over a real socket. Two clients, both seated,
      both receiving snapshots for a full round.
      **First real bandwidth figure: 12–16 kbps down per player** during Infiltration with few
      contacts, against `NETWORKING.md`'s 50–65 estimate. Still not the worst case — that is sixty-
      four players visible to each other, which is the number the interest manager exists to prevent
      and the one still unmeasured.
      Server sim time 15–85 ms, but **read it as noise**: three Unity processes on one machine, each
      generating its own 160k-renderer town. The dedicated-server load test's p99 of 15 ms is the
      better figure, and neither is from server hardware.
      It found three real bugs, none of which any single-process test could have. See Phase 1.
- [x] **Inventory the generated town.** *(2026-09-18, `Unseen ▸ Probe Cost`.)* 154,935 renderers,
      5.6M triangles, **32 distinct materials**, 18,745 shadow casters, 25,385 colliders.
      The material count is fine - 32 SRP batches is not a problem. The object count is the problem:
      **129,690 renderers (84%) are renderer-only trim on the Decoration layer**, and
      `DarkTimber_Weathered` alone is 80,798 renderers averaging 13 triangles each.
      **`GroundMist` is the single largest cost: 630 quads covering 174,000 m² of transparent
      overdraw** on a 562,000 m² map - roughly 31% of the world in alpha-blended surface, paid every
      frame whether anything is behind it or not, and every one of those quads got more expensive
      when the depth fade, triplanar sampling and per-lantern lighting went in.
      Shape of the problem: **not triangles, not materials - object count and overdraw.**
- [ ] **GPU frame time on target hardware.** The inventory above says what is being drawn; it says
      nothing about how long a frame takes. Needs a mid-range card and a target spec, neither of
      which a headless batch run can supply.
- [ ] **Re-measure bandwidth properly.** The first real figure is 12–19 kbps down per player with
      few contacts; `NETWORKING.md` estimates 50–65. Measure with sixty-four players actually
      visible to each other, which is the worst case the interest manager exists to prevent.
- [x] **Mist overdraw: measured, and the panels now cull by distance.** *(2026-09-18.)* 630 quads
      covering 174,000 m² of a 562,000 m² map - a third of the world in alpha, paid every frame.
      They were on the Default layer and therefore **never distance-culled**: they drew at four
      hundred metres exactly as at four. Now on their own `GroundMist` layer with a 140 m cull, next
      to the existing Decoration cull at 85 m. Further than trim because a bank of fog is still a
      bank of fog where a baluster is sub-pixel, and nothing is lost: the near field is the whole
      reason the panels exist, and distance haze is already the exponential fog's job.
      **The saving does not show in a screenshot** - the capture tool builds its own camera and
      applies no cull distances, so renders draw everything at every range. It needs GPU frame time
      in the running game, which is the Phase 0 item still open.
- [ ] **Consider cutting flat-panel count, not just draw distance.** Culling reduces what is drawn;
      the 174,000 m² of coverage is unchanged. The flat panels carry nearly all of it. **Needs a
      decision** - the count has been tuned up and down twice already with reasons recorded both
      times, so it is a judgement about atmosphere rather than a number to optimise.
- [ ] **Establish a target spec and a frame budget** and write both down. Every later decision
      about art density and player count refers back to them.
- [ ] **Soak test.** Run a server for 24 hours with bots. Memory, handle count, connection slots.
      Several bugs this project has already had were slow accumulations, not crashes.

**Exit gate:** you can state, with numbers, what the game costs to run at full player count.

---

## Phase 1 — Finish the netcode

The primitives are built and tested. Most of this is wiring them to the wire.

### Transport completion

- [x] **Sequence numbers and acks on every packet.** *(Done 2026-09-17.)* Payload header is 13
      bytes: protocol id, message type, sequence, ack, ack bitfield. The server keeps a window per
      connection so one player's loss cannot disturb another's ordering; the client keeps one,
      since it only hears from one server. Stale and duplicate payloads are dropped before they
      reach the game.
- [x] **Server reports last-processed input in the snapshot.** *(Done 2026-09-18.)* In the header,
      taken from the agent's own `Intent` - which is what `ServerInputSystem` assigned when it
      accepted the input, so it is the input the server *acted on* rather than merely received, and
      no second copy of the number exists to fall out of step. Protocol version 2 → 3;
      `docs/NETWORKING.md` updated in the same commit.
      Header extracted into one write/read pair, because the body is decoded by offset: a field
      added to the encoder and forgotten in the decoder does not throw, it shifts everything after
      it and draws a plausible, wrong world. That pair is what the new tests exercise.
      **`EncodeSnapshot` itself still has no test** - it needs a registered agent with its
      components awake, which an EditMode run cannot supply. The call site was verified by reading.
- [x] **Wire `InputReconciler` into the client rig.** *(Done 2026-09-18.)* `LocalAgentDriver` owns
      the local ninja on a pure client: moves it on the player's own input, and on each snapshot
      rewinds to the server's position and replays whatever the server has not acknowledged.
      Prediction is movement-only by decision — health, stealth, stance and the parry window come
      off the wire, and predicting stealth in particular would be inventing the number the whole
      game is played against.
      The reconciler is now generic in the input and holds 32-bit sequences. It remembered a bare
      direction, which cannot replay movement: sprint and stance change the speed, a jump leaves
      the ground, and the move is relative to the yaw it was made at. It also held sequences in
      sixteen bits while the wire and `MoveIntent` use thirty-two, so every call site had to narrow
      identically at both ends — a truncation bug that would first appear after eighteen minutes of
      play.
      **Motor state is not rewound.** The replay restores position, not the motor's vertical
      velocity, grounded flag or stamina, so a correction that lands mid-jump or mid-fall replays
      against the wrong internal state. Correcting it needs a ring of motor snapshots keyed by
      sequence. Not attempted; it matters most exactly where it is hardest to notice.
- [x] **Reliable channel for must-arrive messages.** *(Done 2026-09-18.)* `ReliableChannel` holds a
      message until the far end confirms it, resending every 0.25s rather than every tick - a
      per-tick resend turns one unacknowledged message into sixty a second, hardest exactly when
      congestion is what delayed the acknowledgement. Acknowledgement is per message, not "through
      N": acks arrive out of order, and retiring everything below a sequence silently discards what
      is still in flight. Bounded at 64, because what clears the queue is an ack that may never come.
      **Carried by the transport** *(2026-09-18)* - `ReliablePayload` and `ReliableAck` packets, a
      channel per connection, and a separate id window per peer so a resend is acknowledged again
      but not acted on twice. The `reliable` flag on `INetworkService` had been ignored by every
      implementation of it since it was written.
- [x] **Client stops running its own simulation.** *(Done 2026-09-18.)* `SimProfile` names what a
      process is responsible for — owning the match, resolving perception, moving agents,
      replicating — and the bootstrap registers systems against that rather than registering all of
      them in every mode. A client now runs movement and nothing else: no `MatchDirector`, no
      sixty-four bots, no interest management, no mist controller.
      It was not only waste. A client that simulates its own world has no reason to believe the one
      it is sent, and prediction had nothing to predict, because the local ninja was already moving
      under local authority. The ninja on screen was not the one the server believed in; it merely
      started in the same place.
      Perception in particular is never a client's answer: working out locally what you are allowed
      to see is the exact shape of a wallhack, and the whole anti-cheat posture is that the snapshot
      contains only what the server proved you perceived.
      **The pure-client path is still unverified end-to-end** — it needs two processes, which is the
      "sixty-four real clients" item below. Host and offline modes are verified by the existing
      headless probes (results table, glider drop), both passing.
      Known loose end: the local agent's id is assigned by the client's own registry and does not
      match the server's `SelfId`. Nothing depends on it today — self travels in the self block, not
      the contact list — but it is a disagreement waiting to be leaned on.
- [x] **A disconnect ends that player's round.** *(Done 2026-09-18, Mark's call.)* Reconnecting
      into play was the original plan and was dropped for a simpler rule: leaving kills the body,
      and rejoining puts you in the spectator camera until the round ends.
      Handing the body to a bot was wrong three times over. It rewarded disconnecting — a player
      losing a fight could pull the cable and have it taken over by an AI that did not know it was
      losing. It made the results table lie: "bot-014 finished fourth" about somebody who was never
      in the match under that name. And the body kept playing, so a ninja whose player left ten
      minutes ago could stalk and kill you, in a game whose loop is working out who you are looking
      at. Rejoining mid-round gets no body either, or leaving and coming back is the same escape by
      a longer route.
      Killed through the ordinary damage path with a `Disconnected` cause, so placement, the kill
      feed, the standings row and the death other players watch all work already.
      **One minute of grace** *(Mark's call)*: the body is abandoned rather than killed outright. It
      stands where it was left, idle, alive, visible and entirely killable. Come back inside the
      minute and it is yours again; leave it and it dies; let somebody find it first and it dies the
      ordinary way with their name on it. That last part is what stops the window being a free
      minute of invulnerability for anyone willing to pull a cable at the right moment.
      Verified by `Unseen ▸ Test Disconnect` — three scenarios in three separate matches, sixteen
      assertions, headless, because a body can only die once and each of these is a different death.
      `PlayerSeatSystem` owns all of this now. It was in `BotDirector`, which made sense while a
      disconnect produced a bot and stopped making sense the moment it did not — a class for
      steering AI had become the one deciding whether a human was in the match. Uses the previously
      unused `SimOrder.Backfill`.
- [x] **A reclaimed body is identified by a session token, not by a name.** *(Done 2026-09-18.)*
      The hole: a body is held for a minute against the display name its player was using, and the
      transport handed that name straight back to the pool the instant they dropped — so anybody
      could join during the window, ask to be called Mark, and be given Mark's ninja.
      Reserving the name does **not** fix it, which is worth recording: if the roster still holds
      "Mark" then the real Mark is suffixed on return too, and reclaiming by name stops working
      altogether. The problem is not a missing reservation, it is a missing identity.
      So the server issues a random token with the acceptance, the client presents it on the way
      back, and a name is kept for 90 s against that token rather than released. A stranger typing
      the right name during the window gets a suffix, because the name is not available to claim.
      The reservation deliberately outlasts the game's 60 s body grace: the cost of being generous
      is one name unavailable for half a minute, and the cost of being mean is a stranger walking
      into somebody else's ninja.
      Not a substitute for Steam authentication, which is still Phase 2. A token is unguessable but
      it is not proof of who you are — it proves you are whoever held this session, which is exactly
      and only what reclaiming a body needs.
- [x] **Packet size cap.** *(Done 2026-09-18.)* `UdpSocket.Send` refuses anything over
      `MaxDatagramBytes` (1200) and returns whether it went, rather than letting IP fragment it.
      Oversize does not fail, it fragments - and one lost fragment takes the whole datagram, so big
      snapshots stop arriving while small ones do. That reads as a player becoming invisible, not
      as a network fault, and it worsens exactly when a match is busiest.
- [ ] **Decide what happens when a snapshot genuinely exceeds 1200 bytes.** Now refused rather than
      fragmented, which fails loudly instead of intermittently - but it still does not arrive. At 64
      players in a final circle a player could see thirty contacts at 250-400 bytes for six to ten.
      The options are a cap on contacts per snapshot with the nearest prioritised, splitting across
      datagrams, or tighter encoding. **Needs a decision** - it is a question about what a player is
      entitled to see, which belongs in the interest model.
- [ ] **Encryption.** Unencrypted UDP means a network observer can read positions of everyone a
      player can see. For a stealth game that is a competitive leak, not just a privacy one.

### Verified at scale

- [ ] **Sixty-four real clients, one match, full round.** The end-to-end proof. Two processes is
      not evidence for sixty-four. `Tools/multiplayer-test.ps1 -Clients 64` is the command; whether
      one machine can host sixty-five processes that each generate a 160k-renderer town is the
      question, and it probably cannot.
- [x] **A multi-process harness exists.** *(Done 2026-09-18.)* `Tools/multiplayer-test.ps1` builds
      the player, runs it once as a server and N times as clients, and reports peak connections,
      per-player bandwidth, snapshots received and the prediction backlog. Three bugs fell out of the
      first three runs, every one of them invisible to a single-process test:
      **A dedicated server was unreachable by design.** It filled its roster with bots and started a
      match seconds after boot, and a round in progress does not admit players — so the first real
      client was told to spectate, and so was every client after it, for ever. The lobby now holds
      while nobody is connected.
      **The lobby's early-start check counted bots.** `full` asked whether the roster had reached 64
      entities, which backfill guarantees within seconds, so the countdown never ran and the first
      human to connect started the match on the spot. Raising the lobby to ninety seconds changed
      nothing at all, which is what exposed it. It counts connections now.
      **The client status line printed a round trip it cannot know.** The server pings and the client
      echoes — deliberately, so a client cannot choose the latency it is compensated for — so the
      client's own figure was always zero. Removed rather than made up.
      Also `-lobby <seconds>`, because a client spends most of a minute generating its town before it
      can connect, and a ten-second countdown is shorter than the game's own load time.
- [x] **`DestructibleRegistry` id agreement.** *(Done 2026-09-18.)* Verified by
      `Unseen ▸ Test Destructible Ids`, and it found a real defect.
      Ids came from sorting on position alone, and **around a hundred shoji panels per town share a
      centre to the nearest centimetre** — a doorway has a panel on each face of the wall, both
      centred on the same point. The comparison returned "equal" for every one of those pairs and
      the sort behind it is not stable, so their order was whatever the sort happened to leave.
      It agreed between two builds anyway, which is what made it dangerous: it agrees only while
      both sides feed the sort an identically ordered list, and a dedicated server does not build
      the same objects as a client (`EnableVisualUpgrade` differs by mode). The first time the two
      lists differed, a hundred panels would have swapped identities — the server saying "panel 47
      is broken" while a different wall opened on somebody's screen. A free look into a room, in a
      game about not being seen, and it would have been reported as walls falling apart on their own.
      Sorting now falls back to facing, which separates the two panels of a doorway. That took three
      seeds from 106/116/94 collisions to none. One residual pair was two loot chests generated in
      the same spot — a content bug rather than an id-scheme one, since no ordering can separate two
      identical poses; chests are now placed with a minimum separation and a deterministic fallback
      when the random draws will not cooperate. `BuildIndex` warns if any pair is still inseparable,
      because the symptom is very far from the cause.
      Two lesser things fell out of it. `BuildIndex` sorted destroyed objects and threw, because a
      panel leaves the static registry in `OnDisable` and that does not run in edit mode — the same
      asymmetry the generator already works around for registration, with no matching path back. It
      now filters. And the first version of the probe passed perfectly while proving nothing: the
      generated town is a root object beside the bootstrap rather than under it, so tearing down the
      bootstrap left the town standing and the second "independent" build reused the first one's
      objects. Two different seeds producing identical towns is what gave it away.
      **Still not proven across two processes.** This is two builds in one editor, which is strong
      evidence about determinism and no evidence at all about a real client and a real server.
- [x] **Packet loss and jitter testing.** *(Done 2026-09-18.)* `IDatagramSocket` is the seam and
      `SimulatedSocket` sits in it, dropping, delaying, duplicating and reordering on a seeded RNG.
      Asked for with `NetworkConditions` on `Host`/`Join`, or `-netsim domestic|mobile` on the
      command line — developing on loopback means every judgement about how the game feels is made
      on a connection no player will ever have, and prediction, the resend timer and the parry
      window all hide a round trip that is zero on this machine.
      Nullable rather than a "is it perfect" check: no simulation and a simulated link that is
      currently flawless are different things, and only one of them can be degraded mid-connection,
      which is the interesting moment.
      **The simulator is tested before anything is tested against it** — eight assertions including
      one that jitter genuinely makes packets overtake each other. A simulator that quietly did
      nothing would turn every "survives loss" test into a vacuous one.
      What it found: nothing. The reliable channel delivers exactly once through 50% loss in both
      directions, the sequence window never hands up a stale payload under reordering, and a
      duplicated datagram is handed up once. All three were argued for in comments and unverified
      until now.
- [x] **Oversize datagrams are counted rather than swallowed.** *(Done 2026-09-18.)* `UdpSocket.Send`
      has refused anything over 1200 bytes since it was written and returned that refusal to a
      caller that discarded it — all twelve call sites in the transport ignored the result. The
      guard that exists to stop a snapshot being fragmented was instead making it vanish without
      trace, which is the same failure it was added to prevent wearing a different hat. Every send
      now goes through one checked path, `RefusedSends` counts them, and the first is logged loudly.
      Note the deliberate asymmetry: a datagram lost by the *simulated link* is still reported as
      sent, because that is what a real socket does — it hands the bytes over and says yes, and
      nothing comes back to say they died three hops later. Telling the sender would give the
      protocol information no network offers, and the resend timer would never fire in a test. The
      information is not lost, it lives on the link as `Dropped`/`Offered` where it belongs.
- [ ] **Reconciliation has not been tested under loss.** The simulator exists now, but exercising
      prediction needs a client and a server in one test with a real simulation between them, which
      the EditMode suite cannot build. It is the same gap as `EncodeSnapshot` having no test.
- [ ] **No sustained-loss soak.** The tests fire a handful of messages. Nothing has run for minutes
      at 8% loss watching for a backlog that never drains or a window that drifts.

### Anti-cheat

- [ ] **Decide the posture and write it down.** The interest manager already denies wallhacks at
      the packet layer, which is a genuine competitive advantage worth protecting and marketing.
- [x] **Server-side input rate limiting.** *(Done 2026-09-18.)* `ShouldAcceptTraffic` gives every
      established connection a token bucket - burst 90, refill 180/s, against a client that sends
      60/s - checked before the packet is decoded. Getting in was rate limited; staying in was not,
      so the cheapest attack was to be a real player sending a thousand inputs a second. Paired with
      a test that ordinary play is *never* throttled, because a limiter that clips honest traffic is
      worse than the flood it prevents.
- [ ] **Movement delta validation.** Input rate is handled; a client claiming impossible *movement*
      is not. `Sanitise` clamps the axes and pitch, but nothing checks that the distance covered
      between two inputs is achievable. **Needs a decision** - it interacts with prediction, and
      too tight a bound rejects legitimate movement after a lag spike.
- [ ] **Decide on third-party anti-cheat** (EAC is free for Steam titles). It is intrusive and
      unpopular; the alternative is accepting some cheating. Either is defensible; drifting into
      one by not deciding is not.

---

## Phase 2 — Make it a game

Everything a stranger needs in order to get from "bought it" to "playing", without you present.

### Steam integration

- [ ] **Steamworks account, app ID, and the $100 fee.** Everything below depends on it.
- [ ] **Steamworks SDK integrated** (Steamworks.NET or Facepunch.Steamworks).
- [ ] **Steam authentication.** Session tickets so the server knows who a player is. This also
      gives you the identity layer that makes banning meaningful.
- [ ] **Lobbies and friend invites.** Playing with a friend is the strongest retention mechanic a
      multiplayer game has.
- [ ] **Rich presence, game invites, Steam overlay.**
- [ ] **Steam Cloud** for settings and progression.
- [ ] **Achievements.** Cheap to add, materially affects reviews and wishlists.
- [ ] **Steam Input** for controller support, including remapping.

### Matchmaking and infrastructure

- [ ] **Deploy the Agones fleet for real.** `Server/k8s/agones-fleet.yaml` has never run.
- [x] **Matchmaking service.** *(Done 2026-09-20.)* `Matchmaker` is the queue in front of the
      fleet: tickets rather than a blocking call, because the wait is unbounded and the thing
      waiting is a game that has to keep drawing frames. First come first served, packing preserved,
      and a waiting player is told how many are ahead — a queue with no number on it is a spinner,
      and a spinner is where players decide the game is broken.
      `MatchmakerHost` is the front door and `MatchmakerClient` the game's side, over a real socket
      in the tests. Hand-rolled HTTP on `TcpListener` rather than `HttpListener`, which wants a URL
      reservation on Windows and fails for anyone who has not run a command as administrator.
      The protocol is line-oriented text, tested as text: a reply a client cannot read is
      indistinguishable from a service that is down. It refuses an unknown status word rather than
      defaulting, so a newer matchmaker saying something cautious cannot be read by an older client
      as permission to connect, and it refuses an address with no port rather than handing the
      transport something it cannot dial.
      Two real bugs came out of building it. `ServerAllocator` had no way to remove a dead server,
      so the packing would keep choosing a machine that had been evicted. And the host was designed
      to be pumped from outside, which cannot work: a client asking is a blocking call, so nothing
      can answer it from the thread that is waiting. The host owns a thread now, and the matchmaker
      belongs to that thread — which is what keeps the part with all the decisions in it free of
      locks.
      **Not deployed.** It runs and is tested; nothing has put it in front of a real fleet, and
      nothing registers servers with it yet — that is the Agones item below.
- [ ] **Regional servers.** A stealth game with a latency-compensated parry window cannot put
      Australian and European players in one match.
- [ ] **Server browser or quick-play, decided.** Affects the whole UI flow.
- [ ] **Autoscaling and cost model.** Know what a concurrent player costs before launch, not after.
- [ ] **Health checks, graceful drain, rolling deploys.** A server update must not kill matches
      in progress.

### Build and release pipeline

- [x] **CI.** *(Done 2026-09-18.)* `.github/workflows/verify.yml` runs the EditMode suite on every
      push and pull request; `build.yml` builds the Windows client and the Linux headless server on
      main, stamped with the commit so a build given to a playtester can be traced to its source.
      Separate workflows on purpose - tests finish in minutes and matter on every push, a player
      build does not. `fail-fast: false` on the build matrix, because "the client broke" and "the
      server broke" are different problems and the headless server is the one nobody builds by hand
      often enough to notice when it stops working.
      **Will fail until a Unity licence secret is added** - deliberately, as the first step, naming
      what it wants. Without that check the Unity action dies inside activation complaining about a
      licensing client, which reads as a broken runner rather than a missing setting.
      Needs either `UNITY_LICENSE` (personal) or `UNITY_EMAIL` + `UNITY_PASSWORD` + `UNITY_SERIAL`.
- [ ] **Automated Steam depot upload** (steamcmd) with branch support so testers get builds.
- [ ] **Versioning and build stamping**, visible in-game and in crash reports.
- [ ] **Code signing** for the Windows binary.

### Telemetry and support

- [ ] **Crash reporting.** Nothing is installed. Ship without it and you will be guessing.
- [ ] **Gameplay analytics** — match length, zone deaths, weapon use, disconnect rate.
- [ ] **Server-side logging and alerting**, with retention. The verbose gate already exists.
- [ ] **Player reporting and moderation tooling.** The name system already resists impersonation;
      it needs a reporting path and a ban mechanism behind it.

---

## Phase 3 — Make it look like a product

The largest and least predictable phase. It is also the one that decides whether anybody buys it.

### Art

- [ ] **Decide: authored map or procedural.** `MapDescriptor` is the contract either implements.
      This is the single biggest scoping decision left in the project. A hand-built map is better
      and finite; procedural is cheaper and never quite finished.
- [ ] **Performance-driven art rebuild.** LOD groups, occlusion culling, GPU instancing, texture
      atlasing, trim sheets. None exist. This is not polish — at 158k renderers it is a
      prerequisite for shipping.
- [ ] **Character art and animation.** The animation layer is listed as the outstanding item in
      `ROADMAP.md` Phase 3. Motion warping is authoritative transform motion today.
- [ ] **Materials with full PBR maps.** Currently albedo plus normal only, uniform smoothness.
- [ ] **Lighting and mood pass** on a real map, with baked GI where the geometry stops moving.
- [ ] **VFX** — smoke, blood, weather, footfalls, impacts.
- [ ] **Blender pipeline hardening.** Indexed and welded meshes, binary rather than JSON, exported
      tangents, LODs from a decimate modifier, vertex colours. Water is currently 24,576 vertices
      for 4,225 unique positions in 2.7 MB of text.

### Audio

- [ ] **Real footstep, impact and foley assets.** Procedural stand-ins today.
- [ ] **Middleware decision** — FMOD or Wwise, or stay on Unity's mixer. The propagation model is
      already built; middleware would replace the renderer only.
- [ ] **Music.** A stealth game lives on tension and silence; this is a design job, not a
      shopping job.
- [ ] **Mix pass** with headphone and speaker targets.

### UI and UX

- [ ] **Main menu, settings, keybinding, video options.** `SettingsMenu` exists; it is not a
      shippable front end.
- [ ] **Name entry in the UI.** The transport and server side are done; there is no text field.
      *(Partial)*
- [ ] **Match flow** — lobby, loading, spawn, death, spectate, results, requeue.
- [ ] **Onboarding.** Stealth games are unusually easy to be bad at without understanding why.
      The stealth index is computed server-side and is currently invisible to a new player.
- [ ] **Accessibility.** Colourblind-safe palettes, subtitles, remappable everything, motion and
      shake toggles, text scaling. This is both right and, increasingly, expected in reviews.
- [ ] **Localisation.** Decide the launch languages early; it affects every string written after.

---

## Phase 4 — Ship it

- [ ] **Steam store page** — capsule art, screenshots, trailer, description, tags. The trailer
      matters more than anything else on the page.
- [ ] **Wishlist campaign before launch.** Wishlists drive the launch-day visibility that drives
      everything else.
- [ ] **Age ratings** — IARC via Steam covers most territories.
- [ ] **EULA, privacy policy, GDPR compliance.** You collect player names and IP addresses; both
      are personal data. A data deletion path is required, not optional.
- [ ] **Business entity, tax and banking set up with Valve.**
- [ ] **Playtesting with strangers.** Friends are unreliable witnesses. Closed beta via Steam keys.
- [ ] **Launch rehearsal.** Deploy, scale, roll back. Practise the rollback specifically.
- [ ] **Day-one patch process** ready before day one.
- [ ] **Support channel** and someone watching it during launch week.

---

## Phase 5 — Operate it

- [ ] **Live ops** — monitoring, on-call, incident process.
- [ ] **Content cadence.** A multiplayer game with no updates is a dying one.
- [ ] **Community management and moderation.**
- [ ] **Balance from telemetry**, not from opinion.
- [ ] **Anti-cheat response loop.** Cheats arrive after launch, not before.

---

## Carried-over debt

Specific items already known, not covered above.

- [ ] **Smoke bomb visuals unverified.** Billboarding, seventeen puffs and dithered shadows have
      never been seen by a person. The dithered shadow may read as noise.
- [x] **Untested lines from the netcode work.** *(Done 2026-09-19.)* Four tests, each proved red by
      deleting the line it claims to pin and watching it fail — a green test written against code
      that already works proves only that it compiled.
      That step earned its keep twice. One test passed with the line removed: releasing an unknown
      server id. It turned out the guard's real job is not the negative count at all — `Register`
      returns early for any id it already has an entry for, so a release arriving before a
      registration would drop that server on the floor permanently. It would boot, report healthy,
      and never be allocated a player. Agones reuses fleet names, so it is a live path. The test
      now pins that.
      The other was the reverse: the `SIO_UDP_CONNRESET` test passes with the call removed, because
      `Poll` catches `SocketException` and the next poll carries on regardless. The catch is doing
      the protective work and the IOControl is a second layer. The test keeps the guarantee that
      matters — a socket survives provoking an ICMP unreachable — and the comment no longer claims
      to cover the call.
- [ ] **The `SIO_UDP_CONNRESET` call is not pinned by any test.** Its only observable effect is one
      poll cycle of delay, and asserting on that is the kind of timing test that fails on a loaded
      machine for no reason. Deliberately uncovered rather than covered badly.
- [x] **`ApplyCommandLine` is testable.** *(Done 2026-09-18.)* Reading moved to
      `LaunchOptions.Parse(string[])`, six tests. The extraction itself introduced a regression -
      a struct field is always present, so "always present" became "always applied" and every
      screenshot run would have been silently demoted from ListenServer to offline practice. Caught
      by reading, not by a test, because the code being refactored had none. `HasMode` now pins it.
- [ ] **IPv4 only, no DNS.** `NetEndpoint` packs an address into a `uint` and the socket binds
      `InterNetwork`. Changing it later means touching the endpoint, the cookie derivation and the
      socket together.
- [x] **Homoglyph coverage.** *(Done 2026-09-18.)* Three layers, because no one of them is enough.
      NFKD plus mark-stripping folds every accent, width and styling variant structurally - several
      hundred confusables handled by one call, and it keeps working on alphabets Unicode has not
      added yet. Mathematical alphanumerics are folded arithmetically, because Mono does not
      decompose above the basic plane and that block is what every "fancy text" site emits. A
      hand-written table then covers what normalisation formally cannot reach: Cyrillic, Greek,
      Cherokee and the small capitals. Finally `MixesScripts` refuses any name written in two
      alphabets at once, which is the signature every homoglyph attack shares regardless of which
      character it used - so the table no longer has to be complete to hold. Japanese, Chinese and
      Korean script combinations are exempt; the game is set in a Japanese town and a rule that
      flagged 忍者ニンジャ would punish its own audience.
      Remaining gap, deliberate: letters from scripts outside the named ranges all classify as one,
      so two such scripts together read as a single script and pass. Wrongly refusing somebody's
      real name is the worse error of the two.
- [ ] **URP upgrade unverified in play.** Compiles and passes tests on 17.6; the render paths have
      not been examined by eye at length.
- [ ] **Screenshot tool does not apply post-processing.** Renders are reliable for geometry,
      materials and lighting — not for tone, bloom or grade. Cause unidentified; the render call
      was ruled out.

---

## The three decisions that shape everything else

1. **Authored map or procedural?** Changes the art budget, the performance work, and how long
   Phase 3 takes.
2. **Third-party anti-cheat, or accept some cheating?** Changes the player experience and the
   build pipeline.
3. **What player count is actually viable?** Sixty-four is the design target. If Phase 0 says the
   frame budget supports thirty-two, that is a better game than sixty-four that stutters.

Answer these before Phase 3 begins. Everything after them is expensive to undo.
