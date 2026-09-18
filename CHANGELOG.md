# Changelog

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
