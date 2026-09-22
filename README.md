# Undertow

> *The sea remembers where it is going.*

A Valheim mod by **Raven Iron**. Valheim's ocean already has weather — waves that answer the
wind, storms that raise them, a hull that takes damage in a seaway. What it doesn't have is
**water**. Nothing moves. A karve left at half sail on a fixed heading arrives exactly where
geometry says it will, every time, in every part of the map.

Undertow gives the sea its own motion. Currents run across the ocean in a shape you can learn:
a slow basin drift, a stream that follows the coast, fast water between close islands, and dead
water behind a headland. They carry what floats on them.

**No map, no HUD, no icons.** The sea shows its own motion the way water does — foam lying
along the flow, moving with it, glassy where it goes dead — and nothing reads it out for you.

---

## What it does

### 🌊 The sea has a shape
A current field spread across the whole ocean, built from a stream function — so the flow is
**divergence-free**, the way real water is. Gyres, races and slack water all fall out of the
same mechanism rather than being placed by hand.

- **The drift** — slow, basin-scale, the thing you plan a long voyage around.
- **Coastal set** — a stream that follows the shore, with a slight push toward it. This is why
  you don't doze at the tiller with the coast downwind.
- **Races** — water accelerates between close landmasses. A narrow gap is fast water, in one
  direction, and the constriction is read at two scales, so a wide strait registers as well as a
  gap between rocks.
- **Slack and eddies** — where opposing arms meet, the water goes dead. Things collect there.

It is **the same on every machine**, and no water ever crosses the network: the field is a pure
function of the world seed, the position, the world clock and the season, so two players a
thousand metres apart compute the same sea without exchanging a byte of it. What they do have to
agree on is the tuning, and since 0.8.0 the server publishes that (see *On a server*, below).

### 🌒 Tides
A slow flood and ebb on a configurable cycle. It swings how hard the open ocean runs and
**reverses the coastal stream**, so the passage you know is a different passage six hours later.
Departure time becomes a decision.

Watched through a cycle on Valheim 1.0.15: one coastal point read sixteen times from `wake field`
as the tide ran — 0.585 m/s ESE at peak ebb, 0.272 m/s S at peak flood, the along-shore stream
reversing under an open-water drift that never turns, and the peak-flood reading predicted before
it was taken.

### ⛈ Storm surge *(with Ragnarok's Wrath)*
Where a Devastating Storm stands, the water rises — **there and nowhere else**. A sheltered
passage stops being sheltered while the storm sits over it. Entirely optional: without
Ragnarok's Wrath the bridge logs its absence once and the sea runs regardless.

### ⛵ Boats are carried, never braked
A hull under way — paddle or sail set — feels the current as drag relative to the water, which is
what a current is: going into it costs you exactly the water's speed over the ground, going with
it pays exactly that, whatever your hull, and your speed THROUGH the water is never touched.
(Through 1.0.0 the current was a constant push against any hull driving upstream, which stopped a
paddled karve in 0.2 m/s of water; 1.0.1 fixed it.) A boat left drifting settles at **the water's own speed**: a karve
re-measured on Valheim 1.0.15 read 0.99–1.00 against the water's 1.0, with and without Njord and
Sailing loaded. A karve and a longship measured together before Valheim 1.0 read 0.86 and 0.96 —
two hulls that damp very differently, both near the water's speed, which is the point; the
longship has not sailed again since. A hull also resists sideways drift more than
forward drift, so a current on the beam moves you less than one off the bow — that falls out of
Valheim's own per-hull physics rather than being imposed, and it differs from hull to hull.

**Boats with nobody aboard are not touched, by default.** Vanilla already damps an unmanned hull
almost to a stop, and a moored longship wandering off while you are away is not a feature.

### 🪵 Flotsam
Currents converge, so things gather. Driftwood, cargo and what the drowned no longer need
collect in slack water — giving you a second reason to know where the sea goes quiet.

Uses **only vanilla items**, never a new prefab. Capped, reclaimed on a timer, and spawned only
near a real player — an empty ocean stays empty. Watched on a dedicated server on Valheim 1.0.15:
twelve items spawned to the cap and the cap held; fourteen reclaims each landed at 1801 s against
an 1800 s timer, including items five kilometres from the only player; the server's whole object
table moved by exactly one per item; and ten minutes of empty server produced nothing and still
cleaned up.

### 🏊 Swimmers
The current carries a swimming body too, gently. It is **hard-capped below swim speed**: you can
always out-swim the water and reach shore. That is a safety property rather than a balance dial,
and it is enforced across every setting the config permits. Seen engaged on Valheim 1.0.15: in a
race running a full metre per second, with the drift factor at the top of its range, the swimmer's
drift read `0.4 (cap 0.4)` and a swimmer heading upstream made 1.57 m/s over the water — swim
speed minus the cap, to the second decimal.

### 🌫 The sea shows its set *(0.7.0)*
Faint foam streaks lie along the current on the water itself, move at the water's own speed and
ride the swell — thick in a race, sparse at a trickle, and thinning to almost nothing where the
sea goes slack. (Through 0.7 slack water was bare; since 0.8 the code admits a scattering there — a few
flecks at most, and easy to miss by eye — so `wake lines` in an empty-looking sea tells dead water
from a feature that is not running. `DriftLineSlackFloor = 0` restores the old contrast exactly.) From a drifting hull they hold station alongside; under sail they stream past at the
crab angle, and that angle is the set. Nothing states it: no arrow, no number, no screen element.
The streaks are symmetric end to end, so a glance gives you the line of the flow and only
watching gives you the sense — which is how a sailor reads a tide.

Client-side and cosmetic. It changes nothing about how a boat or swimmer moves, is never
networked or saved, and a dedicated server ignores it. Night, fog, distance and a big sea dim it
on their own; how much of the day the foam keeps at midnight is `DriftLineNightFloor` (0.7
shipped, set by eye on black water). `EnableDriftLines` turns it off; section `7 - Drift lines`
tunes count, radius, opacity, the night floor and a per-frame cost budget the mod enforces on
itself.

---

## Install

Drop `Undertow.dll` into `BepInEx/plugins/` on the **server and every client**. Boat physics runs
on whichever machine owns the hull — a player's — so a server-only install pushes nothing.

Config appears at `BepInEx/config/com.raveniron.undertow.cfg`. Every system has its own switch;
every rate, cap and threshold is tunable.

**You do not have to keep the config identical on every machine.** The field is recomputed
independently on each one, so two machines with different tuning would sail different oceans —
which is why the server publishes its gameplay tuning and every client sails by it for the
session (see *On a server*, below). Section `7 - Drift lines` and the other per-machine keys stay
yours by nature: foam is drawn by the client that looks at it, so two players on one deck see the
same set, density and speed from the same field, but not the same individual streaks.

**Your settings survive an update.** When a release changes a default, your file is read before
anything binds, a copy lands beside it as `.vN.bak` before any value is touched, and only values
still sitting at an old shipped default are moved. Anything you set yourself is kept, and named in
the log so you can see it was read and left alone. A `[0 - Meta]` section stamps which layout your
file was written for; leave it be. `wake status` reports it.

## The `wake` console

| Command | Answers |
|---|---|
| `wake status` | what this machine is, what is running on it, which config layout your file carries, and whether you are sailing the server's tuning or your own |
| `wake here` | the current under your keel — speed, bearing, depth, tide |
| `wake field <x> <z>` | the current anywhere, loaded or not |
| `wake drift` | whether the current is actually reaching boats |
| `wake floats` | which item prefabs carry `Floating` (the flotsam palette) |
| `wake lines` | whether the current is showing on the water, and what it costs (`wake lines reset` rebuilds it) |

Turn on `VerboseLogging` and the log carries a boot-time transect of your seed's ocean, plus a
running readout of drift while you sail.

## On a server: the server's sea wins

The current is a pure function of your world seed, so every machine works out the same water
without a byte of traffic — as long as they agree about the *tuning*. If a server raised
`MaxCurrentSpeed` and you did not, you and your crewmate were quietly sailing different oceans:
nothing desyncs, nothing errors, and nobody can tell.

So the server publishes its thirteen gameplay dials — the five that shape the field, the Ragnarok's
Wrath switch, and the drift and swimmer settings — and your client sails by those **for that
session only**. Your config file is never written to. Leave the server and your own settings are
exactly where you left them. Values arriving from a server are clamped to the range your build
allows, so a server can make the sea faster and never impossible.

**What stays yours.** The drift lines and everything about them, the tick budget, the field
refresh rate, verbose logging, and every flotsam setting. Those are your machine's frame rate,
not the server's sea.

**Admins can change it live.** If you are an admin, editing a synced value on your client sends
it to the server, which saves it to its own config and tells everyone. If you are not, the server
decides — with vanilla's own admin check, not your client's word for it — and your edit stays in
your own file.

Undertow does not have to be on a client at all — but a client without it computes no current for
the hulls it owns, because drift is applied by whoever owns the boat.

## Plays well with

- **Ragnarok's Wrath** (Raven Iron) — detected automatically. Storms raise the sea where they
  stand and the season shifts the drift. Optional; absence is logged once and changes nothing else.
- **Seasonality / Seasons** — never touched. Undertow selects no environment, reads no season
  directly, and modifies no material.
- **Njord** (Wubarrk) and **Sailing** (Smoothbrain) — measured together on Valheim 1.0.15: a
  drifting karve settles at the same speed with them as without, and Njord's per-hull speed cap
  and the current coexist without a judder. Njord changes the hull's handling, Sailing its
  propulsion, Undertow the water.
- **Boat stat mods** in general should compose for the same reason: they change the hull,
  Undertow changes the water.

## What it deliberately is not

No map, no compass, no wind gauge, no HUD of any kind — the drift lines are the sea, not an
instrument: nothing is drawn on the screen and nothing reads the current out. No new prefabs, assets or bundles. No
second wave system and no weather — vanilla and Seasonality own that ground. No boat rebalancing
and no new ship types. Undertow changes the sea and nothing else.

---

*Raven Iron. See also **Ragnarok's Wrath** (the world reacts and remembers) and **FireFront**
(fire that spreads).*

---

## Support Raven Iron

Every Raven Iron mod is free, and stays free — all of it, always. Nothing is held
back for patrons, and nothing ever will be.

If you'd like to help cover server hosting and test hardware:

- **Website** — <https://ravenirongames.com>
- **Patreon** — <https://www.patreon.com/cw/RavenIronGames>
- **Discord** — <https://discord.gg/AGKDEurAVa> — a channel per mod, and where the
  testing happens
