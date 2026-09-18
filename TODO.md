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
- [ ] **Re-run the load test with real clients attached.** The figure above has `out 0 kbps`
      throughout because nothing connected. Encoding and interest management are the parts that
      scale with player count, and they are exactly what this run did not touch.
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
- [ ] **Wire `InputReconciler` into the client rig.** Prediction is movement-only by decision;
      stealth and the parry window stay server-authoritative. *(Partial)*
- [x] **Reliable channel for must-arrive messages.** *(Done 2026-09-18.)* `ReliableChannel` holds a
      message until the far end confirms it, resending every 0.25s rather than every tick - a
      per-tick resend turns one unacknowledged message into sixty a second, hardest exactly when
      congestion is what delayed the acknowledgement. Acknowledgement is per message, not "through
      N": acks arrive out of order, and retiring everything below a sequence silently discards what
      is still in flight. Bounded at 64, because what clears the queue is an ack that may never come.
      **Not yet carried by the transport** - the channel exists and is tested; `UnseenUdpService`
      does not use it.
- [ ] **Client stops running its own simulation.** `UnseenBootstrap.Update` steps `_sim` for every
      mode, so a pure client runs its own `MatchDirector` and sixty-four bots. Needs a local agent
      driven by the snapshot self block first, or the camera has nothing to follow.
- [ ] **Reconnect to a match in progress.** A dropped player currently becomes a bot and cannot
      return. Ten seconds of a bad hotel connection should not end someone's game.
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
      not evidence for sixty-four.
- [ ] **`DestructibleRegistry` id agreement under real conditions.** Server and client derive ids
      from sorted positions with no handshake. Elegant, and entirely unproven across a network.
- [ ] **Packet loss and jitter testing.** Use a network simulator. 2% loss and 150 ms jitter is an
      ordinary evening on domestic broadband.

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
- [ ] **Matchmaking service.** `ServerAllocator` has the packing logic, tested; it needs an HTTP
      front end, a queue, and a client that talks to it. *(Partial)*
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
- [ ] **Untested lines from the netcode work** — `Heard`-on-`Accept`, `ServerAllocator.Release`
      flooring at zero, the Windows `SIO_UDP_CONNRESET` call.
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
