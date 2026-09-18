# Changelog

## 0.7.1

**The config file migrates itself.** BepInEx merges a new key into an existing file at its SHIPPED
default — and a shipped default is chosen for a fresh install. It says what a NEW world should feel
like, not what an existing one already feels like. When those differ, an owner who changed nothing
gets a different sea, with no error and nothing in the log. This closes that door before it is ever
opened.

- **The machinery, from the family.** Wu'barrk's from Wings of the Valkyrie, by way of Valkyrie's
  Cargo, and matching the ports that landed in Ragnarok's Wrath and FireFront the same week. The raw
  file is read BEFORE any value binds, a `[0 - Meta] ConfigVersion` stamps the layout, and three kinds
  of change are expressible: a value still equal to an OLD shipped default moves to the new one, a key
  that is ABSENT can be given a legacy value that preserves how your world already behaved, and a key
  no current build binds is dropped instead of riding along forever as a BepInEx orphan. Anything an
  admin actually set is kept untouched and named in the log, so you can see the migration read it and
  leave it alone.
- **It cannot lose a setting.** Before any change that would overwrite or delete something, a copy of
  the previous file lands beside it as `.vN.bak`; if that copy cannot be written, nothing is changed
  and the version is left unstamped, so the whole thing retries next boot instead of half-happening. A
  migration that fails never stops the mod loading.
- **Version 1 changes nothing, and that is the point.** Undertow's config has only ever GROWN — no
  default has ever moved, no key has ever been renamed or removed, no range has ever narrowed. Checked
  against three independent records that agree: the source at every commit, the shipped 0.5.1 and 0.6.0
  binaries, and nine real config files. So this release only stamps your file. The ladder exists before
  the rung that needs it, because the alternative is writing it under pressure on the day a default has
  to move.
- **0.7.0's drift lines stay ON for existing installs.** They were considered for a legacy value and
  deliberately left alone: they are client-side cosmetics that touch no world state, a dedicated server
  ignores them entirely, and they are the feature 0.7.0 was for. `EnableDriftLines = false` is one line
  if you want the old look.
- **`wake status`** now reports the config layout version, and the migration's own boot line when this
  boot migrated anything.
- **Harness 248 → 357.** The decisions are pure (`Core/ConfigLedger.cs`) and every rule is pinned: a
  backfill never overwrites a key that is present, a retirement never fires on one that is absent, an
  admin's value is never mistaken for a default, a commented-out setting stays commented out, and the
  file is read before the first bind. Twenty-eight deliberate breakages were applied to the shipping
  source and all twenty-eight were caught by a named assertion. An independent review then went over
  the finished code and found six more real defects in it, each now fixed and pinned the same way.

- **Two more corrections, found by auditing the sibling ports (same day).**
  - **A retirement that failed was reported as harmless, and the version stamped anyway.**
    Binding and removing a key both touch the file, so a transient lock — antivirus, cloud
    sync, a config manager, a second process in the same directory — takes the drop down
    through nobody's fault. The stamp then wrote "already migrated" and the retirement was
    never retried: a one-second lock made permanent. The drop reports upward now and the
    version is left unstamped, so the next boot tries again. Undertow retires nothing today,
    so this is a guard against the first rung that does — which will be a release, on
    somebody else's config file. `Finish` takes the plan as a parameter for the same reason
    `ConfigLedger.Plan` takes its tables: an empty shipped table must not leave live code
    unmeasured until the rung that uses it ships.
  - **`wake status` reported the plan's INTENT, not what happened.** The summary is written
    before a single step runs, and a refused step logged a warning and nothing else — so the
    one line the owner reads could claim a key was dropped that is still in the file.
    Refusals now correct the summary, and the count is per boot.

  Harness 357 → 367, and thirteen mutations of the migration are each caught by a named
  assertion.

## 0.7.0

The current, visible. **Run in-game the day it was built** — Storm10, Valheim 1.0.15 on both
sides: 0 exceptions, streaks carrying the field to a degree of bearing, height agreeing with
vanilla's water to the millimetre, 0.37 ms a frame at a full pool, and the owner's own reading —
"long ways along the flow, a line instead of an arrow". Night, a storm and a long sail are still
owed; `docs/BACKLOG.md` task 7 has the numbers and what remains.

- **Drift lines.** Faint foam streaks lie along the current on the water itself, move at the
  water's own speed, ride vanilla's wave surface, and are absent in slack water, which stays
  glassy. Thick in a race, sparse at a trickle; night, fog, distance and a big sea dim them on
  their own. From a drifting hull they hold station; under sail they stream past at the crab
  angle. The streaks are symmetric end to end, so a still frame gives the line of the flow and
  only watching gives the sense.
- **Still no HUD.** Nothing is drawn in screen space and nothing reads the current out — no
  arrow, no number, no icon. The locked "no navigation instruments" decision stands; the one
  sentence that changed is the premise's "never on the screen", now "never as an instrument".
- **Procedural, client-only, off the gameplay path.** The particle system, its texture and its
  material are generated in code: no prefab, no asset, no bundle, nothing networked or saved. It
  adds no Harmony patch (the boot line still reads `Harmony patched 3`), owns only its own
  particles, latches itself off after a single error, and halves its own particle count when it
  exceeds a per-frame budget — and says so in the log.
- **`wake lines`** reports state, shader, spawn accounting with rejection reasons, the streaks'
  mean bearing and speed against the field's own, the nearest streak's height against vanilla's
  `Floating.GetWaterLevel`, the scene inputs, and the measured cost. `wake lines reset` rebuilds
  the emitter — the console's first mutation, local and cosmetic.
- **Config.** `EnableDriftLines` under `2 - Systems` (default on); new section `7 - Drift lines`:
  `DriftLineCount` 160, `DriftLineRadius` 60, `DriftLineOpacity` 1.0, `DriftLineMinDepth` 10,
  `DriftLineBudgetMs` 0.5. Per machine by nature — two players on one deck see the same set,
  density and speed, not the same individual foam.
- **Harness 162 → 248.** The streak maths is pure (`Core/DriftLineMath.cs`) and every rule is
  pinned: no foam in slack water or on a beach (cross-checked against the field's own Slack
  classification over 90 000 points), no visible rim, nothing pops, no glow at night, a texture
  with no head end, an injective memo key. Each assertion was proven to fail under a mutation.

## 0.6.0

Valheim 1.0. **If you updated the game, 0.5.1 was broken and this is the fix.**

- **Verified in-game on 1.0.12** (2026-09-12), client-side: loads with no exceptions, all three
  patches attach, the console registers, the Wrath bridge resolves, and the drift force ran on a
  live karve for 182 samples across all four field terms. In slack water the hull converged on
  the water's own speed; in a race it reached 0.91 median and never 0.98, which is recorded as an
  open question rather than a pass. Flotsam spawning, storm surge, the tide cycling, any second
  hull and the swimmer drowning-guard were NOT re-tested. See `docs/BACKLOG.md` task 6.
- **Rebuilt for Valheim 1.0.12.** No behaviour changed — currents, tides, flotsam and every
  tuning value are exactly what 0.5.1 shipped. What changed is the game underneath: 1.0.7 added
  a parameter to `Terminal.ConsoleCommand`'s constructor, and .NET resolves a call like that by
  its exact signature at runtime, so the `wake` console registration in the old binary threw
  `MissingMethodException` on a 1.0.x game. The source was always fine; the binary was compiled
  against an API that no longer exists.
- **Why 0.5.1 looked healthy.** It compiled clean against 1.0.7 the day the update landed, so
  nothing asked to be fixed. A clean build proves the source matches today's game — it says
  nothing about a DLL built in August. The break was found by reading the shipped binary's own
  reference table and resolving each entry against the live game assemblies.


## 0.5.1

First release. The sea gets its own motion.

- **Currents across the whole ocean**, in a shape you can learn: a slow basin drift, a stream
  that follows the coast, fast water between close islands, and dead water behind a headland.
  Built from a stream function, so the flow is divergence-free the way real water is — gyres,
  races and slack all come out of one mechanism instead of being placed by hand.
- **The same on every machine with no network traffic.** The field is a pure function of the
  world seed, position, world clock and season. Verified by a server and a client independently
  producing identical readings for the same points.
- **Tides.** A slow flood and ebb that swings how hard the open ocean runs and reverses the
  coastal stream, so a passage you know is a different passage later.
- **Boats are carried, never braked.** A drifting hull settles at the water's own speed, and a
  raft, a karve and a longship all agree — the push fades as a hull takes up the water's speed
  rather than being calibrated against damping constants that differ per boat. Unattended boats
  are not touched by default.
- **Flotsam.** Driftwood and cargo gather in slack water, with wreckage instead while a storm is
  overhead. Vanilla items only, capped, reclaimed on a timer, and spawned only near a real
  player, so an empty ocean stays empty.
- **Swimmers** are carried gently, and hard-capped below swim speed: you can always out-swim the
  water and reach shore, at every setting the config permits.
- **Storm surge with Ragnarok's Wrath.** Where a Devastating Storm stands, the water rises —
  there and nowhere else. Optional: without RW the bridge logs its absence once and the sea runs
  regardless.
- **The `wake` console** — `status`, `here`, `field`, `drift`, `floats`.

Requires the mod on the server **and every client**: boat physics runs on whichever machine owns
the hull, and that is a player's.
