# Undertow

A Valheim mod by **Raven Iron**. The sea gets its own motion. Currents run across the ocean
in a learnable shape — a basin drift, coastal set, fast water between islands, slack behind a
headland — and they push what floats on them. On a world with no map and no portals, that
turns coordinates into seamarks: knowledge a crew carries in their heads, never as an instrument.

**Undertow owns water motion, and nothing else.** Not weather, not waves, not the water
surface, not wayfinding instruments. If a task seems to call for a map, a compass, a wind
gauge, or a second wave system, that is a signal to re-read the locked decisions below, not to
build one.

Since 0.7.0 the sea also SHOWS its motion — drift lines, foam lying along the flow on the water
itself — by the owner's call on 2026-09-18 ("we still need no hud"). That is the sea being the
sea, not an instrument: nothing is drawn in screen space, nothing reads the field out, and the
locked HUD row below stands word for word. The one sentence of the premise that changed is the
one above: "never on the screen" became "never as an instrument".

Design document (the reasoning behind every decision here):
<https://claude.ai/code/artifact/e213f36d-fdcd-4695-a159-f8e4e1157323>

---

## Status

**THE ROADMAP IS BUILT.** Tasks 0–5, harness **162/162**, every assertion proven to fail without
its fix. Verified in-game on a dedicated server and a client (2026-08-28).

**RE-VERIFIED IN-GAME ON VALHEIM 1.0.12, 2026-09-12 (0.6.0) — CLIENT-SIDE, ONE HULL.** Read the
scope before citing this. A pure client against a real dedicated server: 0.6.0 loaded, all three
Harmony patches attached, the `wake` console registered, the Wrath bridge resolved, `CurrentField`
went live, and **not one exception anywhere in the log**. The drift force fired on a live karve
across all four field terms — 182 samples. Full numbers and the scope limits are in
`docs/BACKLOG.md` task 6. What this run did NOT touch, and what therefore remains unverified on
1.0.12: **flotsam actually spawning and floating** (only the startup prefab scan re-ran), **storm
surge**, **the tide moving through its cycle**, **any hull but the karve**, **a non-spring season
end-to-end**, and **the swimmer drowning-guard clamp**, which was never exercised because the
requested drift never came near its cap. Tasks 2c, 2d and 5c are untouched: none of those three
mods were even loaded.

- **0 — skeleton.** Loads with `dedicated=True`; the `wake` console is registered, confirmed by
  reading `Terminal.commands` back rather than assuming.
- **1 — `CurrentField`.** Evaluates against real `WorldGenerator` terrain, and the server and a
  client independently produce a byte-identical transect: the no-sync architecture demonstrated
  across two machines rather than asserted.
- **2 — drift.** A karve and a longship drifting in the same water both settle near the water's
  own speed along the current (0.86 and 0.96 against a target of 1.0) — the saturation model's
  whole claim, since those two hulls have different damping constants.
- **3 — the Wrath bridge.** Both states proven by parking and unparking RW between boots, and a
  live storm measured on the CLIENT: `STORM at (8101, 368) — IsStormAt(centre)=True, surge x1.6
  | at centre 0.38 m/s | 800m away 0.266 m/s surge x1`, against RW's own
  `storm started at (8101, 368)`. Same coordinates, two mods, two machines.

- **4 — flotsam.** `123 of 1090` item prefabs carry `Floating`, measured **headless** 2026-08-28.
  A client-side scan on 1.0.12 read **`162 of 1523`** instead (2026-09-12) — the catalogue grew
  with the game, but a client and a headless server can register different prefabs, so the two
  are NOT a like-for-like pair and the headless figure stays the baseline until it is re-taken
  headless. Nothing depends on either number: the pool is rebuilt from `ObjectDB.m_items` every
  session. Driftwood spawns in slack water and **was seen floating on the surface by the owner** —
  the last step no log could settle. The cap climbs `1→2→3→4→5` and holds; an empty ocean stays
  empty. **None of that spawn behaviour was re-tested on 1.0.12.**
- **5 — swimmers.** Measured live: computed drift **0.172**, the swimmer's own measured speed
  while drifting **0.164** — a 95% match, and no sign of the 20x amplification trap. Swimming
  held 1.9–2.0 m/s against a 0.17 m/s current, so the drowning guard has a tenfold margin.

**Everything on the original roadmap (0–5) is verified in-game.** The remaining open item there
is compatibility testing against other boat mods: every measurement so far is a clean baseline
taken with none installed.

- **7 — drift lines (0.7.0). BUILT AND RUN IN-GAME 2026-09-18, on Storm10 — Valheim 1.0.15 on
  BOTH sides.** The current made visible:
  `Visuals/DriftLines.cs` owns a pool of foam streaks (structure-of-arrays, pushed through
  `SetParticles` each frame), samples the field through a 16 m / 3 s memo, rides
  `WaterVolume.GetWaterSurface` through `Visuals/WaterSurfaceCache`, and draws with a material
  `Visuals/ParticleKit` generates in code. Every rule about what a streak looks like and where
  one may exist is pure maths in `Core/DriftLineMath.cs`; the harness went **162 → 248** and
  every new assertion was proven to fail under a mutation (eleven mutations, all caught). It
  adds NO Harmony patch — the boot line still reads `Harmony patched 3` — and touches no
  gameplay state. **Measured the same day** (full numbers in `docs/BACKLOG.md` task 7): armed →
  built (`Sprites/Default`, manual fog) → first streak afloat, zero exceptions; 70–160 streaks
  with a **mean bearing of 191° against a field of 191°** and speed 0.55 vs 0.50; the nearest
  streak's height against vanilla's `Floating.GetWaterLevel` at **delta 0.000** and −0.35 m
  below the flat level (it rides the wave); the owner's eye: "long ways along the flow — a line
  instead of an arrow", which is the design; **cost 0.37 ms EMA at a saturated pool of 160**
  (81 surface reads a frame) against the 0.50 budget; the dedicated server loaded 0.7.0, patched
  3, and never armed the visual. The rotation convention was MEASURED — see Known traps — and a
  first reading of it was wrong for a reason worth knowing. **Storm surge seen the same morning**
  with RW 0.27.0 on both sides: a console-fired Devastating Storm at (-2286, 2091) gave the
  client `IsStormAt(centre)=True, surge x1.6`, the centre's water went 0.17 → 0.27 m/s, and the
  drift lines went from 18–41 active at 2.3 m to 71–77 at 2.8–2.9 m — surge reaches the visual
  through speed alone. A ThunderStorm-forced storm then held `chop 1.00` (sea state past 2.3 m)
  with 50–81 streaks active — and the owner saw none of them: present, unseen, and accepted as
  storm behaviour; `docs/BACKLOG.md` task 7 row 9 names the lever. **Not yet seen:** night, a
  long zone-crossing sail, and `wake drift` on/off. `revprobe` binds 0.7.0 clean against 1.0.15.
  **CORRECTED 2026-09-19: the `libs\` set is the 1.0.15 one, not 1.0.12.** The publicized
  assemblies were refreshed at 11:40 on 2026-09-18 — after the 05:54 game update they are taken
  from, and a few hours after this line was written. A publicized DLL is derived from the game's
  and can never be byte-identical to it; what WAS compared byte for byte (2026-09-19) is the rest
  of `libs\` — every `UnityEngine.*` and `BepInEx.dll` matches the installed game's copy exactly. The sentence outlived the fact it
  described — which is the ordinary way a status line goes wrong, and why they carry dates.

- **8 — the config migration. 0.7.2 RUN IN-GAME 2026-09-19, ON BOTH ROLES, AND THE ROUND TRIP
  CLOSED.** This is the run the release owed: the binary tested is the one inside
  `dist\RavenIron-Undertow-0.7.2.zip`, extracted from the zip and hash-matched (sha256 3CCABFF7…)
  across the zip, the server's plugin folder and the client profile BEFORE booting either, because
  the whole point was that no shipped binary had ever been loaded.

  **Server (Storm10, dedicated).** The config was deliberately AGED first — stamp put back to
  `ConfigVersion = 0` and `FlotsamTtlSeconds` moved off its default to 1500 — so the boot proved
  the round trip rather than the happy path. It logged `Loading [Undertow 0.7.2]`,
  `config: version 0 -> 1: nothing to migrate (stamping the layout version)`, `Harmony patched 3`,
  `Undertow v0.7.2 loaded.`, `wake console registered`, and
  `SeaTick online — authority=True, dedicated=True`. **The edited value survived**: the flotsam
  line reported `TTL 1500s`, not the shipped 1800. Afterwards the file opens with `[0 - Meta]` /
  `ConfigVersion = 1`, 1500 still in place, and no `.v0.bak` — correct, because a stamp-only plan
  changes nothing. The pre-test file is kept as `com.raveniron.undertow.cfg.pre072-roundtrip`.

  **Client (Gale `testing` profile).** `Loading [Undertow 0.7.2]`, `Harmony patched 3`,
  `DriftLines: armed on this client (graphics device Direct3D11)`, `Undertow v0.7.2 loaded.`,
  `SeaTick online — authority=False, dedicated=False`, `Ragnarok's Wrath detected — bridged`,
  `CurrentField live — seed -295822236, water level 30, tide 23%`, and
  `DriftLines: emitter built — 'Sprites/Default' (manual fog), pool 160`. The owner typed
  `wake status` and read **`config layout v1`**, which is the one piece of evidence that never
  reaches a log file at all: the console command writes through `Terminal.AddString`, not the
  logger, so it can only ever be read on screen.

  **Zero exceptions from any mod on either side**, and the mojibake repair is confirmed on the
  artifact rather than the source: the client log holds **0 double-encoded runs and 8 proper
  UTF-8 em dashes**, including the two lines that were broken.

  **STILL OWED, and neither blocks a release.** (a) **A client has never been SEEN to run a
  migration.** The profile's config was already stamped, so it correctly short-circuited and
  logged nothing — the behaviour is right, the observation is missing, and a file at version 1 is
  not evidence because `Finish` stamps a fresh file silently. Aging that config the way the
  server's was aged is the five-minute way to close it. (b) **No destructive rung has ever run
  anywhere** — all three tables are empty by measurement, so the only reachable path is the
  stamp. Watch the first real rebase or retirement on a copied config before it ships.

  The 2026-09-18 server-only run and its corrections follow, kept because the reasoning is the
  lesson.

- **8 — the config migration (0.7.1, corrected in 0.7.2). RUN IN-GAME 2026-09-18,
  ON A DEDICATED SERVER ONLY.** The owed run happened and nobody wrote it down;
  recorded here 2026-09-19 from the logs it left. Storm10's `BepInEx\LogOutput.log` carries
  `[Info : Undertow] config: version 0 -> 1: nothing to migrate (stamping the layout version)`
  at INFO, on a real config file, in the same chainloader pass where the sibling mods logged
  their own migrations. The file now opens with `[0 - Meta]` / `ConfigVersion = 1`, every
  other value intact, and **no `.v0.bak`** beside it — which is exactly right, because a
  stamp-only plan changes nothing and has nothing to back up. Nine later boots log no
  migration line at all, so the short-circuit works too. **The CLIENT leg is NOT proven and an
  earlier draft of this line claimed it was** (corrected 2026-09-19). The `testing` profile's
  config now reads `ConfigVersion = 1` with no `.v0.bak`, which is consistent with a migration —
  but Gale/BepInEx overwrite `LogOutput.log` on every relaunch and keep no rotation, the one
  surviving session logs Undertow 0.7.1 with NO migration line, and a stamp on a fresh file is
  written silently by `Finish` with nothing logged at all. A file at version 1 is therefore not
  evidence of anything; only the line is, and the line is gone. **Still owed:** a client boot
  with the line observed, the `wake status` reading, and the round trip of putting
  `ConfigVersion = 0` back by hand with one value edited off its default. **And no destructive rung has ever run anywhere** — all three
  tables are empty by measurement, so the only reachable path is the stamp.
  The original note follows.

- **8 (as first written). BUILT 2026-09-18.** BepInEx merges a new
  key at its SHIPPED default, which is a statement about a NEW world; when that differs from what an
  existing world already does, an owner who changed nothing gets different behaviour with nothing in
  the log. `Core/ConfigLedger.cs` holds the decisions (PURE, under test) and `Config/ConfigMigration.cs`
  the engine. Three step kinds — rebase, backfill, retire — and **all three tables are empty by
  MEASUREMENT**: Undertow's config has only ever grown, checked against the source at all four commits,
  the shipped 0.5.1/0.6.0 DLL string tables, and nine real config files. 0.5.1 was the first release and
  all 27 keys it published are still bound, so the retire surface is closed rather than unobserved.
  Version 1 only stamps. **Harness 248 → 357, 28 mutations applied to the shipping source and 28
  caught.** An adversarial review then raised 25 findings, 22 survived triple refutation, and six were
  real defects in already-green code — the comparer mismatch with BepInEx, a stamp that could move
  DOWN, a retirement that could delete a live string setting, a backfill read-back blind to clamping, a
  negative stamp costing 17.8 s on the boot thread, and one key judged by two rungs. All fixed and
  pinned. `docs/BACKLOG.md` task 8 has the detail and the in-game protocol that is still owed — and a
  FRESH install proves nothing here, because it migrates nothing and logs nothing.

**KNOWN LIMIT — CLOSED 2026-09-18 (history kept below, because the failure modes are the lesson).** RW's
season was client-blind: `SeasonSystem.Current` is set only in `Tick()`, which RW gates on the
simulation authority, so every client computed the field as spring. Boats never desynced (all
clients agree) but the seasonal shift was inert away from a listen host. The fix belongs in RW,
NOT a second season clock here — see rule 4 — and **Ragnarok's Wrath 0.25.0 added
`Net/SeasonSync`**, which broadcasts the season to every client. Nothing in Undertow changed or
needs to: `WrathBridge` reads `SeasonSystem.Current` exactly as before and starts getting a true
answer. Against an older RW it reads spring, as today, so there is no version floor.

**What 2026-09-12 proved, and what it did not.** With RW 0.27.0 loaded, the client log carries
`SeasonSystem: season arriving from the server — Spring`, and Undertow's own line reads
`season index 0 (read from Wrath)`. The MECHANISM is therefore live: that RW line only exists
post-SeasonSync and only fires on a received RPC, and `SeasonWasRead` was true rather than
defaulted. **But the season was Spring, which is also index 0, which is also every failure mode.**

**2026-09-18, on Storm10 (Valheim 1.0.15): a pure client received a NON-SPRING season.** The
client log reads `SeasonSystem: season arriving from the server — Summer.` — a value no failure
mode produces. Note the ordering trap it exposed: Undertow's own `CurrentField live — … season
index 0 (read from Wrath)` line is logged when SeaTick comes online, which is BEFORE the season
RPC arrives, so that line will always say 0 on a client; only a later read carries the received
value. **And it did:** minutes later the owner's `wake here` on that client printed
`tide 40% (flooding), season summer (Wrath)` — a non-spring season, read through the bridge, by
the same accessor `CurrentField` is fed from (the harness pins that a season change moves the
field). The check this file defined is met. Against an older RW a client still reads spring, as
before; there is no version floor.

**The model took three attempts and every one was killed by a measurement, not by review.** The
reasoning is in `Core/DriftForce.cs`; read it before touching the force. Three separate
INSTRUMENT failures cost more round-trips than the bugs did — see `docs/BACKLOG.md` task 2.

Every engine fact in *Known traps* below was read out of the shipping assembly with `ilspycmd`
on 2026-08-28, or measured on a live server. Nothing in this file is inferred from the shape of
a decompile without reading the body - three separate premises turned out to contradict the
obvious reading, and one of them was a note this file had previously stated as fact.

---

## Commands

Ported verbatim from `..\RagnaroksWrath\tools\` — they are debugged, and a second dialect of
the same script is a liability.

```powershell
.\tools\fetch-libs.ps1     # once per machine: copies game/BepInEx DLLs into libs\
.\tools\run-tests.ps1      # off-game logic tests (net10) — run before every commit
.\tools\package.ps1        # release zip; refuses on version drift, missing store files, wrong icon size
```

```powershell
dotnet build .\Undertow\Undertow.csproj -c Debug
```

`tools\package.ps1` builds `dist\RavenIron-Undertow-<version>.zip` and **refuses** on any of
three mistakes a hand-made zip invites: the plugin const, the csproj `<Version>` and
`manifest.json` disagreeing; a store file missing (`manifest.json`, `README.md`, `CHANGELOG.md`,
`icon.png`); or an icon that is not exactly 256x256, which the store rejects late and
without saying why. (The store is **Hexium**, hexium.gg, team `RavenIronStudios`. The zip is
built to Thunderstore's package FORMAT because that is what Hexium consumes — format and channel
are different things, and nothing here is ever uploaded to Thunderstore.) Every guard was tested by breaking it on purpose. Build releases with it,
never by hand — it also writes the zip entries itself, because PS 5.1's `Compress-Archive`
produces archives Hexium's parser rejects.

To inspect a game member — signature, accessibility, default values, or the actual method
body — decompile it. `dotnet tool install -g ilspycmd`, then:

```powershell
ilspycmd -r libs libs\assembly_valheim_publicized.dll -t Ship
```

**Read the body. Do not infer it from the shape of the output.** Every fact in this file that
turned out to matter contradicted a plausible guess: waves already respond to wind, tacking
already exists, and ships and swimmers need opposite injection mechanisms.

---

## Layout

Mirrors Ragnarok's Wrath, because a reader who knows one should be able to read the
other without relearning anything.

```
Undertow/                  one role-aware plugin (net472)
  Config/ModConfig.cs      config surface; every system has an on/off toggle
  Config/ConfigMigration.cs the migration's engine half — the only part that touches a real file
  Core/CurrentField.cs     the maths. PURE — no Unity, no config, no game types, no clock
  Core/SeaContext.cs       the seam: WorldGenerator/ZNet/ZoneSystem reads + the terrain probe
  Core/SeaTick.cs          the single time-budgeted cursor
  Core/IWorldSystem.cs     what ambient systems implement
  Core/DriftLineMath.cs    the drift lines' rules. PURE like CurrentField — no Unity, no config
  Core/ConfigLedger.cs     which config values move on an upgrade, and why. PURE, and under test
  Systems/                 Drift (ships), Flotsam, Swimmers
  Visuals/                 client-only cosmetics: DriftLines, WaterSurfaceCache, ParticleKit
  Bridge/WrathBridge.cs    reflected, read-only reads of Ragnarok's Wrath
  Patches/                 Harmony patches — Ship, Character
  Commands/                the `wake` console
tests/CoreTests/           net10 harness; compiles the REAL source against stubs
tools/                     scripts, ported from RagnaroksWrath
libs/                      gitignored; populated by fetch-libs.ps1
docs/BACKLOG.md            what to build next and in what order
```

Reference assemblies are **publicized** (`assembly_valheim_publicized.dll`) and resolved
through a relative `libs\` path, never a hardcoded Steam path — this repo gets cloned.

---

## House style — non-negotiable

**These rules were earned in FireFront and Ragnarok's Wrath, not here.** Each one cost a
measured production failure in a sibling project. Inherit them; do not re-derive them, and do
not write them up as though Undertow measured them. Rule 6 is the only one this project adds,
and it is a prior from a decompile rather than a scar.

1. **Harmony: prefixes for behaviour** (`Priority.Low`, honour `__runOriginal`, "no opinion"
   means `return true`) — and result-decorating postfixes at default priority where appending
   to a return value is the whole point. A decorating postfix never replaces logic and cedes
   every fight: whoever rewrites the value outright wins, and we decorate what survives. The
   max-priority-prefix-replace pattern is formally retracted — it cost ~50% of a sibling mod's
   entire patch-layer CPU, and `int.MaxValue` defeats every other mod's ordering including
   explicit `HarmonyBefore`. If one specific third-party mod ever forces the issue, put it
   behind a default-off toggle, never in the default path.

2. **No long-lived coroutines. Use a time-budgeted cursor driven from a single `Update`.**
   Every long-lived coroutine in this codebase's lineage independently grew the same bug: a
   `while (true)` whose body can `continue` past its only `yield`, hard-locking the game. It
   reached production once. `SeaTick` is the cursor; systems own no timers of their own.

3. **Keep cosmetics off the gameplay path.** A VFX call that throws inside a shared prefix
   aborts everything downstream. Visual work goes in its own try/catch with a `finally` that
   advances whatever state it owns.

4. **Never patch `EnvMan`, `WaterVolume`, materials, textures, or shaders.** Seasonality
   (RustyMods) and Seasons (shudnal) own environment selection; vanilla owns the water
   surface, and it owns it *twice* — `WaterVolume.CalcWave` feeds CPU buoyancy while the same
   global wind feeds the GPU shader. Change one and the boat floats on water the player cannot
   see. Undertow consumes weather and wave state as **read-only gameplay input** and never
   drives visuals with it.

5. **Publicized assemblies are COMPILE-TIME ONLY.** At runtime the game loads the real
   assembly with original accessibility, and Mono refuses private access. **The build is clean
   and the failure appears only in-game.** Reach private members through a cached
   `AccessTools.MethodDelegate` / `AccessTools.FieldRefAccess` / `AccessTools.Field`, resolved
   once and stored in a static. Keep retrying resolution rather than latching a failure; if the
   member is genuinely absent, log an error naming it, because that means Valheim's API moved.

   **Undertow hit this on its first day, 2026-08-28, and it behaved worse than the rule
   implies.** `Terminal.commands` is public in the publicized reference assembly and non-public
   in the real one; `Terminal.commands.ContainsKey("wake")` compiled with zero warnings and
   threw `FieldAccessException: Field 'Terminal:commands' is inaccessible` in-game. Two
   consequences worth knowing before you trust a `try`:

   - **A try/catch around the access does not help.** Mono resolves field access when the
     method is JIT'd, not when the line runs, so the whole method threw on entry and never
     reached its own catch. Harmony logged it; our handler never saw it.
   - **Everything else in that method died too.** The `ConsoleCommand` registration sat three
     lines above the offending read and never executed, so the console silently vanished — an
     instrument that disabled the feature it was measuring.

   So the rule is stronger than "wrap it": **never name a publicized-only member in code at
   all**, and keep the reflection resolution in a different method from the work.

   **In a Harmony patch, prefer field INJECTION to reflection.** `Ship.m_nview`, `m_body` and
   `m_players` are all private in the shipping assembly, and the drift postfix runs fifty times
   a second per boat — the worst possible place for a per-call reflection lookup or a
   FieldAccessException. Declaring `___m_nview`, `___m_body`, `___m_players` parameters makes
   Harmony generate the accessors, so the patch body contains no field reference at all and
   there is nothing to cache. A renamed field then fails loudly at PATCH time rather than
   silently at call time, which is the failure mode you want.

6. **Never assign `linearVelocity`, and never move anything you do not own.** This mod's
   entire write surface is force on a live `Rigidbody`, on the machine that owns it. Vanilla
   assigns `m_body.linearVelocity` wholesale inside the same tick we run in, so an assignment
   is either overwritten or eats vanilla's damping. And setting a ZDO's position does not move
   an object — it is a suggestion the owning machine overwrites next frame.

**Debugging discipline.** A silent success and a silent no-op are indistinguishable from
outside the game. When something "doesn't work", spend the round-trip on **one log line
proving the code ran at all** before spending it on another guess. When a symptom survives
several confident fixes, stop fixing and audit the instrument — a confident, well-formed,
wrong measurement is the most common cause of a long debugging session here.

**Measure before you push.** Undertow's specific version of the above: `CurrentField` must be
observable through the console (`wake here`) before one newton reaches a boat. A drift you
cannot read is a drift you cannot debug, and "the boat ended up somewhere odd" is the least
diagnostic bug report in this genre.

---

## Locked decisions — do not revisit without asking

| Decision | Answer |
|---|---|
| HUD / map / compass / wind gauge | **None.** Navigation instruments are a different mod; that was the other concept on the table when this one was chosen. |
| Visible current | **Yes, since 0.7.0 — diegetic only.** Drift lines on the water itself, by the owner's call on 2026-09-18. It is the sea showing its own motion and may never become an instrument: no screen-space element, no arrow, no number, no readout, no console verb that forces a bearing onto the water. Anything that reads the field out for the player is the HUD row above, and that row stands. Procedural, client-only, never networked, never saved. |
| Waves, water surface, shaders | **Never touched.** See rule 4. Vanilla's wave sim is shared, deterministic, and drives visuals. |
| New prefabs | **None.** `ZNetScene.CreateObjectsSorted` calls `DestroyZDO` on any hash it cannot resolve — silent data loss. Flotsam uses vanilla `ItemDrop`s only. |
| Unattended boat drift | **Default OFF.** Vanilla already damps an empty hull's horizontal velocity to a tenth per tick; that is a stated intent we honour. Losing a moored longship to a mod is a one-star review. |
| Rivers and lakes | **Ocean only for v1.** Narrow water plus a sideways force pins players against terrain. |
| Persistence | **None.** `CurrentField` is a pure function of seed, position, world time and season, so it needs no save file and no sync. Anything that makes the sea *remember* breaks that; RW already owns "the world remembers". |
| Ragnarok's Wrath | **Read-only, soft, one direction.** Reflected reads when present, fully dormant when absent, never a write back. |
| Moder's wind control | **No exemption from current.** "Moder gives you the wind, not the sea" — a limit on the power without a nerf to it. |
| Config migration | **The family's, not a local dialect.** Wu'barrk's shape by way of Valkyrie's Cargo, matching Ragnarok's Wrath and FireFront. Snapshot before any bind, `[0 - Meta] ConfigVersion` stamps the layout, a backup beside the file before anything destructive, and a failed migration never stops the mod loading. Do not fork it — a reader who knows one of these should read the others without relearning. |
| Console prefix | `wake` (e.g. `wake here`) |
| GUID / namespace | `com.raveniron.undertow` / `RavenIron.Undertow` |
| Name | **Undertow.** Norse sea names are crowded — check the existing sailing mods before renaming. |
| Timeline | Open-ended. Done when it's done. |

---

## Compatibility constraints

**Ragnarok's Wrath (Raven Iron)** — our own world simulation, and the only mod Undertow reads.
Three reflected surfaces, all soft; absence logs once and disables the feature, never errors:
`WeatherSystem.StormActive` / `StormCentre` (storm surge), `WindSystem.IntensityAt(Vector3)`
(RW's positional gameplay wind, already public and already computed per tick), and the season.
Reading RW's wind rather than `EnvMan` keeps rule 4 intact by construction — Undertow never
touches the environment at all.

✅ **VERIFIED 2026-08-28 (0.3.2)** on a real dedicated server, by parking and unparking RW
between boots: absent logs the dormant line and sails on; present resolves both members and
reads a real season. `Season` is `Spring = 0 .. Winter = 3` in RW's source, numerically
identical to `CurrentField`'s ordering, so the cast is a mapping rather than a guess.

⚠️ **One link remains unverified: that `IsStormAt` returns true with a storm overhead.** It
cannot be checked headless — **RW storms cannot fire on an empty server**, confirmed in RW's
source: the event carries `m_pauseIfNoPlayerInArea = true` and its position is chosen from
"somewhere a player actually is". See `docs/BACKLOG.md` task 3 for the five-minute protocol.

**RW must be on the CLIENT for surge to reach a boat.** Drift is computed by the peer that owns
the hull, so a client without RW computes no surge whatever the server believes.

**Seasonality (RustyMods) / Seasons (shudnal)** — no contact. Undertow never selects an
environment, never reads a season directly, and never touches a material. Where season matters
it arrives through RW, which already handles the either/or between those two mods.

**Other sailing and boat mods** — some of them also patch `Ship.CustomFixedUpdate`, and at least
one popular one adds force to the hull and caps its speed there. That is not a conflict in
itself: our postfix runs after any prefix and after vanilla, so the current arrives on top of
whatever the other mod did, and a speed cap simply absorbs it near the ceiling.

**Assume nothing, and do not write another author's implementation into this repo.** Before
declaring compatibility with any specific mod, install it and MEASURE — the drift acceptance in
task 2 run twice, once with the other mod and once without, comparing displacement. If they do
fight, the answer is a default-off compatibility toggle, never a priority war (house rule 1).

**Boat stat mods** (cargo, speed, durability) should compose without contact: they change the
hull, Undertow changes the water.

**Dive In (sighsorry)** — the diving mod; 1.2.0 sits in the owner's `Wonderland` Gale profile,
not on Ravenrest. ⚠️ **EXPECTED TO COMPOSE, NOT YET MEASURED.** Read from its published
GPL-3.0 source (<https://github.com/sighsorry1029/DiveIn>, last push 2026-08-08), never from
its DLL — see `docs/BACKLOG.md` task 5c for the protocol. Its whole contact with us is inside
`Character.UpdateSwimming`, the method our swimmer postfix decorates: a prefix that steers the
LOCAL player's `m_moveDir` and temporarily scales `m_swimSpeed` (skill up to x1.5, fast swim x2,
encumbered x0.5), and a postfix/finalizer pair that restores both. **It never writes
`m_currentVel`, never replaces or skips vanilla's lerp, and never touches `m_swimAcceleration`**
— so our per-frame addition lands on an unchanged servo and the 1/acceleration cancellation
still holds. All its patches run at default priority, as does ours, so which postfix runs first
is decided by load order, and the only thing that changes is which `m_swimSpeed` our cap sees.
Worked at defaults: the strongest drift we ever ask for is `1.2 × 1.6 × 0.5 = 0.96` m/s; the
cap is `0.35 × swimSpeed` as seen, from **0.35** (encumbered, seen) to **0.70** (restored);
the swimmer's real speed is at least **1.0** (encumbered) — so the drowning guard holds in
both orderings, but the encumbered-in-a-storm margin is 0.3 m/s where it is normally tenfold.
Ships, `WaterVolume` state we read, flotsam and creatures are untouched: it patches no `Ship`
member, its `WaterVolume` prefix is visual and we read no water-surface state, and its monster
diving is `MonsterAI`/`BaseAI` work our players-only gate never sees. It declares no
`BepInIncompatibility` against anything of ours.

**Sailing (Smoothbrain)** — the sailing-skill mod, and it is ON RAVENREST (1.1.8, speed factor
1.5 for every hull). ⚠️ **EXPECTED TO COMPOSE WITHOUT CONTACT, NOT YET MEASURED.** Read from its
published source (<https://github.com/blaxxun-boop/Sailing>, which stops at 1.1.7 — see
`docs/BACKLOG.md` task 2c for the gap), never from its DLL. **It is NOT the "adds force to the
hull and caps its speed" mod the paragraph above warns about.** It never patches
`Ship.CustomFixedUpdate` and never caps anything. Its whole effect on a hull is a
result-decorating postfix on `Ship.GetSailForce` — our own house pattern — scaling the SAIL
force by up to `1 + 1.5 = 2.5x` at skill 100 for the sailor at the helm, plus skill-gated
prefixes on `Ship.Forward` and `ShipControlls.Interact` that refuse a sail setting or the helm,
and a "nudge": one impulse of `10 × mass` along the player's facing, at most once a second,
when they hold Shift and use the ladder. So Sailing changes the PROPULSION and Undertow changes
the WATER, which is the boat-stat-mods case exactly. Two consequences worth knowing. With the
sail down `GetSailForce` is zero and 2.5 × 0 is still zero, so **task 2's drifting acceptance
should read IDENTICALLY with Sailing on or off** — the cleanest compatibility prediction this
mod has. Under sail, a boosted hull reaches the water's speed sooner and our saturation term
fades the push out sooner, which is the model working, not a conflict; the anti-braking clamp
means we can never slow the boost. The nudge is an ordinary impulse on the same rigidbody and
sums with ours. It declares incompatibility only with Valheim Plus.

**Njord (Wubarrk)** — the ship-handling overhaul, and it is ON RAVENREST (1.3.5, `Wind_AlwaysFull`
on, caps Raft 7 / Karve 16.8 / Longship 26 / Drakkar 30 m/s). ⚠️ **EXPECTED TO COMPOSE, NOT YET
MEASURED — AND THIS IS THE ONE THE BOAT-MOD WARNING ABOVE WAS WRITTEN FOR.** Configurable sail
and acceleration forces, per-hull speed caps, an overhauled physics curve. **No public source:
its website is a Discord invite and its licence reserves modification to the author, so nothing
here comes from its DLL, by rule.** Everything below is from its published README, changelog and
the Ravenrest config; `docs/BACKLOG.md` task 2d has the protocol. What can be said without the
source: our push is an `AddForce(Impulse)`, which integrates at the physics step AFTER every
`FixedUpdate` patch has run, so wherever Njord's cap clamps, it sees the hull before our push
lands and the overshoot is one tick's `dv` — `water × DriftStrength × dt ≈ 0.024` m/s at full
current, 0.038 in a storm — against caps of 7 to 30. But the cap and the push never actually
meet: our saturation term is ZERO whenever the hull already moves along the current faster than
the water (≤ 1.92 m/s, ever), and Njord's caps begin at 7. Up-current at the cap the push is
full but points against the hull's motion, which no magnitude clamp fights. If Njord REPLACES
vanilla's `CustomFixedUpdate` outright (unknown), our postfix still runs, still re-checks
`IsOwner()`, and still adds force — and the saturation model was built precisely so the
equilibrium is set by our term rather than by a race with the hull's damping. Njord's damping
is the first non-vanilla damping that claim has ever met, which makes task 2's `ALONG-RATIO`
under Njord the single most informative number left to measure. Its forces are propulsion, ours
is water; we push at the centre of mass with no torque, so `SteeringMultiplier` is untouched;
`Wind_AlwaysFull` is a SAIL change, not an `EnvMan` one, and we drive no hull by wind. One
cosmetic contact: BarrkBOT's export samples helmed hulls above 1 m/s, so a helmed hull drifting
at full current can bank a ~1.2 m/s "record" and odometer distance until the first real sail
overwrites it. Both mods are ServerSync-pinned, so a without-run parks Njord on BOTH sides.

**AwayFromHome (Wubarrk)** — no known interaction, since nothing here ticks on zone load state
and nothing spawns in unloaded ocean. Keep it that way: flotsam requires a real player nearby.

---

## Known traps

Verified by decompile 2026-08-28 unless marked otherwise.

- **`ConfigEntryBase.SetSerializedValue` SWALLOWS a value it cannot parse.** Its whole body is a
  try/catch that logs a BepInEx warning and leaves the entry untouched (read out of `libs\BepInEx.dll`
  2026-09-18). So a wrong value in `ConfigLedger` is a silent no-op followed by a confident version
  stamp — and the stamp means it never runs again. `ConfigMigration.ApplyBackfill` therefore reads the
  value BACK and says so by name when nothing moved. A try/catch around the call is unreachable code;
  both sibling mods have one.

- **`ConfigFile.OrphanedEntries` is PRIVATE, and BepInEx writes every orphan back out on each `Save`.**
  So a key you simply stop binding rides along in the file forever, and an owner can keep editing it to
  no effect. Dropping one is `Bind` under a throwaway default (which pulls it out of the orphan set)
  then `Remove` — both public. Never name the property; rule 5's Mono JIT failure applies.

- **BepInEx orders config sections by NAME when it writes the file**, so a section called "Meta" lands
  BELOW "1 - Core", not at the top. Undertow's stamp lives in **`0 - Meta`** for that reason. Ragnarok's
  Wrath states the opposite in a comment and is wrong about it, harmlessly. Worth knowing before
  copying that comment across.

- **`libs\BepInEx.dll` is NOT publicized** — fetch-libs copies it verbatim from the game's
  `BepInEx\core` — so what compiles against it is what runs, and house rule 5's trap does not apply to
  BepInEx members. It very much still applies to `assembly_valheim_publicized.dll`.

- **A STAMPED CONFIG FILE NEVER MIGRATES AGAIN, so a failed step must withhold the stamp.** This is
  the rule the whole migration turns on, and it splits failures in two. A retirement's drop is
  `Bind` then `Remove`, and BepInEx saves after each newly created entry, so a transient file lock —
  antivirus, cloud sync, a config manager, a second process in the same directory — takes it down
  through nobody's fault. That could succeed next time, so `ConsumeRetiredKey` returns false, `Apply`
  reports it, and `Finish` leaves the version unstamped. **The mirror mistake is treating every
  refusal that way:** a ledger row naming a key this build does not bind cannot succeed next time
  either, so withholding the stamp for it would re-run the migration on every boot forever, over a
  bug in our own table that no retry can fix. Those warn and let the stamp through. Undertow retires
  nothing today, which is exactly why `Finish` takes the plan as a parameter — the same seam, and the
  same reason, as `ConfigLedger.Plan` taking its tables.

- **A migration's summary is written BEFORE any step runs, so a refusal has to be folded back in.**
  `LastSummary` comes from `ConfigLedger.Describe(_plan)` inside `Begin`, and `wake status` prints it
  verbatim. `Apply` can then refuse a step and log a warning several hundred lines earlier — so the
  one line an owner actually reads claimed a key was dropped that is still sitting in the file.
  `_refused` counts them and `Finish` appends to the summary. Found by an adversarial audit of
  FireFront's port, 2026-09-18; all three mods had it.

- **`Utils.GetMainCamera()` does not exist in the shipping `assembly_utils`** — only
  `GetMainCameraFrustumPlanes()` does — and `GameCamera.m_camera` is private. A design review on
  2026-09-18 "verified" the former anyway; the decompile said otherwise. Use
  `UnityEngine.Camera.main` (Valheim's camera is tagged MainCamera) and fall back to the local
  player's position.

- **Valheim strips Unity's standard particle shaders.** `Particles/Standard Unlit` is confirmed
  absent; `Sprites/Default` is the first candidate that ships, and it is UNLIT, so a white
  particle glows at night unless the code dims it from the scene's own light. Inherited from
  Ragnarok's Wrath (its 0.7.0 shipped a fog nobody could see); `Visuals/ParticleKit.cs` carries
  the candidate chain verbatim and must never fork from RW's.

- **Slack water is rarer and smaller than a test expects.** On a flat seabed at default tuning,
  the slowest water within a kilometre of the origin is 0.24 m/s against a 0.144 m/s slack
  threshold, and the slack pockets that do exist around the stream function's nodes are tens
  of metres across. A scan that is too narrow or too coarse finds NO slack and any "slack
  behaves correctly" assertion passes vacuously — which is exactly what happened the first time
  the drift lines' cross-test ran (2026-09-18). The harness now scans 12 km at 40 m and asserts
  it saw both kinds of water.

- **`HorizontalBillboard` handedness, MEASURED 2026-09-18 on Storm10.** At `Particle.rotation`
  0 the quad's long (`startSize3D.x`, texture U) axis lies along world **+x**, and a positive
  rotation turns it **clockwise seen from above** (east toward north — heading decreasing). So
  the line's compass heading is `90 − rotation`, and `DriftLineMath.QuadRotationDegrees` is
  `90 − bearing`. `startSize3D.x` IS the U axis. The measurement: in uniform 191° water with
  ~80 streaks whose mean bearing matched the field, a rotation of `−bearing` laid every line at
  102° — a quarter turn across the flow — which only that convention produces; with
  `90 − bearing` the owner read them as "long ways along the flow".

- **A rotation check needs uniform water and many streaks, or it lies.** The first reading of
  the convention above was "90° off" against the SAME correct formula, taken in the slack node
  by spawn with two streaks in view whose own sampled bearings (196°, 118°) had nothing to do
  with the 8° the centre reported. One formula change and one more round-trip were spent on a
  confounded reading. Before judging orientation, get `wake lines` to show a mean streak bearing
  within a few degrees of the field's, with dozens active; only then does the eye measure the
  engine rather than the water.

- **`Ship.CustomFixedUpdate`'s owner check is INSIDE the method.**
  `if ((bool)m_nview && !m_nview.IsOwner()) return;` guards only the lines below it. **A
  Harmony postfix still runs on every client**, so a naive postfix has every peer pushing the
  same hull. Re-check `IsOwner()` in the postfix itself. This is the single easiest thing to
  get wrong in the whole mod.

- **Ships and swimmers need OPPOSITE mechanisms.** A ship is a damped rigidbody and vanilla
  adds forces to it, so `AddForce` works. A swimming `Character` is **servo-controlled**:
  `Character.UpdateSwimming` computes `force = m_currentVel - m_body.linearVelocity`, zeroes
  `force.y`, clamps it to 20, and applies it as `ForceMode.VelocityChange` every frame — so an
  external `AddForce` on a swimmer is **cancelled on the next tick**. Write to `m_currentVel`
  instead — but read the two traps below first, because the obvious way to do it is wrong twice
  over.

- 🚫 **`Character.AddPushbackForce` is a SHOVE, not a nudge. This entry previously recommended
  it and that advice was WRONG** — corrected 2026-08-28 by reading the body before shipping it.
  It looks like vanilla's sanctioned "fold an external push into the velocity target" helper.
  In fact it ignores `m_pushForce`'s MAGNITUDE entirely and drives velocity to a flat **20 m/s**
  along its direction — `velocity += normalized * (20f - num)` — halved to 10 while swimming. It
  exists to eject a body from a creature it is clipping through. A 0.3 m/s current routed
  through it would launch a swimmer at five times swim speed.

- **Adding to `Character.m_currentVel` is amplified by `1 / m_swimAcceleration`.** Vanilla lerps
  that target toward the swimmer's intent every frame, so a per-frame addition `d` settles at
  `d / m_swimAcceleration` — and vanilla's value is **0.05**, a twentyfold amplification. Scale
  by the acceleration first, or a 1 m/s current drags a swimmer at 20 m/s. `m_swimSpeed` and
  `m_swimAcceleration` are public; `m_currentVel`, `m_nview` and `UpdateSwimming` are not.

- **Copy vanilla's force convention from the method you are patching.**
  `CustomFixedUpdate` uses `AddForceAtPosition(v * m_body.mass, pos, ForceMode.Impulse)` with
  `num3 = fixedDeltaTime * 50f` folded in. Matching units matters more than being "correct" in
  isolation — a force in different units is a tuning value nobody can reason about.

- **`RandEventSystem.GetActiveEvent()` is ALWAYS NULL on a dedicated server, and is not the
  accessor you want.** Vanilla assigns `m_activeEvent` only inside
  `else if (m_randomEvent != null && (bool)Player.m_localPlayer)`, and then only when that local
  player is INSIDE the event area - so it means "is the local player standing in an event", and
  a headless server has no local Player at all. Measured 2026-08-28: identical code logged the
  storm on the client and nothing on the server. Use **`GetCurrentRandomEvent()`** - it returns
  `m_randomEvent`, set by the scheduler on the server and by `RPC_SetEvent` on every client
  (name, time, position), so it answers on all three roles regardless of where anyone stands.
  `RandomEvent.m_name`, `m_pos` and `m_eventRange` are all public.

- **A dedicated server UNLOADS an object's instance while its ZDO lives on, so never track a
  spawned thing by `GameObject`.** Flotsam held GameObject references and lost every one within
  a single tick — eight spawns in a live session each logged `[1/12 alive]`, because a nearby
  client takes the item over and the server drops the instance. That silently disabled BOTH the
  spawn cap and the TTL, i.e. the entire safety valve against filling the ZDO table, while the
  log looked perfectly healthy. Track `ZDO.m_uid` instead and look it up through
  `ZDOMan.instance.GetZDO(id)`; it survives the instance and is what actually identifies the
  thing. Measured and fixed 2026-08-28. **Take ownership before destroying** — only the owner may
  remove a ZDO, and a client may have claimed it: `if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID())`.

- **Another mod's ticked state is usually dead on clients; vanilla's replicated state is not.**
  Ragnarok's Wrath's `WeatherSystem.StormActive` is assigned only in `Tick()`, and its
  `WorldTick` returns early when the process is not the simulation authority - so on a pure
  client it is false forever. Anything computed on the peer that OWNS a hull (i.e. everything
  Undertow does to a boat) must therefore not depend on another mod's ticked state. Reach for
  the replicated vanilla fact underneath it instead. Storm surge was rewritten for exactly this
  before it ever shipped; RW's SEASON has the same problem and no such escape, and is documented
  as a known limit in `Bridge/WrathBridge`.

- **Vanilla's hull damping already assumes still water, so a current must be a PUSH and never a
  DRAG.** `m_damping`, `m_dampingForward` and `m_dampingSideway` in `Ship.CustomFixedUpdate` are
  computed against the hull's ABSOLUTE velocity. Adding a second drag term toward the water —
  the obvious `force ∝ (water − hull)` model — double-counts it, and only shows up under sail: a
  karve making 6 m/s in 0.3 m/s water would receive `0.6 × (0.3 − 6) ≈ −3.4 m/s²`, i.e. the sea
  as a brake on every boat under way. `DriftForce.Compute` therefore takes the water velocity
  and NOT the hull's, so the bug is unrepresentable rather than merely tested against. Caught by
  reasoning about the sailing case on 2026-08-28, after the wrong model was written AND its
  tests written to match it — a green harness agreeing with a wrong premise.

- **Valheim's open ocean has a FLAT floor at generator height exactly 0** — a uniform 30m
  depth against the water level, measured by transect on a live server 2026-08-28
  (`x=8000 h=0`, `x=9000 h=0`, while land points along the same line read 79.34, 30.47, 16.71,
  83.53…). Two consequences: **depth is a clean shore-proximity proxy**, which is what the
  coastal term is built on, and **any "shelf" threshold must sit below 30** or it silently
  classifies the entire sea as shelf. `FieldSettings.ShelfDepth` was 40 for exactly one
  afternoon because of this; a test now pins it under 30.
  `WorldGenerator.GetHeightMultiplier()` returns **200**, so generator heights are a base
  height scaled by 200 — worth knowing before comparing any raw number to a world coordinate.

- **Waves are shared, deterministic and shader-coupled.** `WaterVolume.CalcWave` sums ten
  trochoidal octaves scaled by `Mathf.Lerp(0f, wind.w, depth)` — wind intensity attenuated by
  depth — from wrapped day-time. CPU buoyancy and the GPU water shader read the same global
  wind, so they agree without syncing. Touch it and they stop agreeing.

- **Wind is global, uniform, and already deterministic.** `EnvMan.GetWindDir()` and
  `GetWindIntensity()` take no position; both come from time-seeded RNG octaves, which is why
  every client agrees with no network traffic. Do not make wind positional — RW already
  publishes a positional *gameplay* wind for exactly this, and two mods with different ideas
  about wind is the conflict rule 4 exists to prevent.

- **Vanilla already adds a positional force to ships**, at the world edge:
  `Ship.ApplyEdgeForce` pushes a hull back inside 10420m, ramping to 10500m. It is precedent
  for the technique — and a region where Undertow must not fight it. Fade current out past
  10400m.

- **Empty boats are already handled.** With `m_players.Count == 0`, vanilla forces
  `Speed.Stop` and multiplies horizontal velocity by 0.1 each tick. Respect that intent.

- **`m_sailForce` is dead outside `GetSailForce` and a gizmo.** It does not drive the sail
  mesh. So decorating `GetSailForce`'s return value is visually safe — worth knowing if wind
  ever needs a thumb on it, though nothing in the current design does that.

- **Rough water already damages hulls.** `Ship.UpdateWaterForce` deals 10 blunt when depth
  changes faster than 2.5 m/s, at most once per 2s. Anything that moves boats inherits this
  consequence for free — and could amplify it by accident.

- **Which vanilla item prefabs carry `Floating`? ANSWERED 2026-08-28, headless: 123 of 1090** (a client-side scan on 1.0.12 read 162 of 1523 — different role, not a like-for-like re-run).
  This was the question that could have sunk flotsam entirely — one raft of sunken loot
  disproves the approach — so it was measured before a spawner was written, and the pool is
  built from that scan rather than from a hand list. Driftwood was then seen floating on the
  surface by the owner, which is the one step no log could settle.

- **A mod adding a prefab MUST ship server-side** or `ZNetScene.CreateObjectsSorted` calls
  `DestroyZDO` on any hash it cannot resolve — silent data loss. We add no prefabs.

- **Valheim never releases ownership of a persistent ZDO when its owner disconnects.**
  Inherited from the lineage; relevant the moment flotsam exists.

- **Always use `InvariantCulture` for anything written to or parsed from disk.** A
  comma-decimal locale otherwise produces files that work locally and corrupt on a European
  server owner's machine. Undertow writes no save file today; config is still text.

- `ZRoutedRpc.Register` tops out at **6** type parameters, `ZNetView.Register` at **4**.
  (Inherited from Ragnarok's Wrath; not re-verified here.)

---

## Working agreement

- **Run `.\tools\run-tests.ps1` before every commit** once it exists. `CurrentField` is pure
  math and is exactly the kind of logic that fails silently. **Invoke it with
  `powershell -NoProfile -ExecutionPolicy Bypass`** from any script that judges its exit code: this
  machine's default policy refuses the file, and the refusal still exits non-zero. A 20-mutation suite
  scored 20/20 against that error on 2026-09-18 without compiling a line — the instrument failure this
  file warns about, produced by the tool meant to detect it.
- **The harness compiles the *shipping* source, not a copy.** A harness that duplicates logic
  proves nothing and drifts.
- **Prove a new test fails without its fix.** One revert-and-rerun turns a confident guess
  into a fact.
- **A clean build proves nothing about member access.** Anything reaching into game internals
  needs one in-game run before it is done.
- **Definition of done for every task:** tests green, project builds, and anything touching
  game internals has been run in-game once with its log line observed.
- **Verify game APIs by decompiling rather than assuming.** Three of this design's original
  premises were wrong, and all three were caught this way before any code existed.
- **Ask before changing anything in the locked-decisions table.** Those were deliberate calls.

