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

## 2c. Compatibility: Sailing (Smoothbrain) — ANALYSED 2026-09-02, NOT MEASURED

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

## 2d. Compatibility: Njord (Wubarrk) — ANALYSED 2026-09-02 FROM DOCS ONLY, NOT MEASURED

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
| 6 night | **Not seen.** `ambient lum 0.56 → day 1.00` by day; the midnight reading is still owed. |
| 7 cost | **0.37 ms EMA at 160/160 active** (0.34 last frame, 81 surface reads, 0 field evals — memo 24 cells), budget 0.50. Prior was 0.25. Auto-degrade never fired. The first cost summary after build read 2.66 ms with nothing active — a single-frame EMA seed, fixed the same day (EMA now rises from zero and no verdict is taken for 60 warm-up frames). |
| 8 zone crossings | `retired: no-volume 0, reflected 1` over a swimming session; no latch. The 1 km sail is still owed. |
| 9 storm | **SEEN, 08:13–08:19.** RW 0.27.0 on both sides (server un-parked for it; the two ServerSync-pinned mods parked instead, at the owner's direction). `event ragnarokswrath_devastating_storm` from the client console → server `Random event set` and RW `storm began — sky is 'Rain'` at (-2286, 2091) → client `STORM at (-2286, 2091) — IsStormAt(centre)=True, surge x1.6 \| at centre: 0.273 m/s Drift \| 800m away: 0.151 m/s surge x1`. Console, before → under surge (owner's paste): `wake here` 0.173 m/s ESE Drift → **0.265 m/s, `STORM SURGE x1.6 — the sea is up here`**; `wake lines` active 34 → **82**, spawned per 10 s 38 → **73**, mean length 2.4 → **2.8 m**, mean speed 0.21 → 0.30, cost 0.08 → 0.14 ms EMA; nearest-streak delta against vanilla 0.000 both times in a 0.7 m sea. Surge reaches the visual through speed alone, as designed. Chop stayed ~0.21 (the storm's sky was 'Rain', not a big sea). Same paste closed the season check: `season summer (Wrath)` on a pure client. **Then forced to ThunderStorm** (08:21 and 08:28; `StormsForceWeather = true`, `StormForcedEnvironment = ThunderStorm` on both sides, both restarted because RW registers the event's forced sky at boot): **`chop 1.00` for the whole storm** — sea state past 2.3 m, the full chop response — mean length 3.1–3.9 m at 0.23–0.38 m/s water, 50–81 active, cost ≤ 0.21 ms, surge x1.6 again at (-2332, 2101). **And the owner saw NO streaks in it** ("they disappeared, but that's fine in a storm") while the pool held 50–81 active: they existed and were unseen — under the rendered mesh on steep crests (the predicted failure: lift is `0.06 + 0.05 × chop` = 0.11 m at chop 1 against a 2 m+ crest) or lost to the ThunderStorm's rain and fog; the log cannot tell which. Accepted by the owner as storm behaviour and NOT chased. The lever, if it is ever wanted: `ChopLiftMetres` 0.05 → 0.2 first, then a chop-scaled opacity floor. RW's own note applies to that sky: ThunderStorm is a WET environment, so a forced storm rains and its lightning is suppressed — the dry storm look is `Eikthyr`. |
| 10 gameplay untouched | Boot line `Harmony patched 3` on both sides. `wake drift` on/off comparison still owed. |
| 11 owner's eye | "a line instead of an arrow" — the design. Opacity at default read as subtle; the owner did not ask for more. |

**Acceptance so far:** steps 1–4, 7 and 9 met, 5 and 8 partial, 6 and 10 owed. The pool saturates
at 0.5 m/s with `MaxCurrentSpeed 1.2` (62% acceptance x 1.45 mean cluster x 16 attempts/s ≈
15 streaks/s against a 10 s mean life), so in strong water density is the cap rather than the
speed; whether that is right is a tuning question for after the night reading — the storm reading is in (row 9), and its answer was "invisible", which is a different lever.


## 8. The config migration — BUILT 2026-09-18 (0.7.1), NOT YET RUN IN-GAME

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

1. Deploy 0.7.1 to Storm10 and to the `testing` profile. Both already carry a real 0.7.0 config with
   no `[0 - Meta]` section.
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

