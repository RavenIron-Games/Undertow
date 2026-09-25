# Changelog

## 1.0.3

A rebuild for Valheim 1.0.16. Nothing about the water or the drift changed, no config key moved,
and the wire is the same, so 1.0.2 and 1.0.3 still share a sea.

- **Checked against Valheim 1.0.16.** The game's 1.0.16 hotfix (2026-09-25) changed none of the
  game code Undertow patches or calls: its four Harmony patches and its by-name lookups find the
  same methods and fields on 1.0.15 and 1.0.16, and the mod builds cleanly against the 1.0.16
  game. This DLL is built against 1.0.16. The game's network version did not change, so 1.0.15
  and 1.0.16 players and servers still connect to each other.

- **No gameplay change.** The mod's code is 1.0.2's; only the version number and some comments
  in the source changed. This DLL was built from the commit tagged `v1.0.3`; the GitHub release
  names that commit and gives the DLL's md5.

Last tested in game as 1.0.2, on Valheim 1.0.15 (below). Off-game: 495 checks, 0 failed.

## 1.0.2

Fixes and cleanup. Nothing about the water or the drift changed, no config key moved, and the
wire is the same, so 1.0.1 and 1.0.2 still share a sea.

- **Flotsam now washes up in single player and around a host's own player.** Until now it only
  ever spawned near players connected to a server from elsewhere, so a single-player world never
  saw any, and on a hosted (non-dedicated) game only the guests did. The host's own player now
  counts like everyone else. Dedicated servers are unchanged, and an ocean with nobody in it
  still stays empty. A single-player world can now hold up to `FlotsamMaxAlive` pieces when you
  quit; they are vanilla items and vanilla's own despawn clears them.

- **The server now checks who really sent a config message.** Valheim lets a client write its
  own sender id on this kind of message, and the server did not check it. So a modified client
  could pass itself off as an online admin and change the server's synced settings, or pass
  itself off as the server and hand every player a different sea for a while. The server now
  drops any Undertow config message whose sender does not match the connection it arrived on,
  and logs one warning per connection that tries it. The admin check, the ranges and the synced
  keys are unchanged. The check runs on the server, so the server needs 1.0.2; clients need
  nothing new.

- **Less garbage per physics tick on a client connected to a server.** Reading a synced value
  and noting the last pushed hull no longer create a new string every tick.

- **The DLL no longer carries the build machine's folder path.** Every DLL through 1.0.1 embedded
  the absolute path of its debug-symbols file, a path that included the build machine's user
  name. The build now maps its source folders to a neutral `/_/` prefix, so neither the DLL nor
  its symbols name a local folder, and two builds of the same commit, from Windows clones with Git's
  default line endings, the same .NET SDK and the same game libraries, are byte-identical. This DLL was built from the commit tagged `v1.0.2`; the
  GitHub release names that commit and gives the DLL's md5.

- **The README's Support section links the Raven Iron website**, <https://ravenirongames.com>,
  beside Patreon and Discord.

- **The store page's website link now goes to the Raven Iron website.** `manifest.json`'s
  `website_url` pointed at the GitHub repo; it now reads <https://ravenirongames.com/>, matching
  the README's own Support section.

**Tested in game on 2026-09-24**, on a Valheim 1.0.15 dedicated server (crossplay) plus
a single-player world, with one client, all on the DLL built from `5446c9d`, the head of the fix
branch (md5 `8e35436a412c89cbfe7d45a7aa23b2d5`, 132,608 bytes) — the same code as this release;
only documents and the store page's website link changed after it. Built against and tested on
Valheim 1.0.15. Flotsam in single player, with `FlotsamPerHour` raised from its default 6 to 120
for the test: 6 spawns in about 3.5 minutes, 34–107 m from where the drift began, in 30 m of water
with a 0.30–0.33 m/s current. Config sync on join: the client asked the server for its config and
nothing was dropped (the "13 values in force" line was read on screen, not in a log). Admin push:
9 of 9 `MaxCurrentSpeed` changes were accepted in an earlier session the same day, on the same DLL,
and the last one set the value back. Local config was restored on leaving a server. Both sides
booted with `Harmony patched 4`; Undertow logged no errors or warnings on either side, and no
exceptions appeared in either log. Not tried in game: a non-admin's config push being refused
(checked only in code); the forged sender itself, which needs a modified client (the packet-header
read was checked against the 1.0.15 game code; six harness cases cover the accept/drop predicate
but not the patch that calls it; genuine admin traffic passing through the patch is the closest
in-game evidence); a 1.0.1 client with a 1.0.2 server, or the reverse (the handshake is unchanged,
read from the code); flotsam staying off on a dedicated server with an empty ocean (an unchanged
path; the harness covers the origin count); flotsam around a listen host's own player with a guest
connected, which needs a second player (new in 1.0.2: the harness covers the origin count, and
single player ran the host branch in game); and `wake drift` naming the last hull, then reading
back `(none)` once that hull is destroyed, which is the check for the per-tick garbage fix; the
garbage saving itself was not measured (it is read from the code). Off-game: 495 checks, 0 failed.

## 1.0.1

- **A hull under way now pays the current as a speed, not a force — 1.0.0 stopped a paddled karve
  in 0.2 m/s of water.** Reported within hours of 1.0.0 going up (Grishak: below a
  `MaxCurrentSpeed` of 0.25 he could paddle through anything; at 0.3 the water held him or pushed
  him back). Read out of the game's own hull prefabs the same hour: a karve's paddle is 0.004 m/s
  of speed per physics tick, and the push this mod applied against a hull driving upstream was the
  water's speed × 0.02 per tick — the same 0.004 at 0.2 m/s of water. The push was an
  acceleration measured against the hull's engine, and vanilla's engines are tiny, so whether a
  current stopped a boat depended on the boat and not on the water. Every measurement before 1.0
  had been a drifting hull, which the push models well, or a hull under Njord and Sailing, whose
  thrust is many times vanilla's.

  Now the ship's own speed setting decides. A hull with paddle or sail set feels the current as
  drag relative to the water — the exact correction to vanilla's own damping, which acts on
  absolute speed — so going into a current costs exactly the water's speed over the ground, going
  with it pays exactly that, and speed through the water is untouched. A hull adrift keeps the
  carrying push every drift measurement was taken on, so nothing about drifting changed. Watched
  the same afternoon on a paddled karve, verbose: 1.5–1.9 m/s of headway straight into
  0.25–0.31 m/s of water, where 1.0.0 stalled; 3.0–3.5 m/s along a 0.44 m/s current running with
  it; `mode adrift` and the water taking the hull the moment the paddle stopped; zero exceptions.
  The harness paddles a karve and a raft tick by tick and pins both the fix and the 1.0.0 number
  as the defect it was.

- **`UnderWayDragFactor`, a new synced dial.** How much of the water's own drag a hull under way
  pays: 1 is the physics above, 0 lets a boat under way ignore the current entirely (it is still
  carried when adrift), 2 grips twice as hard. `DriftStrength` now governs only how quickly a
  drifting hull is carried. Thirteen dials travel from the server instead of twelve; an older
  client drops the line it does not know, so the wire header did not move.

- **A boot line names every hull's real constants.** `Hulls (vanilla prefab constants; …)`:
  paddle force, sail factor, the three dampings, buoyancy, mass, and the Rigidbody's own drag
  (zero, on every hull) — because the class defaults a decompile shows are overridden by every
  prefab, and the diagnosis above needed numbers nobody had. INFO, once per boot, both roles.

## 1.0.0

**What 1.0 means here.** Not a feature. It is the point at which every sentence in the README about
what the sea does has been watched happen on the shipping game — Valheim 1.0.15, a dedicated server
and a client — and the point after which the config layout is a promise: from here, any default
that moves is a migration with a backup beside your file, never a silent change. The last of those
sightings were taken on 2026-09-21: flotsam spawning to its cap of twelve and then reclaiming
fourteen times at 1801 s against an 1800 s timer, including items five kilometres from the only
player, while the server's whole object table moved by exactly one per item; the swimmer's drowning
guard pinned at its cap in a race running a full metre per second, with 1.58 m/s of headway
straight upstream; one coastal point read sixteen times through a tide, swinging from 0.585 m/s ESE
on the ebb to 0.272 m/s S on the flood, the flood reading predicted before it was taken; a karve
drifting under Njord 2.0.5 and Sailing 1.1.9 at 0.99–1.00 of the water's own speed, against 0.99 in
the same water with neither; and two Undertow builds meeting across the config wire, the older one
refusing the server's tuning, saying so once, and sailing its own. The drift lines at night were
looked at for the first time and turned out to be a defect; it is fixed below. What has **not**
been watched is said where it stands rather than hidden here: Dive In is read from its source and
never run; the longship half of the two-hull convergence rests on its pre-1.0 measurement; the
storm palette for flotsam — wreckage instead of driftwood under a Devastating Storm — is built and
has never had a storm over a spawn, so the README no longer promises it; a non-admin's config push
has never been sent, so its refusal has been seen only in the code; and the two field-maths fixes
below that only the harness has seen say so in their own bullets. 0.7.0 and 0.7.1 never reached the
store; 0.7.2 did, and 0.8.0 has been on Hexium since 22:35Z on 2026-09-19 — so an install coming
from 0.8.0 has nothing to migrate, and one coming from 0.7.2 gets 0.8.0's single rebase
(`DriftLineMinDepth`) with a `.v1.bak` beside its config. Changes since 0.8.0:

- **The drift lines' night dimming works now, and it is a dial.** It was designed in from 0.7.0
  and never engaged: it read Unity's ambient light, and Valheim's midnight ambient is a moonlit
  grey (luminance 0.38, against 0.56 at noon) that never reached the "night" band, so the foam
  drew at full daytime brightness all night through three releases. Found by standing on the
  water with the clock forced to midnight and reading `wake lines`. The dimming now follows the
  **fog colour**, which runs 0.18 at midnight to 0.53 at noon and also darkens under a storm sky.
  The first working build went all the way to nothing at midnight, and that was too far — real
  foam is the most visible thing on black water — so how much of the day the foam keeps at full
  night is a new client-side setting, **`DriftLineNightFloor`**, set by eye at midnight and
  shipped at **0.7**: a little dimmer than day, still plainly foam, and the storm-sky dimming
  intact. 1.0 is the old accidental look; 0 is a foam that vanishes after dark. The readings that
  decided the input are named constants in the code and the test fixture, and `wake lines` prints
  both luminances and the floor.

- **Slack water is an absolute speed now, so a server can raise the ceiling without losing its
  foam.** "Slack" was defined as 12% of `MaxCurrentSpeed`, and `MaxCurrentSpeed` is a ceiling —
  raising it makes the fast places faster and ordinary water no faster at all, but it raised the
  slack line with it, and ordinary water quietly stopped drawing foam (measured: a 2.4 ceiling
  emptied 0.21 m/s water). Slack is 0.144 m/s outright, which is exactly what it was at the
  shipped ceiling, so nothing changes unless you turn the dial — and now you can. The old behaviour was measured on the water; the new definition is pinned
  both ways in the harness, and nobody has sailed a raised ceiling since. The same speed
  is what `wake here` calls Slack and what the foam thins at; they were one definition before and
  still are.

- **The coastal set's push toward land now points toward land on the ebb too.** It always did on
  the flood. On the ebb it pointed off the shore, because the onshore share was scaled by the
  signed stream rather than its size — fifteen percent of the coastal term, the wrong way, for
  half of every tide, since 0.1.0. Found by reading the line while the tide reversal was being
  measured on the water; the reversal itself was and is correct. The fix is proven against a
  mutant (flood x −0.0771, ebb x +0.0771) and not yet read on the water — the reversal run
  predates it, and at fifteen percent of the coastal term it could not have seen it. **This
  changes the water**, so
  the config wire is at `undertow-cfg/2`: a client on 0.8.0 or earlier joining a server on this
  version will refuse the server's tuning, sail on its own, and say so in its log — once per
  session, not on every heartbeat, and `wake status` keeps reporting it. Update both ends.

- **Flotsam now says what it reclaims.** Every verbose flotsam line ends with the size of the
  server's whole ZDO table (`[n/12 alive, N ZDOs]`), and a reclaim on the TTL — the half of the cap
  that bounds a long-running server, which used to happen in silence — logs by id, as does an item
  that was picked up or removed by the world. Verbose only; nothing changes with logging off.

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
    one line `wake status` printed could claim a key was dropped that is still in the file.
    Refusals now correct the summary, and the count is per boot.

  Harness 357 → 367, and thirteen mutations of the migration are each caught by a named
  assertion.

## 0.7.0

The current, visible. **Run in-game the day it was built** — Storm10, Valheim 1.0.15 on both
sides: 0 exceptions, streaks carrying the field to a degree of bearing, height agreeing with
vanilla's water to the millimetre, 0.37 ms a frame at a full pool, and a reading by eye —
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
