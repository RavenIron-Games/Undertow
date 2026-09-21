using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using RavenIron.Undertow.Config;
using RavenIron.Undertow.Core;

namespace Undertow.Tests
{
    /// <summary>
    /// Off-game harness for the pure-logic core. No test framework by design — a console
    /// program returning a nonzero exit code is enough, and adds no dependency to keep current.
    ///
    /// At 0.1.0 there is exactly one shipping file with logic in it, and the tests below are
    /// not filler: both failures they catch are silent in-game and arbitrarily delayed. A field
    /// declared but never bound is a NullReferenceException at whatever future moment something
    /// first reads it, and a duplicated section/key pair makes two config entries share one
    /// value with no error anywhere. Neither shows up in a build.
    ///
    /// This grows into the real harness at task 1, when CurrentField arrives — determinism,
    /// seasonal reversal, tide phase and magnitude bounds are all testable here without
    /// launching Valheim, and all four fail silently if they are wrong.
    /// </summary>
    public static class Program
    {
        private static int _passed;
        private static int _failed;

        public static int Main()
        {
            Console.WriteLine("Undertow — core tests\n");

            ModConfigTests();
            ConfigLedgerTests();
            ConfigMigrationTests();
            ConfigWireTests();
            CurrentFieldTests();
            DriftForceTests();
            DriftUnderWayTests();
            FlotsamMathTests();
            SwimDriftTests();
            DriftLineMathTests();

            Console.WriteLine($"\n{_passed} passed, {_failed} failed.");
            return _failed == 0 ? 0 : 1;
        }

        // ---- terrain fixtures ---------------------------------------------------------------

        /// <summary>Featureless deep ocean. Isolates the open-water stream function.</summary>
        /// <summary>
        /// Wraps another probe and counts the calls. The terrain probe is the mod's dominant
        /// runtime cost — in game it is <c>WorldGenerator.GetHeight</c>, Perlin noise plus biome
        /// work — and the arithmetic around it was measured at 0.23-0.34 us, i.e. irrelevant
        /// beside it. So the number that matters is how many probes ONE evaluation makes, and
        /// that number is pinned below rather than left to be rediscovered.
        /// </summary>
        private sealed class CountingProbe : ITerrainProbe
        {
            public int Calls;
            private readonly ITerrainProbe _inner;
            public CountingProbe(ITerrainProbe inner) { _inner = inner; }
            public float HeightAt(float x, float z) { Calls++; return _inner.HeightAt(x, z); }
        }

        private sealed class FlatSeabed : ITerrainProbe
        {
            private readonly float _height;
            public FlatSeabed(float height) { _height = height; }
            public float HeightAt(float x, float z) => _height;
        }

        /// <summary>
        /// A cone island at the origin: seabed rises linearly toward the middle and breaks the
        /// surface inside `shoreRadius`. Enough to exercise the shore gradient, the shelf ramp
        /// and the waterline fade with arithmetic a reader can check by hand.
        /// </summary>
        private sealed class ConeIsland : ITerrainProbe
        {
            private readonly float _shoreRadius, _slope, _deepHeight;
            public ConeIsland(float shoreRadius, float slope, float deepHeight)
            { _shoreRadius = shoreRadius; _slope = slope; _deepHeight = deepHeight; }

            public float HeightAt(float x, float z)
            {
                double r = Math.Sqrt((double)x * x + (double)z * z);
                float h = 30f + (float)((_shoreRadius - r) * _slope);
                return h < _deepHeight ? _deepHeight : h;
            }
        }

        /// <summary>Two banks either side of a north-south channel down x = 0.</summary>
        private sealed class Channel : ITerrainProbe
        {
            private readonly float _halfWidth;
            public Channel(float halfWidth) { _halfWidth = halfWidth; }
            public float HeightAt(float x, float z)
            {
                float over = Math.Abs(x) - _halfWidth;
                if (over <= 0f) return -40f;          // deep in the channel
                return -40f + over * 0.5f;            // banks rising away from it
            }
        }

        private static void CurrentFieldTests()
        {
            Section("CurrentField");

            FieldSettings s = FieldSettings.Defaults;
            var deep = new FlatSeabed(-60f);
            const int seed = 1337;

            // ---- determinism: the entire no-sync architecture rests on this ----------------
            // If two machines can disagree about the current at a point, every boat desyncs and
            // no amount of tuning saves it.
            FieldSample a = CurrentField.Evaluate(1500f, -800f, seed, 12345.0, 1, 1f, deep, s);
            FieldSample b = CurrentField.Evaluate(1500f, -800f, seed, 12345.0, 1, 1f, deep, s);
            Check(a.X == b.X && a.Z == b.Z, "same inputs give bit-identical output");

            // ---- THE COST OF ONE EVALUATION, pinned -----------------------------------------
            // The terrain probe is this mod's dominant runtime cost: in game it is
            // WorldGenerator.GetHeight, and the arithmetic wrapped around it measured at
            // 0.23-0.34 us per call (2026-09-19), which is nothing beside it. So what a busy
            // server costs is decided almost entirely by how many probes one Evaluate makes and
            // how often Evaluate is called.
            //
            // NINE, measured rather than counted off the source: one for the height here, four
            // for the gradient, and two apiece at the two race distances. Pinned so that adding
            // a tenth is a decision somebody makes on purpose. At the shipped cadence this is
            // 36 GetHeight a second for a crewed hull and the same for a swimmer, both of which
            // refresh on FieldRefreshSeconds.
            var counter = new CountingProbe(new FlatSeabed(-60f));
            CurrentField.Evaluate(1500f, -800f, seed, 12345.0, 1, 1f, counter, s);
            Check(counter.Calls == 9,
                "one Evaluate costs exactly 9 terrain probes (got " + counter.Calls + ") - the mod's dominant runtime cost, pinned");

            // Shallow water short-circuits before the race probes, so it must cost LESS, never
            // more. A change that made the cheap case expensive would not show up above.
            var shallowCounter = new CountingProbe(new FlatSeabed(29.5f));
            CurrentField.Evaluate(1500f, -800f, seed, 12345.0, 1, 1f, shallowCounter, s);
            Check(shallowCounter.Calls <= 9,
                "water too shallow to drift costs no more than deep water (got " + shallowCounter.Calls + ")");

            FieldSample other = CurrentField.Evaluate(1500f, -800f, seed + 1, 12345.0, 1, 1f, deep, s);
            Check(other.X != a.X || other.Z != a.Z, "a different seed gives a different field");

            // ---- magnitude bounds ----------------------------------------------------------
            // Swept rather than spot-checked: a ceiling that holds at one point and not at
            // another is the failure mode that reaches a player as a boat flung across the map.
            float peak = 0f;
            bool everNegative = false;
            for (int ix = -20; ix <= 20; ix++)
            for (int iz = -20; iz <= 20; iz++)
            {
                FieldSample p = CurrentField.Evaluate(ix * 350f, iz * 350f, seed, 500.0, 2, 1f, deep, s);
                if (p.Speed > peak) peak = p.Speed;
                if (p.Speed < 0f) everNegative = true;
                float measured = (float)Math.Sqrt(p.X * p.X + p.Z * p.Z);
                if (Math.Abs(measured - p.Speed) > 0.002f) everNegative = true; // reuse as a fail flag
            }
            Check(peak <= s.MaxSpeed + 1e-4f, $"speed never exceeds MaxSpeed over 1681 points (peak {Fmt(peak)})");
            Check(!everNegative, "reported Speed always matches the vector magnitude, and is never negative");
            Check(peak > 0.05f, $"the open ocean is not dead calm everywhere (peak {Fmt(peak)})");

            // The sweep above does NOT exercise the ceiling: at default settings the natural
            // magnitude never reaches 1.2, so it passed with the clamp deleted. Measured, not
            // assumed — 2026-08-28. Re-run it against a ceiling low enough that the clamp MUST
            // engage, on terrain that also fires the coastal and race multipliers.
            FieldSettings tight = s;
            tight.MaxSpeed = 0.25f;
            var shelfIsland = new ConeIsland(shoreRadius: 300f, slope: 0.5f, deepHeight: -60f);
            float tightPeak = 0f;
            bool clampEngaged = false;
            for (int ix = -12; ix <= 12; ix++)
            for (int iz = -12; iz <= 12; iz++)
            {
                FieldSample p = CurrentField.Evaluate(ix * 90f, iz * 90f, seed, 700.0, 3, 1f, shelfIsland, tight);
                if (p.Speed > tightPeak) tightPeak = p.Speed;
                if (p.Speed > tight.MaxSpeed - 1e-4f) clampEngaged = true;
            }
            Check(clampEngaged, "the low-ceiling sweep actually reaches the ceiling (otherwise it proves nothing)");
            Check(tightPeak <= tight.MaxSpeed + 1e-4f,
                $"the ceiling holds where it is actually binding (peak {Fmt(tightPeak)} vs cap {Fmt(tight.MaxSpeed)})");

            // ---- land and the waterline ----------------------------------------------------
            var island = new ConeIsland(shoreRadius: 300f, slope: 0.5f, deepHeight: -60f);

            FieldSample onLand = CurrentField.Evaluate(0f, 0f, seed, 500.0, 0, 1f, island, s);
            Check(onLand.Speed == 0f && onLand.Dominant == CurrentTerm.Land, "no current on dry land");

            // Just outside the shoreline, inside the fade band: must be calm, so nothing is ever
            // shoved aground by the mod.
            FieldSample atWaterline = CurrentField.Evaluate(306f, 0f, seed, 500.0, 0, 1f, island, s);
            FieldSample offshore = CurrentField.Evaluate(600f, 0f, seed, 500.0, 0, 1f, island, s);
            Check(atWaterline.Depth > 0f && atWaterline.Depth < s.ShallowFadeDepth,
                $"the sample at the waterline is in the fade band (depth {Fmt(atWaterline.Depth)}m)");
            Check(atWaterline.Speed < offshore.Speed,
                "current fades toward the waterline rather than pushing hulls ashore");

            // ---- the shelf ramp reaches zero before the seabed does -------------------------
            // Pins the constant against the measured fact that Valheim's open ocean is a flat
            // floor at exactly 30m depth. If ShelfDepth ever creeps back above that, the coastal
            // term silently switches itself on across the entire sea.
            Check(s.ShelfDepth < 30f,
                $"ShelfDepth ({Fmt(s.ShelfDepth)}) is shallower than Valheim's 30m ocean floor");

            // A sloped seabed at open-ocean depth must produce no coastal steering at all.
            var deepSlope = new ConeIsland(shoreRadius: 300f, slope: 0.001f, deepHeight: -60f);
            FieldSample deepGradient = CurrentField.Evaluate(2000f, 0f, seed, 500.0, 0, 1f, deepSlope, s);
            Check(deepGradient.Dominant != CurrentTerm.Coastal,
                "a gradient in deep water does not read as a coast");

            // ---- the tide reverses the coastal stream --------------------------------------
            // Sampled on the shelf, at peak flood and peak ebb. This is the reversal a player
            // actually feels, and the reason a passage is not the same passage six hours later.
            // Isolated by SUBTRACTION rather than by hoping it dominates: at slack the tide term
            // is exactly zero, so the slack sample is pure open-ocean drift and everything left
            // after subtracting it is the coastal stream. Comparing absolute directions instead
            // made this test a hostage to the relative size of two unrelated constants — it
            // broke the moment ShelfDepth was corrected, while the code was right.
            FieldSettings coastalOnly = s;
            coastalOnly.TideAmplitude = 0f;   // hold the open-ocean term still across the cycle
            coastalOnly.MaxSpeed = 5f;        // lift the ceiling so no clamp confounds the sum
            float flood = 0.25f * s.TidePeriodSeconds;
            float ebb = 0.75f * s.TidePeriodSeconds;

            FieldSample atSlack = CurrentField.Evaluate(320f, 0f, seed, 0.0, 0, 1f, island, coastalOnly);
            FieldSample atFlood = CurrentField.Evaluate(320f, 0f, seed, flood, 0, 1f, island, coastalOnly);
            FieldSample atEbb = CurrentField.Evaluate(320f, 0f, seed, ebb, 0, 1f, island, coastalOnly);

            float fx = atFlood.X - atSlack.X, fz = atFlood.Z - atSlack.Z;
            float ex = atEbb.X - atSlack.X, ez = atEbb.Z - atSlack.Z;
            float dot = fx * ex + fz * ez;
            float coastalMag = (float)Math.Sqrt(fx * fx + fz * fz);

            Check(coastalMag > 0.01f, $"there is a coastal stream to reverse at all ({Fmt(coastalMag)} m/s)");
            Check(dot < 0f, $"the coastal stream reverses between flood and ebb (dot {Fmt(dot)})");

            // The onshore share points toward land on BOTH halves of the cycle. Off a cone island
            // centred on the origin, "toward land" at (320, 0) is -x, and the along-shore tangent
            // there is pure z, so the x component of each delta IS the onshore push. The first
            // version scaled that push by the SIGNED stream, so on the ebb it pointed offshore —
            // and the reversal check above could not see it, because the along-shore part
            // dominates the dot. Found 2026-09-21 by reading the line; this is the assertion that
            // was missing for two releases.
            Check(fx < 0f && ex < 0f,
                $"the onshore push points toward land at flood AND ebb (flood x {Fmt(fx)}, ebb x {Fmt(ex)})");

            Check(Math.Abs(CurrentField.TidePhase01(0.0, 3600f) - 0f) < 1e-5f, "tide phase starts at 0");
            Check(Math.Abs(CurrentField.TidePhase01(900.0, 3600f) - 0.25f) < 1e-5f, "tide phase is 0.25 a quarter through");
            Check(Math.Abs(CurrentField.TidePhase01(3600.0, 3600f) - 0f) < 1e-5f, "tide phase wraps at a full period");
            Check(Math.Abs(CurrentField.TidePhase01(7300.0, 3600f) - CurrentField.TidePhase01(100.0, 3600f)) < 1e-4f,
                "tide phase is periodic across many cycles");

            // ---- the tide swings open-ocean strength ---------------------------------------
            FieldSample deepFlood = CurrentField.Evaluate(1200f, 900f, seed, flood, 0, 1f, deep, s);
            FieldSample deepSlack = CurrentField.Evaluate(1200f, 900f, seed, 0.0, 0, 1f, deep, s);
            Check(deepFlood.Speed > deepSlack.Speed,
                $"open ocean runs harder at flood than at slack ({Fmt(deepFlood.Speed)} vs {Fmt(deepSlack.Speed)})");

            // ---- season shifts the field without erasing it --------------------------------
            // Deliberately NOT a reversal: a seasonal flip would invalidate every seamark a crew
            // had learned, four times a year, which destroys the thing the mod exists for.
            FieldSample spring = CurrentField.Evaluate(2000f, 1200f, seed, 500.0, 0, 1f, deep, s);
            FieldSample winter = CurrentField.Evaluate(2000f, 1200f, seed, 500.0, 3, 1f, deep, s);
            Check(spring.X != winter.X || spring.Z != winter.Z, "the season changes the field");

            float springSpeed = 0f, winterSpeed = 0f;
            for (int i = 0; i < 400; i++)
            {
                float px = (i % 20) * 500f - 5000f;
                float pz = (i / 20) * 500f - 5000f;
                springSpeed += CurrentField.Evaluate(px, pz, seed, 500.0, 0, 1f, deep, s).Speed;
                winterSpeed += CurrentField.Evaluate(px, pz, seed, 500.0, 3, 1f, deep, s).Speed;
            }
            Check(winterSpeed > springSpeed, "winter water runs harder than spring, world-wide");

            float angle = AngleBetween(spring, winter);
            Check(angle < 90f, $"the seasonal shift stays under a quarter turn ({Fmt(angle)} degrees)");

            // ---- a constriction accelerates the flow ---------------------------------------
            var openWater = new FlatSeabed(-40f);
            float inOpen = 0f;
            for (int i = 0; i < 40; i++)
                inOpen += CurrentField.Evaluate(0f, i * 200f, seed, 500.0, 0, 1f, openWater, s).Speed;

            // Both scales the probe looks at. The narrow one is a race between rocks; the wide
            // one is a strait between islands, and a single-scale probe was blind to it — the
            // failure that put two distances in RaceProbeDistances in the first place.
            float inNarrow = 0f, inWide = 0f;
            var narrow = new Channel(halfWidth: 40f);
            var wide = new Channel(halfWidth: 130f);
            for (int i = 0; i < 40; i++)
            {
                float pz = i * 200f;
                inNarrow += CurrentField.Evaluate(0f, pz, seed, 500.0, 0, 1f, narrow, s).Speed;
                inWide += CurrentField.Evaluate(0f, pz, seed, 500.0, 0, 1f, wide, s).Speed;
            }
            Check(inNarrow > inOpen,
                $"water runs faster in a narrow race than over the same open seabed ({Fmt(inNarrow)} vs {Fmt(inOpen)})");
            Check(inWide > inOpen,
                $"water runs faster in a wide strait too — the second probe scale earns its place ({Fmt(inWide)} vs {Fmt(inOpen)})");
            Check(inNarrow >= inWide,
                $"a tighter gap runs at least as fast as a broader one ({Fmt(inNarrow)} vs {Fmt(inWide)})");

            // ---- storm surge ---------------------------------------------------------------
            // Positional by construction: the CALLER decides where a storm is, so the field
            // cannot accidentally apply one globally. What is tested here is that the multiplier
            // does what it says and never breaches the world ceiling.
            FieldSample calm   = CurrentField.Evaluate(1200f, 900f, seed, 500.0, 0, 1f, deep, s);
            FieldSample stormy = CurrentField.Evaluate(1200f, 900f, seed, 500.0, 0, 1.6f, deep, s);
            Check(stormy.Speed > calm.Speed,
                $"a storm makes the water run harder ({Fmt(calm.Speed)} -> {Fmt(stormy.Speed)})");
            Check(Math.Abs(stormy.Speed - calm.Speed * 1.6f) < 1e-4f,
                "the surge multiplies the water speed exactly");
            Check(Math.Abs(stormy.StormSurge - 1.6f) < 1e-6f, "the sample reports the surge it applied");

            // MaxCurrentSpeed is documented as the ceiling ANYWHERE in the world. A storm must
            // drive weak water toward it, never through it, or that description becomes a lie.
            FieldSettings capped = s;
            capped.MaxSpeed = 0.2f;
            float stormPeak = 0f;
            for (int ix = -10; ix <= 10; ix++)
            for (int iz = -10; iz <= 10; iz++)
            {
                FieldSample p = CurrentField.Evaluate(ix * 300f, iz * 300f, seed, 500.0, 0, 4f, deep, capped);
                if (p.Speed > stormPeak) stormPeak = p.Speed;
            }
            Check(stormPeak <= capped.MaxSpeed + 1e-4f,
                $"even a 4x storm cannot breach MaxCurrentSpeed (peak {Fmt(stormPeak)} vs cap {Fmt(capped.MaxSpeed)})");
            Check(stormPeak > capped.MaxSpeed - 1e-4f,
                "the storm sweep actually reaches the ceiling, so the check above means something");

            // A storm over dry land is still no current at all.
            FieldSample stormOnLand = CurrentField.Evaluate(0f, 0f, seed, 500.0, 0, 4f, island, s);
            Check(stormOnLand.Speed == 0f, "a storm over dry land raises nothing");

            // ---- degenerate inputs ---------------------------------------------------------
            FieldSample noProbe = CurrentField.Evaluate(0f, 0f, seed, 500.0, 0, 1f, null, s);
            Check(noProbe.Speed == 0f, "a null probe answers dead calm rather than throwing");

            FieldSettings zeroTide = s;
            zeroTide.TidePeriodSeconds = 0f;
            FieldSample noTide = CurrentField.Evaluate(500f, 500f, seed, 500.0, 0, 1f, deep, zeroTide);
            Check(!float.IsNaN(noTide.Speed) && noTide.TidePhase01 == 0f,
                "a zero tide period does not divide by zero");

            FieldSettings zeroSpeed = s;
            zeroSpeed.MaxSpeed = 0f;
            FieldSample stopped = CurrentField.Evaluate(500f, 500f, seed, 500.0, 0, 1f, deep, zeroSpeed);
            Check(stopped.Speed == 0f, "MaxCurrentSpeed 0 turns the whole field off");

            for (int season = -8; season <= 8; season++)
            {
                FieldSample p = CurrentField.Evaluate(700f, -300f, seed, 500.0, season, 1f, deep, s);
                if (float.IsNaN(p.Speed)) { Check(false, $"season index {season} produced NaN"); break; }
            }
            Check(true, "out-of-range season indices wrap instead of throwing");
        }

        /// <summary>
        /// One tick of vanilla's hull physics along one axis, as read out of the real
        /// Ship.CustomFixedUpdate on 2026-09-21: thrust in, quadratic damping scaled by
        /// submergence out (clamped to ±1 m/s per tick), then whatever the mod adds.
        /// </summary>
        private static float TickHull(float v, float thrustPerTick, float damping, float submergence, float modDv)
        {
            v += thrustPerTick;
            float d = v * Math.Abs(v) * damping * submergence;
            if (d > 1f) d = 1f; else if (d < -1f) d = -1f;
            v -= d;
            v += modDv;
            return v;
        }

        /// <summary>Settled ground speed of a propelled hull under the UNDER-WAY correction.</summary>
        private static float SettleUnderWay(float thrust, float damping, float sub, float waterForward)
        {
            float v = 0f;
            for (int i = 0; i < 6000; i++)
            {
                DriftForce.ComputeUnderWay(waterForward, 0f, v, 0f, damping, 0.15f, sub, 1f, 1f, 1f,
                    out float dvF, out _);
                v = TickHull(v, thrust, damping, sub, dvF);
            }
            return v;
        }

        /// <summary>The same hull under the shipped 1.0.0 push — the model that stopped Grishak's karve.</summary>
        private static float SettleUnderPush(float thrust, float damping, float sub, float waterForward)
        {
            float v = 0f;
            for (int i = 0; i < 6000; i++)
            {
                // Water along +x is waterForward; the hull's along-current component is v when the
                // water runs with it and −v when it runs against it.
                float waterSpeed = Math.Abs(waterForward);
                float along = waterForward >= 0f ? v : -v;
                DriftForce.Compute(waterSpeed, 0f, along, 1f, 0.02f, 1f, 1f, out float dvx, out _);
                float modDv = waterForward >= 0f ? dvx : -dvx;
                v = TickHull(v, thrust, damping, sub, modDv);
            }
            return v;
        }

        private static void DriftUnderWayTests()
        {
            Section("DriftForce — under way");

            // Prefab constants read out of the game with HullReport, 2026-09-21. Submergence at
            // rest would be gravity against buoyancy, g·dt / m_force = 0.196 for a karve; on a
            // live sea the verbose drift line read `sub 0.38–0.46` for a paddled karve the same
            // afternoon, so the karve uses the MEASURED 0.4 and the raft its rest estimate.
            const float karveThrust = 0.2f * 0.02f, karveDampF = 0.001f, karveSub = 0.4f;
            const float raftThrust  = 0.5f * 0.02f, raftDampF  = 0.005f, raftSub  = 0.392f;

            // ---- the pure function -------------------------------------------------------------
            DriftForce.ComputeUnderWay(0f, 0f, 3f, 0f, karveDampF, 0.15f, karveSub, 1f, 1f, 1f, out float f, out float r);
            Check(f == 0f && r == 0f, "under way, still water changes nothing");

            // At the water's own velocity the TOTAL drag must vanish: vanilla still charges
            // −d·sub·w|w| against the absolute velocity, so the correction is exactly +d·sub·w|w|.
            DriftForce.ComputeUnderWay(1.2f, 0.3f, 1.2f, 0.3f, karveDampF, 0.15f, karveSub, 1f, 1f, 1f, out f, out r);
            Check(Math.Abs(f - karveDampF * karveSub * 1.44f) < 1e-6f && Math.Abs(r - 0.15f * karveSub * 0.09f) < 1e-6f,
                "a hull moving exactly with the water pays no net drag: the correction cancels vanilla's, per axis");

            DriftForce.ComputeUnderWay(1f, 0f, 0f, 0f, karveDampF, 0.15f, karveSub, 1f, 1f, 1f, out f, out r);
            Check(Math.Abs(f - karveDampF * karveSub) < 1e-7f && r == 0f,
                $"a propelled hull at rest is nudged along the water by d·sub·w² ({Fmt(f)})");

            DriftForce.ComputeUnderWay(-1.2f, 0f, 4f, 0f, karveDampF, 0.15f, karveSub, 1f, 1f, 1f, out f, out r);
            float expectUp = -karveDampF * karveSub * (5.2f * 5.2f - 16f);
            Check(Math.Abs(f - expectUp) < 1e-6f && f < 0f,
                $"driving upstream costs exactly the extra drag of moving through the water ({Fmt(f)})");

            DriftForce.ComputeUnderWay(1.2f, 0f, 8f, 0f, karveDampF, 0.15f, karveSub, 1f, 1f, 1f, out f, out r);
            Check(f > 0f && Math.Abs(f - karveDampF * karveSub * (64f - 6.8f * 6.8f)) < 1e-6f,
                "sailing with the water is helped by exactly the drag it no longer pays");

            DriftForce.ComputeUnderWay(0f, 1f, 0f, 0f, karveDampF, 0.15f, karveSub, 1f, 1f, 1f, out f, out r);
            Check(f == 0f && Math.Abs(r - 0.15f * karveSub) < 1e-7f, "the beam axis uses the sideways damping");

            DriftForce.ComputeUnderWay(-1.2f, 0f, 4f, 0f, karveDampF, 0.15f, karveSub, 2f, 1f, 1f, out float twice, out _);
            Check(Math.Abs(twice - 2f * expectUp) < 1e-6f, "DriftStrength scales the under-way cost linearly");

            DriftForce.ComputeUnderWay(-1.2f, 0f, 4f, 0f, karveDampF, 0.15f, 0f, 1f, 1f, 1f, out f, out r);
            Check(f == 0f && r == 0f, "a hull out of the water (submergence 0) feels nothing");

            DriftForce.ComputeUnderWay(-100f, 100f, 100f, -100f, 1f, 1f, 1f, 1f, 1f, 1f, out f, out r);
            Check(Math.Abs(f) <= 1f && Math.Abs(r) <= 1f, "each axis is clamped to ±1 m/s per tick, as vanilla clamps its own damping");

            // ---- THE GRISHAK TEST: a paddled karve, tick by tick ----------------------------------
            float v0 = SettleUnderWay(karveThrust, karveDampF, karveSub, 0f);
            // sqrt(thrust / (d·sub)) = sqrt(0.004 / 0.0004). In game the same karve read
            // 3.0–3.5 m/s along a 0.44 m/s current it was running with — 3.16 + 0.44.
            Check(Math.Abs(v0 - 3.162f) < 0.05f,
                $"the simulated karve paddles at the speed the prefab numbers predict in still water ({Fmt(v0)} m/s)");

            float vUp = SettleUnderWay(karveThrust, karveDampF, karveSub, -0.182f);
            Check(Math.Abs((v0 - vUp) - 0.182f) < 0.02f,
                $"into 0.182 m/s of water a paddled karve loses 0.182 m/s over the ground and no more ({Fmt(vUp)})");

            float vRace = SettleUnderWay(karveThrust, karveDampF, karveSub, -1.2f);
            Check(vRace > 0f && Math.Abs((v0 - vRace) - 1.2f) < 0.05f,
                $"into a full race it still makes headway, exactly the water's speed slower ({Fmt(vRace)})");

            float vWith = SettleUnderWay(karveThrust, karveDampF, karveSub, 1.2f);
            Check(Math.Abs((vWith - v0) - 1.2f) < 0.05f,
                $"with the race it gains exactly the water's speed ({Fmt(vWith)})");

            float raft0 = SettleUnderWay(raftThrust, raftDampF, raftSub, 0f);
            float raftUp = SettleUnderWay(raftThrust, raftDampF, raftSub, -1.2f);
            Check(Math.Abs(raft0 - 2.26f) < 0.05f && raftUp > 0f && Math.Abs((raft0 - raftUp) - 1.2f) < 0.05f,
                $"a raft, the slowest hull, still makes headway into a race ({Fmt(raft0)} → {Fmt(raftUp)})");

            // ---- and the shipped 1.0.0 model, pinned as the defect it was -------------------------
            float oldUp = SettleUnderPush(karveThrust, karveDampF, karveSub, -0.182f);
            Check(oldUp < 0.5f * v0,
                $"UNDER THE 1.0.0 PUSH the same karve made less than half its speed in 0.182 m/s of water ({Fmt(oldUp)}) — the report");
            float oldRace = SettleUnderPush(karveThrust, karveDampF, karveSub, -1.2f);
            Check(oldRace <= 0f,
                $"UNDER THE 1.0.0 PUSH a paddled karve went backward in a race ({Fmt(oldRace)})");
        }

        private static void DriftForceTests()
        {
            Section("DriftForce");

            const float dt = 0.02f;      // Valheim's fixed step
            const float coupling = 0.6f;

            // Still water pushes nothing at all.
            DriftForce.Compute(0f, 0f, 0f, coupling, dt, 1f, 1f, out float dvx, out float dvz);
            Check(dvx == 0f && dvz == 0f, "still water pushes nothing");

            // The push runs along the current, and is water^2 * calibration * strength * dt.
            DriftForce.Compute(1f, 0f, 0f, coupling, dt, 1f, 1f, out dvx, out dvz);
            Check(dvx > 0f && Math.Abs(dvz) < 1e-6f, "the push runs along the current");
            Check(Math.Abs(dvx - 1f * coupling * dt) < 1e-6f,
                $"a hull at rest gets the full push, water * strength * dt ({Fmt(dvx)})");

            // ---- SATURATION: the property that makes every hull agree ----------------------
            // Measured live 2026-08-28: a VikingShip under the previous calibrated-push model
            // sailed straight past the water's speed to 1.38x and was still climbing, because
            // m_dampingForward is a SERIALIZED field every prefab overrides — no constant can
            // balance a damping coefficient you do not know. Fading the push out as the hull
            // takes up the water's speed sets the equilibrium directly instead.
            DriftForce.Compute(1f, 0f, 1f, coupling, dt, 1f, 1f, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "a hull already at the water's speed gets no push at all");

            DriftForce.Compute(1f, 0f, 3f, coupling, dt, 1f, 1f, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "a hull outrunning the water gets no push — and is NOT braked");

            DriftForce.Compute(1f, 0f, 0.5f, coupling, dt, 1f, 1f, out float halfway, out _);
            DriftForce.Compute(1f, 0f, 0f, coupling, dt, 1f, 1f, out float atRest, out _);
            Check(Math.Abs(halfway - atRest * 0.5f) < 1e-6f,
                "the push fades linearly as the hull takes up the water's speed");

            // THE ANTI-BRAKING GUARANTEE, swept rather than spot-checked. The push may never
            // oppose the current at ANY hull speed, in either direction — that is the whole
            // reason the saturation term is clamped rather than merely subtracted.
            bool everOpposed = false;
            for (float hull = -8f; hull <= 8f; hull += 0.25f)
            {
                DriftForce.Compute(0.7f, 0f, hull, coupling, dt, 1f, 1f, out float px, out float pz);
                if (px < -1e-9f) everOpposed = true;              // current runs +x
                if (Math.Abs(pz) > 1e-9f) everOpposed = true;     // and only +x
            }
            Check(!everOpposed, "the push never opposes the current at any hull speed, forward or reverse");

            // A hull driving against the current is capped at a full push, never an amplified one.
            DriftForce.Compute(1f, 0f, -50f, coupling, dt, 1f, 1f, out float against, out _);
            Check(Math.Abs(against - atRest) < 1e-6f, "driving against the current gives a full push, not more");

            // Direction is preserved exactly — a current toward the south-west must not push a
            // hull anywhere but south-west.
            DriftForce.Compute(-0.6f, 0.8f, 0f, coupling, dt, 1f, 1f, out dvx, out dvz);
            Check(Math.Abs(dvx / dvz + 0.6f / 0.8f) < 1e-5f, "the push preserves the current's bearing");

            // At rest the push is linear in the water speed again — the saturation term, not the
            // push law, is what sets where a hull settles.
            DriftForce.Compute(0.5f, 0f, 0f, coupling, dt, 1f, 1f, out float slow, out _);
            DriftForce.Compute(1.0f, 0f, 0f, coupling, dt, 1f, 1f, out float fast, out _);
            Check(Math.Abs(fast - slow * 2f) < 1e-6f, "at rest the push is linear in water speed");

            // THE BUG THIS SIGNATURE EXISTS TO PREVENT: the current must never depend on how
            // fast the boat is going, because a drag term against the hull's own velocity
            // double-counts vanilla's damping and brakes every boat under sail. There is no hull
            // velocity to pass, so the only assertion available is that the push for a given
            // current is a constant — which is the property that matters.
            DriftForce.Compute(0.3f, 0f, 0f, coupling, dt, 1f, 1f, out float once, out _);
            DriftForce.Compute(0.3f, 0f, 0f, coupling, dt, 1f, 1f, out float twice, out _);
            Check(once == twice && once > 0f,
                "the push depends only on the water, never on the hull — no braking under sail");

            // A long frame must not hand over more than the current itself.
            DriftForce.Compute(2f, 0f, 0f, coupling, 1000f, 1f, 1f, out dvx, out dvz);
            Check(Math.Abs(dvx - 2f) < 1e-5f,
                $"a huge frame delivers at most the water's own speed ({Fmt(dvx)} vs water 2)");

            DriftForce.Compute(-5f, 0f, 0f, coupling, 10000f, 1f, 1f, out dvx, out dvz);
            Check(Math.Abs(dvx + 5f) < 1e-5f, "the one-tick clamp is symmetric for a reversed current");

            // An unattended hull, at the shipped default, is not touched at all.
            DriftForce.Compute(1f, 1f, 0f, coupling, dt, 0f, 1f, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "crewFactor 0 (the shipped unattended default) pushes nothing");

            DriftForce.Compute(1f, 0f, 0f, coupling, dt, 0.5f, 1f, out float half, out _);
            DriftForce.Compute(1f, 0f, 0f, coupling, dt, 1f, 1f, out float full, out _);
            Check(Math.Abs(half - full * 0.5f) < 1e-6f, "crewFactor scales the push linearly");

            // ---- the world edge ------------------------------------------------------------
            // Vanilla's ApplyEdgeForce starts at 10420. Undertow must be gone by then, or the
            // two argue and a boat judders on the rim of the world.
            Check(DriftForce.EdgeFade(0f) == 1f, "full current at the world centre");
            Check(DriftForce.EdgeFade(9000f) == 1f, "full current well inside the world");
            Check(DriftForce.EdgeFade(10420f) == 0f, "zero current where vanilla's edge force begins");
            Check(DriftForce.EdgeFade(11000f) == 0f, "zero current beyond the edge");
            Check(DriftForce.EdgeFadeEnd <= 10420f,
                "the fade completes at or before vanilla's ApplyEdgeForce threshold of 10420");

            float previous = 1.1f;
            bool monotonic = true;
            for (float d = 10150f; d <= 10450f; d += 10f)
            {
                float fade = DriftForce.EdgeFade(d);
                if (fade > previous + 1e-6f) monotonic = false;
                if (fade < 0f || fade > 1f) monotonic = false;
                previous = fade;
            }
            Check(monotonic, "the edge fade falls monotonically through 0..1");

            DriftForce.Compute(1f, 1f, 0f, coupling, dt, 1f, 0f, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "a zero edge fade pushes nothing");
        }

        private static void FlotsamMathTests()
        {
            Section("FlotsamMath");

            const float maxSpeed = 1.2f;
            const float minDepth = 12f;

            // ---- where flotsam gathers -----------------------------------------------------
            // The whole mechanic: slack water collects, fast water does not. If this inverts,
            // driftwood piles up in the races and the feature says the opposite of what it means.
            var slackSample = new FieldSample { Speed = 0.02f, Depth = 30f };
            var fastSample  = new FieldSample { Speed = 1.1f,  Depth = 30f };
            float slackWeight = FlotsamMath.GatherWeight(slackSample, maxSpeed, minDepth);
            float fastWeight  = FlotsamMath.GatherWeight(fastSample,  maxSpeed, minDepth);
            Check(slackWeight > fastWeight, $"slack water gathers more than fast ({Fmt(slackWeight)} vs {Fmt(fastWeight)})");
            Check(slackWeight > 0.9f, $"dead water gathers near maximum ({Fmt(slackWeight)})");

            // Monotonic across the whole range: no speed gathers more than a slower one.
            bool monotonic = true;
            float previous = 2f;
            for (float sp = 0f; sp <= maxSpeed; sp += 0.05f)
            {
                var s2 = new FieldSample { Speed = sp, Depth = 30f };
                float w = FlotsamMath.GatherWeight(s2, maxSpeed, minDepth);
                if (w > previous + 1e-6f) monotonic = false;
                if (w < 0f || w > 1f) monotonic = false;
                previous = w;
            }
            Check(monotonic, "gather weight falls monotonically through 0..1 as water speeds up");

            // ---- depth gate ----------------------------------------------------------------
            // NON-ZERO SPEED ON PURPOSE. These first used Speed = 0, which meant an unrelated
            // injury to the slackness term ALSO drove them to zero — they passed while the depth
            // gate was deleted, one bug masking another. Caught 2026-08-28 by injuring both at
            // once. At 0.1 m/s the weight is high unless the gate itself is what rejects it, and
            // the control below proves that.
            var atMin    = new FieldSample { Speed = 0.1f, Depth = minDepth };
            var shallow  = new FieldSample { Speed = 0.1f, Depth = 5f };
            var onLand   = new FieldSample { Speed = 0.1f, Depth = -3f };
            var deepEnough = new FieldSample { Speed = 0.1f, Depth = minDepth + 1f };
            Check(FlotsamMath.GatherWeight(deepEnough, maxSpeed, minDepth) > 0.5f,
                "the probe speed scores highly when deep enough, so a 0 below means the gate fired");
            Check(FlotsamMath.GatherWeight(atMin, maxSpeed, minDepth) == 0f, "no flotsam at exactly the minimum depth");
            Check(FlotsamMath.GatherWeight(shallow, maxSpeed, minDepth) == 0f, "no flotsam in the shallows");
            Check(FlotsamMath.GatherWeight(onLand, maxSpeed, minDepth) == 0f, "no flotsam on land");

            // ---- the rate is per HOUR, not per tick ----------------------------------------
            // Why that matters: FlotsamIntervalSeconds can be retuned without silently changing
            // how much washes up. A per-tick rate would couple two unrelated dials.
            Check(FlotsamMath.ShouldSpawn(1f, 3600f, 1f, 0.5f), "3600/hour over one second is certain");
            Check(!FlotsamMath.ShouldSpawn(1f, 0f, 60f, 0.0f), "a zero rate never spawns");
            Check(!FlotsamMath.ShouldSpawn(0f, 3600f, 60f, 0.0f), "zero gather weight never spawns");

            // Doubling the elapsed time doubles the chance - the property that keeps the rate stable.
            // RATE CHOSEN SO THE COUNTS ARE BIG. At the shipped 6/hour the expected counts are
            // about 2 per 1000, and a +/-2 tolerance swallowed the entire elapsed-time factor —
            // this test passed with `deltaSeconds` deleted from the formula. Measured 2026-08-28.
            // At 45/hour the windows land near 250 and 500, where a missing factor cannot hide.
            const float perHour = 45f;
            float shortWindow = 0f, longWindow = 0f;
            for (int i = 0; i < 1000; i++)
            {
                float roll = i / 1000f;
                if (FlotsamMath.ShouldSpawn(1f, perHour, 20f, roll)) shortWindow++;
                if (FlotsamMath.ShouldSpawn(1f, perHour, 40f, roll)) longWindow++;
            }
            Check(shortWindow > 200f && longWindow > 400f,
                $"the rate sweep produces counts large enough to mean something ({Fmt(shortWindow)}, {Fmt(longWindow)})");
            Check(Math.Abs(longWindow - shortWindow * 2f) <= 5f,
                $"twice the elapsed time gives twice the chance ({Fmt(shortWindow)} vs {Fmt(longWindow)} per 1000)");

            // ---- weighted pick -------------------------------------------------------------
            float[] table = { 1f, 3f };
            int zero = 0, one = 0;
            for (int i = 0; i < 1000; i++)
            {
                if (FlotsamMath.PickWeighted(table, i / 1000f) == 0) zero++; else one++;
            }
            Check(Math.Abs(zero - 250) < 15 && Math.Abs(one - 750) < 15,
                $"weights are respected ({zero}/{one} against an expected 250/750)");

            Check(FlotsamMath.PickWeighted(null, 0.5f) == -1, "a null table answers -1, not a crash");
            Check(FlotsamMath.PickWeighted(new float[0], 0.5f) == -1, "an empty table answers -1");
            Check(FlotsamMath.PickWeighted(new float[] { 0f, 0f }, 0.5f) == -1, "an all-zero table answers -1");
            Check(FlotsamMath.PickWeighted(new float[] { 0f, 1f }, 0f) == 1, "a zero-weight entry is never picked");

            // The very top of the range must land in bounds rather than falling off the end.
            bool inBounds = true;
            for (float r = 0f; r <= 1.0001f; r += 0.001f)
            {
                int idx = FlotsamMath.PickWeighted(table, r);
                if (idx < 0 || idx >= table.Length) inBounds = false;
            }
            Check(inBounds, "every roll from 0 to 1 inclusive picks a valid index");
        }

        private static void SwimDriftTests()
        {
            Section("SwimDrift");

            const float swimSpeed = 2f;          // vanilla Character.m_swimSpeed
            const float swimAccel = 0.05f;       // vanilla Character.m_swimAcceleration
            const float cap = 0.35f;
            const float factor = 0.5f;

            // ---- THE SCALING, which is the whole file --------------------------------------
            // Vanilla lerps m_currentVel toward the swimmer's intent each frame, so an addition
            // of d settles at d / m_swimAcceleration. At 0.05 that is a TWENTYFOLD amplification.
            // The delta must therefore be pre-multiplied by the acceleration, and the steady
            // state must come back out as the intended drift rather than 20x it.
            SwimDrift.Compute(1f, 0f, 1f, swimSpeed, 1f, swimAccel, out float dvx, out float dvz);
            float settled = SwimDrift.SteadyStateDrift(dvx, swimAccel);
            Check(Math.Abs(settled - 1f) < 1e-4f,
                $"a 1 m/s current settles a swimmer at 1 m/s, not 20 ({Fmt(settled)})");

            // Simulated rather than reasoned about: run vanilla's own lerp and see where it goes.
            float v = 0f;
            for (int i = 0; i < 4000; i++)
            {
                v = v + swimAccel * (0f - v);   // lerp toward zero intent (treading water)
                v += dvx;                        // our per-frame addition
            }
            Check(Math.Abs(v - 1f) < 0.01f,
                $"simulating vanilla's lerp converges on the intended drift ({Fmt(v)})");

            // ---- THE DROWNING GUARD, swept across the entire config range -------------------
            // A safety property, not a balance dial: at every legal setting a swimmer must still
            // out-swim the water. If this ever fails, someone can be pinned offshore until they
            // drown, and that is a broken feature rather than a mistuned one.
            bool alwaysEscapable = true;
            float worstRatio = 0f;
            for (float f = 0f; f <= 1.0f; f += 0.05f)
            for (float c2 = 0f; c2 <= 0.9f; c2 += 0.05f)
            for (float water = 0f; water <= 5f; water += 0.25f)
            {
                SwimDrift.Compute(water, 0f, f, swimSpeed, c2, swimAccel, out float x, out _);
                float drift = SwimDrift.SteadyStateDrift(x, swimAccel);
                float ratio = drift / swimSpeed;
                if (ratio > worstRatio) worstRatio = ratio;
                if (drift >= swimSpeed) alwaysEscapable = false;
            }
            Check(alwaysEscapable,
                $"across every legal config a swimmer out-swims the current (worst {Fmt(worstRatio)} of swim speed)");
            Check(worstRatio <= 0.9f + 1e-4f,
                $"the cap is honoured at the extreme of the range ({Fmt(worstRatio)})");

            // At the SHIPPED defaults, with the fastest water the field can produce.
            SwimDrift.Compute(1.2f, 0f, factor, swimSpeed, cap, swimAccel, out float defX, out _);
            float defaultDrift = SwimDrift.SteadyStateDrift(defX, swimAccel);
            Check(defaultDrift < swimSpeed * 0.4f,
                $"at shipped defaults the worst drift is well under swim speed ({Fmt(defaultDrift)} vs {swimSpeed})");
            Check(defaultDrift > 0.2f,
                $"and is still enough to feel ({Fmt(defaultDrift)} m/s)");

            // ---- direction, and the degenerate cases ---------------------------------------
            SwimDrift.Compute(-0.6f, 0.8f, factor, swimSpeed, cap, swimAccel, out dvx, out dvz);
            Check(Math.Abs(dvx / dvz + 0.6f / 0.8f) < 1e-4f, "the drift preserves the current's bearing");

            SwimDrift.Compute(0f, 0f, factor, swimSpeed, cap, swimAccel, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "still water moves no swimmer");

            SwimDrift.Compute(1f, 0f, 0f, swimSpeed, cap, swimAccel, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "SwimmerDriftFactor 0 leaves swimmers alone entirely");

            SwimDrift.Compute(1f, 0f, factor, swimSpeed, 0f, swimAccel, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f, "a zero cap leaves swimmers alone entirely");

            SwimDrift.Compute(1f, 0f, factor, swimSpeed, cap, 0f, out dvx, out dvz);
            Check(dvx == 0f && dvz == 0f && !float.IsNaN(dvx),
                "a zero swim acceleration does not divide by zero");

            Check(SwimDrift.SteadyStateDrift(1f, 0f) == 0f, "steady state with no acceleration is zero, not infinity");
        }

        private static float AngleBetween(FieldSample a, FieldSample b)
        {
            double la = Math.Sqrt(a.X * a.X + a.Z * a.Z);
            double lb = Math.Sqrt(b.X * b.X + b.Z * b.Z);
            if (la < 1e-6 || lb < 1e-6) return 0f;
            double d = (a.X * b.X + a.Z * b.Z) / (la * lb);
            d = Math.Max(-1.0, Math.Min(1.0, d));
            return (float)(Math.Acos(d) * 180.0 / Math.PI);
        }

        private static void ModConfigTests()
        {
            Section("ModConfig");

            var cfg = new ConfigFile();
            ModConfig.Bind(cfg);

            // 1. Every declared entry is actually bound.
            //
            // The failure this catches: someone adds `public static ConfigEntry<float> Foo;`
            // and forgets the matching cfg.Bind. The build is clean, the mod loads, and the
            // first read of Foo.Value throws — possibly weeks later, on someone else's server,
            // inside a try/catch that swallows it into a log nobody reads.
            List<FieldInfo> entryFields = ConfigEntryFields();
            Check(entryFields.Count > 0, "ModConfig declares at least one config entry");

            foreach (FieldInfo f in entryFields)
                Check(f.GetValue(null) != null, $"{f.Name} is bound (non-null after Bind)");

            // 2. One bind call per declared field. A field bound twice, or a bind with no
            //    field behind it, both mean the config file and the code disagree about what
            //    exists.
            Check(cfg.BoundCount == entryFields.Count,
                $"bind count matches field count ({cfg.BoundCount} bound, {entryFields.Count} declared)");

            // 3. No duplicate section/key. BepInEx returns the FIRST entry for a repeated key,
            //    so a copy-paste collision silently aliases two settings onto one value — the
            //    user changes one and the other moves with it.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();
            foreach (ConfigFile.BoundEntry e in cfg.Bound)
                if (!seen.Add(e.Path)) duplicates.Add(e.Path);
            Check(duplicates.Count == 0,
                duplicates.Count == 0
                    ? "no duplicate section/key pairs"
                    : $"no duplicate section/key pairs (found: {string.Join(", ", duplicates)})");

            // 4. Every entry carries a description. This mod's settings are invisible in play —
            //    a current has no icon — so the config file's own text is the entire
            //    documentation a server owner gets.
            foreach (ConfigFile.BoundEntry e in cfg.Bound)
                Check(e.Description != null && !string.IsNullOrWhiteSpace(e.Description.Description),
                    $"{e.Path} has a description");

            // 5. Numeric defaults sit inside their own declared range. A default outside its
            //    AcceptableValueRange is clamped by BepInEx on first write, so the shipped
            //    default and the documented default silently differ.
            foreach (ConfigFile.BoundEntry e in cfg.Bound)
            {
                if (e.Description?.AcceptableValues is AcceptableValueRange<float> range
                    && e.DefaultValue is float value)
                {
                    Check(value >= range.MinValue && value <= range.MaxValue,
                        $"{e.Path} default {Fmt(value)} within [{Fmt(range.MinValue)}, {Fmt(range.MaxValue)}]");
                }
                else if (e.Description?.AcceptableValues is AcceptableValueRange<int> irange
                         && e.DefaultValue is int ivalue)
                {
                    Check(ivalue >= irange.MinValue && ivalue <= irange.MaxValue,
                        $"{e.Path} default {ivalue} within [{irange.MinValue}, {irange.MaxValue}]");
                }
            }

            // 5b. The new slack floor is bound where it belongs, at the constant the maths owns,
            //     and RANGED. Without an AcceptableValueRange a hand-edited 50 would sail through
            //     to SpawnWeight, where Clamp01 would silently make it 1 and put a streak on every
            //     candidate point in the sea. The clamp is the safety net; the range is the
            //     instrument that tells an owner they asked for something out of bounds.
            ConfigFile.BoundEntry floorEntry =
                cfg.Bound.Find(e => e.Key == "DriftLineSlackFloor");
            Check(floorEntry != null && floorEntry.Section == "7 - Drift lines",
                "DriftLineSlackFloor is bound under '7 - Drift lines'");
            Check(floorEntry != null && floorEntry.DefaultValue is float df
                  && df == DriftLineMath.DefaultSlackFloor,
                "...at DriftLineMath's own constant, not a second copy of the number");
            Check(floorEntry?.Description?.AcceptableValues is AcceptableValueRange<float> fr
                  && fr.MinValue == 0f && fr.MaxValue == 0.5f,
                "...and ranged [0, 0.5], so 0 (the 0.7 cliff) stays reachable and 50 does not");

            // 6. The drift lines' entries live where the console help and the README say they
            //    do. A section name that drifts from the docs is a support question.
            Check(cfg.Bound.Exists(e => e.Section == "2 - Systems" && e.Key == "EnableDriftLines"),
                "EnableDriftLines is a system toggle under '2 - Systems'");
            foreach (string key in new[] { "DriftLineCount", "DriftLineRadius", "DriftLineOpacity", "DriftLineMinDepth", "DriftLineBudgetMs" })
                Check(cfg.Bound.Exists(e => e.Section == "7 - Drift lines" && e.Key == key),
                    $"{key} lives under '7 - Drift lines'");
        }

        /// <summary>Public static ConfigEntry&lt;T&gt; fields on ModConfig, in declaration order.</summary>
        private static List<FieldInfo> ConfigEntryFields()
        {
            var result = new List<FieldInfo>();
            foreach (FieldInfo f in typeof(ModConfig).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType.IsGenericType
                    && f.FieldType.GetGenericTypeDefinition() == typeof(ConfigEntry<>))
                {
                    result.Add(f);
                }
            }
            return result;
        }

        /// <summary>
        /// The drift lines' rules, every one of which fails silently in-game: foam in slack water
        /// or on a beach, a visible disc edge, a streak that pops, a white sprite glowing at
        /// midnight, a texture with a head end that turns foam into an arrow, a memo cache that
        /// hands one cell's water to another. None of them show up in a build.
        /// </summary>
        private static void DriftLineMathTests()
        {
            Section("DriftLineMath");

            FieldSettings s = FieldSettings.Defaults;
            const float max = 1.2f;
            float prev;

            // ---- SpawnWeight: slack water is glassy, beaches are bare -----------------------
            float slack = CurrentField.SlackSpeed;
            Check(Math.Abs(CurrentField.SlackSpeed - 0.12f * 1.2f) < 1e-6f,
                "SlackSpeed is the old 0.12 share of the shipped 1.2 ceiling, so defaults are untouched");

            // THE TRAP THIS CHANGE EXISTS FOR. Measured 2026-09-19: a server raised MaxCurrentSpeed to
            // 2.4 and the foam vanished from 0.21 m/s water, because slack was a share of the
            // ceiling and the threshold had doubled to 0.288. Slack is absolute now, so the same
            // water in the same ceiling gets more than floor-grade foam. Reverting to the share
            // makes this fail (0.21 <= 0.288 -> floor * 0.73).
            Check(DriftLineMath.SpawnWeight(0.21f, 30f, 2.4f, 2f, DriftLineMath.DefaultSlackFloor)
                      > DriftLineMath.DefaultSlackFloor,
                "raising the ceiling to 2.4 no longer turns 0.21 m/s water slack (the 2026-09-19 trap)");
            // ...and at the shipped ceiling the new form IS the old form, speed for speed.
            bool sameAtDefaults = true;
            for (int i = 0; i <= 120; i++)
            {
                float v = i / 100f;
                float oldSlack = 0.12f * max, oldSpan = (DriftLineMath.FullSpeedShare - 0.12f) * max;
                float oldBySpeed = v <= oldSlack
                    ? DriftLineMath.DefaultSlackFloor * Math.Min(1f, v / oldSlack)
                    : DriftLineMath.DefaultSlackFloor + (1f - DriftLineMath.DefaultSlackFloor) * Math.Min(1f, (v - oldSlack) / oldSpan);
                float now = DriftLineMath.SpawnWeight(v, 30f, max, 2f, DriftLineMath.DefaultSlackFloor);
                if (Math.Abs(now - oldBySpeed) > 1e-5f) sameAtDefaults = false;
            }
            Check(sameAtDefaults, "at the shipped ceiling, absolute slack reproduces the share-based weight at every speed");

            // A GREEN ASSERTION WAS DELETED HERE, and it is named rather than quietly dropped
            // because deleting a passing test is the thing this project distrusts most. It read
            // "SpawnWeight is exactly 0 at or below the slack share, at any depth" and it was the
            // behaviour 0.8 removes on the owner's instruction. Everything below replaces it, and
            // the last of them restores it exactly when the floor is zero.
            const float floor = DriftLineMath.DefaultSlackFloor;

            // 1. THE REQUEST ITSELF: moving water always earns SOME chance, however slow.
            bool someEverywhere = true;
            for (int i = 1; i <= 40; i++)
                if (DriftLineMath.SpawnWeight(slack * i / 40f, 30f, max, 2f, floor) <= 0f)
                    someEverywhere = false;
            Check(someEverywhere, "in deep water every speed above zero earns a non-zero weight");

            // 2. ...but slack water never earns more than the floor, so a race still dominates.
            bool cappedInSlack = true;
            foreach (float fl in new[] { 0f, 0.08f, 0.3f })
                for (int i = 0; i <= 40; i++)
                    if (DriftLineMath.SpawnWeight(slack * i / 40f, 30f, max, 2f, fl) > fl + 1e-6f)
                        cappedInSlack = false;
            Check(cappedInSlack, "at or below the slack share the weight never exceeds the floor");
            Check(Math.Abs(DriftLineMath.SpawnWeight(slack, 30f, max, 2f, floor) - floor) < 1e-6f,
                "the weight is exactly the floor AT the slack threshold");

            // 3. Dead water is still the faintest thing in the sea. A flat floor would pass 1 and
            //    2 above and lose this, which is the whole reason the sub-slack branch ramps.
            bool risingInSlack = true; prev = -1f;
            for (int i = 0; i <= 40; i++)
            {
                float w = DriftLineMath.SpawnWeight(slack * i / 40f, 30f, max, 2f, floor);
                if (w <= prev) risingInSlack = false;
                prev = w;
            }
            Check(risingInSlack, "below the slack threshold the weight still RISES with speed");

            // 4. Continuity. An if/else that applied the floor only strictly below the threshold
            //    would leave a step here, and a step is a visible edge on the water.
            Check(Math.Abs(DriftLineMath.SpawnWeight(slack - 1e-4f, 30f, max, 2f, floor)
                         - DriftLineMath.SpawnWeight(slack + 1e-4f, 30f, max, 2f, floor)) < 1e-3f,
                "the weight is continuous across the slack threshold - no visible edge");

            // 5. THE SAFETY-CRITICAL ONE. The floor multiplies the SPEED term only. Move it onto
            //    the depth term, or apply it after the depth multiply, and foam lifts onto beaches.
            bool zeroShallow = true;
            foreach (float fl in new[] { 0f, 0.08f, 0.5f })
                for (int i = 0; i <= 20; i++)
                {
                    float sp = max * i / 20f;
                    for (int d = -5; d <= 2; d += 1)
                        if (DriftLineMath.SpawnWeight(sp, d, max, 2f, fl) != 0f) zeroShallow = false;
                }
            Check(zeroShallow, "exactly 0 at or below MinDepth, at any speed AND any slack floor");

            // 6. Land stays bare. CurrentField reports Speed 0 on land, so this makes foam on dry
            //    ground unrepresentable rather than merely unlikely.
            bool deadIsBare = true;
            foreach (float fl in new[] { 0f, 0.08f, 0.5f })
                for (int d = 0; d <= 60; d += 10)
                    if (DriftLineMath.SpawnWeight(0f, d, max, 2f, fl) != 0f) deadIsBare = false;
            Check(deadIsBare, "dead-still water earns exactly 0 at any depth and any floor");

            Check(DriftLineMath.SpawnWeight(0.6f * max, 30f, max, 2f, floor) == 1f,
                "full weight at 60% of MaxSpeed in deep water, floor or no floor");

            bool mono = true; prev = -1f;
            for (int i = 0; i <= 200; i++)
            {
                float w = DriftLineMath.SpawnWeight(max * 1.5f * i / 200f, 30f, max, 2f, floor);
                if (w < prev || w > 1f) mono = false;
                prev = w;
            }
            Check(mono, "SpawnWeight is non-decreasing in speed and never above 1");
            mono = true; prev = -1f;
            for (int i = 0; i <= 200; i++)
            {
                float w = DriftLineMath.SpawnWeight(max, 40f * i / 200f, max, 2f, floor);
                if (w < prev || w > 1f) mono = false;
                prev = w;
            }
            Check(mono, "SpawnWeight is non-decreasing in depth");

            // 7. An out-of-range floor is clamped rather than trusted.
            bool floorClamped = true;
            foreach (float fl in new[] { -1f, 0f, 0.08f, 0.5f, 1f, 2f })
                for (int i = 0; i <= 40; i++)
                {
                    float w = DriftLineMath.SpawnWeight(max * 1.5f * i / 40f, 30f, max, 2f, fl);
                    if (w < 0f || w > 1f || float.IsNaN(w)) floorClamped = false;
                }
            Check(floorClamped, "any slack floor, in range or not, keeps the weight inside [0,1]");

            Check(DriftLineMath.SpawnWeight(float.NaN, 30f, max, 2f, floor) == 0f
                  && DriftLineMath.SpawnWeight(1f, float.NaN, max, 2f, floor) == 0f
                  && DriftLineMath.SpawnWeight(1f, 30f, 0f, 2f, floor) == 0f
                  && DriftLineMath.SpawnWeight(1f, 30f, max, 2f, float.NaN) == 0f,
                "SpawnWeight answers 0, never NaN, for NaN in ANY of five inputs or a zero MaxSpeed");

            // 8. A ZERO FLOOR IS THE 0.7 CLIFF, EXACTLY. This is what makes the deleted assertion
            //    above recoverable rather than lost: the old behaviour is still reachable, still
            //    tested, and one config value away.
            bool cliffRestored = true;
            for (int i = 0; i <= 40; i++)
                if (DriftLineMath.SpawnWeight(slack * i / 40f, 30f, max, 2f, 0f) != 0f)
                    cliffRestored = false;
            Check(cliffRestored, "a slack floor of 0 restores 0.7's cliff exactly - no foam below slack");

            // 9. THE TWO READINGS THAT CAUSED THIS CHANGE, pinned as regressions. Both were taken
            //    in game on Storm10, 2026-09-19, and both showed a bare sea.
            // READ THE SHIPPED DEFAULT, do not spell it. A literal 2f here was a decorative
            // assertion: a mutation reverting ModConfig's default to 10 left the harness GREEN,
            // because nothing in this block was actually looking at what the mod binds. Caught by
            // the mutation suite on 2026-09-19, which is the entire reason that suite exists.
            float shippedMinDepth = (float)ModConfig.DriftLineMinDepth.DefaultValue;
            Check(shippedMinDepth == 2f,
                "ModConfig binds DriftLineMinDepth at 2 - the whole point of the 0.8 rebase");
            float ownersRace = DriftLineMath.SpawnWeight(0.665f, 5.1f, 1.2f, shippedMinDepth, floor);
            Check(ownersRace > 0.3f && ownersRace < 0.6f,
                $"the owner's 0.665 m/s race at 5.1 m now spawns freely (weight {Fmt(ownersRace)})");
            Check(DriftLineMath.SpawnWeight(0.665f, 5.1f, 1.2f, 10f, floor) == 0f,
                "...and is exactly 0 at the OLD MinDepth of 10 - the negative control for the rebase");
            float ownersSlack = DriftLineMath.SpawnWeight(0.091f, 30f, 1.2f, shippedMinDepth, floor);
            Check(ownersSlack > 0f && ownersSlack < 0.1f,
                $"the owner's 0.091 m/s slack is sparse but no longer bare (weight {Fmt(ownersSlack)})");

            // 10. THE ANCHOR. MinDepth 2 + a 6 m ramp finishes at 8, which is exactly where
            //     CurrentField's own shallow fade finishes. That coincidence is the entire
            //     justification for the new default, so it is pinned rather than left in a comment.
            Check(shippedMinDepth + DriftLineMath.DepthRampMetres == FieldSettings.Defaults.ShallowFadeDepth,
                "the depth ramp ends exactly where CurrentField's shallow fade ends (2 + 6 == 8)");

            // ---- THE CROSS-TEST: Classify's Slack and SpawnWeight's zero are ONE definition --
            // Two definitions of "slack" that silently disagree would have `wake here` say Slack
            // while foam is drawn, or the reverse. Every deep flat-seabed point must agree.
            // The scan is WIDE (12 km) and the lattice is 40 m, for two reasons found the first
            // time this ran: within a kilometre of the origin the open-ocean term never drops
            // below 0.24 m/s on a flat seabed, so a small window holds no slack at all; and a
            // slack pocket around a node of the stream function is only tens of metres across,
            // so a coarse lattice steps over every one and the agreement check passes
            // vacuously. The second Check below exists so that can never happen quietly again.
            var flat = new FlatSeabed(0f);
            int points = 0, slackCount = 0, mismatches = 0;
            float slowest = float.MaxValue;
            for (int ix = -150; ix <= 150; ix++)
            for (int iz = -150; iz <= 150; iz++)
            {
                FieldSample f = CurrentField.Evaluate(ix * 40f, iz * 40f, 1337, 500.0, 0, 1f, flat, s);
                if (f.Speed < slowest) slowest = f.Speed;
                if (Math.Abs(f.Speed - CurrentField.SlackSpeed) < 1e-6f) continue;
                points++;
                bool isSlack = f.Dominant == CurrentTerm.Slack;

                // REWRITTEN FROM AN EQUALITY TO AN ORDERING, because 0.8 replaced the cliff with
                // a floor. The property being defended is unchanged and is the one that mattered:
                // Classify's Slack and SpawnWeight's idea of slack are ONE definition, so they can
                // never silently drift apart and have `wake here` say Slack while foam pours. The
                // old form asserted "Slack <=> zero foam"; the new form asserts "Slack <=> at most
                // the floor, running water <=> more than the floor", which is the same agreement
                // with the same single source for the constant.
                float w = DriftLineMath.SpawnWeight(f.Speed, f.Depth, s.MaxSpeed, 2f,
                                                    DriftLineMath.DefaultSlackFloor);
                bool foamIsSlackGrade = w <= DriftLineMath.DefaultSlackFloor + 1e-6f;
                if (isSlack) slackCount++;
                if (isSlack != foamIsSlackGrade) mismatches++;
            }
            Check(points > 90000 && mismatches == 0,
                $"over {points} deep flat-seabed points, Slack <=> at most floor-grade foam ({mismatches} disagree)");
            Check(slackCount > 0 && slackCount < points,
                $"the cross-test saw both slack and running water ({slackCount} slack of {points}, slowest {Fmt(slowest)} m/s against a {Fmt(CurrentField.SlackSpeed)} threshold)");

            // ---- LifeEnvelope: nothing pops --------------------------------------------------
            Check(DriftLineMath.LifeEnvelope(0f) == 0f && DriftLineMath.LifeEnvelope(1f) == 0f
                  && DriftLineMath.LifeEnvelope(-0.1f) == 0f && DriftLineMath.LifeEnvelope(1.1f) == 0f,
                "LifeEnvelope is 0 at both ends and outside [0,1]");
            Check(DriftLineMath.LifeEnvelope(0.5f) == 1f, "LifeEnvelope holds at 1 mid-life");
            Check(DriftLineMath.LifeEnvelope(0.01f) < 0.1f && DriftLineMath.LifeEnvelope(0.99f) < 0.1f,
                "a streak is still faint just after it forms and just before it goes - nothing pops");
            bool envOk = true; prev = 0f;
            for (int i = 0; i <= 1000; i++)
            {
                float a = i / 1000f;
                float e = DriftLineMath.LifeEnvelope(a);
                if (e < 0f || e > 1f) envOk = false;
                if (a < 0.2f && e < prev) envOk = false;
                if (a > 0.65f && e > prev + 1e-6f) envOk = false;
                prev = e;
            }
            Check(envOk, "LifeEnvelope stays in [0,1], rises through the fade-in and falls through the fade-out");

            // ---- EdgeFade: the disc has no visible rim -------------------------------------
            Check(DriftLineMath.EdgeFade(0f, 60f) == 1f && DriftLineMath.EdgeFade(39f, 60f) == 1f
                  && DriftLineMath.EdgeFade(60f, 60f) == 0f && DriftLineMath.EdgeFade(90f, 60f) == 0f,
                "EdgeFade is 1 inside 65% of the radius and 0 at and beyond it");
            bool edgeMono = true; prev = 2f;
            for (int i = 0; i <= 200; i++)
            {
                float e = DriftLineMath.EdgeFade(90f * i / 200f, 60f);
                if (e > prev + 1e-6f) edgeMono = false;
                prev = e;
            }
            Check(edgeMono, "EdgeFade never increases with distance");
            Check(Math.Abs(DriftLineMath.EdgeFade(49.5f, 60f) - 0.25f) < 1e-4f,
                $"EdgeFade is a quadratic ramp, a quarter at the midpoint of the rim ({Fmt(DriftLineMath.EdgeFade(49.5f, 60f))})");
            Check(DriftLineMath.EdgeFade(10f, 0f) == 0f, "EdgeFade at radius 0 answers 0 without dividing");

            // ---- Reflect: what falls astern reappears ahead ---------------------------------
            float ix0 = 54f * (float)Math.Cos(0.5), iz0 = 54f * (float)Math.Sin(0.5);   // 0.9R
            Check(!DriftLineMath.Reflect(ref ix0, ref iz0, 60f), "Reflect leaves a streak inside the disc alone");
            float ox = 72f * (float)Math.Sin(30 * Math.PI / 180), oz = 72f * (float)Math.Cos(30 * Math.PI / 180);   // 1.2R on bearing 30
            float rx = ox, rz = oz;
            bool moved = DriftLineMath.Reflect(ref rx, ref rz, 60f);
            float mag = (float)Math.Sqrt(rx * rx + rz * rz);
            Check(moved && Math.Abs(mag - 58.8f) < 1e-3f && rx * ox + rz * oz < 0f,
                $"Reflect mirrors a streak at 1.2R through the centre to 0.98R ({Fmt(mag)} m)");
            Check(!DriftLineMath.Reflect(ref rx, ref rz, 60f), "a reflected streak is not reflected again");

            // ---- DiscPoint: uniform by AREA, or it reads as a personal cloud ---------------
            uint rng = 12345u;
            int inside = 0, inner = 0;
            double sumX = 0, sumZ = 0;
            const int N = 10000;
            for (int i = 0; i < N; i++)
            {
                DriftLineMath.DiscPoint(DriftLineMath.NextRoll(ref rng), DriftLineMath.NextRoll(ref rng), 60f,
                                        out float px, out float pz);
                float d = (float)Math.Sqrt(px * px + pz * pz);
                if (d <= 60f) inside++;
                if (d <= 30f) inner++;
                sumX += px; sumZ += pz;
            }
            Check(inside == N, "every DiscPoint lies within the radius");
            Check(Math.Abs(inner / (float)N - 0.25f) < 0.02f,
                $"a quarter of DiscPoints fall inside half the radius ({Fmt(inner / (float)N)}) - uniform by area, not by radius");
            Check(Math.Abs(sumX / N) < 1.8 && Math.Abs(sumZ / N) < 1.8, "DiscPoint has no bias to one side of the hull");

            // ---- StreakSize: always foam-sized -----------------------------------------------
            bool sizeOk = true;
            float[] maxes = { 0f, 1.2f, 1.95f };
            foreach (float m in maxes)
            for (int i = 0; i <= 25; i++)
            for (int ci = 0; ci <= 2; ci++)
            for (int r = 0; r <= 2; r++)
            {
                DriftLineMath.StreakSize(i * 0.2f, m, ci / 2f, r / 2f, (2 - r) / 2f, out float len, out float wid);
                if (len < 0.9f || len > 7.4f || wid < 0.22f || wid > 0.44f || wid >= len) sizeOk = false;
            }
            Check(sizeOk, "StreakSize stays foam-sized (0.9-7.4 m long, 0.22-0.44 m wide, always longer than wide)");
            bool lenMono = true; prev = 0f;
            for (int i = 0; i <= 50; i++)
            {
                DriftLineMath.StreakSize(i * 0.1f, 1.2f, 0f, 0.5f, 0.5f, out float len, out _);
                if (len < prev) lenMono = false;
                prev = len;
            }
            Check(lenMono, "streak length never shrinks as the water speeds up");
            DriftLineMath.StreakSize(float.NaN, 1.2f, 0f, 0.5f, 0.5f, out float nanLen, out float nanWid);
            Check(!float.IsNaN(nanLen) && !float.IsNaN(nanWid), "StreakSize is NaN-safe");

            // ---- bearings: the streak and the console must agree ----------------------------
            Check(Math.Abs(DriftLineMath.BearingDegrees(0f, 1f)) < 1e-3f
                  && Math.Abs(DriftLineMath.BearingDegrees(1f, 0f) - 90f) < 1e-3f
                  && Math.Abs(DriftLineMath.BearingDegrees(0f, -1f) - 180f) < 1e-3f
                  && Math.Abs(DriftLineMath.BearingDegrees(-1f, 0f) - 270f) < 1e-3f,
                "BearingDegrees: N 0, E 90, S 180, W 270");
            Check(DriftLineMath.BearingDegrees(0f, 0f) == 0f, "a zero vector bears 0");
            bool compassOk = true;
            for (int h = 0; h < 16; h++)
            {
                double b = h * 22.5 * Math.PI / 180.0;
                float deg = DriftLineMath.BearingDegrees((float)Math.Sin(b), (float)Math.Cos(b));
                if ((int)Math.Round(deg / 22.5) % 16 != h) compassOk = false;
            }
            Check(compassOk, "BearingDegrees agrees with the console's sixteen-point compass (Atan2(x, z), +z north) on every point");
            // MEASURED on Storm10, 2026-09-18, in uniform 192° water with ~80 streaks: a rotation
            // of -bearing laid the lines a quarter turn across the flow, which only the
            // "+x at rotation 0, clockwise-positive seen from above" convention produces. So the
            // rotation is 90 - bearing: east needs no turn, north a quarter turn.
            Check(Math.Abs(DriftLineMath.QuadRotationDegrees(90f, false)) < 1e-3f
                  && Math.Abs(DriftLineMath.QuadRotationDegrees(0f, false) - 90f) < 1e-3f
                  && Math.Abs(DriftLineMath.QuadRotationDegrees(180f, false) - 270f) < 1e-3f
                  && Math.Abs(DriftLineMath.QuadRotationDegrees(270f, false) - 180f) < 1e-3f,
                "QuadRotationDegrees: east needs no turn, north a quarter turn (measured 2026-09-18)");
            bool flipOk = true;
            for (int b = 0; b <= 360; b += 5)
            {
                float flipped = DriftLineMath.QuadRotationDegrees(b, true);
                float straight = DriftLineMath.QuadRotationDegrees(b, false);
                float expect = (360f - straight) % 360f;
                float diff = Math.Abs(flipped - expect) % 360f;
                if (diff > 1e-3f && Math.Abs(diff - 360f) > 1e-3f) flipOk = false;
            }
            Check(flipOk, "the flip constant mirrors the rotation rather than doing nothing");

            // ---- the texture: no head, no tail, no edge --------------------------------------
            const int W = 128, H = 32;
            bool ringZero = true;
            for (int x = 0; x < W; x++)
                if (DriftLineMath.StreakAlphaAt(x, 0, W, H, 7) != 0f || DriftLineMath.StreakAlphaAt(x, H - 1, W, H, 7) != 0f) ringZero = false;
            for (int y = 0; y < H; y++)
                if (DriftLineMath.StreakAlphaAt(0, y, W, H, 7) != 0f || DriftLineMath.StreakAlphaAt(W - 1, y, W, H, 7) != 0f) ringZero = false;
            Check(ringZero, "every texel of the texture's outer ring is transparent (Clamp can never bleed an edge)");
            Check(DriftLineMath.StreakAlphaAt(W / 2, H / 2, W, H, 7) > 0.5f, "the texture's centre is solid");
            bool symmetric = true; rng = 777u;
            for (int i = 0; i < 500; i++)
            {
                float u = 0.01f + 0.98f * DriftLineMath.NextRoll(ref rng);
                float v = 0.01f + 0.98f * DriftLineMath.NextRoll(ref rng);
                float a = DriftLineMath.StreakAlpha(u, v, 7);
                if (Math.Abs(a - DriftLineMath.StreakAlpha(1f - u, v, 7)) > 1e-5f
                    || Math.Abs(a - DriftLineMath.StreakAlpha(u, 1f - v, 7)) > 1e-5f) symmetric = false;
            }
            Check(symmetric, "the streak texture is symmetric end to end and side to side - no head, no tail, no arrow");
            bool texelSym = true;
            for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
                if (Math.Abs(DriftLineMath.StreakAlphaAt(x, y, W, H, 7) - DriftLineMath.StreakAlphaAt(W - 1 - x, y, W, H, 7)) > 1e-5f) texelSym = false;
            Check(texelSym, "texel for texel, the rasterised streak reads the same from either end");

            // ---- light: the unlit shader must not glow at night -----------------------------
            // The fixture is the sky that was MEASURED (2026-09-21, `tod 0` / `tod 0.5` on
            // Storm10), not a range. The first version of this block asserted 0 below 0.05 and 1
            // above 0.35 and was green for three days while the foam drew at full brightness at
            // midnight — the real night never goes below 0.38 on the input it was then fed. A test
            // against an imagined range proves the arithmetic and nothing about the sky.
            float midnight = DriftLineMath.DayFactor(DriftLineMath.MeasuredMidnightFogLuminance);
            float noon = DriftLineMath.DayFactor(DriftLineMath.MeasuredNoonFogLuminance);
            Check(midnight <= 0.05f,
                $"the measured midnight fog ({Fmt(DriftLineMath.MeasuredMidnightFogLuminance)}) reads as night, not day ({Fmt(midnight)})");
            Check(noon >= 0.99f,
                $"the measured noon fog ({Fmt(DriftLineMath.MeasuredNoonFogLuminance)}) reads as full day ({Fmt(noon)})");
            Check(DriftLineMath.DayFactor(0f) == 0f && DriftLineMath.DayFactor(float.NaN) == 0f,
                "DayFactor: 0 for black and 0 for NaN");
            // Why the input moved, as a number: the ambient light at the same midnight sits well
            // inside the day band, so on that input no floor can separate night from noon without
            // also dimming a clear day.
            Check(DriftLineMath.MeasuredMidnightAmbientLuminance > DriftLineMath.DayFloorLuminance
                  && DriftLineMath.MeasuredNoonAmbientLuminance - DriftLineMath.MeasuredMidnightAmbientLuminance
                     < DriftLineMath.MeasuredNoonFogLuminance - DriftLineMath.MeasuredMidnightFogLuminance,
                "the fog colour has more midnight-to-noon contrast than the ambient light, which is why it is the input");
            bool dayMono = true; prev = -1f;
            for (int i = 0; i <= 100; i++)
            {
                float d = DriftLineMath.DayFactor(i / 100f);
                if (d < prev) dayMono = false;
                prev = d;
            }
            Check(dayMono, "DayFactor never falls as the light rises");
            DriftLineMath.Tint(0.5f, 0.5f, 0.55f, 0f, 0f, out float nr, out float ng, out float nb, out float na);
            DriftLineMath.Tint(0.5f, 0.5f, 0.55f, 1f, 0f, out float dr, out float dg, out float db, out float da);
            // The night floor: 0 is the cliff the owner called "too dim to find", 1 is no dimming,
            // and anything between lands between — by day it must do nothing at all.
            DriftLineMath.Tint(0.5f, 0.5f, 0.55f, 0f, 1f, out float fr, out _, out _, out float fa);
            Check(Math.Abs(fr - dr) < 1e-6f && Math.Abs(fa - da) < 1e-6f,
                "a night floor of 1 is no dimming at all — the day tint at midnight");
            DriftLineMath.Tint(0.5f, 0.5f, 0.55f, 0f, DriftLineMath.DefaultNightFloor, out float hr, out _, out _, out float ha);
            Check(hr > nr && hr < dr && ha > na && ha < da,
                $"the shipped night floor ({Fmt(DriftLineMath.DefaultNightFloor)}) lands between the cliff and the day ({Fmt(nr)} < {Fmt(hr)} < {Fmt(dr)})");
            DriftLineMath.Tint(0.5f, 0.5f, 0.55f, 1f, DriftLineMath.DefaultNightFloor, out float xr, out _, out _, out float xa);
            Check(Math.Abs(xr - dr) < 1e-6f && Math.Abs(xa - da) < 1e-6f,
                "the night floor does nothing by day");
            Check(DriftLineMath.DefaultNightFloor > 0f && DriftLineMath.DefaultNightFloor < 1f,
                "the shipped night floor is neither the cliff nor 'no dimming'");
            Check(nr <= 0.13f && ng <= 0.13f && nb <= 0.13f && Math.Abs(na - 0.35f) < 1e-6f,
                $"at night the tint is a smear a shade above black water ({Fmt(nr)},{Fmt(ng)},{Fmt(nb)}), never a glow");
            Check(dr >= 0.7f && dg >= 0.7f && db >= 0.7f && Math.Abs(da - 1f) < 1e-6f,
                "by day the tint is near white at full alpha");
            Check(Math.Abs(DriftLineMath.Luminance(1f, 1f, 1f) - 1f) < 1e-5f && DriftLineMath.Luminance(0f, 0f, 0f) == 0f,
                "Luminance of white is 1 and of black is 0");

            // ---- fog: streaks sink into the mist with the water -----------------------------
            bool fogOff = true;
            for (int mode = 0; mode <= 4; mode++)
                if (DriftLineMath.FogVisibility(50f, 0f, mode, 0f, 0f) != 1f
                    || DriftLineMath.FogVisibility(0f, 0.02f, mode, 0f, 100f) != 1f) fogOff = false;
            Check(fogOff, "no fog density, or no distance, means fully visible in every mode");
            bool fogMono = true; float p2 = 2f, p3 = 2f;
            for (int i = 1; i <= 100; i++)
            {
                float v2 = DriftLineMath.FogVisibility(i * 5f, 0.02f, 2, 0f, 0f);
                float v3 = DriftLineMath.FogVisibility(i * 5f, 0.02f, 3, 0f, 0f);
                if (v2 >= p2 || v3 >= p3) fogMono = false;
                p2 = v2; p3 = v3;
            }
            Check(fogMono, "exponential fog strictly thickens with distance");
            Check(Math.Abs(DriftLineMath.FogVisibility(50f, 0.02f, 3, 0f, 0f) - (float)Math.Exp(-1.0)) < 1e-4f,
                "exp-squared fog at d = 1/k is e^-1");
            Check(DriftLineMath.FogVisibility(10f, 0f, 1, 10f, 110f) == 1f
                  && DriftLineMath.FogVisibility(110f, 0f, 1, 10f, 110f) == 0f
                  && Math.Abs(DriftLineMath.FogVisibility(60f, 0f, 1, 10f, 110f) - 0.5f) < 1e-5f,
                "linear fog: 1 at start, 0.5 midway, 0 at end");
            Check(DriftLineMath.FogVisibility(50f, 0f, 1, 100f, 100f) == 1f, "a degenerate linear range does not divide by zero");
            Check(DriftLineMath.FogVisibility(50f, 0.02f, 9, 0f, 0f) == 1f, "an unknown fog mode is treated as clear");

            // ---- sea state: chop from our own samples ---------------------------------------
            float ss = DriftLineMath.SeaState(0f, 1f, 2f, 2f);
            Check(Math.Abs(ss - 0.632f) < 0.005f, $"SeaState with dt = tau moves 63.2% of the way ({Fmt(ss)})");
            Check(DriftLineMath.SeaState(0.2f, 0.9f, 0.016f, 0f) == 0.9f, "SeaState with no time constant is the observation");
            Check(DriftLineMath.Chop(0f) == 0f && DriftLineMath.Chop(0.3f) == 0f && DriftLineMath.Chop(2.3f) == 1f,
                "Chop: 0 in a calm, 1 in a full storm");
            Check(DriftLineMath.ChopFade(1.5f) == 1f && Math.Abs(DriftLineMath.ChopFade(3f) - 0.5f) < 1e-6f
                  && DriftLineMath.ChopFade(6f) > 0.09f,
                "ChopFade tempers a big sea to half at 3 m and never erases it");
            bool cfMono = true; prev = 2f;
            for (int i = 0; i <= 100; i++)
            {
                float f = DriftLineMath.ChopFade(i * 0.1f);
                if (f > prev + 1e-6f) cfMono = false;
                prev = f;
            }
            Check(cfMono, "ChopFade never increases with sea state");

            // ---- keys and rolls -------------------------------------------------------------
            var keys = new HashSet<long>();
            for (int ix = -12; ix < 13; ix++)
            for (int iz = -12; iz < 13; iz++)
                keys.Add(DriftLineMath.CellKey(ix * 16f + 0.5f, iz * 16f + 0.5f, 16f));
            Check(keys.Count == 625, $"CellKey is injective over a 400x400 m grid spanning negative coordinates ({keys.Count} of 625)");
            Check(DriftLineMath.CellKey(15.9f, 0f, 16f) == DriftLineMath.CellKey(0.1f, 0f, 16f)
                  && DriftLineMath.CellKey(16.1f, 0f, 16f) != DriftLineMath.CellKey(0.1f, 0f, 16f),
                "CellKey groups a cell and splits at its edge");

            rng = 42u;
            double sum = 0;
            int run = 0, worstRun = 0;
            float last = -1f;
            bool inRange = true;
            for (int i = 0; i < 100000; i++)
            {
                float r = DriftLineMath.NextRoll(ref rng);
                if (r < 0f || r >= 1f) inRange = false;
                sum += r;
                if (r == last) { run++; if (run > worstRun) worstRun = run; } else run = 0;
                last = r;
            }
            Check(inRange && Math.Abs(sum / 100000 - 0.5) < 0.01 && worstRun <= 5,
                $"NextRoll stays in [0,1) with mean {Fmt((float)(sum / 100000))} and no runs");
            uint zero = 0u;
            float z1 = DriftLineMath.NextRoll(ref zero);
            float z2 = DriftLineMath.NextRoll(ref zero);
            Check(zero != 0u && z1 != z2, "a zero state is reseeded and does not stick");

            rng = 99u;
            int ones = 0, twos = 0, threes = 0;
            double meanCluster = 0;
            for (int i = 0; i < 10000; i++)
            {
                int cs = DriftLineMath.ClusterSize(DriftLineMath.NextRoll(ref rng));
                if (cs == 1) ones++; else if (cs == 2) twos++; else if (cs == 3) threes++;
                meanCluster += cs;
            }
            Check(ones + twos + threes == 10000 && Math.Abs(meanCluster / 10000 - 1.7) < 0.05,
                $"ClusterSize is 1, 2 or 3 with mean {Fmt((float)(meanCluster / 10000))}");
            Check(DriftLineMath.LifetimeSeconds(0f) == 6f && DriftLineMath.LifetimeSeconds(1f) == 14f
                  && DriftLineMath.LifetimeSeconds(-1f) == 6f && DriftLineMath.LifetimeSeconds(2f) == 14f,
                "LifetimeSeconds spans 6-14 s and clamps stray rolls");

            // ---- riding and self-protection -------------------------------------------------
            Check(DriftLineMath.VerticalVelocity(30f, 130f, 0.016f) == 5f
                  && DriftLineMath.VerticalVelocity(30f, 29.9f, 0.016f) == -5f
                  && DriftLineMath.VerticalVelocity(1f, 2f, 0f) == 0f,
                "VerticalVelocity is clamped to +/-5 m/s and 0 over no time");
            Check(DriftLineMath.ClampDt(3f) == 0.25f && DriftLineMath.ClampDt(-1f) == 0f
                  && DriftLineMath.ClampDt(float.NaN) == 0f && DriftLineMath.ClampDt(0.016f) == 0.016f,
                "ClampDt caps a loading hitch at a quarter second");
            Check(DriftLineMath.DegradedCap(160, 0.7f, 0.5f) == 80 && DriftLineMath.DegradedCap(160, 0.3f, 0.5f) == 160
                  && DriftLineMath.DegradedCap(20, 9f, 0.5f) == 16 && DriftLineMath.DegradedCap(160, float.NaN, 0.5f) == 160,
                "DegradedCap halves over budget, floors at 16, ignores NaN");

            // ---- purity ---------------------------------------------------------------------
            Check(DriftLineMath.StreakAlpha(0.3f, 0.6f, 7) == DriftLineMath.StreakAlpha(0.3f, 0.6f, 7)
                  && DriftLineMath.Hash01(3, 4, 5) == DriftLineMath.Hash01(3, 4, 5),
                "identical calls give bit-identical output");
            double hashSum = 0;
            var seenHashes = new HashSet<float>();
            for (int i = 0; i < 1000; i++)
            {
                float h = DriftLineMath.Hash01(i, 17, 7);
                hashSum += h;
                seenHashes.Add(h);
            }
            Check(seenHashes.Count > 990 && Math.Abs(hashSum / 1000 - 0.5) < 0.02, "Hash01 spreads consecutive salts evenly");
        }


        // ---- the config migration ----------------------------------------------------------

        /// <summary>A dictionary shaped like a parsed config file, for the Plan tests below.</summary>
        private static Dictionary<string, string> Snapshot(params string[] slotThenValue)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < slotThenValue.Length; i += 2) d[slotThenValue[i]] = slotThenValue[i + 1];
            return d;
        }

        private static Dictionary<int, T[]> Table<T>(int version, params T[] steps)
            => new Dictionary<int, T[]> { { version, steps } };

        private static readonly Dictionary<int, ConfigLedger.Rebase[]> NoRebases = new Dictionary<int, ConfigLedger.Rebase[]>();
        private static readonly Dictionary<int, ConfigLedger.Backfill[]> NoBackfills = new Dictionary<int, ConfigLedger.Backfill[]>();
        private static readonly Dictionary<int, ConfigLedger.Retire[]> NoRetires = new Dictionary<int, ConfigLedger.Retire[]>();

        /// <summary>
        /// The PURE half. Every shipped table is empty today - a measured fact, not an oversight -
        /// so testing Plan only against them would prove that nothing happens, which is the one
        /// thing needing no proof. These drive the same method through its table-taking overload
        /// with synthetic rungs, so the matching rules are already measured on the day the first
        /// real one lands rather than running for the first time on somebody's server.
        /// </summary>
        private static void ConfigLedgerTests()
        {
            Section("ConfigLedger");

            // ---- ParseIni: a real BepInEx file, including the shapes Undertow's own config has.
            var ini = ConfigLedger.ParseIni(new[]
            {
                "## a comment",
                "",
                "[1 - Core]",
                "TickBudgetMs = 2",
                "",
                "# Setting type: Boolean",
                "VerboseLogging = true",
                "# TickBudgetMs = 999",
                "[5 - Flotsam]",
                "FlotsamCommon = Wood,RoundLog,FineWood",
                "Weird = a = b",
                "   Padded   =   spaced   ",
                "noequalshere",
                "= headless",
            });

            Check(ini["1 - Core::TickBudgetMs"] == "2", "ParseIni reads a key under its section");
            Check(ini["1 - Core::VerboseLogging"] == "true", "ParseIni skips comments and blank lines");
            foreach (string k in ini.Keys)
                Check(!k.Contains("#"), $"no comment line became a key ({k})");
            Check(ini["1 - Core::TickBudgetMs"] == "2",
                "a setting an admin COMMENTED OUT does not overwrite the live one above it");
            Check(ini["5 - Flotsam::FlotsamCommon"] == "Wood,RoundLog,FineWood",
                "ParseIni keeps a comma list whole (Undertow's flotsam tables are comma lists)");
            Check(ini["5 - Flotsam::Weird"] == "a = b", "ParseIni splits on the FIRST = and keeps the rest of the value");
            Check(ini["5 - Flotsam::Padded"] == "spaced", "ParseIni trims the key and the value");
            Check(!ini.ContainsKey("5 - Flotsam::noequalshere"), "ParseIni ignores a line with no =");
            Check(ini.Count == 5, $"ParseIni ignores a headless '= value' line ({ini.Count} keys read)");
            Check(!ini.ContainsKey("1 - core::tickbudgetms"),
                "ParseIni is ORDINAL and case-SENSITIVE, because BepInEx's ConfigDefinition is: "
                + "a mis-cased line is a different key there, and an ignore-case snapshot would answer "
                + "'present' for a key BepInEx treats as absent - cancelling the one step whose whole safety is that test");
            Check(ConfigLedger.ParseIni(null).Count == 0, "ParseIni(null) is an empty file, not a crash");
            Check(ConfigLedger.ParseIni(new string[] { null }).Count == 0, "ParseIni survives a null line");

            var dupe = ConfigLedger.ParseIni(new[] { "[S]", "K = first", "K = second" });
            Check(dupe["S::K"] == "second", "ParseIni lets the last duplicate win, as BepInEx's own loader does");

            // ---- ReadVersion: everything that is not a number is a pre-migration file.
            Check(ConfigLedger.ReadVersion(null) == 0, "ReadVersion(null) is 0");
            Check(ConfigLedger.ReadVersion(Snapshot()) == 0, "an unstamped file reads as version 0");
            Check(ConfigLedger.ReadVersion(Snapshot(ConfigLedger.Slot(ConfigLedger.MetaSection, ConfigLedger.VersionKey), "  3  ")) == 3,
                "ReadVersion trims whitespace around the stamp");
            Check(ConfigLedger.ReadVersion(Snapshot(ConfigLedger.Slot(ConfigLedger.MetaSection, ConfigLedger.VersionKey), "banana")) == 0,
                "an unparseable stamp reads as 0 rather than throwing");

            // ---- Slot / SplitSlot round-trip.
            Check(ConfigLedger.Slot("1 - Core", "TickBudgetMs") == "1 - Core::TickBudgetMs", "Slot joins with ::");
            Check(ConfigLedger.SplitSlot("1 - Core::TickBudgetMs", out string sec, out string key)
                  && sec == "1 - Core" && key == "TickBudgetMs", "SplitSlot is Slot's inverse");
            Check(!ConfigLedger.SplitSlot("nocolons", out _, out _), "SplitSlot rejects a string that is not a slot");
            Check(!ConfigLedger.SplitSlot("::key", out _, out _), "SplitSlot rejects an empty section");
            Check(!ConfigLedger.SplitSlot("section::", out _, out _), "SplitSlot rejects an empty key");
            Check(!ConfigLedger.SplitSlot(null, out _, out _), "SplitSlot(null) is false, not a crash");

            // ---- REBASE: a stored old default is the mod's and moves; anything else is the admin's.
            var rebase = Table(1, new ConfigLedger.Rebase
            {
                Section = "3 - The current", Key = "MaxCurrentSpeed",
                OldDefaults = new[] { "1", "1.2" }, Because = "the sea got a size",
            });

            var moved = ConfigLedger.Plan(Snapshot("3 - The current::MaxCurrentSpeed", "1.2"), 0, 1, rebase, NoBackfills, NoRetires);
            Check(moved.ResetToDefault.Count == 1 && moved.ResetToDefault[0] == "3 - The current::MaxCurrentSpeed",
                "a stored value equal to an old shipped default is moved to the new one");
            Check(moved.Kept.Count == 0, "a moved value is not also counted as kept");

            var matchedSecond = ConfigLedger.Plan(Snapshot("3 - The current::MaxCurrentSpeed", "1"), 0, 1, rebase, NoBackfills, NoRetires);
            Check(matchedSecond.ResetToDefault.Count == 1, "ANY of a rung's old defaults matches, not just the first");

            var kept = ConfigLedger.Plan(Snapshot("3 - The current::MaxCurrentSpeed", "1.95"), 0, 1, rebase, NoBackfills, NoRetires);
            Check(kept.ResetToDefault.Count == 0 && kept.Kept.Count == 1 && kept.Kept[0].Value == "1.95",
                "a value the admin chose is KEPT and reported with what it holds");

            var nearMiss = ConfigLedger.Plan(Snapshot("3 - The current::MaxCurrentSpeed", "1.20"), 0, 1, rebase, NoBackfills, NoRetires);
            Check(nearMiss.Kept.Count == 1,
                "old defaults are compared as TEXT: '1.20' is not '1.2', so it is treated as the admin's");

            var absent = ConfigLedger.Plan(Snapshot("1 - Core::TickBudgetMs", "2"), 0, 1, rebase, NoBackfills, NoRetires);
            Check(absent.IsEmpty, "a rebase for a key the file does not contain plans nothing");

            // ---- BACKFILL: absent only, and that is the whole safety of it.
            var backfill = Table(1, new ConfigLedger.Backfill
            {
                Section = "2 - Systems", Key = "EnableDriftLines",
                LegacyValue = "false", Because = "your sea looks the way it already did",
            });

            var filled = ConfigLedger.Plan(Snapshot("1 - Core::TickBudgetMs", "2"), 0, 1, NoRebases, backfill, NoRetires);
            Check(filled.Backfilled.Count == 1 && filled.Backfilled[0].Value == "false",
                "a backfill writes its legacy value into a file that lacks the key");

            var present = ConfigLedger.Plan(Snapshot("2 - Systems::EnableDriftLines", "true"), 0, 1, NoRebases, backfill, NoRetires);
            Check(present.IsEmpty,
                "a backfill NEVER overwrites a key already in the file - the admin's choice, or an earlier partial run's");

            // ---- RETIRE: present only.
            var retire = Table(1, new ConfigLedger.Retire { Section = "1 - Core", Key = "OldKey", Because = "renamed" });

            var dropped = ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "whatever"), 0, 1, NoRebases, NoBackfills, retire);
            Check(dropped.Retired.Count == 1 && dropped.Retired[0] == "1 - Core::OldKey", "a retired key present in the file is dropped");
            Check(ConfigLedger.Plan(Snapshot("1 - Core::TickBudgetMs", "2"), 0, 1, NoRebases, NoBackfills, retire).IsEmpty,
                "a retired key absent from the file plans nothing - the ordinary case after a rename");

            // ---- Which rungs run: the version window, applied in order.
            var atTwo = Table(2, new ConfigLedger.Retire { Section = "1 - Core", Key = "OldKey" });
            Check(ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "x"), 0, 1, NoRebases, NoBackfills, atTwo).IsEmpty,
                "a rung that produces version 2 does not run on a 0 -> 1 migration");
            Check(ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "x"), 0, 2, NoRebases, NoBackfills, atTwo).Retired.Count == 1,
                "the same rung DOES run on a 0 -> 2 migration");
            Check(ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "x"), 2, 2, NoRebases, NoBackfills, atTwo).IsEmpty,
                "a file already at the current version plans nothing");
            Check(ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "x"), 9, 1, NoRebases, NoBackfills, atTwo).IsEmpty,
                "a file stamped BEYOND the current version plans nothing rather than migrating backwards");
            Check(ConfigLedger.Plan(null, 0, 1, NoRebases, NoBackfills, retire).IsEmpty, "a null snapshot plans nothing");
            Check(ConfigLedger.Plan(Snapshot(), 0, 1, NoRebases, NoBackfills, retire).IsEmpty,
                "an empty snapshot - a fresh install - plans nothing");
            Check(ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "x"), 0, 1, null, null, null).IsEmpty,
                "null tables plan nothing rather than throwing");

            // A hand-edited or corrupt stamp must not become a loop bound. ConfigVersion carries no
            // AcceptableValueRange on purpose, so nothing stops someone typing a large negative
            // number - and an unclamped window would then spin billions of times on the boot thread.
            var negativeClock = System.Diagnostics.Stopwatch.StartNew();
            var fromNegative = ConfigLedger.Plan(Snapshot("1 - Core::OldKey", "x"), -2000000000, 1, NoRebases, NoBackfills, retire);
            negativeClock.Stop();
            Check(fromNegative.Retired.Count == 1,
                "a wildly negative stamp is treated as version 0 - a file claiming to predate version 0 IS a version 0 file");
            Check(negativeClock.ElapsedMilliseconds < 250,
                $"and it costs ONE step rather than two billion ({negativeClock.ElapsedMilliseconds} ms) - the right answer "
                + "arrived at slowly is still a config file freezing the boot thread");

            // One slot, one decision. Two rungs naming the same key must not judge it twice against
            // the same unchanged snapshot, or one key is reported and reset once per rung.
            var twice = new Dictionary<int, ConfigLedger.Rebase[]>
            {
                { 1, new[] { new ConfigLedger.Rebase { Section = "1 - Core", Key = "TickBudgetMs", OldDefaults = new[] { "2" } } } },
                { 2, new[] { new ConfigLedger.Rebase { Section = "1 - Core", Key = "TickBudgetMs", OldDefaults = new[] { "2" } } } },
            };
            var once = ConfigLedger.Plan(Snapshot("1 - Core::TickBudgetMs", "2"), 0, 2, twice, NoBackfills, NoRetires);
            Check(once.ResetToDefault.Count == 1,
                $"a key named by two rungs is decided ONCE, by the first that matches ({once.ResetToDefault.Count} decisions)");

            // ---- IsDestructive decides whether a failed backup may block the whole migration.
            Check(moved.IsDestructive, "a rebase is destructive: it overwrites a value that is on disk");
            Check(dropped.IsDestructive, "a retirement is destructive: it deletes a line that is on disk");
            Check(!filled.IsDestructive, "a backfill is NOT destructive: it writes a key that was absent");
            Check(!ConfigLedger.Plan(Snapshot(), 0, 1, NoRebases, NoBackfills, NoRetires).IsDestructive,
                "an empty plan is not destructive");

            // ---- Describe: an owner has to be able to act on this line.
            Check(ConfigLedger.Describe(null) == "config: nothing to migrate", "Describe(null) is a sentence, not a crash");
            string emptyLine = ConfigLedger.Describe(ConfigLedger.Plan(Snapshot(), 0, 1, NoRebases, NoBackfills, NoRetires));
            Check(emptyLine.Contains("0 -> 1") && emptyLine.Contains("nothing to migrate"),
                "Describe names both versions even when nothing moved");
            string movedLine = ConfigLedger.Describe(moved);
            Check(movedLine.Contains("3 - The current.MaxCurrentSpeed") && !movedLine.Contains("::"),
                "Describe writes slots as Section.Key, not with the internal ::");
            Check(ConfigLedger.Describe(kept).Contains("1.95"), "Describe names a kept value WITH what it holds");
            Check(ConfigLedger.Describe(filled).Contains("your sea looks the way it already did"),
                "Describe gives a backfill's reason, so an owner knows what changed and why");
            Check(ConfigLedger.Describe(dropped).Contains("retired"), "Describe says when a key was dropped");

            // ---- The SHIPPED tables. Empty by measurement (see ConfigLedger's remarks), so the
            //      only thing 0.7.1 does to any real file is stamp it.
            var realWorld = ConfigLedger.Plan(Snapshot(
                "1 - Core::TickBudgetMs", "2",
                "1 - Core::VerboseLogging", "true",
                "2 - Systems::EnableDrift", "true",
                "3 - The current::MaxCurrentSpeed", "1.951174",
                "5 - Flotsam::FlotsamPerHour", "120"), 0);
            Check(realWorld.IsEmpty,
                "a real config with no drift-line keys still plans nothing against the SHIPPED tables");
            Check(!realWorld.IsDestructive, "so it can never lose a setting");
            Check(realWorld.ToVersion == ConfigLedger.CurrentVersion, "and it targets the current layout version");

            // ---- VERSION 2: THE FIRST RUNG THIS LEDGER HAS EVER HAD -------------------------
            //
            // Everything above was written while all three tables were empty, which made this
            // whole file a ladder with nothing on it. 0.8 moves DriftLineMinDepth's shipped
            // default from 10 to 2, and BepInEx never rewrites a value already in a file — so
            // without this rung the fix would ship DISABLED for every existing install, silently.
            // That is the exact failure the ledger was built for, met for the first time.
            Check(ConfigLedger.CurrentVersion == 2,
                "the layout version is 2 - a table that exists but is never reached is not a migration");

            var rung = ConfigLedger.Plan(Snapshot(
                "0 - Meta::ConfigVersion", "1",
                "7 - Drift lines::DriftLineMinDepth", "10",
                "7 - Drift lines::DriftLineOpacity", "0.83"), 1);
            Check(!rung.IsEmpty && rung.ToVersion == 2, "a file at version 1 holding the old default plans a step to 2");
            Check(rung.ResetToDefault.Contains("7 - Drift lines::DriftLineMinDepth"),
                "...and that step resets DriftLineMinDepth, which was never the admin's value");
            Check(rung.IsDestructive,
                "the plan is DESTRUCTIVE, so ConfigMigration takes a .v1.bak before touching the file");

            // THE MUTATION THAT MATTERS MOST: a rebase that fires regardless of the stored value
            // would delete a setting an admin chose on purpose. 20 is nobody's shipped default.
            var chosen = ConfigLedger.Plan(Snapshot(
                "0 - Meta::ConfigVersion", "1",
                "7 - Drift lines::DriftLineMinDepth", "20"), 1);
            Check(!chosen.ResetToDefault.Contains("7 - Drift lines::DriftLineMinDepth"),
                "a DriftLineMinDepth the admin chose themselves is NOT reset");
            Check(chosen.Kept.Exists(k => k.Slot == "7 - Drift lines::DriftLineMinDepth" && k.Value == "20"),
                "...it is kept, and named, so the boot line can tell them it was left alone");

            // Ordinal TEXT comparison, which is the trap ConfigLedger's own comment warns about:
            // spelling the old default "10.0" in the table would match nothing and do nothing,
            // green all the way.
            var spelledLong = ConfigLedger.Plan(Snapshot(
                "0 - Meta::ConfigVersion", "1",
                "7 - Drift lines::DriftLineMinDepth", "10.0"), 1);
            Check(!spelledLong.ResetToDefault.Contains("7 - Drift lines::DriftLineMinDepth"),
                "'10.0' is a different string from '10' - the ledger compares text, never numbers");

            // A file that already ran the rung must never run it again.
            Check(ConfigLedger.Plan(Snapshot(
                    "0 - Meta::ConfigVersion", "2",
                    "7 - Drift lines::DriftLineMinDepth", "2"), 2).IsEmpty,
                "a file already at version 2 plans nothing");

            // A FRESH install has no drift-line slot at all, so the rung must not invent work.
            Check(!ConfigLedger.Plan(Snapshot("1 - Core::TickBudgetMs", "2"), 1).IsDestructive,
                "a file with no DriftLineMinDepth at all plans nothing destructive");

            // ---- The stamp's own slot. It is a migration key forever, so it is pinned here.
            Check(ConfigLedger.MetaSection == "0 - Meta",
                "the stamp's section is numbered, so BepInEx's alphabetical Save puts it first rather than last");
            // BepInEx's ConfigFile.Save groups by section and orders with the DEFAULT comparer, not
            // an ordinal one, so that is the comparison to make. Both are checked because the two
            // disagreeing on digits would be worth knowing about.
            Check(string.Compare(ConfigLedger.MetaSection, "1 - Core", StringComparison.CurrentCulture) < 0,
                "'0 - Meta' sorts above the first real section under the comparer BepInEx's Save actually uses");
            Check(string.CompareOrdinal(ConfigLedger.MetaSection, "1 - Core") < 0,
                "and ordinally too, so the written file's order does not depend on the server's locale");
            Check(ConfigLedger.CurrentVersion >= 1, "the current layout version is at least 1");
        }

        /// <summary>
        /// The ENGINE half, driven against the real ModConfig and a real file on disk. The pure
        /// tests above prove what a plan says; these prove the plan reaches the config.
        /// </summary>
        private static void ConfigMigrationTests()
        {
            Section("ConfigMigration");

            string dir = Path.Combine(Path.GetTempPath(), "ut_cfgmig_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string cfgPath = Path.Combine(dir, "com.raveniron.undertow.cfg");

                // A file exactly as 0.6.0 would have left it: no stamp, and values the owner chose.
                // Written with an invariant '.' decimal, which is what BepInEx writes.
                File.WriteAllLines(cfgPath, new[]
                {
                    "## Settings file was created by plugin Undertow v0.6.0",
                    "",
                    "[1 - Core]",
                    "TickBudgetMs = 2",
                    "VerboseLogging = true",
                    "",
                    "[3 - The current]",
                    "MaxCurrentSpeed = 1.951174",
                });

                var cfg = new ConfigFile { ConfigFilePath = cfgPath };
                ModConfig.Bind(cfg);

                Check(ModConfig.ConfigVersion != null && ModConfig.ConfigVersion.Value == ConfigLedger.CurrentVersion,
                    "an unstamped file is stamped with the current layout version");
                Check(Math.Abs(ModConfig.MaxCurrentSpeed.Value - 1.951174f) < 1e-6f,
                    "the owner's own value survives the migration untouched");
                Check(ModConfig.VerboseLogging.Value, "so does a bool the owner set");
                Check(cfg.SaveCount > 0,
                    "the config is saved - in the stamp-only case that Save is the ONLY thing that writes");
                // Reads CurrentVersion rather than hardcoding it: the literal "0 -> 1" here broke
                // the moment version 2 existed, which is a test failing for the right reason but
                // the wrong cause. What is being defended is that the summary SURVIVES, not which
                // number it names.
                Check(ConfigMigration.LastSummary.Contains("0 -> " + ConfigLedger.CurrentVersion),
                    "the boot line SURVIVES the migration that wrote it, so `wake status` can read it back");
                Check(!File.Exists(cfgPath + ".v0.bak"),
                    "a stamp-only migration writes no backup: there is nothing to lose and the copy would be identical");

                // THE ORDERING RULE, and the only assertion that can catch it while every shipped
                // ledger table is empty. A backfill acts on a key being ABSENT; BepInEx's own Bind
                // makes it present at its shipped default. Snapshot after any bind and every future
                // backfill silently becomes a no-op that still logs success and still stamps its
                // version — permanently, because the next boot then reads a current file.
                Check(cfg.BindCountAtPathRead == 0,
                    $"the migration reads the file BEFORE the first bind ({cfg.BindCountAtPathRead} keys were bound when it looked)");

                // Second boot against the file it just stamped. The snapshot now carries the stamp,
                // so Begin must short-circuit rather than migrate a current file all over again.
                File.WriteAllLines(cfgPath, new[]
                {
                    "[" + ConfigLedger.MetaSection + "]",
                    ConfigLedger.VersionKey + " = " + ConfigLedger.CurrentVersion.ToString(CultureInfo.InvariantCulture),
                    "",
                    "[3 - The current]",
                    "MaxCurrentSpeed = 1.951174",
                });
                var second = new ConfigFile { ConfigFilePath = cfgPath };
                ModConfig.Bind(second);
                Check(ModConfig.ConfigVersion.Value == ConfigLedger.CurrentVersion, "a second boot leaves the stamp where it is");
                Check(ConfigMigration.LastSummary == "",
                    "an already-current file reports NO migration summary - it short-circuits before planning one");
                Check(Math.Abs(ModConfig.MaxCurrentSpeed.Value - 1.951174f) < 1e-6f,
                    "and it still does not touch the owner's value");

                // A fresh install: no file at all. Every shipped default is right, and the only
                // thing to do is stamp, so the next release's migration knows where it started.
                var fresh = new ConfigFile { ConfigFilePath = Path.Combine(dir, "does-not-exist.cfg") };
                ModConfig.Bind(fresh);
                Check(ModConfig.ConfigVersion.Value == ConfigLedger.CurrentVersion, "a fresh install is stamped too");
                Check(Math.Abs(ModConfig.MaxCurrentSpeed.Value - 1.2f) < 1e-6f, "and gets the shipped default");
                Check(ConfigMigration.LastSummary == "", "a fresh install reports no migration");

                // THE STAMP ONLY GOES UP. A file written by a NEWER build has already had rungs
                // this build knows nothing about. Lowering the stamp makes the next upgrade replay
                // them against values the owner has since chosen - and a rebase cannot tell a
                // deliberate choice from the old default it happens to equal.
                string futurePath = Path.Combine(dir, "from-the-future.cfg");
                File.WriteAllLines(futurePath, new[]
                {
                    "[" + ConfigLedger.MetaSection + "]",
                    ConfigLedger.VersionKey + " = 7",
                    "",
                    "[3 - The current]",
                    "MaxCurrentSpeed = 1.951174",
                });
                var future = new ConfigFile { ConfigFilePath = futurePath };
                ModConfig.Bind(future);
                Check(ModConfig.ConfigVersion.Value == 7,
                    $"a file stamped ABOVE this build's layout keeps its own stamp ({ModConfig.ConfigVersion.Value}), "
                    + "rather than being dragged back to a version whose rungs already ran");
                Check(Math.Abs(ModConfig.MaxCurrentSpeed.Value - 1.951174f) < 1e-6f,
                    "and a newer file's values are left alone by an older build");

                // The stamp is a real bound key, in the section the ledger names. Get this wrong and
                // every future migration reads version 0 forever and re-runs on every boot.
                Check(fresh.Bound.Exists(e => e.Section == ConfigLedger.MetaSection && e.Key == ConfigLedger.VersionKey),
                    "ModConfig actually binds the stamp at the slot ConfigLedger addresses");

                // ---- The APPLY path. Every shipped table is empty, so nothing above ever ran
                //      these three loops. Driven here with synthetic plans against the real
                //      ModConfig, so the first real rung is not the first execution.
                var live = new ConfigFile();
                ModConfig.Bind(live);

                ModConfig.MaxCurrentSpeed.Value = 3.5f;
                var resetPlan = new ConfigLedger.MigrationPlan();
                resetPlan.ResetToDefault.Add(ConfigLedger.Slot("3 - The current", "MaxCurrentSpeed"));
                ConfigMigration.Apply(live, resetPlan);
                Check(Math.Abs(ModConfig.MaxCurrentSpeed.Value - 1.2f) < 1e-6f,
                    "applying a rebase puts the entry back to its SHIPPED default");

                var backfillPlan = new ConfigLedger.MigrationPlan();
                backfillPlan.Backfilled.Add(new ConfigLedger.BackfilledSlot
                {
                    Slot = ConfigLedger.Slot("2 - Systems", "EnableDriftLines"), Value = "false", Because = "test",
                });
                ConfigMigration.Apply(live, backfillPlan);
                Check(!ModConfig.EnableDriftLines.Value, "applying a backfill writes its legacy value into the bound entry");

                var floatBackfill = new ConfigLedger.MigrationPlan();
                floatBackfill.Backfilled.Add(new ConfigLedger.BackfilledSlot
                {
                    Slot = ConfigLedger.Slot("3 - The current", "TideAmplitude"), Value = "0.4", Because = "test",
                });
                ConfigMigration.Apply(live, floatBackfill);
                Check(Math.Abs(ModConfig.TideAmplitude.Value - 0.4f) < 1e-6f,
                    "a float backfill parses with an invariant '.' decimal, whatever the machine's locale");

                // A value BepInEx cannot parse is swallowed by its own SetSerializedValue, leaving
                // the entry untouched - so the requested change silently does not happen. The mod
                // survives it; ApplyBackfill's job is to say so rather than stamp in silence.
                float before = ModConfig.TideAmplitude.Value;
                RavenIron.Undertow.Undertow.Log.Clear();
                var badBackfill = new ConfigLedger.MigrationPlan();
                badBackfill.Backfilled.Add(new ConfigLedger.BackfilledSlot
                {
                    Slot = ConfigLedger.Slot("3 - The current", "TideAmplitude"), Value = "not-a-number", Because = "test",
                });
                ConfigMigration.Apply(live, badBackfill);
                Check(Math.Abs(ModConfig.TideAmplitude.Value - before) < 1e-6f,
                    "an unparseable backfill leaves the entry alone rather than corrupting it");
                Check(RavenIron.Undertow.Undertow.Log.Said("3 - The current::TideAmplitude"),
                    "and names the slot it could not set, rather than stamping the version in silence");

                // A retirement acts on an ORPHAN: a line an old build wrote that no current build
                // binds. BepInEx keeps those in a private dictionary and writes every one back out
                // on each Save, so a key that is merely no longer bound rides along forever. That
                // is what this proves, and it is why ConsumeRetiredKey binds before it removes.
                string orphanPath = Path.Combine(dir, "with-an-orphan.cfg");
                File.WriteAllLines(orphanPath, new[]
                {
                    "[1 - Core]",
                    "TickBudgetMs = 2",
                    "RetiredLongAgo = true",
                });
                var orphaned = new ConfigFile { ConfigFilePath = orphanPath };
                ModConfig.Bind(orphaned);
                Check(orphaned.HasOrphan("1 - Core", "RetiredLongAgo"),
                    "a key in the file that no current build binds survives binding as a BepInEx orphan");

                var retirePlan = new ConfigLedger.MigrationPlan();
                retirePlan.Retired.Add(ConfigLedger.Slot("1 - Core", "RetiredLongAgo"));
                ConfigMigration.Apply(orphaned, retirePlan);
                Check(!orphaned.HasOrphan("1 - Core", "RetiredLongAgo"),
                    "applying a retirement takes that orphan out, so the next Save stops writing it");
                Check(!orphaned.ContainsKey(new ConfigDefinition("1 - Core", "RetiredLongAgo")),
                    "and leaves nothing bound behind either, so the key is gone from both of BepInEx's sets");
                Check(Math.Abs(ModConfig.TickBudgetMs.Value - 2f) < 1e-6f,
                    "a retirement touches nothing else in the file");

                // A ledger that retires a key this build STILL binds is a bug in the ledger. The
                // real ConfigFile.Bind casts a stored entry to the requested type, so this throws
                // inside ConsumeRetiredKey — which must stay caught, and must leave the value be.
                bool retireThrew = false;
                try
                {
                    var wrongRetire = new ConfigLedger.MigrationPlan();
                    wrongRetire.Retired.Add(ConfigLedger.Slot("1 - Core", "VerboseLogging"));
                    ConfigMigration.Apply(live, wrongRetire);
                }
                catch { retireThrew = true; }
                Check(!retireThrew, "retiring a key this build still binds is survivable rather than fatal");
                Check(live.ContainsKey(new ConfigDefinition("1 - Core", "VerboseLogging")),
                    "and leaves that still-bound key exactly where it was");


                // ---- A STEP THAT FAILED FOR A RETRIABLE REASON MUST BE REPORTED UPWARD, because
                //      the version stamp is what decides whether it is ever tried again. Bind and
                //      Remove both touch the file — BepInEx saves after each newly created entry —
                //      so a transient lock (antivirus, cloud sync, a config manager, a second
                //      process in the same directory) takes a retirement down through no fault of
                //      the ledger. Swallow that and Finish stamps the version anyway, the file
                //      reads as current on every future boot, and a one-second lock is permanent.
                //
                //      Undertow's Retirements table is EMPTY today, so none of this can be reached
                //      through the shipped ledger and these plans are hand-built. That is exactly
                //      why it is tested: the first real retirement must not be the run that finds
                //      out this path was never exercised. The sibling mods carry the same code.
                Check(ConfigMigration.Apply(orphaned, new ConfigLedger.MigrationPlan()),
                    "Apply reports success when every step it ran succeeded");

                var throwingRetire = new ConfigLedger.MigrationPlan();
                throwingRetire.Retired.Add(ConfigLedger.Slot("1 - Core", "LockedAgainstUs"));
                Check(!ConfigMigration.Apply(new ThrowingConfigFile(), throwingRetire),
                    "a retirement that THREW is reported, so Finish can withhold the stamp and the next boot retries");

                // The inverse, and the reason the flag is named for RETRIABLE steps rather than for
                // steps in general. A ledger row naming a key this build does not bind, or one the
                // build still binds, cannot succeed next time either — withholding the stamp for
                // those would re-run the migration on every boot, forever, for a bug in our own
                // table that no retry can fix. They warn; they do not block.
                var unknownRetire = new ConfigLedger.MigrationPlan();
                unknownRetire.Retired.Add(ConfigLedger.Slot("9 - Nope", "NeverBoundAnywhere"));
                Check(ConfigMigration.Apply(new ThrowingConfigFile(), unknownRetire) == false,
                    "an unknown retire slot still reaches the drop (nothing binds it), so a throw there is retriable too");

                var stillBoundRetire = new ConfigLedger.MigrationPlan();
                stillBoundRetire.Retired.Add(ConfigLedger.Slot("1 - Core", "VerboseLogging"));
                Check(ConfigMigration.Apply(live, stillBoundRetire),
                    "but a row retiring a key this build STILL binds is refused without blocking the stamp — no retry can fix our own table");

                var unknownReset = new ConfigLedger.MigrationPlan();
                unknownReset.ResetToDefault.Add(ConfigLedger.Slot("9 - Nope", "NoSuchKey"));
                Check(ConfigMigration.Apply(live, unknownReset),
                    "and nor does a rebase row naming a key this build does not bind");

                // ---- AND THE GATE ITSELF: a failed retirement must actually stop the stamp. The
                //      two facts above are only worth having if Finish reads them, and a boolean
                //      that is computed and then ignored is the most ordinary bug there is.
                //      Driven with no config file on disk, so `migrationAttempted` is false and
                //      `safeToFinish` is true on its own — otherwise the stamp would be withheld
                //      for the missing backup instead, and the assertion would pass without ever
                //      touching the branch it names.
                RavenIron.Undertow.Undertow.Log.Clear();
                var noFile = new ThrowingConfigFile
                {
                    ConfigFilePath = Path.Combine(dir, "there-is-no-such-file.cfg"),
                };
                ConfigMigration.Begin(noFile);
                var stamp = noFile.Bind(ConfigLedger.MetaSection, ConfigLedger.VersionKey, 0);
                var failingPlan = new ConfigLedger.MigrationPlan();
                failingPlan.Retired.Add(ConfigLedger.Slot("1 - Core", "LockedAgainstUs"));
                ConfigMigration.Finish(noFile, stamp, failingPlan);
                Check(stamp.Value == 0,
                    "a plan whose retirement threw leaves the layout version UNSTAMPED, so the next boot migrates again");
                Check(RavenIron.Undertow.Undertow.Log.Said("did not finish cleanly"),
                    "and says so, because an unstamped file that never explains itself is a bug report nobody can read");

                RavenIron.Undertow.Undertow.Log.Clear();
                var okFile = new ConfigFile
                {
                    ConfigFilePath = Path.Combine(dir, "there-is-no-such-file.cfg"),
                };
                ConfigMigration.Begin(okFile);
                var okStamp = okFile.Bind(ConfigLedger.MetaSection, ConfigLedger.VersionKey, 0);
                ConfigMigration.Finish(okFile, okStamp, new ConfigLedger.MigrationPlan());
                Check(okStamp.Value == ConfigLedger.CurrentVersion,
                    "while a plan that applied cleanly stamps as it always did — the gate withholds, it does not block");

                // ---- AND THE LINE THE OWNER READS. LastSummary is written in Begin from the
                //      plan's INTENT, before a step has run, and `wake status` prints it verbatim.
                //      A refused step logs a warning several hundred lines earlier and nothing
                //      else, so the status line says a key was dropped that is still in the file.
                var refusing = new ConfigFile
                {
                    ConfigFilePath = Path.Combine(dir, "there-is-no-such-file.cfg"),
                };
                ConfigMigration.Begin(refusing);
                var refusedPlan = new ConfigLedger.MigrationPlan();
                refusedPlan.ResetToDefault.Add(ConfigLedger.Slot("9 - Nope", "NoSuchKey"));
                ConfigMigration.Finish(refusing, refusing.Bind(ConfigLedger.MetaSection, ConfigLedger.VersionKey, 0), refusedPlan);
                Check(ConfigMigration.LastSummary.IndexOf("REFUSED", StringComparison.Ordinal) >= 0,
                    "a refused step corrects the summary the console prints, rather than leaving it claiming the step happened");

                ConfigMigration.Begin(refusing);
                ConfigMigration.Finish(refusing, refusing.Bind(ConfigLedger.MetaSection, ConfigLedger.VersionKey, 0), new ConfigLedger.MigrationPlan());
                Check(ConfigMigration.LastSummary.IndexOf("REFUSED", StringComparison.Ordinal) < 0,
                    "and the count is per-boot, so a clean migration after a refused one does not inherit its complaint");


                // A ledger that names a key this build does not bind must warn and carry on, not
                // throw: one bad row would otherwise abandon every step after it.
                var ghostPlan = new ConfigLedger.MigrationPlan();
                ghostPlan.ResetToDefault.Add(ConfigLedger.Slot("9 - Nope", "NoSuchKey"));
                ghostPlan.Backfilled.Add(new ConfigLedger.BackfilledSlot { Slot = "also::missing", Value = "1", Because = "test" });
                ghostPlan.Retired.Add("not a slot at all");
                bool threw = false;
                try { ConfigMigration.Apply(live, ghostPlan); } catch { threw = true; }
                Check(!threw, "a ledger row naming a key this build does not bind warns and continues rather than throwing");

                // Read the entry through the ConfigFile Apply was GIVEN. ModConfig's statics were
                // re-pointed at another ConfigFile by the orphan test above, so asserting on them
                // here would inspect something Apply never touched - an assertion that cannot fail.
                var liveSpeed = (ConfigEntry<float>)live[new ConfigDefinition("3 - The current", "MaxCurrentSpeed")];
                Check(Math.Abs(liveSpeed.Value - 1.2f) < 1e-6f, "and leaves every real entry as it was");

                threw = false;
                try { ConfigMigration.Apply(null, resetPlan); ConfigMigration.Apply(live, null); } catch { threw = true; }
                Check(!threw, "Apply survives a null config or a null plan");

                // ---- A MIS-CASED ledger row must resolve to nothing and say so, rather than
                //      quietly finding an entry the game would not. Both the snapshot and the
                //      lookup are ordinal; if either drifts back to ignore-case they disagree, and
                //      a plan can then report a value moved while the config file never changed.
                var misCased = new ConfigLedger.MigrationPlan();
                misCased.ResetToDefault.Add(ConfigLedger.Slot("3 - the current", "maxcurrentspeed"));
                var caseProbe = (ConfigEntry<float>)live[new ConfigDefinition("3 - The current", "MaxCurrentSpeed")];
                caseProbe.Value = 3.5f;
                RavenIron.Undertow.Undertow.Log.Clear();
                ConfigMigration.Apply(live, misCased);
                Check(RavenIron.Undertow.Undertow.Log.Said("binds no such key"),
                    "a mis-cased ledger row is reported as a ledger bug rather than passing silently");
                Check(Math.Abs(caseProbe.Value - 3.5f) < 1e-6f,
                    "a ledger row whose section or key is mis-cased reaches NO entry, exactly as it would in game, "
                    + "rather than resolving case-insensitively in the harness alone");

                // ---- A retirement must never touch a key this build STILL binds. Relying on
                //      Bind's cast to throw only protects the types that differ: BepInEx returns
                //      the EXISTING entry for an already-bound definition, so a string key would
                //      be bound and removed in silence. FlotsamCommon is one of three string keys
                //      and the one an owner is most likely to have curated.
                var liveStrings = new ConfigFile();
                ModConfig.Bind(liveStrings);
                var flotsamDef = new ConfigDefinition("5 - Flotsam", "FlotsamCommon");
                string curated = "Wood,RoundLog,MyFavouriteThing";
                ((ConfigEntry<string>)liveStrings[flotsamDef]).Value = curated;

                var wrongStringRetire = new ConfigLedger.MigrationPlan();
                wrongStringRetire.Retired.Add(ConfigLedger.Slot("5 - Flotsam", "FlotsamCommon"));
                RavenIron.Undertow.Undertow.Log.Clear();
                ConfigMigration.Apply(liveStrings, wrongStringRetire);
                Check(RavenIron.Undertow.Undertow.Log.Said("still binds that key"),
                    "and says why it refused, by name");
                Check(liveStrings.ContainsKey(flotsamDef),
                    "retiring a STRING key this build still binds is refused - BepInEx hands back the live entry, "
                    + "so removing it would delete the owner's value with nothing thrown and nothing logged");
                Check(((ConfigEntry<string>)liveStrings[flotsamDef]).Value == curated,
                    "and the curated value is still there");

                // ---- A backfill that lands CLAMPED is not a backfill that worked. BepInEx's
                //      setter clamps into the entry's range rather than refusing, so the entry
                //      MOVES - and a did-it-move check would call that success.
                var clamped = new ConfigLedger.MigrationPlan();
                clamped.Backfilled.Add(new ConfigLedger.BackfilledSlot
                {
                    Slot = ConfigLedger.Slot("3 - The current", "TideAmplitude"), Value = "9", Because = "test",
                });
                var clampTarget = (ConfigEntry<float>)liveStrings[new ConfigDefinition("3 - The current", "TideAmplitude")];
                RavenIron.Undertow.Undertow.Log.Clear();
                ConfigMigration.Apply(liveStrings, clamped);
                Check(Math.Abs(clampTarget.Value - 1f) < 1e-6f,
                    "an out-of-range backfill is clamped by the config system rather than refused (TideAmplitude tops out at 1)");
                Check(RavenIron.Undertow.Undertow.Log.Said("rather than the '9' it intended"),
                    "and the mod SAYS it stored something other than what the ledger asked for - a clamped value still "
                    + "moves the entry, so the log line is the only way that failure is visible at all");

                // ---- Backup(): every shipped table is empty, so Begin short-circuits past this on
                //      every real boot. It would otherwise first run on the day it matters most.
                string bakSrc = Path.Combine(dir, "backup-me.cfg");
                File.WriteAllText(bakSrc, "[1 - Core]\nTickBudgetMs = 2\n");
                Check(ConfigMigration.Backup(bakSrc, 0), "Backup reports success when it writes a copy");
                Check(File.Exists(bakSrc + ".v0.bak"), "and the copy lands beside the file as .v0.bak");
                Check(ConfigMigration.Backup(bakSrc, 0),
                    "a second run over a byte-identical backup is a success without writing anything new");

                File.WriteAllText(bakSrc, "[1 - Core]\nTickBudgetMs = 4\n");
                Check(ConfigMigration.Backup(bakSrc, 0), "a second run over a DIFFERENT backup still succeeds");
                string[] baks = Directory.GetFiles(dir, "backup-me.cfg.v0*.bak");
                Check(baks.Length == 2,
                    $"and falls back to a timestamped name rather than clobbering someone's only clean copy ({baks.Length} backups)");
                Check(!ConfigMigration.Backup(Path.Combine(dir, "no-such-file.cfg"), 0),
                    "Backup reports FAILURE rather than throwing when there is nothing to copy");
            }
            finally
            {
                // Leave ModConfig pointing at a throwaway file rather than a deleted temp one, so
                // later sections read the shipped defaults.
                ModConfig.Bind(new ConfigFile());
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The wire the server's sea travels on. PURE, so all of it is testable here — and the
        /// two properties worth the most are both invisible in a single-machine test run: that
        /// every key on the wire is a key this build actually binds, and that a comma-decimal
        /// locale cannot change what a float looks like.
        /// </summary>
        private static void ConfigWireTests()
        {
            Section("ConfigWire");

            // ---- the table names real keys ------------------------------------------------
            //
            // This is the assertion the whole feature rests on. A row naming a key ModConfig does
            // not bind is a key that never syncs, in EITHER direction, and nothing about a running
            // game would say so: the server would not send it, the client would not miss it, and
            // the sea would quietly differ. Ordinal and case-sensitive matching (BepInEx's own
            // ConfigDefinition.Equals) puts that failure one wrong letter away.
            var file = new ConfigFile();
            ModConfig.Bind(file);

            var boundAddresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConfigFile.BoundEntry b in file.Bound)
                boundAddresses.Add(ConfigWire.Address(b.Section, b.Key));

            foreach (string address in ConfigWire.SyncedKeys)
                Check(boundAddresses.Contains(address),
                    $"'{address}' is on the wire and ModConfig binds it under exactly that name");

            Check(new HashSet<string>(ConfigWire.SyncedKeys, StringComparer.Ordinal).Count
                  == ConfigWire.SyncedKeys.Length,
                "no key is listed twice on the wire");

            // ---- and deliberately excludes the rest ----------------------------------------
            //
            // A server owner must not be able to reach into a player's frame rate. These are the
            // per-machine keys, pinned by name so adding one to the wire has to be a decision.
            Check(!ConfigWire.IsSynced("1 - Core", "TickBudgetMs"), "the tick budget stays local");
            Check(!ConfigWire.IsSynced("1 - Core", "VerboseLogging"), "verbose logging stays local");
            Check(!ConfigWire.IsSynced("4 - Drift", "FieldRefreshSeconds"), "the refresh cadence stays local");
            Check(!ConfigWire.IsSynced("2 - Systems", "EnableDriftLines"), "the drift lines stay local");
            Check(!ConfigWire.IsSynced("7 - Drift lines", "DriftLineCount"), "the drift line pool stays local");
            Check(!ConfigWire.IsSynced("7 - Drift lines", "DriftLineBudgetMs"), "the drift line budget stays local");
            Check(!ConfigWire.IsSynced("2 - Systems", "EnableFlotsam"), "flotsam is the authority's alone");
            Check(!ConfigWire.IsSynced("5 - Flotsam", "FlotsamMaxAlive"), "the flotsam cap is the authority's alone");
            Check(!ConfigWire.IsSynced("0 - Meta", "ConfigVersion"), "the layout stamp never travels");

            Check(ConfigWire.IsSynced("3 - The current", "MaxCurrentSpeed"), "the sea's ceiling travels");
            Check(ConfigWire.IsSynced("4 - Drift", "UnderWayDragFactor"),
                "what the current costs a hull under way travels — it is the sea's tuning, not the machine's");
            Check(!ConfigWire.IsSynced("3 - The current", "maxcurrentspeed"),
                "IsSynced is CASE-SENSITIVE, as BepInEx's ConfigDefinition.Equals is");
            Check(!ConfigWire.IsSynced("9 - Nowhere", "MaxCurrentSpeed"),
                "the same key name in a different section is a different key");

            // ---- a comma-decimal locale cannot change the wire -----------------------------
            //
            // The instrument here is the point. BepInEx parses floats with
            // NumberFormatInfo.InvariantInfo and SetSerializedValue SWALLOWS what it cannot parse,
            // so a culture leak would be a silent no-op on one server owner's machine only — the
            // hardest possible bug to be told about.
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                Check(ConfigWire.Write(1.25f) == "1.25",
                    "a float writes with a '.' even where the machine's locale uses ','");
                Check(ConfigWire.TryReadFloat("1.25", out float invariantRead) && Math.Abs(invariantRead - 1.25f) < 1e-6f,
                    "a '.' float reads back under a comma-decimal locale");
                Check(!ConfigWire.TryReadFloat("1,25", out _),
                    "a ',' float is REFUSED rather than read as 125");
                Check(ConfigWire.Write(true) == "true" && ConfigWire.Write(false) == "false",
                    "a bool writes lower case, the spelling BepInEx puts in the file");
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }

            // ---- round trip ----------------------------------------------------------------
            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("3 - The current|MaxCurrentSpeed", "1.5"),
                new KeyValuePair<string, string>("2 - Systems|EnableDrift", "false"),
            };
            string payload = ConfigWire.Format(pairs);

            Check(payload.StartsWith(ConfigWire.Header, StringComparison.Ordinal),
                "a payload leads with the wire header");

            List<KeyValuePair<string, string>> read = ConfigWire.Parse(payload);
            Check(read != null && read.Count == 2, "a payload round-trips both values");
            Check(read[0].Key == "3 - The current|MaxCurrentSpeed" && read[0].Value == "1.5",
                "the first value survives the round trip intact");
            Check(read[1].Value == "false", "so does the second");

            Check(ConfigWire.Parse(ConfigWire.Header)?.Count == 0,
                "a header with no values parses to an empty list, not a failure");

            // ---- a wrong SHAPE is refused wholesale ----------------------------------------
            Check(ConfigWire.Parse("undertow-cfg/1\n3 - The current|MaxCurrentSpeed=1.5") == null,
                "a payload from the PREVIOUS wire version (0.8.0's /1) is refused entirely");
            Check(ConfigWire.Parse("undertow-cfg/3\n3 - The current|MaxCurrentSpeed=1.5") == null,
                "a payload from a future wire version is refused entirely");
            Check(ConfigWire.Header == "undertow-cfg/2",
                "the wire is at /2: bumped for the onshore fix, per the rule that a field-maths change bumps it");
            Check(ConfigWire.Parse("3 - The current|MaxCurrentSpeed=1.5") == null,
                "a payload with no header at all is refused entirely");
            Check(ConfigWire.Parse(null) == null, "a null payload is refused rather than thrown on");

            // ---- but a single bad LINE only costs that line ---------------------------------
            //
            // The forward-compatibility rule: a key we do not know means the other end is a
            // different build of this mod, which is ordinary. It must not cost the other values.
            List<KeyValuePair<string, string>> mixed = ConfigWire.Parse(
                ConfigWire.Header +
                "\n3 - The current|MaxCurrentSpeed=1.5" +
                "\nthis line has no equals sign" +
                "\nNoSectionHere=7" +
                "\n|LeadingBarNoSection=7" +
                "\n3 - The current|TrailingEquals=" +
                "\n=novalue" +
                "\n" +
                "\n8 - From the future|SomethingNew=3" +
                "\n2 - Systems|EnableDrift=false");

            Check(mixed != null && mixed.Count == 3,
                "six malformed lines are dropped and the three usable ones survive");
            Check(mixed[1].Key == "8 - From the future|SomethingNew",
                "a key from a NEWER build is parsed, not dropped — filtering it is the caller's job");
            Check(mixed[2].Value == "false", "a good line after the bad ones still arrives");

            // A value is split on the FIRST '=', so a value containing one survives whole.
            List<KeyValuePair<string, string>> eq = ConfigWire.Parse(
                ConfigWire.Header + "\n1 - A|B=x=y");
            Check(eq != null && eq.Count == 1 && eq[0].Value == "x=y",
                "an address is split from its value at the FIRST '=' only");

            // A payload that arrived over a wire that inserted CRs still reads.
            List<KeyValuePair<string, string>> crlf = ConfigWire.Parse(
                ConfigWire.Header + "\r\n3 - The current|MaxCurrentSpeed=1.5\r\n");
            Check(crlf != null && crlf.Count == 1 && crlf[0].Value == "1.5",
                "a payload carrying CRLF line endings still parses");

            // ---- the log line ---------------------------------------------------------------
            Check(ConfigWire.Describe(null) == "nothing" && ConfigWire.Describe(
                      new List<KeyValuePair<string, string>>()) == "nothing",
                "an empty payload describes itself as nothing");
            string described = ConfigWire.Describe(pairs);
            Check(described.Contains("2 value(s)") && described.Contains("MaxCurrentSpeed=1.5")
                  && !described.Contains("3 - The current|"),
                "a description counts the values and names them WITHOUT their sections");

            var many = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < 12; i++)
                many.Add(new KeyValuePair<string, string>("S|K" + i, i.ToString(CultureInfo.InvariantCulture)));
            Check(ConfigWire.Describe(many).Contains("and 9 more"),
                "a long payload names the first three and counts the rest");
        }

        // ---- harness ----------------------------------------------------------------------

        private static void Section(string name) => Console.WriteLine($"-- {name}");

        private static void Check(bool condition, string what)
        {
            if (condition)
            {
                _passed++;
            }
            else
            {
                _failed++;
                Console.WriteLine($"   FAIL  {what}");
            }
        }

        private static string Fmt(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
