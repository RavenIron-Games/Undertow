<!-- README schema v2 -->
# 🌊 Undertow

![UNDERTOW](https://img.shields.io/badge/UNDERTOW-THE_SEA_GETS_ITS_OWN_MOTION-c9a227?style=for-the-badge&labelColor=1b1b1b)
![VERSION](https://img.shields.io/badge/VERSION-1.0.4-8b1e1e?style=for-the-badge&labelColor=1b1b1b)
![CLIENT & SERVER](https://img.shields.io/badge/CLIENT_%26_SERVER-SERVER_AND_EVERY_PLAYER-1f4e79?style=for-the-badge&labelColor=1b1b1b)

> *The sea remembers where it is going. The old steersmen knew it, and none of them dozed at the tiller with the coast downwind. Midgard's water forgot for a while. Now it remembers again.*

### Valheim's sea has weather. Undertow gives it water.

Waves answer the wind, storms raise them, a hull takes damage in a seaway. But nothing moves: a karve left at half sail on a fixed heading arrives exactly where geometry says it will, every time, anywhere on the map. Undertow gives the sea its own motion. Currents run across the ocean in a shape you can learn, and they carry what floats on them.

**No map, no HUD, no icons.** The sea shows its own motion the way water does: foam lying along the flow, moving with it, glassy where it goes dead. Nothing reads it out for you.

---

![NEW IN 1.0.4](https://img.shields.io/badge/NEW_IN_1.0.4-VALHEIM_1.0.21-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**This is the Valheim 1.0.21 build of Undertow.**

| You | It |
|---|---|
| **Play on Valheim 1.0.21** | Undertow runs on it; the sea, the drift and your settings are the same as before |
| **Run a server** | update the server and every player together: Valheim 1.0.21 is network version 41 |

Nothing else is new in this version.

---

![BEFORE YOU INSTALL](https://img.shields.io/badge/BEFORE_YOU_INSTALL-READ_THIS-b8860b?style=for-the-badge&labelColor=1b1b1b)

| | If… | Then… |
|---|---|---|
| 🤝 | you play on a **server** | put `Undertow.dll` on the **server and every player**. A boat feels the current on the machine that owns it, which is a player's, so a server-only install moves no boats. |
| 🧭 | you want to **tune the sea** for a server | tune it on the server. Its sea wins: every player sails by the server's dials for the session, and nobody's own file is written. |
| ⚓ | you **moor a boat** and walk away | it stays put. A boat with nobody aboard is left alone by default. |
| 💾 | you **update** from an older Undertow | your settings are kept. A copy of your file is saved beside it first, and only values still at an old default are moved. |

---

![I](https://img.shields.io/badge/I-THE_SEA_HAS_A_SHAPE-c9a227?style=for-the-badge&labelColor=1b1b1b)

**One current across the whole ocean, in a shape you can learn and plan a voyage around.**

| | |
|---|---|
| 🌀 **The drift** | slow, basin-wide water: the thing you plan a long voyage around |
| 🏝️ **Coastal set** | a stream that follows the shore, with a slight push toward it |
| 💨 **Races** | water speeds up between close landmasses: a narrow gap is fast water, in one direction |
| 🪞 **Slack and eddies** | where opposing arms meet, the water goes dead, and things collect there |

<details>
<summary>📖 <b>Read the saga</b> — one sea on every machine, and nothing sent over the network</summary>

The current is built from a stream function, so the flow is **divergence-free**, the way real water is. Gyres, races and slack water all fall out of the same mechanism rather than being placed by hand. The narrows are read at two scales, so a wide strait registers as well as a gap between rocks.

It is **the same on every machine**, and no water ever crosses the network. The field is a pure function of the world seed, the position, the world clock and the season, so two players a thousand metres apart compute the same sea without exchanging a byte of it. What they do have to agree on is the tuning, and the server publishes that (see **MULTIPLAYER** below).

</details>

---

![II](https://img.shields.io/badge/II-BOATS_ARE_CARRIED%2C_NEVER_BRAKED-1f4e79?style=for-the-badge&labelColor=1b1b1b)

**The current changes where you go, never how hard your hull can row.**

| Your boat | The water |
|---|---|
| **Under way**, paddle or sail set | going into the current costs you exactly the water's speed over the ground; going with it pays you exactly that. Your speed *through* the water is never touched. |
| **Adrift** | carries you until you move at the water's own speed, whatever the hull |
| **Beam-on** to the current | moves you less than a current off the bow, because a hull resists sideways drift more than forward drift |
| **Nobody aboard** | not touched, by default |

<details>
<summary>📖 <b>Read the saga</b> — why a drifting karve and a drifting longship agree</summary>

A hull under way feels the current as drag relative to the water, which is what a current is. That is the same for every hull, so what a headwater costs you never depends on how small a boat's engine is.

A boat left drifting settles at the water's own speed. A karve was measured doing exactly that on the shipping game, with and without Njord and Sailing loaded, and a karve and a longship measured together earlier both came out near the water's speed, though their hulls damp very differently. The sideways resistance is not imposed by Undertow: it falls out of Valheim's own per-hull physics, so it differs from hull to hull.

**Why unmanned boats are left alone.** Vanilla already damps an unmanned hull almost to a stop, and a moored longship wandering off while you are away is not a feature. A crew that moors properly and wants the sea to punish the ones who do not can raise `UnattendedDriftFactor`.

</details>

---

![III](https://img.shields.io/badge/III-TIDES_AND_STORMS-5b2c83?style=for-the-badge&labelColor=1b1b1b)

**The passage you know is a different passage half a tide later.**

| | |
|---|---|
| 🌒 **Tides** | a slow flood and ebb on a cycle you set. It swings how hard the open ocean runs and **reverses the coastal stream**, so departure time becomes a decision. |
| ⛈️ **Storm surge** *(with Ragnarok's Wrath)* | where a Devastating Storm stands, the water rises, there and nowhere else. A sheltered passage stops being sheltered while the storm sits over it. |
| 🍂 **Seasons** *(with Ragnarok's Wrath)* | the season shifts the drift |

<details>
<summary>📖 <b>Read the saga</b> — a tide watched from one point</summary>

One coastal point was read again and again through a tide with `wake field`: the stream along the shore reversed between ebb and flood, under an open-water drift that never turns, and the peak-flood reading was predicted before it was taken.

A full tide takes two in-game days by default. Ragnarok's Wrath is entirely optional: without it Undertow logs its absence once and the sea runs regardless.

</details>

---

![IV](https://img.shields.io/badge/IV-FLOTSAM-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

**Currents converge, so things gather: a second reason to know where the sea goes quiet.**

| | |
|---|---|
| 🪵 **What washes up** | driftwood and cargo, and now and then what the drowned no longer need |
| 🪞 **Where** | in slack, deep water near a real player: an empty ocean stays empty |
| ⚖️ **How much** | capped, and reclaimed on a timer, so a long-running world never fills up with driftwood |
| 🧺 **Only vanilla items** | never a new prefab |

<details>
<summary>📖 <b>Read the saga</b> — the cap, the clock and the empty server</summary>

Flotsam is the server's job, and it was watched on a dedicated server: items spawned up to the cap and the cap held; each one was reclaimed on its timer, including items kilometres from the only player; the server's whole object table moved by exactly one per item; and an empty server produced nothing and still cleaned up. In single player and on a hosted game, the host's own player counts like everyone else.

The items come from lists you can change. They must be items that float in Valheim, or they sink out of reach, and `wake floats` lists them. Floating is Valheim's loss-prevention marker rather than buoyancy, so an iron shield floats and ore does not: choose for the story, not for physics.

</details>

---

![V](https://img.shields.io/badge/V-SWIMMERS-1f4e79?style=for-the-badge&labelColor=1b1b1b)

**The current carries a swimmer too, gently, and you can always out-swim it.**

| | |
|---|---|
| 🏊 **Carried** | a swimming body is set down the coast by the water, at a share of its speed |
| 🛟 **Capped below swim speed** | you can always make headway against the water and reach shore, at every setting the config allows |

<details>
<summary>📖 <b>Read the saga</b> — a safety property, not a balance dial</summary>

If a player can be held offshore by the current until they drown, the feature is wrong rather than mistuned, so the cap is enforced across every value the config permits. It was seen engaged in a race running a full metre per second, with the drift factor at the top of its range: a swimmer heading upstream still made headway, at their swim speed minus the cap.

</details>

---

![VI](https://img.shields.io/badge/VI-THE_SEA_SHOWS_ITS_SET-c9a227?style=for-the-badge&labelColor=1b1b1b)

**Faint foam streaks lie along the current on the water itself. Nothing else tells you where it runs.**

| On the water | It means |
|---|---|
| **Thick streaks** | a race: fast water |
| **Sparse streaks** | a trickle |
| **A few faint flecks, or almost nothing** | slack water |
| **Streaks holding station alongside** | you are drifting with the water |
| **Streaks streaming past at an angle** under sail | that angle is the set |

<details>
<summary>📖 <b>Read the saga</b> — reading the foam like a sailor reads a tide</summary>

The foam moves at the water's own speed and rides the swell. The streaks are symmetric end to end, so a glance gives you the line of the flow and only watching gives you its direction. There is no arrow, no number and no screen element.

Slack water keeps a scattering of faint flecks on purpose, so an empty-looking sea can be told apart from a mod that is not running: `wake lines` answers that. Set `DriftLineSlackFloor` to 0 if you want slack water strictly bare.

Night, fog, distance and a big sea dim the foam on their own, and `DriftLineNightFloor` sets how much of its daytime brightness it keeps at midnight. It is drawn by each player's own game, cosmetic only: it changes nothing about how a boat or swimmer moves, is never networked or saved, and a dedicated server ignores it. It keeps to a per-frame cost budget of its own, and thins itself out and says so in the log if it goes over. Two players on one deck see the same set, density and speed, but not the same individual streaks.

</details>

---

![VII](https://img.shields.io/badge/VII-WHAT_IT_IS_NOT-8b1e1e?style=for-the-badge&labelColor=1b1b1b)

**Undertow changes the sea and nothing else.**

| | |
|---|---|
| 🚫 **No instruments** | no map, no compass, no wind gauge, no HUD of any kind: the foam is the sea, not an instrument |
| 📦 **No new assets** | no new prefabs, assets or bundles |
| 🌦️ **No second weather** | no second wave system and no weather: vanilla and Seasonality own that ground |
| ⛵ **No boat rebalancing** | no boat rebalancing and no new ship types |

---

![THE TOOLBOX](https://img.shields.io/badge/THE_TOOLBOX-QUICK_START_%C2%B7_COMMANDS_%C2%B7_HOW--TO-1b1b1b?style=for-the-badge&labelColor=8b1e1e)

**First ten minutes:** put Undertow on the server and every player → sail out and stop paddling in open water → watch the foam beside the hull → open the console (F5) and type `wake here`.

| Command (F5) | Answers |
|---|---|
| `wake status` | what this machine is, what is running on it, which config layout your file has, and whether you sail by the server's tuning or your own |
| `wake here` | the current under your keel: speed, bearing, depth, tide |
| `wake field <x> <z>` | the current anywhere, loaded or not |
| `wake drift` | whether the current is actually reaching boats |
| `wake floats` | which items float, and so can wash up as flotsam |
| `wake lines` | whether the current is showing on the water, and what it costs · `wake lines reset` rebuilds it |

<details>
<summary>🛠️ <b>See it running</b></summary>

A current has no sound and no icon, so the log is how you watch the mod work. Turn on `VerboseLogging` and the log carries a survey of your seed's ocean at start-up, plus a running readout of the drift while you sail.

</details>

<details>
<summary>🛠️ <b>Change the sea on a live server</b></summary>

If you are an admin, edit a synced value on your own client while connected. It goes to the server, which saves it to its own config and tells every player. If you are not an admin, the server decides, using vanilla's own admin check rather than your client's word for it, and your edit stays in your own file.

</details>

<details>
<summary>🛠️ <b>Choose what washes up</b></summary>

Type `wake floats` to see which items float. Put the ones you want in `FlotsamCommon` and `FlotsamRare`, comma separated, on the machine that spawns the flotsam: the server, or the host.

</details>

<details>
<summary>🛠️ <b>Keep your settings across an update</b></summary>

When a release changes a default, your file is read before anything else, a copy lands beside it as `.vN.bak` before any value is touched, and only values still at an old shipped default are moved. Anything you set yourself is kept, and named in the log so you can see it was read and left alone. The `[0 - Meta]` section stamps which layout your file was written for; leave it be. `wake status` reports it.

</details>

---

![MULTIPLAYER](https://img.shields.io/badge/MULTIPLAYER-THE_SERVER%27S_SEA_WINS-1f4e79?style=for-the-badge&labelColor=1b1b1b)

| Works with vanilla players | Needs Undertow on the server | Needs Undertow on both ends |
|---|---|---|
| a player without Undertow can join · their own boats feel no current and they see no foam | flotsam · one tuning for everyone, published to every player | the same sea for everyone: the current on boats and swimmers is worked out on each player's own machine, by the server's tuning |

**The server's sea wins.** The current is worked out from the world seed on every machine, so they only have to agree on the tuning. The server publishes its thirteen gameplay dials (the shape of the current, the Ragnarok's Wrath switch, the drift and the swimmer settings) and every player sails by those **for that session only**. Your config file is never written to; leave the server and your own settings are exactly where you left them. Values from a server are clamped to the range your Undertow allows, so a server can make the sea faster and never impossible. The foam, the cost budgets, the logging and the flotsam settings stay each machine's own.

**Update together.** Keep the server and its players on the same Undertow, and update them together.

---

![COMPATIBILITY](https://img.shields.io/badge/COMPATIBILITY-PLAYS_WELL_WITH_OTHERS-2e5e3a?style=for-the-badge&labelColor=1b1b1b)

| Mod | |
|---|---|
| **Ragnarok's Wrath** (Raven Iron) | found by itself. Storms raise the sea where they stand and the season shifts the drift. Optional: its absence is logged once and changes nothing else. |
| **Seasonality · Seasons** | never touched. Undertow selects no environment and modifies no material. |
| **Njord** (Wubarrk) **· Sailing** (Smoothbrain) | measured together: a drifting karve settles at the same speed with them as without, and Njord's per-hull speed cap and the current get on without a judder. Njord changes the hull's handling, Sailing its propulsion, Undertow the water. |
| **Boat stat mods** in general | should get on for the same reason: they change the hull, Undertow changes the water. |

---

![SETTINGS](https://img.shields.io/badge/SETTINGS-BEPINEX_CONFIG-b8860b?style=for-the-badge&labelColor=1b1b1b)

All in `BepInEx/config/com.raveniron.undertow.cfg`. Every system has its own switch, and every rate, cap and threshold can be tuned.

<details>
<summary>🔒 <b>Server-synced</b> — set on the server; every player sails by them</summary>

| Section | Setting | What it does |
|---|---|---|
| 2 - Systems | `EnableDrift` | the current's effect on boats |
| 2 - Systems | `EnableSwimmers` | whether the current carries swimmers |
| 2 - Systems | `EnableWrathBridge` | read Ragnarok's Wrath for storm surge and the season |
| 3 - The current | `MaxCurrentSpeed` | the size of the sea: the fastest the water runs anywhere |
| 3 - The current | `TidePeriodSeconds` · `TideAmplitude` | how long one flood and ebb takes, and how much the tide swings the open ocean |
| 3 - The current | `CoastalStrength` | how strong the stream along the shore runs; 0 for open-ocean drift only |
| 3 - The current | `StormSurgeMultiplier` | how much harder the water runs under a Ragnarok's Wrath storm |
| 4 - Drift | `DriftStrength` | how quickly a drifting boat takes up the water's speed |
| 4 - Drift | `UnderWayDragFactor` | how much of the current a boat under way pays; 0 ignores it, 2 grips twice as hard |
| 4 - Drift | `UnattendedDriftFactor` | how much of the current moves a boat with nobody aboard (0 by default) |
| 6 - Swimmers | `SwimmerDriftFactor` · `SwimmerMaxShareOfSwimSpeed` | how much of the water's speed carries a swimmer, and the hard cap below swim speed |

</details>

<details>
<summary>🖥️ <b>Local</b> — each machine's own</summary>

| Section | Settings |
|---|---|
| **1 - Core** | `TickBudgetMs` · `VerboseLogging` |
| **2 - Systems** | `EnableFlotsam` · `EnableDriftLines` |
| **4 - Drift** | `FieldRefreshSeconds` |
| **5 - Flotsam** (read where flotsam spawns: the server, or the host) | `FlotsamIntervalSeconds` · `FlotsamPerHour` · `FlotsamMinDepth` · `FlotsamRingMinMeters` · `FlotsamRingMaxMeters` · `FlotsamMaxAlive` · `FlotsamTtlSeconds` · `FlotsamRareChance` · `FlotsamCommon` · `FlotsamRare` · `FlotsamWreckage` |
| **7 - Drift lines** (the foam) | `DriftLineCount` · `DriftLineRadius` · `DriftLineOpacity` · `DriftLineMinDepth` · `DriftLineSlackFloor` · `DriftLineNightFloor` · `DriftLineLiftMetres` · `DriftLineBudgetMs` |
| **0 - Meta** | `ConfigVersion` (which layout your file was written for; leave it be) |

</details>

---

![CREDITS](https://img.shields.io/badge/CREDITS-THANK_YOU-1b1b1b?style=for-the-badge&labelColor=c9a227)

**Needs:** [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) `5.4.2360` · works alongside **Ragnarok's Wrath** when it is installed.

*A Valheim mod by Raven Iron.* See also **Ragnarok's Wrath** (the world reacts and remembers) and **FireFront** (fire that spreads).

**Support Raven Iron.** Every Raven Iron mod is free, and stays free: all of it, always. Nothing is held back for patrons, and nothing ever will be. If you'd like to help cover server hosting and test hardware:

- **Website** — <https://ravenirongames.com/>
- **Patreon** — <https://www.patreon.com/cw/RavenIronGames>
- **Discord** — <https://discord.gg/AGKDEurAVa> — a channel per mod, and where the testing happens
