# Undertow — backlog

Ordered. Each task lists its acceptance criteria. Read `CLAUDE.md` first — the house style
rules and locked decisions there constrain every task below.

**Definition of done for every task:** `.\tools\run-tests.ps1` green, project builds, and
anything touching game internals has been run in-game once with its log line observed.

The design is at <https://claude.ai/code/artifact/e213f36d-fdcd-4695-a159-f8e4e1157323>.

---

## 0. Skeleton — plugin, config, cursor, console — DONE 2026-08-28 (0.1.0)

Verified on a real dedicated server (world `UndertowSmoke`, alongside the full Ravenrest mod
set), three lines, no errors:

```
[Info   :   BepInEx] Loading [Undertow 0.1.0]
[Info   :  Undertow] wake console registered — `wake status` is available on this machine.
[Info   :  Undertow] SeaTick online — 0 system(s), budget 2ms/frame, authority=True, dedicated=True
```

`dedicated=True` is the fact no client run can establish — `ZNet.IsDedicated()` is a
compile-time constant, false in the client assembly.

Shipped: `tools\fetch-libs.ps1` and `tools\run-tests.ps1` ported verbatim from RW,
`Undertow.csproj` (net472, relative `libs\`), `Undertow.slnx`, `Plugin.cs`,
`Config/ModConfig.cs`, `Core/IWorldSystem.cs`, `Core/SeaTick.cs`,
`Commands/WakeConsole.cs`, and the net10 harness at **16/16**.

**Three deliberate departures from Ragnarok's Wrath, each with a reason:**

1. **`SeaTick` announces itself even with zero systems registered.** RW's `WorldTick` returns
   early on an empty list, which would have made this very task unverifiable — a skeleton with
   nothing to tick would have booted in complete silence, and a silent success is
   indistinguishable from a silent no-op from outside the game.
2. **`SeaTick` logs on a pure client too**, at Info, saying ambient systems idle there by
   design. A client is a correct place for this mod to live: it owns the boats it sails.
3. **No persistence and no autosave.** RW's `WorldTick` carries both because it owns stores.
   Undertow owns none, by locked decision.

**Two findings that cost a round-trip and are now written into `CLAUDE.md`:**

- **House rule 5 fired on `Terminal.commands`** — public in the publicized assembly, private in
  the real one. It compiled clean and threw `FieldAccessException` in-game. Worse than the rule
  implies: Mono resolves field access at JIT time, so the enclosing `try/catch` never ran, and
  the `ConsoleCommand` registration three lines above it never ran either. The instrument
  disabled the feature it was measuring. Fixed with cached `AccessTools.Field` reflection, kept
  in a separate method from the registration.
- **Other boat mods may patch `Ship.CustomFixedUpdate` too** - see task 2 and `CLAUDE.md`.

`tools\package.ps1` was deliberately not ported: its whole value is refusing to package when
the three version strings disagree, and that is worth having on a real release, not a skeleton.
It arrives with the first publishable build.

---

## 1. `CurrentField` — the math, and a way to read it — DONE 2026-08-28 (0.1.0)

Verified on the dedicated server, a land-to-ocean transect straight out from spawn:

```
CurrentField live — seed -1790482695, water level 30, tide 57%, season index 0
  x=0     h=79.34  land
  x=2000  h=16.71  depth 13.29m 0.335m/s Coastal
  x=8000  h=0      depth 30m    0.315m/s Drift
  x=9000  h=0      depth 30m    0.197m/s Drift
  x=10000 h=19.7   depth 10.3m  0.665m/s Coastal
```

Harness **57/57**, every assertion proven to fail without its fix. Nothing is pushed: the
field touches no rigidbody anywhere.

**Design change, made during the build and worth knowing before task 2 reads the code.** The
four hand-written terms in the original sketch became **one stream function plus terrain**:
open water is the perpendicular gradient of a scalar field, `u = dpsi/dz, v = -dpsi/dx`, built
from three plane waves. That makes the field **divergence-free by construction** — water is
neither created nor destroyed — and gyres, fast water and slack all fall out of one mechanism
instead of three that have to be balanced against each other. Coastal set and race detection
stay terrain-driven on top of it.

**The seasonal REVERSAL was deliberately softened to a 15-degree rotation plus a magnitude
change.** A full flip would invalidate every seamark a crew had learned, four times a year,
which destroys the one thing this mod exists for. The reversal a player actually feels is the
tide flipping the coastal stream, which is implemented and tested.

**Three things the work turned up, all now in `CLAUDE.md`:**

1. **Valheim's open ocean is a flat floor at generator height exactly 0** — a uniform 30m
   depth. Caught by refusing to accept two ocean points reporting depth "exactly 30.0m" and
   re-logging the raw height instead of reasoning about what it must mean. `ShelfDepth` was 40,
   above the sea's own floor, which classified the entire ocean as shelf; it is 28 now and a
   test pins it under 30.
2. **A single-scale race probe is blind to a strait.** 64m either side of the flow can only see
   gaps under ~128m. Two scales now (64m and 160m), so both a tight race between rocks and a
   300m strait between islands register.
3. **A passing test can still be a worthless test.** The "speed never exceeds MaxSpeed" sweep
   passed with the clamp deleted, because at default settings natural magnitudes never approach
   the ceiling. It now also sweeps against a deliberately low ceiling and asserts the clamp
   actually engages. Separately, the coastal-reversal test was rewritten to isolate the term by
   subtracting the slack-tide sample — comparing absolute directions made it a hostage to two
   unrelated constants, and it broke when one of them was corrected while the code was right.

**Verbose-gated field dump.** `VerboseLogging` makes `SeaTick` log a one-shot transect at boot.
It is not scaffolding to remove: a headless server has no console anyone can type `wake field`
into, so without it the field is unobservable on the machine that matters most.

## 1b. Original task 1 specification (kept for reference)

Pure arithmetic in `Core/CurrentField.cs`: no clock, no config, no Unity types beyond
`Vector3`, so the harness compiles and tests the shipping source. **No forces are applied in
this task.** Reporting comes before pushing — a drift you cannot read is a drift you cannot
debug.

Composed of four terms, each independently togglable so they can be tuned apart:

- **The Great Drift** — a basin-scale gyre keyed off world seed, seasonally reversing.
- **Coastal set** — parallel to the shore with a slight onshore component. Needs a cheap
  land-proximity read; prefer `WorldGenerator` height sampling over anything that touches
  loaded zones, so the field stays a pure function of coordinates.
- **Races** — acceleration where two landmasses are close.
- **Slack and eddies** — near-zero magnitude with slow rotation where arms oppose.
- **Tide** — a global phase on a ~2-day cycle, swinging magnitude and reversing coastal set.
  A pure function of world time. No state, no save file.

`wake here` reports the vector, its magnitude, the tide phase and which term dominates, at the
caller's position. `wake field <x> <z>` answers for an arbitrary point.

**Acceptance:** unit tests cover determinism (same seed + position + time ⇒ identical vector,
on repeat calls and across process restarts), the seasonal reversal, the tide cycle, and
magnitude bounds. `wake here` answers on a live server and the numbers change as you sail.
Two different machines on the same world report the **same vector for the same point** — the
whole no-sync design rests on that, so measure it rather than assuming it.

**Tuning target, from the design:** strongest water in the world ≈ 15–25% of half-sail speed,
typical water ≈ 5%. Vanilla reference values: `m_backwardForce = 50f`,
`m_sailForceFactor = 0.1f`.

---

## 2. Set and drift — DONE 2026-08-28 (0.2.1), MEASURED LIVE

**Verified on a live client, in a boat.** A VikingShip drifting at (8060, 246) in 0.256 m/s
water settled at **0.97x the water's speed along the current** — the model's target is 1.0.
The push faded from 0.0051 at rest to 0.00017 at convergence, which is the saturation term
doing exactly what it was built to do.

The model took **three attempts**, and each was killed by a measurement rather than by review:

1. **Drag toward the water**, `force ∝ (water − hull)`. Double-counts vanilla's damping, which
   already assumes still water. Would have braked every boat under sail by ~3.4 m/s². Caught by
   reasoning about the sailing case — but only after the tests had been written to match the
   wrong premise, so the harness was green on it.
2. **A push calibrated against vanilla's damping** so the two balance at the water's speed.
   Measured: a karve drifted 19m in 30s, matching the prediction to 3% — including the half of
   the prediction that was bad news, that it drifted at **twice** the water's speed, worse in
   weaker water. The fix looked like a square law. It was not.
3. **A square law.** Also wrong, and unfixable in principle: `m_dampingForward` is a SERIALIZED
   UNITY FIELD that every boat prefab overrides. The class default read off the decompile (0.01)
   applies to no actual boat — a VikingShip measured ~0.0053 effective and settled at 1.38x. A
   raft, karve and longship would each land on a different multiple, so **no single constant can
   be correct**.

**What shipped: saturation.** The push fades to zero as the hull's speed along the current
reaches the water's own. That sets the equilibrium by construction, without knowing anything
about how a hull damps, so every boat type converges on the same answer — and `wake here`'s
number becomes the speed a sailor will actually drift, on any hull.

**THREE instrument failures, one per round-trip, and they cost more than the bugs:**

- `wake drift` reporting **"pushed 0 hulls"** was the designed control on land, reported as a
  failure. The control needs to be labelled as one.
- The **RATIO readout printed total hull speed**, so a converged hull at 0.97x along the current
  read as a runaway at 1.33x. The gap was motion ACROSS the current — wave-driven surge the mod
  neither causes nor controls. Now reports ALONG-RATIO with total beside it.
- The **anti-braking test failed to fail** when first injured, because two clamps guard that
  property and only one had been removed. A test that cannot be made to fail proves nothing.

**TWO-HULL CONVERGENCE CONFIRMED 2026-08-28 (0.2.1)** — the saturation model's central claim,
and the one thing the harness cannot check. Same water (~0.245 m/s), same spot:

| Hull | ALONG-RATIO | total |
|---|---|---|
| Karve | 0.84 – 0.98 (mostly ~0.86) | 1.5 – 2.5 |
| VikingShip | ~0.96 | 1.03 – 1.41 |

Under the calibrated model these two would have settled on quite different multiples, because
their `m_dampingForward` values differ. They now agree within ~0.1 and both sit near 1.0.

Two things fell out of the same run:

- **The anti-brake clamp caught working live, in a case nobody constructed.** One karve line
  reads `along -0.41 ALONG-RATIO -1.68 dv 0.00488` — the hull momentarily moving AGAINST the
  current, and the push going to its at-rest maximum rather than negative.
- **Wave-driven cross-motion is large and hull-dependent.** The karve shows total ratios above
  2.0 while sitting at 0.86 along; the longship stays near 1.2. A light hull gets thrown around
  by waves far more. Reporting total speed would have made the karve look twice as broken as the
  longship when both were fine — which is exactly the mistake the ALONG/total split fixed.

**A hull sitting still under near-full push turned out to be two spawned boats collided**, not
a defect. Worth recording because the wrong explanation was nearly written up as an engine
fact: the guess was that `WorldGenerator.GetHeight` returns base terrain and so cannot see
placed rock prefabs. That may well be true, but it is UNVERIFIED and was not the cause here.
Do not cite it.

**Residual, accepted:** the karve settles ~0.10 lower than the longship. Within wave noise, and
closing it would mean raising `DriftStrength`, which also changes how fast a hull is grabbed.
Left alone deliberately.

**Still open, needs a human:** compatibility testing against other boat mods. Everything
measured so far is a clean baseline, taken with none installed.

## 2c. Compatibility: Sailing (Smoothbrain) — ANALYSED 2026-09-02, MEASURED 2026-09-21 (task 11 item 2)

The second named compatibility, and the one that matters most today: **Sailing is already on
Ravenrest** (1.1.8, every hull at speed factor 1.5, nudge on at force 10), so every boat
measurement Undertow makes there is made with it present. Same standard as task 5c: read from
the author's published source (<https://github.com/blaxxun-boop/Sailing>), never from the
shipping DLL, and nothing of theirs is reproduced here — only which vanilla members it touches.

**The version gap, stated plainly.** The public source is 1.1.7; Ravenrest ships 1.1.8; the
Thunderstore changelog is empty. The delta is unknown and stays unknown — the shipping DLL is
not decompiled in this repo, by rule. The protocol below measures whatever 1.1.8 actually does.

### What it does to a hull

Nothing in the method we patch. It never touches `Ship.CustomFixedUpdate`, never caps a speed
and never assigns a velocity — so **it is not the mod `CLAUDE.md`'s boat-mod warning was
written about**. Its entire contact with a hull:

- A **result-decorating postfix on `Ship.GetSailForce`**, default priority, our own house
  pattern: the sail force is multiplied by `1 + skill × speedFactor` for the sailor at the
  helm, up to 2.5x at skill 100 with the Ravenrest setting. It also drips skill XP on a timer.
- **Prefixes on `Ship.Forward` and `ShipControlls.Interact`** that refuse a sail setting or the
  helm below a configured skill level. Gates, not forces; all requirements are 0 on Ravenrest.
- **The nudge:** a prefix on `Ladder.Interact` that, with Shift held, applies ONE impulse of
  `10 × mass` along the player's facing, throttled to once a second. An ordinary `AddForce`
  on the same rigidbody we push; physics sums them.
- `WearNTear` health, `Minimap.Explore`, a skill float on the player's ZDO — no contact. It
  declares `BepInIncompatibility` only with Valheim Plus.

So Sailing changes **propulsion** and Undertow changes **the water** — the "boat stat mods
compose without contact" case, and it holds by construction: `DriftForce.Compute` takes the
water's velocity and the hull's component along it, and nothing about how the hull is driven.

### Two predictions, and they are the test

1. **Drifting is untouched.** With the sail down, `GetSailForce` returns zero, and 2.5 × 0 is
   zero. Task 2's acceptance — `ALONG-RATIO` settling near 1.0 with the sail down — should
   therefore read the **same to the second decimal** with Sailing installed or removed. This is
   the cleanest compatibility prediction the mod has, and the one to run first.
2. **Under sail, the saturation term does its job sooner.** A boosted hull running down-current
   crosses the water's speed earlier, so `head` clamps to 0 and the push fades out earlier — the
   drift contributes LESS to a fast hull, by design. Up-current, `head` clamps to 1 and the
   push is exactly what it always was. The anti-braking clamp guarantees Undertow can never
   slow the boosted hull; at worst it stops helping. If step 6 of task 2's protocol shows a
   smaller on/off displacement under sail than the clean baseline did, that is this — not a
   fight — and it should scale with how far above the water's speed the hull was running.

**No code was changed and none should be until this is measured.** If the two runs disagree
in a way the saturation model does not explain, the answer is a default-off compatibility
toggle, never a priority war — and since the two mods patch different methods there is no
ordering to fight over anyway.

### Measurement protocol

Task 2's verification protocol, run twice. Ravenrest already has Sailing, so "with" is the
default and "without" means parking `Smoothbrain-Sailing` out of the server's plugins AND the
client's profile for one session — it is server-enforced with a version floor, so a one-sided
removal will not join.

1. **Drifting, with.** Sail down, from known water (`wake here`), watch the 2-second `drift`
   log line settle. Record the settled `ALONG-RATIO` and the hull type.
2. **Drifting, without.** Same spot, same hull, Sailing parked. Prediction 1 says the two
   ratios match. If they do not, the drifting case has a contact the source did not show,
   and 1.1.8 differs from 1.1.7 in a way that matters — stop and measure before theorising.
3. **Under sail, down-current, with.** Half sail along the reported bearing, 120 s. The
   `along` figure will exceed `water` quickly; confirm `dv` reads 0 once it does — that is the
   saturation clamp, visible.
4. **Under sail, up-current, with.** Same, against the bearing. `dv` should stay at its full
   value throughout, and the hull should still make headway: the boost is never braked.
5. **The nudge.** Sail down, in the current, Shift + use the ladder once. The hull jumps,
   `along` spikes, `dv` drops to 0 while the hull outruns the water, then recovers as vanilla
   damps it back. One impulse, then the model resumes. Nothing to fix if it looks like that.

**Acceptance:** step 2 matches step 1; steps 3 and 4 behave as predictions 1 and 2 describe.
Then the `CLAUDE.md` entry loses its ⚠️, gains a date, and Ravenrest's every boat number so far
is retroactively a "with Sailing" number — which they already were.

## 2d. Compatibility: Njord (Wubarrk) — ANALYSED 2026-09-02 FROM DOCS ONLY, MEASURED 2026-09-21 (task 11 item 2)

The third named compatibility, and the one that actually matches the shape `CLAUDE.md`'s
boat-mod warning describes: configurable sail and acceleration forces, per-hull speed caps, an
"overhauled physics curve". **It is ON RAVENREST at 1.3.5**, so — as with Sailing — every boat
number Undertow has measured there was taken with it present.

**This entry is held to a weaker standard than 2c and 5c, and says so.** Njord publishes no
source: `website_url` is a Discord invite, the Thunderstore page links no repository, and the
README's licence reserves modification to the author. Nothing here comes from the shipping DLL,
by rule. What follows is reasoned from three published things — the README, the changelog and
the Ravenrest config — plus properties of Unity physics and of our own code. Two questions
only the author or a measurement can answer are named at the end.

**Ravenrest config, the parts that touch physics:** `Wind_AlwaysFull = true`,
`AccelerationMultiplier 2.2`, `BaseForwardForce 0.85`, `SailForwardForce 0.3`,
`HalfSailForce 1.2`, `FullSailForce 4.2`, `ReverseKick 1.8`, `SteeringMultiplier 1.85`, caps
Raft 7 / Karve 16.8 / Longship 26 / Drakkar 30. The changelog says the Longship and Drakkar
caps only started applying in 1.3.4 — "crews used to these two hulls will feel them slow down".

### What can be said without the source

1. **Our push and Njord's cap never meet, by arithmetic.** `DriftForce.Compute`'s saturation
   term is `1 − hullAlong / waterSpeed`, clamped to [0,1], so the push is exactly zero whenever
   the hull already moves along the current faster than the water — at most 1.92 m/s (1.2 max
   current × 1.6 storm surge). Njord's lowest cap is 7. There is no speed at which both act on
   a down-current hull. Up-current at the cap, our push is full but opposes the hull's motion,
   which no clamp on speed magnitude fights.
2. **Even where a clamp and a push coincide, the overshoot is one tick.** Our write is
   `AddForce(..., Impulse)`, which Unity integrates at the physics step AFTER every FixedUpdate
   patch has run. So wherever Njord's cap lives inside `CustomFixedUpdate`, it sees the hull
   BEFORE our impulse lands, and the most a hull can sit above the cap is one tick's `dv`:
   `water × DriftStrength × dt = 1.2 × 1.0 × 0.02 ≈ 0.024` m/s, 0.038 in a storm. Vanilla's own
   sail and rudder impulses have the same relationship to any in-tick clamp, and are larger.
3. **If Njord replaces vanilla's update outright, we still run.** A prefix that skips the
   original does not skip postfixes. Ours still re-checks `IsOwner()`, still reads the field,
   still adds force. What changes is the DAMPING our push settles against — and that is the
   whole reason the model is saturation rather than a calibrated push: "the equilibrium is set
   by this term rather than by a race between our push and vanilla's damping". Njord's damping
   would be the first non-vanilla damping that claim has met. The honest statement of the
   claim: with damping small next to `DriftStrength`, every hull settles near the water's
   speed (vanilla measured 0.86 and 0.96); with damping comparable to it, the settled ratio
   drops — linear damping `c` gives `1/(1+c)`. That is a TUNING outcome (`DriftStrength` up on a
   Njord server), not a conflict, and the ratio is the number that decides it.
4. **Propulsion versus water, again.** Njord's forces drive the hull; ours is the water it sits
   in. We push at the centre of mass with no torque, so `SteeringMultiplier` and the helm are
   untouched. `Wind_AlwaysFull` changes what the SAIL sees, not `EnvMan`'s wind, and Undertow
   drives no hull by wind in any case.
5. **BarrkBOT, cosmetic.** The export samples helmed hulls above `Barrkbot_MinSpeed` (1 m/s)
   and the odometer accumulates on the owning peer. A helmed hull drifting at full current
   therefore banks a ~1.2 m/s "record" and some distance without a sail up, until the first real
   sail overwrites the record. Off by default; on Ravenrest it is off.
6. **Unattended hulls.** Njord's 1.3.3 note says a hull "loses power once you actually step
   off the boat". Our `UnattendedDriftFactor` is 0, so we never push an empty hull whatever
   Njord does to it.

### The two things only the author or a measurement can answer

- **Does Njord replace `Ship.CustomFixedUpdate`, or decorate it?** Decides whether vanilla's
  damping is still what we settle against (point 3).
- **Where and how does the cap act** — a velocity clamp in the tick, a force scale, a drag
  term above the cap? Decides whether point 2's one-tick bound is the whole story.

Neither changes the compatibility verdict; both change how sharply it can be stated.

**No code was changed and none should be until this is measured.** If the settled ratio under
Njord is well below vanilla's, the first response is `DriftStrength` on that server, not a
toggle. If something fights that no damping explains, the answer is a default-off
compatibility toggle, never a priority war.

### Measurement protocol

Task 2's verification protocol, on Ravenrest, where "with" is the default. Njord is
ServerSync-pinned to its own version, so "without" means parking `Wubarrk-Njord` on the server
AND in the client profile for one session.

1. **Drifting, with.** Sail down, from known water (`wake here`), on a Karve. Watch the
   2-second `drift` log line settle and record `ALONG-RATIO`. This is the number: vanilla gave
   0.86 (karve) and 0.96 (longship). Near those, point 3's claim holds under Njord's physics.
   Well below — 0.6, say — and Njord's damping is doing what point 3 warned; try
   `DriftStrength` 2.0 and re-measure before concluding anything.
2. **Drifting, without.** Same spot, same hull, Njord parked both sides. The pair of ratios
   from 1 and 2 IS the compatibility result.
3. **Down-current at the cap.** Full sail along the bearing until the HUD sits at 16.8. Confirm
   the log line's `dv` reads 0 the whole time the hull is above the water's speed — point 1,
   visible. If `dv` is non-zero with `along` above `water`, the saturation clamp is broken and
   that is OUR bug, not Njord's.
4. **Up-current at the cap.** Same, against the bearing. `dv` full, hull still makes headway,
   and nothing judders: a magnitude cap and an opposing push coexisting.
5. **The one-tick bound.** With Debug off this is invisible and that is the point. If Njord's
   own HUD ever shows a hull sitting more than ~0.05 m/s above its cap while drifting
   down-current, point 2 is wrong about where the cap acts — note it, and ask the author.

**Acceptance:** step 1 near vanilla's ratios or explained by `DriftStrength`; steps 3 and 4
as described. Then the `CLAUDE.md` entry loses its ⚠️ and gains the ratio under Njord.

## 2z. Original task 2 build notes (superseded)

Built, unit-tested (**83/83**, every assertion proven to fail without its fix), and confirmed
armed on a dedicated server:

```
[Info :  Undertow] Harmony patched 2: Terminal.InitTerminal, Ship.CustomFixedUpdate
```

`Ship.CustomFixedUpdate` attaching is a real result rather than a formality: Harmony resolves
the three private field injections (`___m_nview`, `___m_body`, `___m_players`) at patch time, so
a wrong field name throws there. All three are **private** in the shipping assembly and public
only in the publicized reference — naming any of them directly would have been the
`Terminal.commands` failure again, fifty times a second.

🚫 **THE ACCEPTANCE IS NOT MET AND CANNOT BE MET FROM A SCRIPT.** Boat physics runs only on the
peer that OWNS the hull, which is a player's machine. A dedicated server with nobody aboard
never executes the postfix once. Everything below the line is verified; the quantitative
displacement test needs a person in a boat. `wake drift` exists precisely so that person can
check it in under a minute.

### The model changed during the build, and this is the important part

The first implementation was **drag toward the water**, `force ∝ (water − hull)`. It is wrong,
and wrong only under sail: vanilla's damping already models hull-water resistance against
absolute velocity, so a second drag term double-counts it and a karve making 6 m/s in 0.3 m/s
water gets `0.6 × (0.3 − 6) ≈ −3.4 m/s²` — the sea braking every boat under way.

It is now **entrainment**: a push proportional to the water's velocity and nothing else, which
is the first-order correction for vanilla damping being computed in the wrong frame. It carries
a boat and never fights the sail. `DriftForce.Compute` does not take the hull's velocity as a
parameter at all, so the bug is unrepresentable rather than merely tested against.

**The harness was green on the wrong model**, because the tests were written from the same
mistaken premise as the code — the failure mode a test suite cannot catch by itself.

### Verification protocol for whoever next sails

1. Install `Undertow.dll` on the server **and the client** — physics runs on the client that
   owns the hull, so a server-only install pushes nothing, ever.
2. Set `VerboseLogging = true` in `com.raveniron.undertow.cfg` on both.
3. Standing on land: `wake drift` should report **pushed 0 hulls**. That is the control.
4. Board a boat and get under way, then `wake drift` again. It should now report a push count,
   the water speed under you, and the velocity change applied that tick. If it still says 0,
   the patch is not reaching hulls and nothing below this line is worth doing.
5. `wake here` gives the current's speed and BEARING under you. **The strongest single signal is
   directional**: drift with the sail down and confirm you move the way `wake here` said.
6. Quantitative run: from a fixed spot, fixed heading, half sail, 120 seconds, note the end
   position. Set `EnableDrift = false`, repeat identically. The displacement difference should
   be on the order of `water speed × 120s` (≈36m at 0.3 m/s), and along the reported bearing.
   Expect somewhat less than that: vanilla's quadratic damping opposes the drift, and it
   opposes sideways motion five times harder than forward.
7. **Repeat step 6 with any other boat mod disabled.** Some of them patch this same method, and
   at least one adds force to the hull and caps its speed there. Our postfix runs last, so a
   push arrives on top of whatever they did and a speed cap simply absorbs it near the ceiling -
   expected to compose, but measure rather than assume. If the two runs differ by more than a
   cap explains, the answer is a default-off compatibility toggle, never a priority war.

### What was deliberately NOT done

Nothing assigns `linearVelocity`; nothing touches a ZDO position; the postfix re-checks
`IsOwner()` itself and is stricter than vanilla — a hull whose ownership cannot be established
is declined rather than assumed. Unattended boats get `UnattendedDriftFactor`, **default 0**.
The current fades to nothing by 10420m so it never argues with `Ship.ApplyEdgeForce`.

## 2b. Original task 2 specification (kept for reference)

The mod's entire write surface. One postfix on `Ship.CustomFixedUpdate`.

- 🚫 **Re-check `IsOwner()` inside the postfix.** Vanilla's owner guard is *inside* the
  method and does not protect a postfix. Without this, every peer pushes the same hull.
- Apply as force at the centre of mass, in vanilla's own units from that same method.
- **Never assign `linearVelocity`** — vanilla assigns it wholesale in the same tick.
- **Never touch the ZDO position.**
- Unattended boats (`m_players.Count == 0`) get a configurable fraction, **default 0**.
- Fade the current out past 10400m so it never fights `Ship.ApplyEdgeForce`.

⚠️ **Other boat mods may patch this same method**, and at least one popular one adds force to
the hull and caps its speed there. Our postfix runs last - after any prefix and after vanilla -
so the current arrives on top of whatever they did, and a speed cap simply absorbs it near the
ceiling. Expected to compose. **Run the acceptance below twice, with and without**, and compare;
if the displacement differs by more than a cap explains, the answer is a default-off
compatibility toggle, never a priority war.

**Acceptance, and it is quantitative:** sail a fixed heading at half sail for a fixed duration
from a fixed start, with the mod off, and record where you end up. Repeat with the mod on. The
displacement between the two runs matches `CurrentField`'s prediction for that passage. A
"felt about right" acceptance is not acceptance — this is the task where a sign error or a
units error hides for months.

Second run to do while you are there: two players in one boat, sailing together. Confirm one
push, not two, and no fight over the hull.

---

## 3. Tide, storm surge, and the Wrath bridge — BUILT 0.3.2, VERIFIED EXCEPT ONE LINK

Harness **97/97**. Both bridge states verified headless on a real dedicated server, by parking
and unparking RW between boots:

```
RW ABSENT:  Ragnarok's Wrath not installed — storm surge and seasons are dormant.
            The sea still runs; it just has no weather behind it.
RW PRESENT: Ragnarok's Wrath detected — bridged.
            CurrentField live — ... season index 0 (read from Wrath)
```

**`(read from Wrath)` is the whole point of that line.** Spring is index 0, and so is every
failure mode — RW absent, getter unresolved, invoke throwing. On a day-0 world where RW
genuinely reports Spring, a correct read and a total failure print the identical number.
`WrathBridge.SeasonWasRead` is the only thing that separates them, and without it the log
entry would have been worthless.

**We read FACTS, not tuning.** RW exposes `WindMultiplierAt(pos)`, which already returns a
number that rises in a storm and would have been one fewer read. It is deliberately unused:
that value is `StormWindMultiplier`, which a server owner sets to tune FIRE SPREAD. Borrowing
it would mean raising your fire risk silently roughened the sea — a coupling neither mod's
owner asked for, and one nobody would think to look for. We read `IsStormAt` and apply our own
multiplier.

**Surge is applied BEFORE the ceiling**, so `MaxCurrentSpeed` stays a true ceiling on water
speed anywhere in the world. A storm drives weak water toward it, never through it. Tested,
including a sweep proving a 4x storm cannot breach a 0.2 m/s cap.

✅ **THE LAST LINK IS CLOSED — measured live 2026-08-28 (0.3.3), ON THE CLIENT**, which is
exactly the machine the first implementation was dead on:

```
[Undertow]        STORM at (8101, 368) — IsStormAt(centre)=True, surge x1.6
                  | at centre: 0.38 m/s Drift | 800m away: 0.266 m/s surge x1
[RagnaroksWrath]  [WeatherSystem] storm started at (8101, 368).
```

Two mods, two machines, the same centre — our client recovered from vanilla's replicated event
the exact position RW chose on the server. And the design promise reads off one line: **x1.6 at
the centre, x1.0 at 800m.** The sea rises where the storm stands and nowhere else.

**A defect was found BEFORE this test ran, by reading RW's source rather than trusting the
plan.** The bridge originally called `WeatherSystem.IsStormAt(pos)`. That is dead on a
dedicated server: `StormActive` is assigned only in `WeatherSystem.Tick()`, and RW's
`WorldTick` returns early when the process is not the simulation authority — so on a pure
client it is false forever, and drift is applied by the peer that OWNS the hull, i.e. a client.
Storm surge would have reached nobody except on a listen host. Rewritten to read vanilla's
replicated `RandomEvent`, which needs nothing of RW's to be ticking.

**Then a SECOND accessor bug, caught by the server logging nothing while the client logged the
storm.** `GetActiveEvent()` returns `m_activeEvent`, which vanilla sets only when a LOCAL PLAYER
is inside the event area — so it is permanently null on a headless server. `GetCurrentRandomEvent()`
is the correct call (0.3.4): the scheduler sets it on the server, `RPC_SetEvent` sets it on every
client, and it does not care where anyone is standing. Both facts are now in `CLAUDE.md`.

🚫 **THE SEASON HAS THE SAME DISEASE AND NO CURE.** `SeasonSystem.Current` is also assigned
only inside `Tick()`, and RW syncs no season state anywhere - so every client computes the
field as spring while the server knows the truth. Boats do NOT desync (every client agrees with
every other) but the seasonal shift is inert away from a listen host. The effect is a
15-degree rotation and a magnitude nudge, so this is a lost flourish rather than a broken
feature. The fix is asking RW to sync its season, NOT running a second season clock here -
that is precisely the conflict house rule 4 exists to prevent.

**Also worth keeping:** RW storms cannot fire on an EMPTY server at all - the event carries
`m_pauseIfNoPlayerInArea = true` and its position is chosen from "somewhere a player actually
is" via character ZDOs. Eight minutes at a 60-120s interval with nobody online produced
nothing, exactly as that design predicts. Storm work needs a player online.

**To close it (needs a player, ~5 minutes):**
1. RW must be on the CLIENT too, not just the server — drift is computed on the peer that owns
   the hull, so a client without RW computes no surge no matter what the server thinks. Copy
   `RavenIronStudios-RagnaroksWrath` into the Gale `Default` profile.
2. Shorten `StormMinIntervalSeconds` / `StormMaxIntervalSeconds` in RW's config (both machines),
   or trigger one directly with `event ragnarokswrath_devastating_storm` from an admin console.
3. Sail into it and run `wake here`. It prints `STORM SURGE x1.6 — the sea is up here` inside
   and nothing outside. With `VerboseLogging` on, `SeaTick` also logs the storm centre, the
   speed there, and the speed 800m away, so the "here and nowhere else" promise is one line.

## 3z. Original task 3 specification (kept for reference)

`Bridge/WrathBridge.cs` — reflected, read-only, soft. Resolve once into cached delegates;
retry until resolved rather than latching a failure; log once on success and once on absence,
naming the member.

- `WeatherSystem.StormActive` / `StormCentre` → storm surge: magnitude and confusion rise
  under a storm, positionally, never globally.
- `WindSystem.IntensityAt(Vector3)` → feeds current strength, so Undertow never reads
  `EnvMan` and rule 4 holds by construction.
- Season → the Great Drift's seasonal reversal.

**Acceptance:** with RW installed, a storm passing over water measurably changes the reported
field at its centre and not 500m outside it. **With RW absent, the mod loads clean, logs the
absence once, and sails normally** — that second half is the one that gets skipped, and it is
the one users will hit.

---

## 4. Flotsam — UNBLOCKED 2026-08-28 (0.4.1): the `Floating` question is answered

**123 of 1090 item prefabs carry `Floating`.** Measured headless by scanning
`ObjectDB.instance.m_items` on a live dedicated server — the one question no decompile could
answer, because component attachment lives in Unity asset data rather than in the assembly.
Flotsam can be built from vanilla `ItemDrop`s, so the no-new-prefabs constraint holds and the
design survives.

**The surprise, and it changes how the palette is chosen: `Floating` is loss-prevention, not
buoyancy.** `ShieldIronTower`, `MaceIron` and `ShieldFlametal` float; ore, stone, berries and
most food do not. Valheim attached it to things a player might drop and never recover. So what
washes up is a FLAVOUR decision, not a list dictated by physics — pick for the story, not for
what happens to be buoyant.

Usable palette, from the measured list:

| Kind | Prefabs |
|---|---|
| Driftwood | `Wood` `RoundLog` `FineWood` `ElderBark` `Blackwood` `YggdrasilWood` `Root` |
| Forest debris | `FirCone` `PineCone` `HardAntler` `WitheredBone` `Tar` |
| From the sea | `SerpentMeat` `BonemawSerpentTooth` `VoltureMeat` |
| Wreckage | `ShieldWood` `SpearWood` `Club` `BowFineWood` `FishingRod` `FishingBaitOcean` |
| Rare prize | `DragonTear` `Wishbone` `Demister` `MeadSwimmer` |

`wake floats` reports this in-game; `VerboseLogging` logs it once at boot. Kept rather than
removed: it is the only way to re-answer the question after a Valheim update moves the assets.

⚠️ **One step still unproven:** that a spawned `ItemDrop` actually SITS on the surface where we
put it. The component's presence is strong evidence, not proof — spawn one and watch it before
trusting the spawner.

✅ **FULLY VERIFIED 2026-08-28 (0.5.1) — including the step no log could settle: IT FLOATS.**
Driftwood spawns in slack water and was seen bobbing on the surface by the owner. `Root`,
`RoundLog`, `Wood`, `FirCone`, `FineWood` all spawned cleanly, no missing prefabs, no warnings.

**A REAL BUG WAS FOUND BY THE LIVE TEST, and the log looked healthy the whole time it was there.**
Eight spawns in a row each printed `[1/12 alive]` — the tracking list emptied every tick, because
the first version held `GameObject` references and **a dedicated server unloads the instance the
moment a nearby client takes the item over**, while the item lives on as a ZDO. That silently
disabled BOTH the cap and the TTL: the entire safety valve against filling the ZDO table was
inert, and nothing in the output said so. Fixed by tracking `ZDO.m_uid` and looking it up through
`ZDOMan.GetZDO`, taking ownership before destroying since only the owner may remove a ZDO. The
count then climbed `1→2→3→4→5` as it should.

This is the clearest case in the project of a green-looking log hiding a dead safety mechanism —
the sort of thing only a live test finds, which is why task 4 was gated on one.
## 4z. Original task 4 specification

🚫 **Blocked until the `Floating` question is answered in-game.** Do not write the spawner
first.

- Vanilla `ItemDrop` prefabs only. No new prefabs, ever.
- Server-authoritative, driven from `SeaTick`, budgeted.
- Spawn only near a real player. Nothing accumulates in unloaded ocean.
- Hard cap per zone and a decay timer, both configured, both logged when hit.
- Post-storm wreckage differs from calm-day driftwood.

**Acceptance:** items spawn in slack water, float correctly, and are collectable. On a
long-running world the per-zone cap holds and the ZDO count is stable across several hours —
measure it, do not assume it. Removing every player from the area stops production entirely.

---

## 5. Swimmers — BUILT 0.5.0, ARMED, drift itself needs a swimmer

Harness **162/162**, every assertion proven to fail without its fix. The patch attaches on a
dedicated server:

```
Harmony patched 3: Terminal.InitTerminal, Character.UpdateSwimming, Ship.CustomFixedUpdate
```

That is a real result: `UpdateSwimming` is PRIVATE, and `m_currentVel` and `m_nview` are too, so
attachment proves Harmony resolved the method and both field injections — including the writable
`ref ___m_currentVel`. A wrong name throws at patch time.

**THE BACKLOG'S OWN PRESCRIPTION WAS WRONG, and reading the body before building caught it.**
This task said to fold the current in "the way vanilla's own `AddPushbackForce(ref m_currentVel)`
does". That helper ignores `m_pushForce`'s magnitude completely and drives velocity to a flat
20 m/s along its direction, halved to 10 while swimming — it ejects a body from a creature it is
clipping through. A 0.3 m/s current through that channel would fire a swimmer off at five times
swim speed. Both `CLAUDE.md` and this entry are corrected; the old advice is marked as wrong
rather than quietly deleted.

**The scaling is the other trap, and it is a factor of twenty.** Vanilla lerps `m_currentVel`
toward the swimmer's intent each frame, so a per-frame addition `d` settles at
`d / m_swimAcceleration` — and that is 0.05. The delta is therefore pre-multiplied by the
acceleration, which cancels the amplification exactly and leaves the steady state equal to the
intended drift. A test simulates vanilla's own lerp for 4000 frames rather than trusting the
algebra.

**The drowning guard is a SAFETY PROPERTY, not a balance dial.** `SwimmerMaxShareOfSwimSpeed`
caps drift as a share of the character's own swim speed. The harness sweeps the ENTIRE legal
config range — every drift factor, every cap, water from 0 to 5 m/s — and asserts a swimmer
always out-swims the water. If a player can be held offshore until they drown, the feature is
wrong rather than mistuned. Worst case across the whole range is 0.9 of swim speed, at the
extreme of a setting the description warns about; at shipped defaults it is 0.21 m/s against a
swim speed of 2.

**Players only, deliberately.** Vanilla lerps a CREATURE's swim velocity with a factor of 0.5
rather than `m_swimAcceleration`, so the scaling above would be wrong for them — and dragging
swimming creatures around changes AI pathing, which nothing asked for.

🚫 **Unverified, and needs a swimmer:** that the drift is actually felt in the water. Swim out
into open ocean with `VerboseLogging` on and the log prints water speed, computed drift, the cap
and the swimmer's measured speed on one line every three seconds. **Then test the guard on
purpose:** swim directly upstream in the fastest water you can find and confirm you make
progress. That is the one acceptance criterion that matters.

✅ **VERIFIED LIVE 2026-08-28 (0.5.1), including the safety property.** The decisive lines are
the ones where the player stopped swimming and simply floated:

```
swim drift @ (8075,-23) | water 0.345 drift 0.172 (cap 0.7) | swimmer 0.164 m/s, swimSpeed 2
swim drift @ (8077,-22) | water 0.347 drift 0.173 (cap 0.7) | swimmer 0.165 m/s, swimSpeed 2
```

**Computed drift 0.172, measured 0.164** — a 95% match, and conclusive proof that the
1/m_swimAcceleration amplification is cancelled: had the scaling been omitted the swimmer would
have moved at roughly 3.4 m/s instead of 0.16.

**The drowning guard has a tenfold margin.** While actually swimming the player held 1.9-2.0 m/s
— full swim speed — against a 0.17 m/s current. Nobody can be pinned offshore.
## 5c. Compatibility: Dive In (sighsorry) — ANALYSED 2026-09-02, NOT MEASURED

The owner asked for this one by name. It is the first specific mod Undertow has been checked
against, and it is checked the way `CLAUDE.md` demands: from the author's **published source**
(GPL-3.0, <https://github.com/sighsorry1029/DiveIn>, last push 2026-08-08, version 1.2.0) and
never from a decompile of the shipping DLL. Nothing of theirs is reproduced here — only which
vanilla members they touch, which is the same standard RW's shudnal-Seasons entry was held to.

**Where it is:** `Wonderland` Gale profile, plugins and both config files. Not on Ravenrest
and not in any Ravenrest profile, so the measurement happens on Wonderland or after adding it.

### What it does to the method we patch

Everything Dive In does to a player swimmer happens inside `Character.UpdateSwimming` — our
postfix's method — and it is all default priority, as ours is:

- A **prefix** that, for the LOCAL player only, temporarily scales `m_swimSpeed` (swim skill
  up to x1.5, fast swim x2, encumbered x0.5, all config) and, while ascend/descend is held,
  steers `m_moveDir` to include a vertical component. Depth is then adjusted through
  `m_swimDepth`, which vanilla already uses for how deep a swimmer sits.
- A **postfix and a finalizer** that restore `m_swimSpeed` and `m_moveDir`.
- A second **postfix** that only sets animator bools while blocking underwater.

**It never writes `m_currentVel`, never replaces or skips vanilla's lerp, and never changes
`m_swimAcceleration`.** That is the whole finding. Our per-frame addition therefore lands on
an unchanged servo, the `1/m_swimAcceleration` cancellation in `SwimDrift` still holds, and
the two mods compose linearly: the swimmer's intent (theirs, possibly boosted) plus the water
(ours). It touches no `Ship` member, so drift is untouched; its `WaterVolume` prefix is purely
visual and we read no water-surface state; its creature diving is `MonsterAI`/`BaseAI` work
our players-only gate never sees; it declares no `BepInIncompatibility` against us.

### The one ordering-dependent detail, worked through

Both postfixes are default priority, so **load order decides which runs first**, and the only
consequence is which `m_swimSpeed` our drowning-guard cap reads: the scaled value (if we run
before their restore) or the vanilla one (if after). At shipping defaults:

| Quantity | Value |
|---|---|
| Strongest drift ever requested (`1.2 × 1.6 storm × 0.5 factor`) | **0.96 m/s** |
| Cap, vanilla `m_swimSpeed` 2.0 seen (`× 0.35`) | 0.70 |
| Cap, encumbered 1.0 seen | 0.35 |
| Cap, fast+skill 6.0 seen | 2.1 (drift stays 0.96 — never asked for more) |
| Swimmer's real speed: vanilla / encumbered / fast | 2.0 / 1.0 / 4.0–6.0 |

The guard holds in every cell — drift never reaches the swimmer's real speed — but the
**encumbered diver in a storm** case is the tight one: 0.70 of drift against 1.0 of swim if we
read the restored value, a 0.3 m/s headway where the design normally has tenfold. Dive In
already makes encumbered swimming a stamina emergency, so this is unlikely to be the thing
that drowns anyone; it is the case to measure first precisely because it is the worst.

**No code was changed and none should be until this is measured.** If the tight case turns
out to matter, the answer is the one `CLAUDE.md` prescribes — a default-off compatibility
toggle (most likely "read the cap against vanilla swim speed only") — never a priority war.

### Measurement protocol

Task 5's acceptance, run with Dive In alongside. One client, both mods, `VerboseLogging =
true`; the 3-second `swim drift` log line is the instrument, exactly as in task 5.

1. **Control.** Float idle at the surface in known water (`wake here` first). Read the line:
   `drift` is the computed value, `swimmer` the measured. Task 5's clean baseline was 0.172
   computed / 0.164 measured. The pair should still agree to within ~10%.
2. **Which ordering did you get?** The same line prints `swimSpeed`. Hold fast swim while
   idle-ish: if it prints 4 (or 3, or 6) our postfix runs BEFORE their restore; if it stays 2,
   after. Record it — the answer is load-order dependent and may differ per profile.
3. **Diving idle.** Descend and hold depth mid-water, no horizontal input. `swimmer` should
   again match `drift`: the current carries a diver exactly as it carries a floater, since
   the field is planar and depth is theirs.
4. **Diving against it.** Swim into the current at depth. The swimmer's speed should be their
   swim speed minus the drift, and positive — a diver must always be able to make headway.
5. **The tight case.** Encumbered, in the strongest water you can find (a storm over deep sea
   if RW obliges), underwater. Confirm headway is still possible before stamina runs out.
   If it is not, that is the toggle case above — and note that an encumbered diver in a storm
   drowns in Dive In alone; separate the two before blaming the current.
6. **No amplification.** At no point should `swimmer` exceed `drift` by more than wave noise
   while idle. A reading near 20x is the trap `SwimDrift` exists to cancel; if it appears,
   something has changed the lerp and this analysis is stale — re-read their source at the
   commit they shipped.

**Acceptance:** steps 1, 3 and 4 agree with the computed drift the way task 5 did; step 5
leaves headway. Then the `CLAUDE.md` entry loses its ⚠️ and gains a date and a number.

## 6. Valheim 1.0.12 re-verification — MEASURED 2026-09-12 (0.6.0), CLIENT-SIDE, ONE HULL

The first in-game run since the game left 0.2x. It is a real result and it is narrower than it
first looked: the headline numbers below were **written up wrong once** before an adversarial
pass caught them, so read the scope paragraph before quoting any figure.

**Setup.** Undertow 0.6.0, Valheim 1.0.12, Ragnarok's Wrath 0.27.0 present. A pure client
(`authority=False, dedicated=False`) against a real PlayFab-backed dedicated server, so every
ambient system idled by design and only hull-owner physics ran here. Seed 1823819530.
**The config was NOT at defaults**: `MaxCurrentSpeed = 1.951174` and `TideAmplitude = 0.4988263`,
retuned mid-session by the owner. Every number below is at those settings, not shipping ones.

### What loaded, and it is all of it

```
Loading [Undertow 0.6.0]
Harmony patched 3: Terminal.InitTerminal, Character.UpdateSwimming, Ship.CustomFixedUpdate
wake console registered
Ragnarok's Wrath detected — bridged
CurrentField live — seed 1823819530, water level 30, tide 96%, season index 0 (read from Wrath)
float scan (ObjectDB.m_items): 162 of 1523 prefabs carry Floating
SeaTick online — 1 system(s), authority=False, dedicated=False
```

**Zero exceptions in the whole log**, Undertow-tagged or not, and no Harmony, MissingMethod,
FieldAccess or TypeLoad failures anywhere. That is the 1.0.12 port standing up.

### Drift: 182 karve samples across all four field terms

| Dominant | n | ALONG-RATIO range | median | water m/s |
|---|---|---|---|---|
| Slack | 49 | 0.57 – 1.00 | **0.99** | 0.135 – 0.149 |
| Race | 120 | 0.48 – 0.97 | **0.91** | 0.461 – 0.749 |
| Drift | 9 | 0.03 – 0.47 | 0.29 | 0.268 – 0.317 |
| Coastal | 4 | 0.16 | 0.16 | 0.247 |

Crew factor was 1 on every one of the 182 lines. In slack water the hull converged on the water's
own speed and `dv` fell to `0.00001` or exactly `0`, which is the saturation term doing precisely
what it was built to do: the push fades because the hull already matches the water.

**THE INTERESTING RESULT IS NOT THE 0.99. It is that convergence degrades as the water speeds
up.** Slack medians 0.99 at 0.14 m/s; Race medians 0.91 at 0.46–0.75 m/s and never once reaches
0.98 across 120 samples. The likely reason is structural rather than a bug: a race is by
definition a spatial gradient, so a hull crossing it is chasing a moving target and never reaches
steady state, whereas slack water is uniform enough to settle in. **That is a hypothesis, not a
measurement** — it has not been tested, and the honest way to test it is to park a hull inside a
race and let it sit rather than sail through. Until then, "a drifting hull settles at the water's
own speed" is demonstrated for slow water and only approached in fast water.

⚠️ **Do NOT read this as re-confirming the two-hull convergence claim.** August's result
(karve 0.84–0.98, VikingShip ~0.96, same water) mattered *because two hulls with different
`m_dampingForward` agreed. Only a karve sailed on 2026-09-12.** The karve's own number improved
on August; the claim that every hull converges regardless of damping is untouched by this run.

### 🚨 Unexplained anomaly — near-shore hulls reading 70 m/s

Four samples around `(-1075,159)` to `(-1106,146)`, depth 26.9–27.1m, read:

```
water 0.162 along -5.689 ALONG-RATIO -35.13 (total 70.13) | dv 0.00324
water 0.123 along -5.295 ALONG-RATIO -43.14 (total 68.12) | dv 0.00245
```

A karve at **70 m/s**, moving *against* the current at 5.7 m/s. Our `dv` is three thousandths, so
this is not us. It resolves back toward normal over the following samples. Candidates: terrain
collision, a zone load, or the hull being flung by something else entirely. **It is recorded here
unexplained deliberately.** The backlog already contains one fabricated engine explanation marked
*do not cite*, and a second guess is worth less than an honest open question. If it recurs, catch
it with the log line and the position, not with a theory.

### Swimmers: the model holds, the guard was never tested

51 passive samples: computed drift 0.084–0.097 against measured swimmer speed 0.080–0.091. The
gap is **0.004–0.005 m/s, about a 95% match** — the same ratio August got (0.172 vs 0.164), not a
tighter one. Cap 0.7 and swimSpeed 2 confirmed, and both follow from `SwimmerDriftFactor 0.5` and
`SwimmerMaxShareOfSwimSpeed 0.35`.

🚫 **The drowning guard was NOT demonstrated.** Requested drift never exceeded 0.097 against a
0.7 cap, so `SwimDrift.Compute`'s clamp branch never executed, and no swim direction was logged
to show the swimmer working *against* the water. The 1.7–2.1 m/s samples are a swimmer swimming,
which is not the same thing. A real test needs drift pushed past the cap — raise
`SwimmerDriftFactor` or find much faster water — with the swimmer making headway upstream.

### The Slack lesson, and it cost this session a full diagnostic pass

A karve reading `Slack` at 0.14 m/s near spawn looks exactly like a broken mod. It is not, and
the two obvious explanations are both wrong:

- **It is not "dead water behind a headland."** That is the Coastal mechanism, and at 29.4m the
  hull is past `ShelfDepth` (28m), so the coastal term never activated at all. `Classify` never
  reported Coastal anywhere in that stretch.
- **It is not distance from the world centre.** No such term exists in the code. What it is: an
  open-ocean interference node, the stream function's opposing arms cancelling, exactly as the
  `CurrentTerm.Slack` comment says. Raw stream magnitude there is ~0.114 against 0.3–0.9 a few
  hundred metres away in every direction.

`Classify` reports Slack below `MaxSpeed * 0.12`. At the session's retuned ceiling of 1.95 that
threshold is 0.234, and the reading sat at 7% — well inside the dead band, not marginally.

⚠️ **"Sail further out" is therefore the wrong advice**, and it was given during this session
before the code was read. The Race term keys purely on terrain rises within 64–160m of the flow,
with no distance-from-centre dependency, so a strait between two close islands runs fast at any
range, and the open-ocean stream oscillates rather than growing outward. **The correct advice is
to look for a constriction, or shallow water inside 28m for a coastal set.** The owner found the
Race water by doing exactly that, which is where 120 of the 182 samples came from.

### What this run does not license anyone to claim

Left entirely untested on 1.0.12: **flotsam spawning, capping or floating** (only the startup
prefab scan re-ran); **storm surge** (no storm fired); **the tide moving** (one snapshot at boot,
never sampled again); **any hull but the karve**; **a non-spring season end-to-end**; **the
drowning guard**; **two-client field agreement** (one client, one character); and **tasks 2c, 2d
and 5c** — Sailing, Njord and Dive In were not even loaded, so the session adds nothing for or
against any of those three predictions.

The float-scan figure moved from `123 of 1090` to `162 of 1523`, but August's was taken
**headless** and this one client-side. A client and a dedicated server can register different
prefabs, so these are not a like-for-like pair; re-take it headless before treating 162/1523 as
the baseline.

## 7. Drift lines — the current made visible — BUILT 2026-09-18 (0.7.0), RUN IN-GAME THE SAME DAY

The owner's call on 2026-09-18: a visible current on the water, and "we still need no hud". The
design was a three-lens panel (sea realism, helmsman readability, engine safety) judged twice and
synthesised; the visual language is **drift lines** — the one sea phenomenon that carries
direction, relative speed and dead water at once without a symbol. Everything below is off-game
fact; nothing below has been seen on a screen.

**What was built.** `Visuals/DriftLines.cs` (client-only MonoBehaviour, added in `Plugin.Awake`
behind `SystemInfo.graphicsDeviceType != Null`), `Visuals/WaterSurfaceCache.cs` (a read-only
XZ → `WaterVolume` resolver off `Floating.GetWaterLevel`'s per-call `GetComponent` path),
`Visuals/ParticleKit.cs` (RW's shader chain verbatim + a generated 128x32 streak texture),
`Core/DriftLineMath.cs` (pure, harness-tested). `wake lines` and `wake lines reset`. Config
`EnableDriftLines` + section `7 - Drift lines`. `CurrentField.SlackShare` extracted so the
field's Slack and the streaks' absence are one constant. Harness 162 → 248; eleven mutations,
each caught. Build clean. **Adds no Harmony patch.**

**Verified against the shipping assembly, 2026-09-18** (non-publicized decompile): every game
member the new code names is public — `WaterVolume.Instances`, `.GetWaterSurface`,
`.GetLiquidType`, `Floating.GetWaterLevel`, `Player.m_localPlayer`, `ZoneSystem.m_waterLevel`.
`Utils.GetMainCamera` does NOT exist (a design review claimed it did); `Camera.main` is used.

### What the first in-game run must settle, in order

Each of these is a prior, not a fact, and each has a one-line fix. Record the answer in
`CLAUDE.md` Known traps whichever way it falls.

1. **It ran at all.** Log: `DriftLines: armed on this client`, then `emitter built — shader
   '<name>'` (expect `Sprites/Default`, `manual fog`), then `first streak afloat at (x, z) …
   surface 30.xx (flat 30.00)` with a surface that differs from 30.00 by the wave. `wake lines`
   reads `LIVE`, active > 0. On a dedicated server the armed line must be ABSENT and `wake lines`
   must answer "no renderer on this machine".
2. **The streaks carry the field.** `wake lines` line 3: mean bearing within ~8° of the field's
   bearing and mean speed within a few percent of the field's speed. `wake here` and line 2 must
   print the same field.
3. **They ride the water, and agree with vanilla.** Line 4: `wave +x.xx` non-zero in any wind,
   and `delta` against `Floating.GetWaterLevel` at 0.000 ± a centimetre. A persistent non-zero
   delta means our resolver picked a different volume than vanilla; the fix is to prefer
   vanilla's rule (already: highest surface among containing volumes) — check the volume name.
4. **The line lies ALONG the flow — the handedness measurement.** Two stations. Where `wake here`
   reads a current within 10° of due north, streaks must run N–S (a mirror is invisible here:
   this proves the 90° offset). Then find a NE set: streaks must run NE–SW, not NW–SE (a mirror
   is obvious here: this proves the sign). If mirrored, flip `FlipRotation` in `DriftLines.cs`.
   If streaks lie ACROSS the flow everywhere, the `startSize3D` axes are transposed: swap to
   `(width, length, 1)`. `wake lines` prints the applied rotation beside the field bearing.
5. **Sort order and clipping.** At noon on flat-ish water, streaks 10–40 m out must be crisp
   foam-white, not blue-tinted or hidden. In wind ≥ 0.8 watch a crest pass under one. Fix
   ladder, one step at a time, each recorded: `BaseLiftMetres` 0.06 → 0.12; `ChopLiftMetres`
   0.05 → 0.15; `ParticleKit.RenderQueue` 3100 → 3200.
6. **Night.** `wake lines` line 5 at noon, dusk, midnight and in rain: `ambient lum` must MOVE.
   Midnight should be a faint grey smear a shade lighter than the water (alpha ≈ 0.08), never a
   glow. If lum barely changes between noon and midnight, switch `DayFactor`'s input to the fog
   colour's luminance (also plain Unity state). `DayFloorLuminance` / `DaySlopeLuminance` in
   `DriftLineMath` are the only two knobs.
7. **Cost — the acceptance number.** Line 6 at full current in a race during a wind change:
   record the EMA the way `ALONG-RATIO` was recorded for drift. Prior: ≈ 0.25 ms at pool 160.
   If the auto-degrade fires (`pool 160 → 80` in the log), that IS the measurement; fallbacks in
   order: stride 3, default pool 128.
8. **Zone crossings.** Sail 1 km at full sail across several zone lines with `VerboseLogging`.
   Expect a small non-zero `retired: no-volume` count and NO latch line. A latch here means the
   cache rebuild trigger is wrong, not the null guard.
9. **Storm.** Vanilla storm first: `sea state` past 1.5 m, `chop fade` toward 0.5, streaks ~30%
   longer. Then task 3's protocol with RW on the client: surge x1.6 in `wake here`, accepted %
   and mean length up versus the calm reading.
10. **The gameplay path is untouched.** `wake drift` prints identical numbers with
    `EnableDriftLines` on and off, and the boot line still reads `Harmony patched 3`.
11. **The owner's eye.** From the deck: "would you mistake this for an overlay?" Levers in order
    if yes: `BearingJitterDegrees` 16 → 28, more 2s and 3s in `ClusterSize`, wider life spread,
    lower pool with longer streaks, more grain in `StreakAlpha`. A judgement, not a number.

**Known, by design:** inland lakes deeper than `DriftLineMinDepth` with a non-zero field get a
few streaks — the visual shows where the water runs, and drift already acts there. Raise the
depth if it offends; never add a lake detector. Two players on one deck see different individual
foam; the field, density and set agree.

### Results, 2026-09-18 — Storm10 (local dedicated server), Valheim 1.0.15 both sides

**Setup.** Storm10 updated from 1.0.12 to 1.0.15 for the run (the client had updated that
morning; `ErrorVersion` on the first join). Client: Gale profile `testing` — Undertow 0.7.0,
Valkyrie's Cargo 0.1.4 and Yggdrasil's Reckoning 0.1.3 (both ServerSync-pinned on the server,
added to the profile at the server's exact bytes), FireFront 0.21.2, devcommands, Configuration
Manager. **No Ragnarok's Wrath on the client**, so surge was x1.00 throughout. Client config:
`VerboseLogging = true`, `TideAmplitude = 0.499`, `MaxCurrentSpeed` 1.95 for the first session
and **1.2** (rewritten at 07:53) for the readout below. Drift lines at defaults.

| Step | Result |
|---|---|
| 1 ran | `armed on this client (Direct3D11)` → `emitter built — shader 'Sprites/Default' (manual fog), pool 160, radius 60 m, texture 128x32, queue 3100` → `first streak afloat at (-328, 37) — 0.5 m/s bearing 213°, surface 27.97 (flat 30.00)`. **0 exceptions** across three client boots. Server: `Loading [Undertow 0.7.0]`, `Harmony patched 3`, SeaTick authority, **no armed line** — the headless proof. |
| 2 carries the field | Summaries in uniform water: `active 70–95/160 bearing 191–193° vs 192–193° speed 0.55 vs 0.58`. Readout: `mean bearing 191°` vs `field here 0.503 m/s bearing 191° Race`. |
| 3 rides the water | `nearest streak 8.4m: surface 29.65 (flat 30.00, wave -0.35) | vanilla Floating.GetWaterLevel 29.65 (delta 0.000)`. |
| 4 handedness | **MEASURED: `90 − bearing`.** With `−bearing` deployed, lines lay at 102° in 191° water (a quarter turn across); with `90 − bearing`, "long ways along the flow so a line instead of an arrow" (owner). `rot 257° for bearing 191°`. `startSize3D.x` is the length axis. A first "90° off" against `90 − bearing` was a confounded reading in the slack node by spawn — see CLAUDE.md Known traps. |
| 5 sort order | One daylight screenshot: faint foam-white streaks on green water, not blue-tinted, no clipping seen. In the 2.3 m+ ThunderStorm sea nothing was visible at all (row 9), so sort order there is moot — no clipping artefact was seen because no streak was. |
| 6 night | **SEEN 2026-09-21, AND IT WAS A DEFECT.** `tod 0` on Storm10: `ambient lum 0.38 -> day 1.00` — the input never reads as night. Valheim's midnight ambient is moonlit grey, the floor was 0.05 and saturation 0.35, so the foam drew at full daytime brightness all night for three releases while the harness stayed green against a 0–1 range the sky never uses. Noon under the same sky read 0.56, the same as the 18th. The fog colour, recovered by inverting the printed tint, runs **0.18 → 0.53** midnight → noon against ambient's 0.38 → 0.56 — the lever step 6 pre-planned. **Fixed the same day:** `DayFactor` is fed the fog luminance, floor 0.05 → 0.20, slope unchanged; the four readings are named constants in `DriftLineMath` and the harness's fixture (444 → 447), and the new midnight assertion was proven to fail against the old floor (`0.4333`). `wake lines` now prints `fog lum … -> day … (ambient lum …)`. **SEEN 2026-09-21 on the fixed build, and the eye overruled the numbers** (task 11 item 4): at `tod 0`, `fog lum 0.15 -> day 0.00 (ambient lum 0.37)`, tint `(0.07, 0.07, 0.08)` — "too dim to find". The night level became the per-machine dial `DriftLineNightFloor`, slid live at midnight: 0.35 too dim, 1.0 the accidental look, **0.7 ships**. Fixture 454 by then, 459 at 1.0. |
| 7 cost | **0.37 ms EMA at 160/160 active** (0.34 last frame, 81 surface reads, 0 field evals — memo 24 cells), budget 0.50. Prior was 0.25. Auto-degrade never fired. The first cost summary after build read 2.66 ms with nothing active — a single-frame EMA seed, fixed the same day (EMA now rises from zero and no verdict is taken for 60 warm-up frames). |
| 8 zone crossings | `retired: no-volume 0, reflected 1` over a swimming session; no latch. ~~The 1 km sail is still owed.~~ **Struck from the 1.0 ladder 2026-09-21:** no README sentence rests on it ("never networked or saved" is by construction), and every session since 2026-09-18 has crossed zones under sail with no exception and no latch. Still worth one deliberate look; task 11 "Owed" carries it. |
| 9 storm | **SEEN, 08:13–08:19.** RW 0.27.0 on both sides (server un-parked for it; the two ServerSync-pinned mods parked instead, at the owner's direction). `event ragnarokswrath_devastating_storm` from the client console → server `Random event set` and RW `storm began — sky is 'Rain'` at (-2286, 2091) → client `STORM at (-2286, 2091) — IsStormAt(centre)=True, surge x1.6 \| at centre: 0.273 m/s Drift \| 800m away: 0.151 m/s surge x1`. Console, before → under surge (owner's paste): `wake here` 0.173 m/s ESE Drift → **0.265 m/s, `STORM SURGE x1.6 — the sea is up here`**; `wake lines` active 34 → **82**, spawned per 10 s 38 → **73**, mean length 2.4 → **2.8 m**, mean speed 0.21 → 0.30, cost 0.08 → 0.14 ms EMA; nearest-streak delta against vanilla 0.000 both times in a 0.7 m sea. Surge reaches the visual through speed alone, as designed. Chop stayed ~0.21 (the storm's sky was 'Rain', not a big sea). Same paste closed the season check: `season summer (Wrath)` on a pure client. **Then forced to ThunderStorm** (08:21 and 08:28; `StormsForceWeather = true`, `StormForcedEnvironment = ThunderStorm` on both sides, both restarted because RW registers the event's forced sky at boot): **`chop 1.00` for the whole storm** — sea state past 2.3 m, the full chop response — mean length 3.1–3.9 m at 0.23–0.38 m/s water, 50–81 active, cost ≤ 0.21 ms, surge x1.6 again at (-2332, 2101). **And the owner saw NO streaks in it** ("they disappeared, but that's fine in a storm") while the pool held 50–81 active: they existed and were unseen — under the rendered mesh on steep crests (the predicted failure: lift is `0.06 + 0.05 × chop` = 0.11 m at chop 1 against a 2 m+ crest) or lost to the ThunderStorm's rain and fog; the log cannot tell which. Accepted by the owner as storm behaviour and NOT chased. The lever, if it is ever wanted: `ChopLiftMetres` 0.05 → 0.2 first, then a chop-scaled opacity floor. RW's own note applies to that sky: ThunderStorm is a WET environment, so a forced storm rains and its lightning is suppressed — the dry storm look is `Eikthyr`. |
| 10 gameplay untouched | Boot line `Harmony patched 3` on both sides. ~~`wake drift` on/off comparison still owed.~~ **Struck from the 1.0 ladder 2026-09-21:** the README's "changes nothing about how a boat or swimmer moves" holds by construction — `Visuals/` adds no patch and writes no gameplay state — and the drift numbers taken with the lines armed (task 11 items 2 and 3, and the baseline that re-confirmed task 6) match the ones taken before the lines existed. The two-minute on/off comparison is still the honest measurement; task 11 "Owed" carries it. |
| 11 owner's eye | "a line instead of an arrow" — the design. Opacity at default read as subtle; the owner did not ask for more. |

**Acceptance at 1.0:** steps 1–4, 6, 7 and 9 met, 5 partial, 8 and 10 struck from the ladder (see
the rows). The pool saturates
at 0.5 m/s with `MaxCurrentSpeed 1.2` (62% acceptance x 1.45 mean cluster x 16 attempts/s ≈
15 streaks/s against a 10 s mean life), so in strong water density is the cap rather than the
speed; whether that is right is a tuning question 1.0 leaves open — the night reading is in (row 6) and the storm reading is in (row 9), and the storm's answer was "invisible", which is a different lever.


## 8. The config migration — BUILT 2026-09-18 (0.7.1), RUN IN-GAME THE SAME DAY

> **CLOSED 2026-09-19 for 0.7.2.** Steps 1–6 are MET, on the shipped binary, on both roles.
> The zip's own DLL was hash-matched into place before booting; the server config was aged to
> `ConfigVersion = 0` with `FlotsamTtlSeconds` moved to 1500 first, so step 6's round trip was
> proved rather than assumed — the migration line appeared, the stamp returned to 1, and the
> edited value survived (`TTL 1500s` in the flotsam line). The client logged every boot line
> with the drift-line emitter built, and `wake status` read `config layout v1` on screen, which
> is the only place that string ever appears. Zero exceptions either side.
>
> **Step 7 is still owed and does not block:** no destructive rung has run anywhere, because
> none exists yet. So is one observation — a client has never been SEEN to migrate, its config
> having already been stamped.
>
> The earlier note follows. **Superseded 2026-09-19.** Steps 1–4 of "What is owed" below are MET, from the logs
> the 2026-09-18 deployment left behind: the migration's own INFO line on a dedicated
> server, a config file now carrying `[0 - Meta]` / `ConfigVersion = 1` with every other
> value intact, no `.v0.bak` (correct for a stamp-only plan), and nine later boots with no
> migration line at all. Steps 5–7 are still owed: the `wake status` reading, the hand-rolled
> round trip, and watching a real destructive rung — which cannot happen until one exists.
>
> The binary that produced those lines was built at 10:30 against the 1.0.12 references.
> 0.7.2 is the build that ships, so the boot has to be repeated on it before upload.

**Why it exists before it is needed.** BepInEx merges a new key into an existing file at its
SHIPPED default, and a shipped default is a statement about a NEW world. When it differs from what
an existing world already does, an owner who changed nothing gets different behaviour with no error
and nothing in the log — this codebase's named enemy arriving through the front door. The ladder
belongs in place before the day a default has to move, because that day is the worst possible time
to write it.

**What shipped.** The family's shape (Wu'barrk's from Wings of the Valkyrie, by way of Valkyrie's
Cargo, matching the ports that landed in Ragnarok's Wrath and FireFront the same week):

- `Core/ConfigLedger.cs` — PURE, no BepInEx and no Unity, so the harness compiles it. Holds the
  DECISIONS: `ParseIni`, `ReadVersion`, `Plan`, `Describe`, and three step kinds — REBASE (present,
  still an old shipped default, so it moves), BACKFILL (absent, given a legacy value that preserves
  how that world behaved), RETIRE (present, bound by nothing, dropped).
- `Config/ConfigMigration.cs` — the engine. `Begin` before the first bind, `Finish` after the last,
  a `.vN.bak` beside the file before anything destructive, and a failed migration never stops the
  mod loading.
- `ModConfig.ConfigVersion` under `[0 - Meta]`, bound LAST and stamped by `Finish`.
- `wake status` prints the layout version, and the boot line when this boot migrated anything.

**Three decisions worth keeping, each of which differs from at least one sibling.**

1. **The backup gate is a property of the PLAN, not a house opinion.** FireFront blocks the version
   stamp when a backup fails; Ragnarok's Wrath proceeds. Both are right about their own plan —
   FireFront's retires a key, RW's only backfills. Here it is
   `MigrationPlan.IsDestructive => ResetToDefault.Count > 0 || Retired.Count > 0`, so Undertow
   becomes strict automatically the first time a destructive rung is added, rather than when
   somebody remembers to make it strict.
2. **The stamp lives in `0 - Meta`, not `Meta`.** BepInEx's `ConfigFile.Save` orders sections by
   name, so "Meta" sorts BELOW "1 - Core" and lands at the bottom of the file. RW's comment asserts
   the opposite and is wrong, harmlessly. Numbered also matches Undertow's own sections. This is a
   migration slot string forever, so it is not something to tidy later.
3. **`ConfigVersion` is bound with NO `AcceptableValueRange`.** BepInEx clamps an out-of-range value
   SILENTLY, so a ceiling here would one day refuse the stamp and turn the migration into something
   that re-applies on every boot. A stamp is not a dial.

**And one improvement on both siblings.** `ConfigEntryBase.SetSerializedValue` catches every
exception itself, logs a BepInEx warning and leaves the value untouched — so a mistyped ledger value
is a silent no-op followed by a confident stamp, which makes it permanent. `ApplyBackfill` reads the
value BACK and names the slot when nothing moved. The try/catch both siblings wrap around that call
is unreachable code.

**All three tables are EMPTY, and that is a measurement.** Undertow's config history is append-only
across its whole life. Three independent records agree: the source at all four commits that ever
touched `ModConfig.cs` (14 -> 25 -> 27 -> 33 binds, nothing ever removed), the shipped 0.5.1 and
0.6.0 DLLs' own string tables, and nine real `com.raveniron.undertow.cfg` files on the owner's
machines spanning 0.5.1, 0.6.0 and 0.7.0. No default moved, no key was renamed or retyped, no
section string changed, no range narrowed — and of the 255 stored values across those nine files,
none sits outside a current range, so nothing can be silently clamped on upgrade. Because
CHANGELOG.md records 0.5.1 as the first public release, the retire surface is CLOSED rather than
merely unobserved: no stranger holds a key this repo never published.

**`EnableDriftLines` was the one judgement call, and the answer is no backfill.** It is the only key
0.7.0 added to an existing section. Arguments both ways were written out; against backfilling: it is
client-side cosmetics that touch no world state, it is double-gated off on a dedicated server (five
of the nine real files are server-side, where the key does nothing at all), it is the advertised
feature of the release an owner chose to install, and defaulting it off would quietly overturn the
locked "visible current — diegetic only" row. If the owner ever wants the opposite, that single key
is the lever, not the tuning section.

**Harness 248 -> 357**, and every new assertion proven to fail without its fix — 28 mutations applied
to the SHIPPING source, 28 caught. Two gaps were found that way and closed:

- The ParseIni comment test was VACUOUS. Its fixture's comments contained no `=`, so they were
  skipped by the no-`=` rule whether or not the `#` check existed. The fixture now contains
  `# TickBudgetMs = 999` — a setting an admin commented out, which is the comment that matters and
  the one whose mishandling would silently re-enable a value they turned off.
- `LastSummary` was cleared by `Finish`'s own reset, so the `wake status` line could never have
  shown anything and the assertion about it could never have failed. `Reset()` no longer touches it;
  only `Begin` clears it, at the start of the next attempt.

### What the adversarial review changed, after the first version passed its own tests

Five independent reviewers went over the finished code and raised 25 findings; each was then put to
three refuters told to default to "refuted". **22 survived**, most unanimously, and they were not
style notes — six were real defects in code that was already green on 332 assertions. Every one is
fixed and every fix is pinned by a mutation.

1. **The snapshot's comparer disagreed with BepInEx.** `ParseIni` keyed OrdinalIgnoreCase; BepInEx's
   `ConfigDefinition.Equals` is `string.Equals(Key, other.Key) && string.Equals(Section,
   other.Section)` — the two-argument overload, ORDINAL and case-SENSITIVE, over a case-sensitive
   `GetHashCode`. Confirmed by decompile. So `enabledriftlines` and `EnableDriftLines` are two
   different keys to BepInEx: one binds, the other is an orphan. An ignore-case snapshot answers
   "present" for a key BepInEx considers absent, which silently cancels the one step whose entire
   safety is the absence test — and the stamp then makes that permanent. Now Ordinal.
   **The test stub had the same bug, with a comment asserting it "matched the real one".** It did
   not, so every Apply assertion would have passed for a reason that does not hold on a server.
2. **The stamp could go DOWN.** `Finish` assigned `CurrentVersion` unconditionally, and a file
   stamped by a NEWER build reaches `Finish` through the AlreadyCurrent path. Roll a mod back for an
   afternoon and the stamp is dragged to 1; roll forward and the newer rungs replay against values
   the owner has since chosen — and a rebase cannot tell a deliberate choice from the old default it
   happens to equal. The stamp is now a high-water mark.
3. **A retirement could delete a LIVE setting.** The retire loop had no guard, relying on
   `Bind`'s cast to throw. That only protects types that differ: BepInEx returns the EXISTING entry
   for an already-bound definition, so the three string keys (`FlotsamCommon`, `FlotsamRare`,
   `FlotsamWreckage`) — the ones an owner is most likely to have curated — would have been bound and
   removed in silence. The harness "proved" survivability only against a bool. Now refused by name.
4. **The backfill read-back could not see a CLAMP.** `ConfigEntry<T>`'s setter runs `ClampValue`
   against the entry's range rather than refusing, so an out-of-range legacy value parses fine, lands
   clamped, and MOVES the entry — satisfying a did-it-move check. It now compares what landed against
   what was asked for, numerically for numbers.
5. **A negative stamp was a loop bound.** `ConfigVersion` carries no range on purpose, so
   `-2000000000` was reachable by hand — and the version window ran from there. Measured at **17.8
   seconds** on the boot thread under the mutation. Clamped at 0, and the test times it, because the
   right answer arrived at slowly is still a frozen boot.
6. **Two rungs naming one key judged it twice** against the same unchanged snapshot. First match now
   owns the slot.

Three prose claims were also wrong and are corrected: the trailing `cfg.Save()` is not "the only
thing that writes" (Bind saves on every new entry; what it does NOT save is a no-op assignment or a
`Remove`, which is what a retirement is), the `wake status` comment misdescribed when the summary
appears, and the stub's case-sensitivity comment is the one quoted above.

**THE MUTATION SUITE ITSELF LIED FIRST, AND THAT IS THE LESSON.** Its first run scored 20/20 without
compiling a single line: it invoked `run-tests.ps1` through `powershell -NoProfile -Command`, this
machine's execution policy refused the file, and the refusal exits non-zero — which the suite read as
"the test caught it" every time. A confident, well-formed, wrong instrument, produced by the tool
built to detect exactly that. Any script that judges the harness by its exit code must pass
`-ExecutionPolicy Bypass` AND assert that the harness actually ran; the suite now refuses to score a
run whose output does not contain the harness banner.

### What is owed: the in-game run

Definition of done in this repo is one observed boot, and a FRESH install proves nothing here — it
migrates nothing and logs nothing. The run has to be against an existing file.

1. ~~Deploy 0.7.1 to Storm10 and to the `testing` profile. Both already carry a real 0.7.0 config
   with no `[0 - Meta]` section.~~ **DONE on 2026-09-18, historical.** Re-read as: deploy the build
   that is about to ship (0.7.2) to both, against a config that still has no `[0 - Meta]`.
2. Boot the server. Expect exactly one line:
   `config: version 0 -> 1: nothing to migrate (stamping the layout version)` — at INFO, not a
   warning, because nothing changed.
3. Read the file back. Expect a `[0 - Meta]` section at the TOP with `ConfigVersion = 1`, every other
   value byte-identical to before, and NO `.v0.bak` beside it (a stamp-only plan writes none).
4. Boot again. Expect NO migration line at all: the file now reads as current and `Begin`
   short-circuits.
5. On the client, `wake status` should end with `config layout v1 (nothing migrated this boot)`.
6. The one that needs setting up: put `ConfigVersion = 0` back by hand with one value edited away
   from its default, boot, and confirm the edited value survives and the line reappears. That proves
   the round trip rather than the happy path.
7. **A destructive rung has never run anywhere.** The apply path is exercised in the harness against
   synthetic plans, which is why `ConfigMigration.Apply` and `Backup` are `internal` rather than
   private — but the first REAL rebase or retirement should be watched in-game on a copied config
   before it ships.

## 9. The config sync — BUILT 2026-09-19, RUN IN-GAME THE SAME DAY, ADMIN PUSH CLOSED

The owner's call, taken from four options on 2026-09-19: **"Server wins, and admins can push."**

### The problem, and why it was invisible

`CurrentField` is a pure function of seed, position, world time and season. That is the whole
no-sync architecture: every machine computes the same water from facts they all already hold, and
task 1 proved it by getting a byte-identical transect out of a server and a client independently.

The argument has a premise nobody had checked: **that both ends hold the same TUNING.** A server
that raised `MaxCurrentSpeed` and a client that did not are evaluating two different oceans from
one seed. Drift is applied by the peer that OWNS each hull, so those two players genuinely sail
different seas — and nothing desyncs, nothing errors, no ZDO disagrees, and no log line says so.
It is the exact failure shape this repo's debugging discipline is written against.

### What was built

- `Core/ConfigWire.cs` — PURE, compiled by the harness: WHICH keys travel and WHAT the payload
  looks like. Twelve keys — the five field terms, `EnableWrathBridge` (a field term in disguise:
  it gates both the season and the surge), and the drift and swimmer groups.
- `Net/ConfigSync.cs` — the engine: three routed RPCs (publish to everybody, an admin's push up,
  a joiner's request), the admin gate, and the override layer.
- The read path is `entry.Live()` rather than `entry.Value` at 23 call sites. A one-bool
  short-circuit means a server, a single-player session and any client before the first payload
  pay a `bool` test rather than a dictionary hash.
- Driven from `SeaTick` (house rule 2), above the authority gate, because the two directions need
  different machines. **No new Harmony patch** — the boot line still reads `Harmony patched 3`.

Harness **414 passed** (369 → 414), and **16 mutations applied to the shipping `ConfigWire.cs`,
16 caught**, file restored byte-identical. The two that matter most are the ones a single-machine
run cannot see: a key on the wire that ModConfig does not bind under exactly that name (checked
against a real `ModConfig.Bind`, ordinal and case-sensitive), and a float written or read in the
machine's own culture.

### RUN IN-GAME 2026-09-19 — Storm10 (dedicated) + the `testing` Gale profile, both on 1.0.15

Deployed from `bin\Release`, hash-matched repo → server → client (`EC9FE5B7…`) BEFORE either
booted. The two ends were deliberately set APART first — server `MaxCurrentSpeed = 2.4`,
`SwimmerDriftFactor = 0.9`, client left at 1.2 / 0.5 — because a run where both ends already
agree cannot tell adoption from a no-op, which is the vacuous-pass trap this file keeps naming.

**The handshake, both halves.** Server: `ConfigSync: RPCs registered (undertow-cfg/1).` then
`published (peer list changed) — 12 value(s): MaxCurrentSpeed=2.4, TidePeriodSeconds=3600,
TideAmplitude=0.25 and 9 more`, followed by 30 s heartbeats. Client:
`ConfigSync: the server's sea is in force — MaxCurrentSpeed 1.2 -> 2.4, TideAmplitude 0.4988263
-> 0.25, SwimmerDriftFactor 0.5 -> 0.9.` **Zero exceptions on either side**, and no
`ConfigSync:` error about an unbound wire key — which is the first live confirmation that all
twelve rows in `SyncedKeys` resolve to real entries under exactly those names.

**IT FOUND A REAL DIVERGENCE ON ITS FIRST RUN, AND NOBODY PUT IT THERE FOR THE TEST.** Only two
keys were set apart; a THIRD moved. The client's `TideAmplitude` was **0.4988263**, not the
shipped 0.25 — a config-manager slider dragged at some forgotten point. That profile had been
sailing a measurably different tide from the server, silently, and the only reason anybody knows
now is that this feature printed it. That is the whole argument for the feature, observed rather
than reasoned about.

**The override reaches GAMEPLAY, not merely a dictionary.** The verbose swimmer lines settle it
arithmetically, so this needed no sailing:

```
water 0.204  drift 0.184  ratio 0.9020
water 0.203  drift 0.183  ratio 0.9015
water 0.200  drift 0.180  ratio 0.9000
```

Ratio 0.90 is the SERVER's `SwimmerDriftFactor`. On the client's own 0.5 the drift would read
0.102, not 0.184. (`cap 0.7` also checks out: swimSpeed 2 × `SwimmerMaxShareOfSwimSpeed` 0.35.)

**The client's config file was NOT written, measured rather than assumed.** Hashed before joining
and again mid-session. The hash DID change, which looked at first like the design's central
promise failing — the diff is one line, `VerboseLogging false → true`, a key deliberately NOT on
the wire and changed by the owner in ConfigurationManager. All three synced keys still hold the
client's own values on disk (1.2 / 0.4988263 / 0.5) while the running game uses the server's.
Worth keeping: the hash alone would have read as a failure, and only the diff said otherwise.

**`wake status` on the client read `sailing the server's sea — 12 value(s)`** — the one piece of
evidence that can never reach a log, because the console writes through `Terminal.AddString`.

**The disconnect path is CLOSED, on two independent instruments.** After a log-out to the main
menu the client logged `ConfigSync: disconnected — local config back in force.` and `wake status`
read `config sync: on your own values (left the server — back on local values)`. The parenthesis
is the load-bearing half: that string is `LastEvent`, assigned ONLY inside `Reset()`'s non-empty
branch, so it proves the table really held values and was really cleared — a bare "on your own
values" would also be what a `Reset()` that never ran on an empty table produced.

That path is not cosmetic, and here is the one case where it would have bitten. Joining a
DIFFERENT server self-heals, because the new server publishes all twelve keys and overwrites
every override. Going from a server to SINGLE PLAYER does not: single player is
`IsServer() == true`, so `OnPublished` stands down by design and nothing would ever overwrite a
stale table — `_any` would still be true and `Live()` would hand the player the last server's sea
in their own world.

**Two instrument failures were hit getting that one line, and neither was a bug in the mod.**
Both are the "audit the instrument" rule, and both will happen again:

  1. **Quitting to desktop does not test it.** `Reset()` runs from `SeaTick.Update`'s
     `ZNet.instance == null` branch, so the PROCESS must outlive the disconnection. Quit and it
     dies first — and BepInEx truncates `LogOutput.log` on the relaunch, taking the evidence with
     it. Log out to the MENU. (Snapshot the log before a relaunch either way.)
  2. **BepInEx's disk log lags, and a grep run too soon reads as a missing line.** The second
     attempt DID log correctly; the file was 24 KB when first read and 51 KB a moment later, with
     the line in the gap. A defect was nearly filed against correct code on the strength of an
     absence. An absent log line is evidence only after the writer has caught up — confirm with a
     second read, or with `wake status`, which goes through `Terminal.AddString` and never touches
     the log file at all.

**THE ADMIN PUSH IS CLOSED — verified in game 2026-09-19, and it took a real fix to get there.**

The first attempt failed at the CLIENT gate, not the server's. Undertow refused to send because
`ZNet.LocalPlayerIsAdminOrHost()` said the owner was not an admin — while they were in
`adminlist.txt` in all three forms (bare numeric, `V_`, `Steam_`). That accessor falls through to
`PlayerIsAdmin(UserInfo.GetLocalUser().UserId)`, a single `adminList.Contains(userId.ToString())`,
and under `-crossplay` the local identity is a PlayFab one (the server's own handshake logs
`playfab/8BF4F5368AF43770`) while the list holds Steam ids. Vanilla survives this because the
method only drives cosmetic client-side UI hints.

**This file had already recorded that gate as an acceptable "known limit" that fails closed. It
does fail closed — on a legitimate admin, silently, with the feature unusable.** That is a defect,
not a limit, and the note was wrong. The fix was to stop the client deciding at all: it sends
optimistically and the server judges with `ZNet.IsAdmin(hostName)`, vanilla's own authoritative
check, which matched the same player instantly. The server then ACKNOWLEDGES every push over a
one-to-one RPC so an optimistic send can never become a silent no-op.

Measured, both legs, on Storm10 against a real client:

```
CLIENT  ConfigSync: the server accepted it and told every client.
SERVER  ConfigSync: admin (peer 739844175) set DriftStrength 3.169014 -> 4.
        ConfigSync: published (an admin changed a value) — 12 value(s)
DISK    DriftStrength = 4
```

The last line is the one that matters: an admin's change reaches the server's own config file and
survives a restart, rather than living only in memory.

~~**STILL OWED:** two Undertow versions meeting across the wire~~ — **CLOSED 2026-09-21.** The
onshore fix bumped the header to `undertow-cfg/2`, and the server was restarted on it while the
client still ran the `/1` build. Client log: `ConfigSync: RPCs registered (undertow-cfg/1)`,
`asked the server for its config`, then `ConfigSync: the server's config payload is not
'undertow-cfg/1'. Sailing on local values — check that both ends run the same Undertow.` Console:
`config sync: on your own values (refused a payload this build cannot read)`. Then the client was
updated to `/2` and rejoined: `RPCs registered (undertow-cfg/2)` and `wake status` read `sailing
the server's sea — 12 value(s) in force over your own`. Both halves, both instruments. **One
defect found by watching it:** the refusal was logged on every publish — twice at join and then
on every 30 s heartbeat, five times inside two minutes — which for a mismatched pair is 120
warnings an hour about a condition that cannot change until somebody updates. Now latched once
per session (`_refusalLogged`, cleared in `Reset()`); `LastEvent` is still set every time so
`wake status` stays honest.

**KNOWN, NOT YET FIXED — the slider storm.** ConfigurationManager raises `SettingChanged` on every
increment of a drag, so one gesture sent FOUR pushes (1.56 → 2.06 → 2.52 → 3.17 → 4) and the server
answered each with a full twelve-value broadcast to every client. Four round trips for one human
action. Harmless with a single player and it scales with the lobby, so it wants a ~0.5 s debounce
holding only the last value per key. Predicted before the test and confirmed by it.

One incidental correction this run produced: the client logged `CurrentField live — … season
index 1 (read from Wrath)`. Task 7 and CLAUDE.md state that line will "always say 0 on a client"
because it is logged before the season RPC arrives. It is a RACE, not a guarantee, and it can
land the other way.

### The protocol (written before the run above; steps 8 and 9 are still owed)

**Two machines are the minimum** — this feature is
unobservable on one, because a listen host stands down on `IsServer()` by design.

1. Deploy to Storm10 and to the `testing` profile. **Deliberately set them apart first**: put
   `MaxCurrentSpeed` to something obvious on the SERVER (say 2.0) and leave the client at its
   default. A run where both ends already agree cannot tell adoption from a no-op.
2. Boot the server. Expect `ConfigSync: RPCs registered (undertow-cfg/1).` and, with
   `VerboseLogging` on, a `published (…)` line once the client joins.
3. Boot the client and join. Expect `ConfigSync: RPCs registered`, then
   `ConfigSync: the server's sea is in force — MaxCurrentSpeed 1.2 -> 2` (the exact numbers
   depending on step 1).
4. `wake status` on the client: `config sync: sailing the server's sea — 12 value(s) in force
   over your own`. On the server: `this machine is the source — 12 value(s) published`.
5. `wake here` on the client should now report water faster than its own config allows — that is
   the proof the override reaches `CurrentField` and not merely a dictionary.
6. **The file is never written.** Hash the client's `com.raveniron.undertow.cfg` before joining
   and after leaving. It must be byte-identical, and `MaxCurrentSpeed` must still read the
   client's own value. This is the promise the whole design is built around; measure it, do not
   assume it.
7. Disconnect to the main menu. Expect `ConfigSync: disconnected — local config back in force.`
   and `wake status` back to "on your own values".
8. **The admin push, which is the half with no fallback.** With the client's user in the server's
   `adminlist.txt`, change a synced value through a config manager on the client. Expect
   `ConfigSync: pushed …` on the client, `ConfigSync: admin (peer N) set …` on the server, and
   the server's own config file to carry the new value afterwards. Then take that user OUT of the
   admin list and repeat: expect the client to log the "you are not an admin" line and the
   server's file to be unchanged.
9. **The bare-numeric admin id.** Write the admin list with the numeric form only and confirm a
   push is still accepted — that is the case `PlayerIsAdmin` would have refused and the reason
   `SenderIsAdmin` exists. (The SEND side still uses vanilla's public
   `LocalPlayerIsAdminOrHost()`, which is the stricter one, so this may need the full form on the
   client's side to fire at all. If it does, that asymmetry is worth writing down here — it is a
   real limit, not a bug to chase.)

### Not covered, and known

- **A client with a DIFFERENT Undertow version.** The wire is forward-compatible by construction
  (unknown keys are dropped per line, a wrong header refuses the whole payload) and both paths
  are under test — and two versions have now met (2026-09-21, above): a `/1` client on a `/2`
  server refused the payload, logged it once, and sailed its own tuning.
- **Ordering against other ServerSync-style mods.** Undertow's sync is its own; it does not use
  ServerSync and does not contend with one.

## 10. Foam in all moving water (0.8.0) — BUILT 2026-09-19, SERVER LEG RUN THE SAME DAY

The owner's instruction, verbatim: *"i need the drift lines to show in all water"*, after two
sessions of never seeing foam anywhere they stood.

### Why they saw nothing, which took two readings to establish and neither was a bug

1. **(-3200, 3400): `rejected slack 145` of 160.** Water 0.091 m/s against a slack threshold of
   `0.12 × MaxCurrentSpeed` = 0.144. Working exactly as designed — they were parked in a slack
   pocket that a scan later showed to be **over 1.2 km across**, which the CLAUDE.md note calling
   slack "rare" had not anticipated (that note was measured near the origin).
2. **(-3287, 3954): `rejected shallow 63`.** 0.665 m/s of genuine **Race** at 5.1 m depth against
   `DriftLineMinDepth` = 10. This one IS a design fault: Valheim's open ocean is a flat 30 m, so
   every race, strait, shelf and coastal set is shallower than that by definition. The headline
   feature — "fast water between islands" — shipped gated out of its own habitat.

A third cause was mine: setting the server's `MaxCurrentSpeed` to 2.4 for the sync test doubled
the slack threshold to 0.288 and suppressed the foam everywhere. Recorded as a trap in CLAUDE.md.

### What changed

- **`DriftLineMinDepth` 10 → 2.** Not a round number chosen by feel: acceptance ramps over the
  6 m above the floor (`DepthRampMetres`), so at 2 it finishes at depth 8 — exactly where
  `CurrentField`'s own `ShallowFadeDepth` finishes. Two unrelated thresholds became one curve, and
  the harness pins `minDepth + DepthRampMetres == ShallowFadeDepth` so a future re-tune has to
  re-derive it rather than break it quietly.
- **The slack cliff became a floor.** `SpawnWeight` now ramps from 0 at dead-still water up to
  `DriftLineSlackFloor` (new key, default 0.08) below the threshold, then resumes the old ramp
  from that floor. Continuous at the threshold, so there is no visible edge on the water. **At and
  above 60% of MaxCurrentSpeed nothing changed at all.**
- **`ConfigLedger` version 2**, one rebase row — the first rung this ledger has ever had.

Harness **414 → 441**; 14 mutations against the shipping source, 14 caught. One MISS was found and
fixed in the process: the "shipped default" assertion used a literal `2f` rather than reading
`ModConfig.DriftLineMinDepth.DefaultValue`, so reverting the default to 10 left the harness green —
a decorative assertion, which is exactly what the mutation suite exists to expose. A fifteenth
mutation was identified as an **equivalent mutant** (a symbolic constant swapped for a numerically
identical literal) and removed rather than chased: no runtime assertion can distinguish them, and
contorting one to try would be theatre.

### What was observed in game, and what was not

✅ **The first destructive migration rung, on Storm10.** Against a real config holding
`ConfigVersion = 1` and `DriftLineMinDepth = 10`:
`config: version 1 -> 2: 1 value(s) moved to their new defaults: 7 - Drift lines.DriftLineMinDepth
(your previous config is backed up beside it, .v1.bak)` — at WARNING, naming the key, and the
`.v1.bak` verified **byte-identical** to the pre-migration file with `cmp`. Afterwards the file
reads `ConfigVersion = 2`, `DriftLineMinDepth = 2`, `DriftLineSlackFloor = 0.08`.

✅ **(a) CLOSED 2026-09-19 — the protective branch, on the client.** The `testing` profile held a
hand-set `DriftLineMinDepth = 2`, not the old shipped 10, so its 0.8.0 boot ran exactly the guard
branch and logged `config: version 1 -> 2: 1 kept as yours: 7 - Drift lines.DriftLineMinDepth=2
(your previous config is backed up beside it, .v1.bak)`. Kept, named, not reset — and a `.v1.bak`
written anyway, because the backup gate keys on the PLAN being destructive rather than on the
outcome. That is the conservative side to err on, and it also means a `.bak` beside a file is
not evidence that anything in it moved. This was also the first time a client was ever SEEN to
migrate at all (task 8's other open observation), so both close together.

✅ **(b) CLOSED 2026-09-21 — "lying on the water", the owner's words, in Drift water near
(-700, -1050) at the shipped `DriftLineLiftMetres = 0.02` and default opacity.** The paragraph
below is how it stood the day before. Foam was seen the same day at (-3044, 3728) — `wake lines` reading
`active 23`, `min depth 2m`, in 2.3 m water where 0.7 drew nothing — and the owner's next report
was that it **floated above the surface**. The hover was uniform, so the lift was rebuilt as one
`DriftLineLiftMetres` key (default 0.02 m, replacing `BaseLiftMetres` 0.06 + `ChopLiftMetres` 0.05
× chop) and deployed. **Nobody has looked at the water since.** The owner's "much better" came from
`DriftLineOpacity` 1 → 2, before the lift change was on the client. So: foam in formerly-bare water
is seen; the two originally reported coordinates were not revisited; and whether 2 cm sits the foam
ON the water is the one 0.8.0 change that is built, deployed and unobserved. (c) `wake lines` cost
at a saturated pool and (d) the inland-lake look are still owed as written above.

## 11. The road to 1.0 — LADDER WRITTEN 2026-09-21, CLIMBED THE SAME DAY

Tasks 0–10 are built, and every one has been run in game at least once. So 1.0 is not a feature.
What it is here: **the point at which every sentence in `README.md` has been watched happen on the
shipping game, and the config layout becomes a promise.** The second half is the one with a
cost attached — after 1.0, every default that moves is a `ConfigLedger` rung on a stranger's file
with a `.vN.bak` beside it, and 0.8.0 has just shown what that looks like. Before 1.0 a retune is
a number in `ModConfig.cs`; after it, a migration.

What follows is the README's claims that rest on the harness alone, or on a measurement taken
before Valheim 1.0, sorted by what happens to a server owner if the claim turns out wrong. Two
rungs: **0.9 carries the two server-side risks, 1.0 the sightings, one decision and the docs.**
Nothing on the ladder is new code. Each item is a session in the water with `VerboseLogging` on
and a protocol that already exists in this file, named rather than repeated. The one thing every
item shares: a claim the harness already proves, which is precisely why it has been easy to leave
unobserved.

### 0.9 — the server-side risks

**1. Flotsam on the shipping game, and the ZDO count over hours.** Last seen floating on
2026-08-28, on 0.5.1, before Valheim 1.0. Since then only the boot-time prefab scan has re-run
(task 6), and that on a client, which is not the role that spawns. The README promises "capped,
reclaimed on a timer, and spawned only near a real player" — and that exact safety valve was once
silently dead while every spawn logged `[1/12 alive]` and the log looked healthy (task 4). Task 4's
acceptance — "on a long-running world the per-zone cap holds and the ZDO count is stable across
several hours — measure it, do not assume it" — has never been taken by anyone.
*Where:* Storm10, at shipped defaults — reverted 2026-09-21 (`FlotsamTtlSeconds` 1500 → 1800 from
the 0.7.2 round trip, `DriftStrength` 4 → 1 and `VerboseLogging` off from the sync test; the
pre-revert file is beside it as `.pre-1.0-revert-20260921`), because a run at test values is a
run at nobody's settings. **With ONE exception, and the arithmetic says why:** `FlotsamPerHour`
6 → 60 for the run only. At 6/hour against an 1800 s TTL the steady state is `6 × 0.5 = 3` alive
per player and the cap of 12 is never reached inside an hour — task 4's "climbs 1→2→3→4→5 and
holds" was that steady state, not the cap binding. At 60 the cap binds in about twelve minutes and
the first reclaim lands at thirty, so both halves of the safety valve run inside one sitting. It
is per-machine and never synced, so the client is untouched. `VerboseLogging` goes back on for the
run, because it gates the instrument.
*Instrument:* **built 2026-09-21, the one code change this rung is allowed.** Every verbose flotsam
line now ends `[n/cap alive, N ZDOs]`, where N is `ZDOMan.NrOfObjects()` — public in the shipping
assembly and `m_objectsByID.Count` by its body, read out of the real `assembly_valheim.dll` rather
than the publicized one (rule 5). And two events that used to happen in silence now log, verbose
only: a TTL reclaim (`reclaimed <uid> after Ns`) and an item that went away on its own
(`<uid> gone (picked up, or removed by the world)`), because a count that drops with no line beside
it is a diagnostic round-trip. Before this, the acceptance below had no instrument at all: the
reclaim — the half of the valve that bounds a long-running server — never printed anything, so
"reclaimed on a timer" was a claim the log could neither confirm nor deny. Deployed to Storm10 and
the `testing` profile as a dev build that still carries the 0.8.0 const; its hash is NOT the
shipped 0.8.0 zip's and is recorded in the session, not the boot line.
*Protocol:* a player parked in slack water for one and a half TTLs (45 min at 1800 s), reading
the summary every ten minutes. Then leave the area for ten more.
*Acceptance:* the cap is reached and held; at least one reclaim is observed by uid, on schedule;
the ZDO total is flat across the second half of the run; production stops when the area empties.
If the alive count only climbs, the dedicated-server ownership bug is back in some new shape and
0.9 waits until it is found. One accepted cost, already in the code's own comment: flotsam alive at
a restart is forgotten and never reclaimed, so at most `FlotsamMaxAlive` items per session outlive
the mod. That is a ceiling, not a leak — confirm it stays one by counting after a restart.

**RUN 2026-09-21 — CLOSED, on the shipping game, with the instrument built the same morning.**
Storm10 at defaults except `FlotsamPerHour 60` and verbose. What the log shows, in order:

- **Spawning works on 1.0.15.** Twelve items in about thirteen minutes, one a minute as 60/hour
  says, from `FirCone at (-600, -220) … [1/12 alive, 162318 ZDOs]` to
  `FirCone at (-622, -1093) … [12/12 alive, 170229 ZDOs]`. The palette drew common, rare
  (`Demister`) and forest debris; no missing prefab, no warning.
- **The cap binds.** Not one spawn line between `12/12` and the first reclaim; the next spawn
  landed at `11/12` immediately after a reclaim freed the slot, and the count then oscillated
  between reclaims and spawns exactly as a rolling cap should. Positive evidence both ways.
- **Reclaims land on the clock.** Fourteen `reclaimed <uid> after 1801s` lines by the end of the
  session — every one at 1801 s against an 1800 s TTL — including items five kilometres from the
  only player, in unloaded zones. `DestroyZDO` needs no instance, only the uid, which is the
  2026-08-28 fix doing its job.
- **The ZDO table moves by exactly the item.** While the owner was still, a spawn read +1
  (`193176 → 193177`) and consecutive reclaims read −1, −1, −1, −1 (`193498 → 193495 → 193494 →
  193493`). While the owner sailed, the total jumped by hundreds to tens of thousands — that is
  the world generating zones and has nothing to do with us, which is why the acceptance says
  "flat while parked". Note the reading is ONE BEHIND on a reclaim line: vanilla's `DestroyZDO`
  only queues the uid (`m_destroySendList`), and the removal from `m_objectsByID` happens when
  the routed `DestroyZDO` RPC comes back round to the server itself — read out of the real
  assembly the same hour. Two equal readings in a row are therefore not a leak; a trend is the
  evidence, and the trend is −1 per reclaim.
- **An empty server produces nothing and still cleans up.** Ten minutes with no peer: **0 spawn
  lines, 4 reclaims**, the table 193,498 → 193,493 and never up. Driftwood does not sit forever
  after everyone leaves, and an idle server does not fill its own world.

Not measured: hours. The run was fifty minutes at ten times the shipped spawn rate, which is
worth about eight hours at defaults in cap and reclaim events, but a real long-running world
also restarts, and a restart forgets whatever was alive (at most `FlotsamMaxAlive` items, by the
code's own comment). Counting after a restart is the one line of this rung still open, and it
is a ceiling of twelve per session, not a growth term.

**2. Njord and Sailing, measured.** Both are on Ravenrest, so every boat number ever taken there
was taken "with" them, and Njord is the one mod that matches the shape `CLAUDE.md`'s boat-mod
warning was written for: a physics overhaul with per-hull caps and no public source. The README
says "boat stat mods should compose", on reasoning only. Tasks 2c and 2d hold the protocols and
the predictions; this rung is running them.
*Where:* the owner's call. Ravenrest is production, and "without" means parking a ServerSync-pinned
mod on both sides for a session — or Storm10 with both mods added for the afternoon, which is the
owner's infrastructure and not to be touched without asking.
*Acceptance, and it is one number per mod:* a karve's settled `ALONG-RATIO` drifting with Njord
and with it parked (2d step 1–2). Vanilla gave 0.86; near that and the saturation claim holds under
Njord's damping; well below and the first answer is `DriftStrength` on that server, never a toggle.
Then Sailing's cleanest prediction — the drifting ratio identical to the second decimal with it on
or off (2c steps 1–2). Each closes a ⚠️ in `CLAUDE.md` and puts a date and a ratio in its place.
**BASELINE TAKEN 2026-09-21 — neither mod, Storm10 at shipped defaults, Valheim 1.0.15.** Karve,
sail down, uniform `Drift` water at (-1200, 2555), depth 30 m, flat sea:
`water 0.238 along 0.236 ALONG-RATIO 0.99 (total 0.99) | dv 0.00005` and, sixteen seconds later,
`water 0.237 along 0.235 ALONG-RATIO 0.99 (total 1.02) | dv 0.00004`. The push had faded to
nothing and the hull sat at the water's speed; total equals along because there was no swell to
throw a light hull across the flow. **0.99 at 0.24 m/s is the number runs 2 and 3 are judged
against, on this water and this hull.** It also re-confirms task 6's slow-water result on the
shipping game, on a different seed.
**RUN 2+3 (DRIFTING), 2026-09-21 — NJORD 2.0.5 AND SAILING 1.1.9 TOGETHER, SAME SPOT, SAME
HULL: `ALONG-RATIO 0.99`, then `1.00 (total 1.00) | dv 0.00001`.** Indistinguishable from the
baseline. Njord's damping — the first non-vanilla damping the saturation claim has ever met — does
not move the equilibrium; and with the sail down Sailing multiplies zero, so one run answers both
drifting predictions (2c prediction 1, 2d point 3). The setup: Storm10's parked `Njord.dll.off`
was byte-identical to the `Ravenrest` profile's copy (`ff0dbde8…`, **2.0.5 — `CLAUDE.md`'s
"1.3.5" was stale**, caps unchanged at 7 / 16.8 / 26 / 30 and every physics key in Storm10's
`wubarrk.njord.cfg` equal to Ravenrest's; only `Debug_*`/`Vendor_*` differ), so it was unparked;
Sailing came from the owner's Gale install of 1.1.9 (`7ea58b72…`), the same bytes on both sides.
Both loaded clean on server and client, zero errors from any mod. The sail-up runs follow.

**SAIL-UP RUNS, SAME SESSION — CLOSED, with one sample honestly not re-taken.**

- **Up-current at Njord's cap (2d step 4): a dozen samples.** Karve at 16.0–16.7 m/s by the log
  (`total × water`), Njord's own readout pinned at **16.8** the whole time by the owner's eye, and
  `dv` reading `water × 0.02` to the fifth decimal on every line — `0.569 → 0.01137`,
  `0.472 → 0.00944`, `0.288 → 0.00576`, `0.326 → 0.00653`, `0.339 → 0.00678` — the full push,
  unclamped, with the hull making 16 m/s of headway against it. **"No judder"** (owner). A
  magnitude cap and an opposing push coexist exactly as 2d point 2 argued from Unity's
  integration order. Njord's readout never sat above its cap while we pushed, so 2d step 5's
  one-tick bound was not contradicted.
- **The saturation formula across the range, incidentally and better than planned.** Every
  crossing sample read `dv = water × 0.02 × (1 − ALONG-RATIO)` exactly: ratio 0.93 → 0.00058 at
  0.4 m/s, 0.8 → 0.00198 at 0.484, 0.5 → 0.00369 at 0.367, 0.2 → 0.00110 at 0.23. Four points on
  the line, under Njord's physics.
- **Down-current at the cap (2d step 3): NOT re-observed under Njord.** The water on that stretch
  runs WNW straight onto a coast, and every attempt at a with-the-current run at 16 m/s ended in
  slack water or on the beach. What stands in for it: the formula above is the same line clamped
  at ratio ≥ 1, the harness pins the clamp, `dv` read *exactly 0* with the hull at the water's
  speed on 2026-09-12 (task 6) and again in every settled drift sample today (`0.00001`), and
  nothing in the clamp reads anything of Njord's — it is a function of the water and the hull's
  velocity, both of which we read ourselves. The risk Njord posed was to the EQUILIBRIUM, and
  the drifting number answers that. Worth taking if a with-the-current stretch of open water
  turns up; not worth another hour of sailing into land.
- **Sailing's nudge (2c step 5): not run.** Optional, and an ordinary impulse on the same
  rigidbody; the drifting prediction (2c prediction 1) was the one that mattered and it held.

*Protocol lesson, written for the next boat:* the water's bearing changes with position, so a
heading taken at one spot is wrong a few hundred metres on, and "toward SE" in `wake here` means
the water is GOING south-east — put the bow on it to run with it. Twenty minutes of this run
were spent sailing round an eddy the wrong way; read `wake here` at the spot, steer that, and
expect it to change.

### 1.0 — the sightings, one decision, and the docs

**3. The drowning guard's clamp branch, seen.** "You can always out-swim the water" is the mod's
single safety property, and its clamp has never executed in game: on 2026-08-28 and 2026-09-12 the
requested drift never came within a factor of seven of the cap, so `SwimDrift.Compute`'s clamp
branch has only ever run in the harness. The 2026-09-19 report "swimming against a current makes
you go backward" was taken under a test-contaminated `SwimmerDriftFactor = 0.9`, not defaults —
but it is exactly the symptom shape, and the owner noticed it inside a minute.
*Protocol:* task 5's, with the factor at the top of its range on the SERVER (it is synced, so one
edit reaches the swimmer), in the fastest water on the map — a race, or a storm over deep sea if RW
obliges. The 3-second `swim drift` line must show `drift` pinned at `cap` (0.7 at swimSpeed 2).
Then swim straight upstream.
*Acceptance:* headway, on the line and on the screen, with the clamp visibly engaged. Then the
factor back to 0.5. If a swimmer at full stamina cannot make headway with the clamp engaged, the
feature is wrong and not the tuning — task 5's own words.
**RUN 2026-09-21, CLOSED.** Storm10, `SwimmerDriftFactor 1.0` and `SwimmerMaxShareOfSwimSpeed 0.2`
pushed from the server (the client adopted them over the wire — the sync's first use as a test
rig). The owner found a race at (3540, 792) running **0.965–1.002 m/s**, so the request was a
full metre per second of drift, and the line read `drift 0.4 (cap 0.4)` — pinned, the clamp
branch of `SwimDrift.Compute` executing on real water for the first time. Floating: `swimmer
0.38 m/s`, 95% of the drift, the same match as 2026-08-28 and 2026-09-12. Swimming: `swimmer
1.577 m/s`, which is `2.0 − 0.4` to the second decimal — upstream, gaining 1.6 m/s on the water
with the clamp engaged. The safety property holds where it was never before exercised.

**4. The drift lines at night.** `Sprites/Default` is unlit. Ragnarok's Wrath 0.7.0 shipped a fog
nobody could see for that reason; the inverse — foam that glows on black water — is the trap
named in `CLAUDE.md`, and the README's "night, fog, distance and a big sea dim it on their own"
has never been looked at. Task 7 row 6 has been "not seen" since 2026-09-18.
*Protocol:* task 7 step 6. `wake lines` at noon, dusk, midnight and in rain; `ambient lum` must
MOVE, and midnight must read as a faint grey smear a shade lighter than the water, never a glow.
*Acceptance:* the owner's eye at midnight, and the four `lum` readings recorded in row 6. The one
lever if it fails is `DayFactor`'s input (fog luminance instead of ambient); the two knobs are
`DayFloorLuminance` and `DaySlopeLuminance` in `DriftLineMath`.
**RUN 2026-09-21 — it failed, exactly the way the lever anticipated, and the lever was pulled.**
Midnight ambient 0.38 → day factor 1.00: no dimming at all. Fog luminance 0.18 → 0.53 across
the same two readings; `DayFactor` now takes the fog, floor 0.20. Task 7 row 6 has the numbers.
*The eye, same session, and it overruled the numbers.* On the fixed build at `tod 0`: `fog lum
0.15 -> day 0.00 (ambient lum 0.37)`, tint `(0.07, 0.07, 0.08)` — and the owner: **"too dim to
find."** The design sentence "a faint grey smear" had been read as "nearly nothing", and nearly
nothing is not what foam does on black water. So the night floor became a per-machine dial,
`DriftLineNightFloor`, slid live at midnight through the config manager: 0.35 too dim, **1.0
fine** (no dimming — the look three releases had shipped by accident), **0.7 "works too"**.
Ships at 0.7, which keeps some night and all of the storm-sky dimming. **CLOSED.** The lesson
for the ladder: a visual acceptance is the owner's eye, and a number that reads 0.00 exactly
where the harness says it should is not a substitute for it.

**5. The tide reversing the coastal stream, seen.** The README's whole Tides section — "reverses
the coastal stream, so the passage you know is a different passage six hours later" — has been
verified in the harness and never on the water. `wake here` has printed tide phases from 23% to
96% across sessions, so the CLOCK is known to move; the REVERSAL has never been read.
*Protocol:* coastal water — inside `ShelfDepth` (28 m), `wake here` reporting `Coastal` — and the
same spot at flood and at ebb. `TidePeriodSeconds` is synced and 3600 by default, so a half-cycle
is 30 minutes; shorten it on the server for the session and the two readings are minutes apart.
Task 1's own lesson applies: compare bearings at ONE point, because the open-water term does not
reverse and a reading from a different spot is a hostage to it.
*Acceptance:* two `wake here` lines, same coordinates, coastal term dominant in both, bearings
roughly opposed. Then the period back to 3600.
**RUN 2026-09-21, CLOSED — and the protocol above was wrong in one way that cost twenty minutes.**
The first flood readings were taken by `wake here` at (4798, 1019) and (4804, 1006) and the slack
ones at (4786, 1003); twelve to twenty metres apart is enough on a shelf to change the shore
gradient the coastal tangent is built from, and the set was not comparable. The fix was
**`wake field 4786 1003` from wherever the owner happened to be** — the field is a pure function,
so the same point can be read remotely through a whole cycle without holding station. Sixteen
readings at that one point, `TidePeriodSeconds 600` pushed from the server:

| tide | current at (4786, 1003) | dominant |
|---|---|---|
| 46% slack | 0.315 m/s SE (0.24, −0.21) | Coastal |
| 57% ebb | 0.574 m/s ESE (0.55, −0.18) | Race |
| 94% ebb | 0.585 m/s ESE (0.55, −0.19) | Race |
| 6% → 13% flood | east component 0.197 → 0.083, north steady at −0.24 | Coastal |
| **25% peak flood** | **0.272 m/s S (−0.013, −0.271)** | Coastal |

Read against the source (`coastalSpeed = CoastalStrength × shelf × sin(tide)`, open water
`× (1 + 0.25 sin)`): an open-water residual of ~0.36 m/s ESE that never turns, and a coastal
stream of ~0.5 m/s running east–west along this shore — east on the ebb, west on the flood —
which the 25% reading was **predicted from before it was taken** ("roughly 0.3 m/s toward the SSW
or S"; measured 0.272 S). The along-shore component reverses; the total swings a quarter-turn and
halves, and the label flips between Race (the two add) and Coastal (they fight). That is the
README's "a different passage six hours later", measured at one point through one cycle.
*Found while reading the term:* the "slight push toward land, ALWAYS" (`onshoreShare`) is
multiplied by the signed `coastalSpeed`, so on the ebb it pushes off the shore. Fifteen percent of
the coastal term; the comment and the README's lee-shore argument say always. A field-maths
change, so it waits for the item 6 decision rather than being fixed mid-session.

**6. The decision — the speed, and version gating. Both are the owner's, not the code's.**
On 2026-09-19 the owner asked for "actual current speeds", then "as real as possible", and then
chose to leave the tuning where it is. That stands and is not reopened here. What this entry
records is the COST SHAPE: every field constant that changes before 1.0 is a number; every one
that changes after is a migration on every existing install. The analysis behind that session —
a scan of the shipping field's speed distribution across a seed, and the gap between
`MaxRaceMultiplier = 1.6` and real tidal races at 3–5 m/s — **did not make it into this repo and
its scratch files are gone**, so if the question is reopened the scan has to be re-run first.
The second decision is smaller: the open question below says to revisit version gating "the day
a release changes the field's maths". The config sync has since closed the TUNING half of that
risk entirely; the maths half is now the only way two versions can disagree about one sea, and
1.0 is the natural place to decide whether to copy RW's `VersionSync` (warn once, never kick) or
to keep relying on the wire header. Either answer is fine; not deciding is the one that costs.
**DECIDED 2026-09-21 — "defaults stay, do the two fixes."** The sync had made the question
smaller than it looked: the ceiling and the race multiplier are synced keys, so "real" races are a
server owner's dial already, and the only thing making that dial unsafe was slack being a SHARE of
the ceiling. So: (1) the shipped tuning stays at the design target, and every number measured
today keeps describing the sea that ships; (2) `CurrentField.SlackSpeed` is an absolute 0.144
m/s — identical at defaults to the float, harness-pinned both ways, no rung; (3) the onshore
share scales by `|coastalSpeed|`, so the lee-shore push holds on the ebb — the last field-maths
change before the freeze, and the reversal test could never have seen it (the mutant reads
`flood x −0.0771, ebb x +0.0771`); (4) **version gating is the wire header**: a change to the
field maths bumps `ConfigWire.Header`, and this one did, `/1 → /2`. A 0.8.0 client on a /2 server
refuses the payload, logs it, and sails its own tuning — which for that pair is the honest
outcome, since they would not compute the same coast on the ebb. Harness 454 → 459 (commit 339017f's message says 444 → 459; 444 was the count before the
night-dimming commit), both mutants caught by exactly the assertion written for them. The locked-decisions table carries
the rule.

**7. The docs pass.** By this rung every ⚠️ in `CLAUDE.md`'s compatibility section has a date or a
reason it stays; the status block loses "NOT YET" lines it no longer needs; task 7's rows 6, 8 and
10 are filled or struck; the README's Tides, Swimmers and Flotsam sections cite the session that
watched them; `CHANGELOG.md`'s 1.0 entry says what 1.0 means in one paragraph and lists nothing
that is not observed. And the CLAUDE.md sentence "Timeline: open-ended. Done when it's done" gets
its date.
**DONE 2026-09-21 — and it found two lines staler than the ones it was written to find.** The
compatibility section: `IsStormAt` had been VERIFIED on 2026-08-28 and again on 2026-09-18 while
its ⚠️ stood for three weeks (now ✅ with both dates); Dive In stays ⚠️ with the reason beside it.
The status block: 0.8.0 was not "not yet published" — Hexium's API has listed it since 2026-09-19
22:35Z, so the version-2 rebase has been on strangers' files for two days; the harness count is
459; the "five claims" paragraph is now the 1.0 paragraph. Task 7 rows 8 and 10 struck, each with
the reason it does not gate a README sentence. The README: Tides, Swimmers and Flotsam cite the
session and its numbers; Boats carries the 1.0.15 karve re-take; "Plays well with" names Njord and
Sailing as measured; the Install paragraph that told owners to keep every config identical — stale
since the sync shipped — now says what travels and what stays; the drift-lines paragraph names the
night dial. `CHANGELOG.md` opens with a `## 1.0.0` paragraph that says what 1.0 means and lists
only what was watched. "Done when it's done" has its date. **An independent audit ran the same
afternoon** — four readers over the three documents, every finding handed to a second reader to
refute, one reviewer over the survivors: 23 raised, 12 confirmed, and two of the twelve wrong on
inspection (the lift sighting WAS recorded at task 10 (b); the harness count at item 6 was the
right one). What it added, all of it wording and none of it code: the README no longer promises
storm wreckage (built, `STORM wreckage` never once in a log — no storm has stood over a spawn), a
non-admin's refusal line (no non-admin has ever pushed), a 300 m strait measured against a gap
(that is the harness's two-scale probe), or "not a byte of traffic" (twelve constants travel since
0.8.0); the longship's 0.96 is marked as the pre-1.0 measurement it is; the two field-maths fixes
carry "harness only, not yet read on the water" in their own bullets; the changelog's midnight fog
figure is the code's 0.18, not the later 0.15; and task 7 row 6's tail, its acceptance line, the
lift "owed" bullet and task 9's "never met" line were all stale in the direction of owing what had
been done. Version 1.0.0 in all three places; `package.ps1` built the zip; **the owner uploaded it
the same afternoon, and Hexium's API read `latest 1.0.0` at 19:32Z on 2026-09-21.**
**Three five-minute readings would let three softened sentences go back to full strength, and
none of them blocks the upload:** a non-admin push refused (task 9 step 8, one `adminlist.txt`
edit); a `STORM wreckage` spawn line under a console-fired storm at `FlotsamPerHour 60`; and
`wake field 4786 1003` at ~94% ebb against the pre-fix `0.585 m/s ESE (0.55, −0.19)` — the
onshore fix moves that point by about 0.15 m/s. **The release number is 1.0.0, not
0.9.0:** the ladder's 0.9 rung was the two server-side risks, and both closed in the same
unreleased span as the sightings, so a 0.9 would have shipped nothing the 1.0 does not.

### Owed, and deliberately not on the ladder

None of these would stop the number going on, and each is written up where it belongs:

- ~~The 2 cm lift, looked at (task 10 (b))~~ — closed 2026-09-21: "lying on the water" at the
  shipped `DriftLineLiftMetres = 0.02`. Still owed there: the two coordinates that first showed the
  hover, revisited.
- ~~Two Undertow versions meeting across the wire~~ (closed 2026-09-21, task 9), and the
  non-admin push REFUSED and acknowledged — only the accept leg has been observed (task 9).
- The slider debounce — four pushes and four broadcasts per drag (task 9).
- Two-hull convergence re-taken on the shipping game: only the karve has sailed since August, and
  the "every hull regardless of damping" claim rests on 0.2.1 (task 6). The race-convergence
  hypothesis (0.91 medians in fast water) wants a hull parked inside a race rather than sailed
  through one.
- Drift lines: the 1 km zone-crossing sail, `wake drift` identical with lines on and off, the cost
  at a now-common saturated pool, one look at a lake (task 7 rows 8 and 10, task 10 (c) and (d)).
- Dive In (task 5c) — not on Ravenrest, expected to compose, tight case is the encumbered diver in
  a storm.
- The 70 m/s near-shore anomaly (task 6) — stays open until it recurs; no theory is to be written
  for it in the meantime.
- The headless float re-scan (`123 of 1090` vs a client-side `162 of 1523`) — nothing depends on
  either number.

### Seen on the way, and not ours

- **A vanilla `NullReferenceException` in `Ship.UpdateSailSize` at login, 2026-09-21.** Fifty
  identical throws on the `testing` client, every one between the loading screen and `Spawned
  after 8.0`, none after, none on the server. The stack names `Ship.DMD<Ship::CustomFixedUpdate>`,
  which is Harmony's rewrite of the method we postfix — so anyone reading that trace will read it
  as Undertow's. It is not: the throw is in vanilla's sail-swap effect branch, which dereferences
  `Player.m_localPlayer` with no null check when a sail starts moving, and a postfix never runs
  on a tick where the original throws. Read out of the real assembly the same day; the full
  mechanism, the exact-fifty arithmetic and what the throw does to the rest of the fixed step are
  the CLAUDE.md Known trap. What started the sail moving on a client with no player yet was NOT
  established — vanilla's writers are the ZDO's `s_forward` on a non-owner and three RPCs, and a
  mod that sets a ship's speed on load reaches the same path. Cheap to settle if anyone cares:
  log out with the sail down and back in, then log out under sail and back in; whichever recurs
  says whether the ZDO or a mod set it. Not owed for 1.0. **The instrument lesson is:** the
  client-log monitor filtered `Unity Log` lines out for quiet and Unity logs every exception
  under that source, so the monitor said "no exceptions" for the whole day. Grep for `Exception`
  before filtering sources, never after.

## 5z. Original task 5 specification (its AddPushbackForce advice was WRONG - see above)

Last, deliberately: the highest-annoyance surface in the mod, and it wants the most tuning
evidence behind it.

- 🚫 **`AddForce` does not work here.** `Character.UpdateSwimming` servos the body toward
  `m_currentVel` every frame, so an external force is cancelled next tick. Fold the current
  into the velocity target the way vanilla's own `AddPushbackForce(ref m_currentVel)` does.
- Cap well below `m_swimSpeed` (2f) so a player can always make headway against it.
- Config-gated, and gentle.

**Acceptance:** a swimmer is visibly set down-current while still able to swim to shore from
any point in the field, at the strongest current the config allows. Verify the drowning case
explicitly and deliberately: **if a player can be held offshore until they drown, the feature
is wrong**, not the tuning.

---

## Open questions

- **Which vanilla item prefabs carry `Floating`?** SETTLED 2026-08-28 — 123 of 1090, measured
  headless before the spawner was written; see task 4.
- **Does the mod have to be on every client?** SETTLED for 0.5.1: **yes, server and every
  client**, and the README and CHANGELOG both say so. Boat physics runs on the owning peer, so
  an unmodded client sails a currentless sea; flotsam spawns on the server, so a client-only
  install gets currents and no wreckage. **Not version-gated**, deliberately for now: the field
  is a pure function of seed, position, clock, season and config, so a version-skewed pair
  behaves exactly like the config-mismatch case the README already warns about — each machine
  computes its own ocean, and no hull desyncs because one peer owns it. Revisit the day a
  release changes the field's maths, since that is when two versions would disagree about the
  same water; RW's `VersionSync` (warn once, never kick) is the pattern to copy.
- **Does Moder's wind control exempt you from the current?** Leaning strongly no —
  "Moder gives you the wind, not the sea". Locked in `CLAUDE.md` unless argued otherwise.
- **Should the field ever be persisted?** Leaning no for v1. It is a pure function today,
  which is exactly why it needs no sync and no save file; a sea that *remembers* is a sequel
  question, and RW already owns "the world remembers".
- **Do currents belong in rivers and lakes?** Ocean-only for v1; narrow water plus a sideways
  force pins players against terrain.
- **What does a fully loaded server cost?** `CurrentField` is evaluated per boat per fixed
  update. It is cheap arithmetic, but it has never been profiled. Do so before release.

