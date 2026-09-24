using System;
using System.Globalization;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using RavenIron.Undertow.Config;
using RavenIron.Undertow.Core;
using RavenIron.Undertow.Net;

namespace RavenIron.Undertow.Patches
{
    /// <summary>
    /// The whole write surface of this mod: one postfix that adds force to a hull.
    ///
    /// WHY A POSTFIX AND NOT A PREFIX. House rule 1 — behaviour goes in prefixes, but this is
    /// not a decision about whether vanilla runs; it is an addition to what vanilla already did
    /// this tick. Running after the original means the buoyancy, damping, sail and rudder forces
    /// have all been applied and the hull's velocity is settled, so the drag model below is
    /// computed against the velocity the boat actually has rather than the one it had last tick.
    ///
    /// PRIVATE FIELDS COME FROM HARMONY, NOT FROM THE PUBLICIZED ASSEMBLY. `m_nview`, `m_body`
    /// and `m_players` are all PRIVATE in the shipping assembly and public only in the
    /// publicized reference — naming any of them here would compile clean and throw
    /// FieldAccessException in-game, fifty times a second, the same way `Terminal.commands` did
    /// on 2026-08-28. The `___name` parameters below make Harmony generate the accessors
    /// instead, so this method never contains a field reference to check.
    ///
    /// NEVER ASSIGNS linearVelocity, never touches the ZDO. Force on a live rigidbody, on the
    /// machine that owns it. Vanilla assigns `m_body.linearVelocity` wholesale inside the method
    /// this runs after, so an assignment here would either be pointless or would eat vanilla's
    /// damping; and a ZDO position is a suggestion the owner overwrites next frame.
    /// </summary>
    [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
    public static class Patch_Ship_Drift
    {
        // ---- diagnostics the `wake drift` console reads -----------------------------------
        public static long PushCount;
        public static float LastWaterSpeed;
        public static float LastAppliedDv;
        /// <summary>
        /// The hull last pushed, for `wake drift`. The REFERENCE is kept and the name read only
        /// when the console asks: `name` allocates a new managed string on every call, and this
        /// is set on every physics tick a push lands. Unity's null check covers a destroyed hull.
        /// </summary>
        private static Ship _lastShip;
        public static string LastShip => _lastShip != null ? _lastShip.name : "(none)";
        public static bool EverRan;

        /// <summary>Seconds between verbose drift lines. Two is enough to watch a hull settle.</summary>
        private const float LogIntervalSeconds = 2f;
        private static float _nextLogTime;

        /// <summary>
        /// Throttle for the catch-all below. This postfix runs in the physics step of EVERY
        /// hull in the world, so an exception that keeps happening - the realistic case after
        /// a game update moves an API - would write about fifty warnings a second per boat and
        /// bury the one instrument this mod has under its own noise. Say it once in full, then
        /// once per interval, and carry the suppressed count so the log still reports the scale.
        /// </summary>
        private const float ErrorIntervalSeconds = 30f;
        private static float _nextErrorLogTime;
        private static long _suppressedErrors;

        private sealed class Cached
        {
            public float NextEvalTime;
            public FieldSample Sample;
            public bool Valid;

            // Vanilla's water-level lookups take a per-point cache of the last WaterVolume hit;
            // one per sample point, per hull, the way Ship keeps m_previousCenter and friends.
            public WaterVolume WvCentre, WvLeft, WvRight, WvFore, WvAft;
        }

        /// <summary>Which regime the last push used — read by `wake drift`.</summary>
        public static string LastMode = "(none)";
        public static float LastSubmergence;

        /// <summary>
        /// Per-hull cache of the field, refreshed on a timer rather than every physics tick.
        ///
        /// One evaluation costs nine WorldGenerator.GetHeight calls; at fifty ticks a second per
        /// boat that is 450 a second per hull, for a field whose shortest wavelength is 1800m.
        /// A boat at full sail moves under two metres between refreshes at the default interval,
        /// which is nothing at that scale — so this is cheap and safe rather than a trade.
        ///
        /// A weak table so a destroyed or unloaded ship takes its entry with it; a plain
        /// Dictionary keyed on Ship would hold every hull the session ever saw.
        /// </summary>
        private static readonly ConditionalWeakTable<Ship, Cached> _cache =
            new ConditionalWeakTable<Ship, Cached>();

        private static void Postfix(
            Ship __instance,
            float fixedDeltaTime,
            ZNetView ___m_nview,
            Rigidbody ___m_body,
            List<Player> ___m_players,
            Ship.Speed ___m_speed)
        {
            try
            {
                if (!ModConfig.EnableDrift.Live()) return;
                if (__instance == null || ___m_body == null) return;

                // THE OWNER CHECK, AND IT IS THE WHOLE REASON THIS BLOCK EXISTS.
                //
                // Vanilla's own guard sits INSIDE CustomFixedUpdate and protects only the lines
                // below it — a postfix runs on every peer regardless. Without re-checking here,
                // every client in the world would push the same hull simultaneously and the boat
                // would move at a speed proportional to how many people were online.
                //
                // Stricter than vanilla on purpose: vanilla proceeds when m_nview is null, this
                // does not. An object whose ownership cannot be established is one we decline to
                // touch rather than one we assume is ours.
                if (___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner()) return;

                Vector3 position = __instance.transform.position;

                // CREW CHECK FIRST, and that ordering is the whole point rather than tidiness.
                // Evaluating the field costs NINE WorldGenerator.GetHeight calls (counted
                // 2026-09-19), and UnattendedDriftFactor ships at ZERO — so with the check below
                // the sample, every moored hull paid 36 GetHeight a second to compute a current
                // that was then discarded one line later. A dedicated server owns exactly the
                // boats no player is near, i.e. the moored ones, so that was the single largest
                // thing this mod asked of a busy server and all of it was waste.
                bool crewed = ___m_players != null && ___m_players.Count > 0;
                float crewFactor = crewed ? 1f : ModConfig.UnattendedDriftFactor.Live();
                if (crewFactor <= 0f) return;

                // Cheap and position-only, so it also belongs above the sample: a hull outside
                // the fade gets no push whatever the water is doing.
                float edgeFadeEarly = DriftForce.EdgeFade(
                    Mathf.Sqrt(position.x * position.x + position.z * position.z));
                if (edgeFadeEarly <= 0f) return;

                if (!TryGetSample(__instance, position, out FieldSample sample)) return;
                if (sample.Speed <= 0f) return;

                float edgeFade = edgeFadeEarly;

                // Only the hull's speed ALONG the current is consulted, and only through a term
                // clamped to [0,1] inside DriftForce — so this can fade the push out but can
                // never reverse it into a brake.
                Vector3 hullVelocity = ___m_body.linearVelocity;
                float waterSpeed = sample.Speed;
                float hullAlongCurrent = waterSpeed > 1e-5f
                    ? (hullVelocity.x * sample.X + hullVelocity.z * sample.Z) / waterSpeed
                    : 0f;

                // TWO REGIMES, decided by the one thing that says whether anything is propelling
                // the hull: vanilla's own speed setting. Stop → adrift → the saturating push that
                // carries a free hull to the water's speed (measured, kept). Anything else →
                // under way → drag relative to the water, which costs the water's own speed over
                // the ground and never more. The reason for the split, with the numbers, is on
                // DriftForce.ComputeUnderWay; the short form is that 1.0.0's push stopped a paddled
                // karve dead in 0.2 m/s of water.
                //
                // `___m_speed` is Harmony's accessor for the private field — vanilla forces it to
                // Stop for an empty hull before this runs, so an unattended hull is always adrift.
                bool underWay = crewed && ___m_speed != Ship.Speed.Stop;
                float dvx, dvz;
                if (underWay)
                {
                    if (!TryUnderWay(__instance, ___m_body, sample,
                                     ModConfig.UnderWayDragFactor.Live(), crewFactor, edgeFade,
                                     out dvx, out dvz))
                        return;
                    LastMode = "underway";
                }
                else
                {
                    DriftForce.Compute(
                        sample.X, sample.Z,
                        hullAlongCurrent,
                        ModConfig.DriftStrength.Live(), fixedDeltaTime,
                        crewFactor, edgeFade,
                        out dvx, out dvz);
                    LastMode = "adrift";
                }

                if (dvx == 0f && dvz == 0f) return;

                // Vanilla's convention from this very method: a velocity change expressed as
                // mass * dv, delivered as an impulse. Horizontal only — buoyancy owns y, and
                // this must never argue with it.
                ___m_body.AddForce(
                    new Vector3(dvx, 0f, dvz) * ___m_body.mass,
                    ForceMode.Impulse);

                EverRan = true;
                PushCount++;
                LastWaterSpeed = sample.Speed;
                LastAppliedDv = Mathf.Sqrt(dvx * dvx + dvz * dvz);
                _lastShip = __instance;

                // THE TUNING INSTRUMENT. The single number that matters for calibration is the
                // RATIO of hull speed to water speed — at DriftStrength 1.0 it should sit near
                // 1.0 along the hull's axis and near 0.45 across it. Everything else about this
                // mod can be reasoned about off-game; this cannot, because it depends on
                // vanilla's damping behaving the way the arithmetic says it does.
                //
                // Written to the log rather than held for the console because a drift
                // measurement is a TIME SERIES — one console snapshot cannot show a hull
                // accelerating toward its terminal speed, and the terminal speed is the whole
                // question. Throttled hard: this runs fifty times a second per boat.
                if (ModConfig.VerboseLogging.Value && Time.realtimeSinceStartup >= _nextLogTime)
                {
                    _nextLogTime = Time.realtimeSinceStartup + LogIntervalSeconds;

                    float hullSpeed = Mathf.Sqrt(hullVelocity.x * hullVelocity.x +
                                                 hullVelocity.z * hullVelocity.z);

                    // ALONG is the number that matters and TOTAL is the one that misleads.
                    //
                    // Measured 2026-08-28: a hull reading 1.33x the water's speed in TOTAL was
                    // sitting at 0.97x ALONG the current — dead on target. The difference was
                    // motion ACROSS the current, almost all of it wave-driven surge, which the
                    // mod neither causes nor controls. Reporting total speed made a converged
                    // model look like a runaway, and cost a round-trip to work out. The
                    // saturation term acts on the along-component, so that is what a tuning
                    // readout has to show; total is kept beside it, clearly labelled, because
                    // the gap between the two IS the wave contribution.
                    float alongRatio = waterSpeed > 0.001f ? hullAlongCurrent / waterSpeed : 0f;
                    float totalRatio = waterSpeed > 0.001f ? hullSpeed / waterSpeed : 0f;

                    Undertow.Log.LogInfo(
                        $"drift {__instance.name} @ ({position.x:0},{position.z:0}) " +
                        $"depth {sample.Depth:0.#}m {sample.Dominant} | " +
                        $"water {waterSpeed:0.###} along {hullAlongCurrent:0.###} " +
                        $"ALONG-RATIO {alongRatio:0.##} (total {totalRatio:0.##}) | " +
                        $"dv {LastAppliedDv:0.#####} crew {crewFactor:0.##} " +
                        $"mode {LastMode} sub {LastSubmergence:0.##}");
                }
            }
            catch (Exception ex)
            {
                // A throw here lands in the physics step of every boat in the world, so it is
                // reported on a timer rather than per occurrence. Vanilla carries on unharmed
                // either way: this catch exists so a fault in our code never reaches the game.
                ReportError("drift postfix", ex);
            }
        }

        /// <summary>
        /// The under-way regime: rebuild vanilla's own submergence factor from the same five
        /// water samples it takes, then hand the hull's and the water's velocities in the hull's
        /// axes to the pure correction.
        ///
        /// Every member named here is PUBLIC in the shipping assembly — `m_floatCollider`,
        /// `m_waterLevelOffset`, `m_disableLevel`, `m_forceDistance`, `m_dampingForward`,
        /// `m_dampingSideway`, and `Floating.GetWaterLevel(Vector3, ref WaterVolume)` — read out
        /// of the real `assembly_valheim.dll` on 2026-09-21 (rule 5). The body comes injected.
        /// The five points are vanilla's: centre of mass, and the float collider's four edges.
        /// </summary>
        private static bool TryUnderWay(
            Ship ship, Rigidbody body, FieldSample sample,
            float strength, float crewFactor, float edgeFade,
            out float dvx, out float dvz)
        {
            dvx = 0f;
            dvz = 0f;

            BoxCollider fc = ship.m_floatCollider;
            if (fc == null) return false;

            Cached entry = _cache.GetValue(ship, _ => new Cached());

            Transform ft = fc.transform;
            Vector3 size = fc.size;
            Vector3 fp = ft.position;
            Vector3 ff = ft.forward;
            Vector3 fr = ft.right;
            Vector3 centre = body.worldCenterOfMass;

            float level =
                Floating.GetWaterLevel(centre, ref entry.WvCentre) +
                Floating.GetWaterLevel(fp - fr * (size.x * 0.5f), ref entry.WvLeft) +
                Floating.GetWaterLevel(fp + fr * (size.x * 0.5f), ref entry.WvRight) +
                Floating.GetWaterLevel(fp + ff * (size.z * 0.5f), ref entry.WvFore) +
                Floating.GetWaterLevel(fp - ff * (size.z * 0.5f), ref entry.WvAft);
            level *= 0.2f;

            float floatError = centre.y - level - ship.m_waterLevelOffset;
            // Out of the water: vanilla applies no hull physics on this tick, so neither do we.
            if (floatError > ship.m_disableLevel) return false;

            float forceDistance = ship.m_forceDistance > 0.01f ? ship.m_forceDistance : 0.01f;
            float submergence = Mathf.Clamp01(Mathf.Abs(floatError) / forceDistance);
            LastSubmergence = submergence;

            Vector3 fwd = ship.transform.forward;
            Vector3 right = ship.transform.right;
            Vector3 v = body.linearVelocity;
            Vector3 w = new Vector3(sample.X, 0f, sample.Z);

            DriftForce.ComputeUnderWay(
                Vector3.Dot(w, fwd), Vector3.Dot(w, right),
                Vector3.Dot(v, fwd), Vector3.Dot(v, right),
                ship.m_dampingForward, ship.m_dampingSideway,
                submergence,
                strength, crewFactor, edgeFade,
                out float dvForward, out float dvRight);

            Vector3 dv = fwd * dvForward + right * dvRight;
            dvx = dv.x;
            dvz = dv.z;
            return true;
        }

        private static bool TryGetSample(Ship ship, Vector3 position, out FieldSample sample)
        {
            Cached entry = _cache.GetValue(ship, _ => new Cached());

            float now = Time.realtimeSinceStartup;
            if (entry.Valid && now < entry.NextEvalTime)
            {
                sample = entry.Sample;
                return true;
            }

            if (!SeaContext.TryEvaluate(position.x, position.z, out sample))
            {
                entry.Valid = false;
                return false;
            }

            entry.Sample = sample;
            entry.Valid = true;
            entry.NextEvalTime = now + Mathf.Max(0f, ModConfig.FieldRefreshSeconds.Value);
            return true;
        }

        /// <summary>
        /// Report a fault from the hot path without flooding the log. The FIRST one is always
        /// written in full, because a fault nobody is told about is the same as no instrument at
        /// all; after that it is one line per <see cref="ErrorIntervalSeconds"/>, carrying the
        /// number suppressed in between so the reader can tell "once, oddly" from "constantly".
        /// </summary>
        private static void ReportError(string where, Exception ex)
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextErrorLogTime)
            {
                _suppressedErrors++;
                return;
            }

            string tail = _suppressedErrors > 0
                ? " (and " + _suppressedErrors.ToString(CultureInfo.InvariantCulture) +
                  " more like it since the last line)"
                : "";
            _suppressedErrors = 0;
            _nextErrorLogTime = now + ErrorIntervalSeconds;

            Undertow.Log.LogWarning(where + ": " + ex.Message + tail);
        }

    }
}
