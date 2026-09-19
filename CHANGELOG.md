# Changelog

## 0.8.0

- **The drift lines now show in all moving water.** Two shipped values were keeping the foam off
  the sea, and both were found by standing in the water and reading `wake lines` rather than by
  reasoning about the code.

  **`DriftLineMinDepth` shipped at 10 m, and that was wrong for the feature it constrains.**
  Valheim's open ocean is a flat 30 m floor, so every race, strait, shelf and coastal set is
  shallower than that *by definition* — which meant the "fast water between islands" this mod
  exists to show was gated out of its own habitat. Measured in game: 0.665 m/s of genuine Race at
  5.1 m depth, and `wake lines` reporting `rejected shallow 63`. The floor is now **2 m**, where
  it stops being a filter and becomes what it was always described as: a beach guard. The number
  is not arbitrary — acceptance ramps over the 6 m above it, so it finishes at a depth of 8,
  exactly where `CurrentField`'s own shallow fade finishes. Two unrelated thresholds became one
  curve. Foam on land remains impossible at any setting.

  **Slack water is no longer bare.** It used to be a cliff: below 12% of `MaxCurrentSpeed` the
  foam was strictly absent, on the argument that slack water is glassy. The argument was sound and
  the consequence was not — an empty sea is *also* what you see when the feature is broken, when a
  config is mis-set, or when you happen to be parked in a slack pocket, and a slack pocket can be
  over a kilometre across (measured). Three indistinguishable causes for one observation. The
  cliff is now a floor: below the threshold, density ramps from nothing at dead-still water up to
  a new `DriftLineSlackFloor` (default 0.08), so a glassy patch carries roughly a dozen short
  flecks where a race carries a hundred and sixty long lines.

  **What this costs, plainly: an empty sea no longer means slack water.** The contrast that makes
  the set learnable becomes a difference of density and streak length rather than of presence and
  absence. `DriftLineSlackFloor = 0` restores 0.7's behaviour exactly, and the harness pins that
  it does. At and above 60% of `MaxCurrentSpeed` nothing changed at all — a race looks
  pixel-for-pixel as it did.

  No new frame cost: the field is sampled *before* the spawn test, so widening what is accepted
  adds no field evaluations. The pool is still hard-capped, and the capped state was already
  measured at 0.37 ms against a 0.50 ms budget.

- **The config migration ran its first destructive step, on a real server.** Moving a shipped
  default is exactly what `ConfigLedger` was built for and had never done: BepInEx never rewrites
  a value already in a file, so changing the C# default alone would have shipped the fix disabled
  for every existing install, silently. Version 2 carries one rebase row. On Storm10 it logged
  `config: version 1 -> 2: 1 value(s) moved to their new defaults: 7 - Drift lines.DriftLineMinDepth
  (your previous config is backed up beside it, .v1.bak)`, and the backup was verified
  byte-identical to the pre-migration file. A value you set yourself is kept and named instead.
  The ladder was built two releases before anything needed it, and the first rung turned out to be
  a data edit rather than new code on the boot path — which was the whole claim.


- **The server's sea, on every client.** `CurrentField` is a pure function of seed, position,
  world time and season, which is why it needs no save file and no per-tick traffic — every
  machine computes the same water from facts they all already have. That argument holds only
  while the TUNING is also the same. A server that raised `MaxCurrentSpeed` and a client that
  did not were computing two different oceans from one seed, and because drift is applied by the
  peer that OWNS each hull, those two players genuinely sailed different seas. Nothing desynced,
  nothing errored, and nobody could tell.

  The server now publishes its twelve gameplay dials — the five field terms, the Wrath bridge
  switch, and the drift and swimmer groups — and a client **adopts them in memory for the
  session**. Your config file is never written, never backed up and never touched; leave the
  server and your own settings are exactly as you left them. Incoming values are clamped to the
  range your own build allows, so a server can make the sea faster and never physically
  impossible.

  What does NOT travel is as deliberate as what does: the drift lines, the tick budget, the field
  refresh cadence, verbose logging and every flotsam key stay yours. Those are your machine's
  frame rate, not the server's sea.

  An **admin** who changes a synced value on their own client pushes that one value up; the
  server applies it to its own config — so it survives a restart — and re-publishes to everyone.
  A non-admin's edits never leave their machine, and say so in the log rather than silently doing
  nothing. Admin identity is checked on the SERVER against its own admin list, accepting both the
  full platform id and the bare numeric form the way vanilla does.

  `wake status` reports which side of this you are on and how many values are in force.

  No new Harmony patch (the boot line still reads `Harmony patched 3`), nothing saved, nothing
  sent per frame, and with the mod alone on a machine it never speaks at all.

- **The field was profiled, and it was being computed twice for nothing.** One `Evaluate` makes
  exactly nine `WorldGenerator.GetHeight` calls — counted with an instrumented probe, not read
  off the source, and now pinned by an assertion. The arithmetic around them is 0.23–0.34 µs and
  irrelevant. So the only thing worth optimising is how often `Evaluate` is called at all, and
  two places were calling it and throwing the answer away: a boat nobody is aboard was evaluated
  before the check that `UnattendedDriftFactor` is zero (its shipped default), which on a
  dedicated server is the dominant cost and all of it waste, because a server owns exactly the
  boats no player is near; and the swimmer postfix had no cache at all, asking for 450
  `GetHeight` a second per swimmer against a hull's 36. The same values come out — they are just
  no longer computed and discarded.

## 0.7.2

**Built and tested against Valheim 1.0.15 and BepInEx pack 5.4.2350.** A release-readiness pass
over 0.7.1, which was packaged but never uploaded. Nothing here changes how the sea behaves; it is
the pass that makes 0.7.1 fit to ship, and it takes a new version number rather than becoming a
second 0.7.1 — the whole reason it exists is that a version number which means two different
binaries is exactly the trap this release was caught in.

- **Log messages were mojibake.** Twenty-four runs of double-encoded UTF-8 across four source
  files — twenty-one em dashes and three bullets — each one the correct bytes decoded as
  Windows-1252 and re-encoded, so `—` had become `â€"`. Two of them are lines a player reads. One is `Harmony attached NO patches`, which is the
  single message that has to be legible on the day a Valheim update moves an API, and the other
  is the warning that nothing in the game floats. The README and this changelog were never
  affected. Found by reading the raw bytes; every console this was viewed through had been
  quietly showing it correctly or quietly showing it wrong, and neither could be trusted.

- **A persistent fault could drown its own log.** The catch-all in the ship and swimmer postfixes
  logged every occurrence, and those run in the physics step — about fifty times a second, per
  hull. A fault that kept happening, which is the realistic shape after a game update, would bury
  the only instrument this mod has under its own noise at the exact moment it was needed. The
  first one is still reported immediately and in full; after that it is one line per thirty
  seconds, carrying the number suppressed in between so the log distinguishes "once, oddly" from
  "constantly". Vanilla was never at risk either way: the catch exists so our faults never reach
  the game.

- **Flotsam could leak past its own cap.** When the timed reclaim of a piece of driftwood threw,
  the item was dropped from the tracking list anyway, while it may well have still been out
  there. Every such failure permanently freed a slot under the cap that is the only thing between
  a long-running server and an unbounded object table — and the log looked healthy throughout.
  The item now stays counted and is retried on the next sweep.

- **Two claims in the shipping documents were not true.** The README stated that a hull resists
  sideways drift about twice as hard as forward drift, true of every hull. No such measurement
  exists: the project measured the along-current ratio only, and its own notes conclude the
  damping is a per-hull value where no single constant can be right. It now quotes the numbers
  actually measured — 0.86 for a karve and 0.96 for a longship against the water's 1.0 — and
  describes the sideways effect qualitatively. Separately, the project's status notes said the
  config migration had never run in-game and that the build referenced Valheim 1.0.12 assemblies.
  Both were stale: the migration's own line is in a dedicated server's log from 2026-09-18, and
  the reference set was refreshed to 1.0.15 hours after the note was written.

- **The release script now refuses a red harness.** "Tests green" was this project's own
  definition of done and the one guard packaging could skip. It checks the harness's OUTPUT and
  not merely its exit code, because a script this machine's execution policy refuses also exits
  non-zero — a distinction that once let a mutation suite score twenty out of twenty without
  compiling a line.

- **The BepInEx dependency now names 5.4.2350**, the pack actually in use, rather than the
  5.4.2333 carried unchanged since the first release.


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
