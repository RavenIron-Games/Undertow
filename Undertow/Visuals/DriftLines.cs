using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using RavenIron.Undertow.Config;
using RavenIron.Undertow.Core;
using RavenIron.Undertow.Net;

namespace RavenIron.Undertow.Visuals
{
    /// <summary>
    /// The current, visible: drift lines. Faint foam streaks lying flat on the water along the
    /// flow, moving at the water's own speed, riding vanilla's wave surface, thick in a race,
    /// sparse at a trickle, and down to a few faint flecks where the sea goes slack. (Through 0.7
    /// slack water was BARE; 0.8 gave it a floor, because a bare sea was also what a player saw
    /// when the feature was broken or mis-configured — see DriftLineMath.SpawnWeight.) A drifting hull sees
    /// them hold station alongside; a hull under sail sees them stream past at the crab angle.
    /// That angle is the set, and nothing says so.
    ///
    /// NOT A HUD. Nothing here is drawn in screen space: no canvas, no arrow, no number, no
    /// sprite that faces the camera. The quads lie in world XZ at the sampled wave height, are
    /// occluded by hulls and headlands, fogged by distance, dimmed by night, end-to-end
    /// symmetric (a still frame gives the line and never the sense), and exist only where and
    /// to the degree the water actually runs. The locked "no navigation instruments" row is
    /// untouched; what changed is one sentence of the premise — "never on the screen" became
    /// "never as an instrument" — by the owner's call on 2026-09-18.
    ///
    /// PROCEDURAL, NO ASSETS, CLIENT-ONLY. The ParticleSystem, its texture and its material
    /// are built in code (the Ragnarok's Wrath pattern); there is no prefab, no ZNetView, no
    /// ZDO, nothing networked and nothing saved. The component is added only where something
    /// renders and never touches gameplay: it adds NO Harmony patch, owns only its own arrays,
    /// and writes only its own particles' positions (house rules 3 and 6). Two players on one
    /// deck see the same set, density and speed from the same deterministic field, but not
    /// the same individual foam.
    ///
    /// WHO OWNS THE PARTICLES: this class, entirely. The ParticleSystem is a pure renderer —
    /// emission and shape are off, every particle is pushed through SetParticles each frame
    /// with zero velocity and an effectively infinite lifetime, and the simulation Unity runs
    /// on them changes nothing we do not overwrite. It does not hang off SeaTick, which is the
    /// authority's cursor and returns before ticking on a pure client.
    ///
    /// RULE 3 THROUGHOUT: one try/catch around Build and one around the frame; the first throw
    /// latches the visual off for the session, logs once, and `wake lines reset` is the
    /// deliberate way back. Missing camera, player, world or water is IDLE, not an error.
    /// </summary>
    public class DriftLines : MonoBehaviour
    {
        public static DriftLines Instance { get; private set; }

        /// <summary>
        /// The horizontal-billboard handedness is a MEASUREMENT, not a derivation — see
        /// <see cref="DriftLineMath.QuadRotationDegrees"/>. Flip this once if the two-station
        /// in-game check (BACKLOG task 7) shows every streak mirrored, and record it in
        /// CLAUDE.md's Known traps.
        /// </summary>
        private const bool FlipRotation = false;

        public const int MaxPool = 512;

        private const float FieldCellMetres      = 16f;
        private const float FieldMemoSeconds     = 3f;
        private const int   MaxAttemptsPerFrame  = 4;
        private const float MaxSpawnDebt         = 8f;
        private const float MeanLifeSeconds      = 10f;
        private const float BaseAlpha            = 0.26f;
        private const float MaxAlpha             = 0.6f;
        // THE LIFT, and why it is no longer two constants.
        //
        // A streak is drawn at the water surface plus a small offset, because the material sits at
        // render queue 3100 with ZWrite off — it draws after the water and depth-tests LEqual, so
        // a perfectly coplanar quad flickers on float precision. The offset buys that clearance
        // and nothing else.
        //
        // 0.7 used 0.06 + 0.05 x chop, and BOTH halves were wrong once anyone looked at the water
        // from close up (owner, 2026-09-19: "foam floats above the water", reported as a uniform
        // hover rather than the ends of a plank poking through a wave). Six centimetres is roughly
        // an order of magnitude more clearance than precision needs at these distances, and
        // scaling it UP with chop is backwards: a rough sea is exactly when the quad already
        // stands proud, and that is when the old formula lifted it furthest. The chop term is
        // gone rather than inverted, because nothing it was protecting against was ever observed.
        //
        // Configurable, because this is a number that can only be judged by eye on real water and
        // against a particular GPU's depth precision: too high and the foam hovers, too low and it
        // flickers or vanishes into the surface. See ModConfig.DriftLineLiftMetres.
        private const float DefaultLiftMetres    = 0.02f;
        private const float ClusterJitterMetres  = 2f;
        private const float BearingJitterDegrees = 16f;
        private const float SeaStateTau          = 2f;
        private const float IdleProbeSeconds     = 1f;
        private const float UnderwaterMargin     = 0.2f;
        private const int   OverBudgetFrames     = 120;
        private const int   WarmupFrames         = 60;
        private const float VerboseSeconds       = 10f;
        private const float WindowSeconds        = 10f;

        private enum Slot : byte { Dormant, Active }

        // ---- the emitter --------------------------------------------------------------------
        private ParticleSystem _system;
        private ParticleSystemRenderer _renderer;
        private ParticleSystem.Particle[] _buf;
        private Material _streakMaterial;
        private bool _built;
        private int _builtAtFrame = -1;
        private string _shaderName = "-";
        private bool _shaderHasFog;

        // ---- the latch ----------------------------------------------------------------------
        private bool _latched;
        private string _lastError = "none";

        // ---- the pool (structure of arrays; slots are fixed, side data never moves) ----------
        private int _configuredPool;
        private int _effectivePool;
        private Slot[] _state;
        private float[] _x, _z, _y, _yVel, _vx, _vz, _age, _life, _length, _width, _rotation,
                        _alphaMul, _retry, _lastRide, _speed;
        private WaterVolume[] _vol;
        private Bounds[] _volBounds;

        // ---- simulation state ---------------------------------------------------------------
        private readonly WaterSurfaceCache _cache = new WaterSurfaceCache();
        private WaterVolume _camVol;
        private Bounds _camBounds;
        private uint _rng;
        private float _spawnDebt;
        private float _seaState;
        private int _drawn;
        private bool _idle;
        private string _idleWord = "IDLE: no water nearby";
        private float _nextIdleProbe;
        private string _stateWord = "not started";
        private bool _firstAfloatLogged;
        private Vector3 _centre;
        private float _camAbove = float.NaN;

        // ---- per-frame constants, kept so the console can print what the frame saw ----------
        private float _ambientLum, _fogLum, _day, _tintR, _tintG, _tintB, _alphaScale;
        private Color _fogColor;
        private int _fogMode;
        private float _fogDensity, _fogStart, _fogEnd;

        // ---- the field memo: 16 m cells for 3 s ----------------------------------------------
        private struct Memo
        {
            public long Key;
            public float Expires;
            public FieldSample Sample;
            public bool Ok;
        }
        private readonly Memo[] _memo = new Memo[256];

        // ---- instrumentation ----------------------------------------------------------------
        private readonly Stopwatch _sw = new Stopwatch();
        private float _costEma, _lastCost;
        private int _overBudget;
        private int _surfaceReads, _fieldEvals;
        private float _nextVerbose;
        private float _windowStart;
        private int _wTried, _wSpawned, _wRejSlack, _wRejShallow, _wRejWeak, _wRejNoVolume;
        private int _lTried, _lSpawned, _lRejSlack, _lRejShallow, _lRejWeak, _lRejNoVolume;
        private long _retiredLife, _retiredReflect, _retiredNoVolume, _totalSpawned;

        // =====================================================================================

        private void Awake()
        {
            Instance = this;
            _rng = (uint)Environment.TickCount | 1u;
            Undertow.Log.LogInfo(
                $"DriftLines: armed on this client (graphics device {SystemInfo.graphicsDeviceType}).");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DestroyEmitter();
        }

        private void Update()
        {
            if (_latched) return;
            if (Undertow.IsDedicated()) { _stateWord = "DORMANT: dedicated"; return; }

            if (!ModConfig.EnableDriftLines.Value) { Sleep("OFF in config"); return; }

            int pool = Mathf.Clamp(ModConfig.DriftLineCount.Value, 0, MaxPool);
            if (pool == 0) { Sleep("OFF: count 0"); return; }

            Player player = Player.m_localPlayer;
            if (player == null) { Sleep("IDLE: no local player"); return; }

            try
            {
                _sw.Restart();
                _surfaceReads = 0;
                _fieldEvals = 0;

                EnsurePool(pool);
                if (!_built) Build();
                if (_latched) return;

                float now = Time.time;
                float dt = DriftLineMath.ClampDt(Time.deltaTime);

                Camera cam = Camera.main;
                _centre = cam != null ? cam.transform.position : player.transform.position + Vector3.up * 2f;
                float radius = Mathf.Clamp(ModConfig.DriftLineRadius.Value, 20f, 120f);

                if (now >= _nextIdleProbe)
                {
                    _nextIdleProbe = now + IdleProbeSeconds;
                    _idle = NoWaterNear(_centre, radius, now);
                    // Built once per probe, not once per frame: an idle client allocates nothing.
                    if (_idle) _idleWord = $"IDLE: no water within {radius.ToString("0", CultureInfo.InvariantCulture)}m";
                }
                if (_idle) { Sleep(_idleWord); return; }

                _cache.Refresh(now, false);

                float level = SeaContext.WaterLevel;
                if (_cache.TryGetSurface(_centre.x, _centre.z, level, ref _camVol, ref _camBounds, out float camSurface))
                {
                    _surfaceReads++;
                    _camAbove = _centre.y - camSurface;
                }
                else
                {
                    _camAbove = float.NaN;
                }
                bool underwater = !float.IsNaN(_camAbove) && _camAbove < -UnderwaterMargin;
                _renderer.enabled = !underwater;
                _stateWord = underwater ? "IDLE: camera under water" : "LIVE";

                ReadScene();
                float chop = DriftLineMath.Chop(_seaState);
                float chopFade = DriftLineMath.ChopFade(_seaState);
                float lift = Mathf.Clamp(ModConfig.DriftLineLiftMetres.Value, 0f, 0.25f);
                float chopBoost = Mathf.Min(1.25f, 1f + 0.25f * chop);
                float opacity = Mathf.Clamp(ModConfig.DriftLineOpacity.Value, 0f, 2f);
                float maxSpeed = ModConfig.MaxCurrentSpeed.Live();
                float minDepth = Mathf.Clamp(ModConfig.DriftLineMinDepth.Value, 2f, 30f);
                float slackFloor = Mathf.Clamp01(ModConfig.DriftLineSlackFloor.Value);

                _spawnDebt = Mathf.Min(MaxSpawnDebt, _spawnDebt + dt * _effectivePool / MeanLifeSeconds);
                int attempts = Mathf.Min(MaxAttemptsPerFrame, (int)_spawnDebt);

                float frameMaxDev = -1f;
                int frame = Time.frameCount;
                int n = 0;

                for (int i = 0; i < _effectivePool; i++)
                {
                    if (_state[i] == Slot.Dormant)
                    {
                        _retry[i] -= dt;
                        if (_retry[i] > 0f || attempts <= 0) continue;
                        attempts--;
                        _spawnDebt -= 1f;
                        TrySpawnCluster(i, radius, level, now, chop, maxSpeed, minDepth, slackFloor);
                        continue;
                    }

                    _age[i] += dt;
                    if (_age[i] >= _life[i])
                    {
                        _retiredLife++;
                        Retire(i, 0f);
                        continue;
                    }

                    _x[i] += _vx[i] * dt;
                    _z[i] += _vz[i] * dt;

                    float dx = _x[i] - _centre.x;
                    float dz = _z[i] - _centre.z;
                    bool forceRide = false;
                    if (DriftLineMath.Reflect(ref dx, ref dz, radius))
                    {
                        _retiredReflect++;
                        _x[i] = _centre.x + dx;
                        _z[i] = _centre.z + dz;
                        if (!Reroll(i, now, chop, maxSpeed, minDepth, slackFloor)) continue;
                        forceRide = true;
                    }

                    bool ride = forceRide || ((i + frame) & 1) == 0;
                    if (ride)
                    {
                        if (!_cache.TryGetSurface(_x[i], _z[i], level, ref _vol[i], ref _volBounds[i], out float surface))
                        {
                            _retiredNoVolume++;
                            Retire(i, 1f);
                            continue;
                        }
                        _surfaceReads++;
                        _yVel[i] = DriftLineMath.VerticalVelocity(_y[i], surface, now - _lastRide[i]);
                        _y[i] = surface;
                        _lastRide[i] = now;

                        float dev = Mathf.Abs(surface - level);
                        if (dev > frameMaxDev) frameMaxDev = dev;

                        if (!_firstAfloatLogged) LogFirstAfloat(i, surface, level);
                    }
                    else
                    {
                        _y[i] += _yVel[i] * dt;
                    }

                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    float alpha = BaseAlpha * opacity * _alphaMul[i]
                                  * DriftLineMath.LifeEnvelope(_age[i] / _life[i])
                                  * DriftLineMath.EdgeFade(d, radius)
                                  * _alphaScale * chopBoost * chopFade;
                    if (alpha > MaxAlpha) alpha = MaxAlpha;
                    if (alpha <= 0f) continue;

                    float r = _tintR, g = _tintG, b = _tintB;
                    if (!_shaderHasFog)
                    {
                        float vis = DriftLineMath.FogVisibility(d, _fogDensity, _fogMode, _fogStart, _fogEnd);
                        r = Mathf.Lerp(_fogColor.r, r, vis);
                        g = Mathf.Lerp(_fogColor.g, g, vis);
                        b = Mathf.Lerp(_fogColor.b, b, vis);
                    }

                    ParticleSystem.Particle p = default(ParticleSystem.Particle);
                    p.position = new Vector3(_x[i], _y[i] + lift, _z[i]);
                    p.velocity = Vector3.zero;
                    p.startSize3D = new Vector3(_length[i], _width[i], 1f);
                    p.rotation = _rotation[i];
                    p.startColor = new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(alpha));
                    p.remainingLifetime = 1e6f;
                    p.startLifetime = 1e6f;
                    p.angularVelocity = 0f;
                    _buf[n++] = p;
                }

                _system.SetParticles(_buf, n);
                _drawn = n;

                if (frameMaxDev >= 0f)
                    _seaState = DriftLineMath.SeaState(_seaState, frameMaxDev, dt, SeaStateTau);

                RollWindow(now);
                MeasureCost(now);
            }
            catch (Exception ex)
            {
                Latch("disabled after error", ex);
            }
        }

        // ---- spawning -----------------------------------------------------------------------

        private void TrySpawnCluster(int slot, float radius, float level, float now,
                                     float chop, float maxSpeed, float minDepth, float slackFloor)
        {
            _wTried++;
            DriftLineMath.DiscPoint(Roll(), Roll(), radius, out float dx, out float dz);
            float px = _centre.x + dx;
            float pz = _centre.z + dz;

            if (!FieldAt(px, pz, now, out FieldSample sample))
            {
                _retry[slot] = 1f;                       // no world yet: not an error, try later
                return;
            }

            float weight = DriftLineMath.SpawnWeight(sample.Speed, sample.Depth, maxSpeed, minDepth, slackFloor);
            if (weight <= 0f || Roll() >= weight)
            {
                if (sample.Speed <= CurrentField.SlackSpeed) _wRejSlack++;
                else if (sample.Depth <= minDepth) _wRejShallow++;
                else _wRejWeak++;
                _retry[slot] = 0.5f + Roll();
                return;
            }

            WaterVolume vol = null;
            Bounds bounds = default(Bounds);
            if (!_cache.TryGetSurface(px, pz, level, ref vol, ref bounds, out float surface))
            {
                _wRejNoVolume++;
                _retry[slot] = 1f;
                return;
            }
            _surfaceReads++;

            int count = DriftLineMath.ClusterSize(Roll());
            Fill(slot, px, pz, sample, vol, bounds, surface, now, chop, maxSpeed);

            int next = slot;
            for (int k = 1; k < count; k++)
            {
                next = NextDormant(next);
                if (next < 0) break;
                float jx = px + (Roll() - 0.5f) * 2f * ClusterJitterMetres;
                float jz = pz + (Roll() - 0.5f) * 2f * ClusterJitterMetres;
                // Each member rides its own point: two metres of wave slope is a visible step.
                WaterVolume jVol = vol;
                Bounds jBounds = bounds;
                if (!_cache.TryGetSurface(jx, jz, level, ref jVol, ref jBounds, out float jSurface)) continue;
                _surfaceReads++;
                Fill(next, jx, jz, sample, jVol, jBounds, jSurface, now, chop, maxSpeed);
            }
        }

        private void Fill(int i, float px, float pz, FieldSample sample, WaterVolume vol, Bounds bounds,
                          float surface, float now, float chop, float maxSpeed)
        {
            _state[i] = Slot.Active;
            _x[i] = px;
            _z[i] = pz;
            _y[i] = surface;
            _yVel[i] = 0f;
            _lastRide[i] = now;
            _vx[i] = sample.X;
            _vz[i] = sample.Z;
            _speed[i] = sample.Speed;
            _life[i] = DriftLineMath.LifetimeSeconds(Roll());
            // Born at age 0, always. The spawn-debt limiter already spreads the first fill over
            // ~10 s and lives are randomised 6–14 s, so nothing pulses — and a streak born
            // mid-envelope pops in at full alpha, the one artifact LifeEnvelope exists to
            // prevent (review finding, 2026-09-18).
            _age[i] = 0f;
            DriftLineMath.StreakSize(sample.Speed, maxSpeed, chop, Roll(), Roll(), out _length[i], out _width[i]);
            float bearing = DriftLineMath.BearingDegrees(sample.X, sample.Z) + (Roll() - 0.5f) * BearingJitterDegrees;
            _rotation[i] = DriftLineMath.QuadRotationDegrees(bearing, FlipRotation);
            _alphaMul[i] = 0.7f + 0.3f * Roll();
            _vol[i] = vol;
            _volBounds[i] = bounds;
            _wSpawned++;
            _totalSpawned++;
        }

        /// <summary>A reflected streak is re-rolled where it landed; false means it went dormant.</summary>
        private bool Reroll(int i, float now, float chop, float maxSpeed, float minDepth, float slackFloor)
        {
            if (!FieldAt(_x[i], _z[i], now, out FieldSample sample))
            {
                Retire(i, 1f);
                return false;
            }
            float weight = DriftLineMath.SpawnWeight(sample.Speed, sample.Depth, maxSpeed, minDepth, slackFloor);
            if (weight <= 0f || Roll() >= weight)
            {
                Retire(i, 0.5f + Roll());
                return false;
            }
            _vx[i] = sample.X;
            _vz[i] = sample.Z;
            _speed[i] = sample.Speed;
            _life[i] = DriftLineMath.LifetimeSeconds(Roll());
            _age[i] = 0f;
            DriftLineMath.StreakSize(sample.Speed, maxSpeed, chop, Roll(), Roll(), out _length[i], out _width[i]);
            float bearing = DriftLineMath.BearingDegrees(sample.X, sample.Z) + (Roll() - 0.5f) * BearingJitterDegrees;
            _rotation[i] = DriftLineMath.QuadRotationDegrees(bearing, FlipRotation);
            _vol[i] = null;                              // the ride re-resolves the volume
            return true;
        }

        private void Retire(int i, float retry)
        {
            _state[i] = Slot.Dormant;
            _retry[i] = retry;
            _vol[i] = null;
        }

        private int NextDormant(int after)
        {
            for (int k = 1; k < _effectivePool; k++)
            {
                int j = (after + k) % _effectivePool;
                if (_state[j] == Slot.Dormant) return j;
            }
            return -1;
        }

        private float Roll() => DriftLineMath.NextRoll(ref _rng);

        // ---- the field, memoised ------------------------------------------------------------

        /// <summary>
        /// The field at a point, through a 16 m / 3 s memo. A tide reversal reaches new streaks
        /// within three seconds; a slack region costs one evaluation per cell per three seconds
        /// rather than one per rejected attempt. Open addressing over a fixed struct array —
        /// no allocation, ever.
        /// </summary>
        private bool FieldAt(float x, float z, float now, out FieldSample sample)
        {
            long key = DriftLineMath.CellKey(x, z, FieldCellMetres);
            int home = (int)((((uint)key ^ (uint)(key >> 32)) * 2654435761u) >> 24);

            int free = -1;
            for (int k = 0; k < 4; k++)
            {
                int idx = (home + k) & 255;
                ref Memo m = ref _memo[idx];
                if (m.Expires > now && m.Key == key)
                {
                    sample = m.Sample;
                    return m.Ok;
                }
                if (free < 0 && m.Expires <= now) free = idx;
            }

            bool ok = SeaContext.TryEvaluate(x, z, out sample);
            _fieldEvals++;

            int store = free >= 0 ? free : home;
            _memo[store].Key = key;
            _memo[store].Expires = now + FieldMemoSeconds;
            _memo[store].Sample = sample;
            _memo[store].Ok = ok;
            return ok;
        }

        private int MemoLiveCells(float now)
        {
            int live = 0;
            for (int i = 0; i < _memo.Length; i++)
                if (_memo[i].Expires > now) live++;
            return live;
        }

        /// <summary>
        /// Land everywhere the disc reaches — the centre and four points 70% out on the
        /// cardinals all read Depth ≤ 0 — means no water reads at all until the next probe.
        /// </summary>
        private bool NoWaterNear(Vector3 c, float radius, float now)
        {
            float r = 0.7f * radius;
            float q = 0.495f * radius;   // the same ring, on the diagonals
            return IsLand(c.x, c.z, now)
                   && IsLand(c.x + r, c.z, now) && IsLand(c.x - r, c.z, now)
                   && IsLand(c.x, c.z + r, now) && IsLand(c.x, c.z - r, now)
                   && IsLand(c.x + q, c.z + q, now) && IsLand(c.x - q, c.z + q, now)
                   && IsLand(c.x + q, c.z - q, now) && IsLand(c.x - q, c.z - q, now);
        }

        private bool IsLand(float x, float z, float now)
            => !FieldAt(x, z, now, out FieldSample s) || s.Depth <= 0f;

        // ---- the scene ----------------------------------------------------------------------

        private void ReadScene()
        {
            Color ambient = RenderSettings.ambientLight;
            _ambientLum = DriftLineMath.Luminance(ambient.r, ambient.g, ambient.b);
            _fogColor = RenderSettings.fogColor;
            // The FOG colour drives the day factor, not the ambient. Measured 2026-09-21: ambient
            // runs 0.38 → 0.56 from midnight to noon and never reads as night; fog runs
            // 0.18 → 0.53. Both stay in the `wake lines` readout so the next sky can be measured
            // the same way this one was — see DriftLineMath.MeasuredMidnightFogLuminance.
            _fogLum = DriftLineMath.Luminance(_fogColor.r, _fogColor.g, _fogColor.b);
            _day = DriftLineMath.DayFactor(_fogLum);
            DriftLineMath.Tint(_fogColor.r, _fogColor.g, _fogColor.b, _day,
                               Mathf.Clamp01(ModConfig.DriftLineNightFloor.Value),
                               out _tintR, out _tintG, out _tintB, out _alphaScale);
            _fogMode = RenderSettings.fog ? (int)RenderSettings.fogMode : 0;
            _fogDensity = RenderSettings.fogDensity;
            _fogStart = RenderSettings.fogStartDistance;
            _fogEnd = RenderSettings.fogEndDistance;
        }

        // ---- build, sleep, latch ------------------------------------------------------------

        private void EnsurePool(int pool)
        {
            if (pool == _configuredPool && _state != null) return;

            _configuredPool = pool;
            _effectivePool = pool;
            _state = new Slot[pool];
            _x = new float[pool]; _z = new float[pool]; _y = new float[pool]; _yVel = new float[pool];
            _vx = new float[pool]; _vz = new float[pool]; _age = new float[pool]; _life = new float[pool];
            _length = new float[pool]; _width = new float[pool]; _rotation = new float[pool];
            _alphaMul = new float[pool]; _retry = new float[pool]; _lastRide = new float[pool];
            _speed = new float[pool];
            _vol = new WaterVolume[pool];
            _volBounds = new Bounds[pool];
            _buf = new ParticleSystem.Particle[pool];
            _spawnDebt = 0f;
            _overBudget = 0;

            if (_system != null)
            {
                ParticleSystem.MainModule main = _system.main;
                main.maxParticles = pool;
            }
        }

        /// <summary>Build the emitter lazily, first time a frame is actually owed.</summary>
        private void Build()
        {
            try
            {
                var go = new GameObject("undertow_drift_lines");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.position = Vector3.zero;
                _system = go.AddComponent<ParticleSystem>();

                ParticleSystem.MainModule main = _system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Local;
                main.maxParticles = _configuredPool;
                main.startSize3D = true;
                main.startSpeed = 0f;
                main.gravityModifier = 0f;
                main.playOnAwake = false;
                main.loop = true;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

                ParticleSystem.EmissionModule emission = _system.emission;
                emission.enabled = false;
                ParticleSystem.ShapeModule shape = _system.shape;
                shape.enabled = false;

                _renderer = go.GetComponent<ParticleSystemRenderer>();
                _renderer.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
                _renderer.sortMode = ParticleSystemSortMode.None;
                _renderer.sortingFudge = 0f;
                _renderer.maxParticleSize = 1f;
                _renderer.pivot = Vector3.zero;
                _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
                _streakMaterial = ParticleKit.BuildStreakMaterial("DriftLines", out _shaderName, out _shaderHasFog);
                _renderer.material = _streakMaterial;

                _system.Play();
                _built = true;
                _builtAtFrame = Time.frameCount;
                _windowStart = Time.time;
                _nextVerbose = Time.time + VerboseSeconds;

                Undertow.Log.LogInfo(
                    $"DriftLines: emitter built — shader '{_shaderName}' ({(_shaderHasFog ? "shader fog" : "manual fog")}), " +
                    $"pool {_configuredPool}, radius {ModConfig.DriftLineRadius.Value.ToString("0", CultureInfo.InvariantCulture)} m, " +
                    $"texture {ParticleKit.StreakTextureWidth}x{ParticleKit.StreakTextureHeight}, queue {ParticleKit.RenderQueue}.");
            }
            catch (Exception ex)
            {
                Latch("build failed, streaks disabled", ex);
            }
        }

        private void Sleep(string word)
        {
            _stateWord = word;
            if (_system != null && _drawn > 0)
            {
                try { _system.SetParticles(_buf, 0); } catch { /* going quiet is best effort */ }
                _drawn = 0;
            }
            if (_renderer != null) _renderer.enabled = false;
        }

        private void Latch(string what, Exception ex)
        {
            _latched = true;
            string frame = ex.StackTrace != null ? FirstFrame(ex.StackTrace) : "";
            _lastError = $"{ex.GetType().Name}: {ex.Message}{frame}";
            _stateWord = $"LATCHED: {_lastError}";
            Undertow.Log.LogWarning($"DriftLines: {what}: {_lastError}");
            DestroyEmitter();
        }

        private static string FirstFrame(string stack)
        {
            int nl = stack.IndexOf('\n');
            string first = nl > 0 ? stack.Substring(0, nl) : stack;
            return " @ " + first.Trim();
        }

        private void DestroyEmitter()
        {
            try
            {
                if (_system != null)
                {
                    _system.SetParticles(_buf, 0);
                    Destroy(_system.gameObject);
                }
                // Destroying the GameObject frees the renderer, not the material and texture we
                // assigned to it — those are ours to release, or every reset leaks them.
                if (_streakMaterial != null)
                {
                    Texture tex = _streakMaterial.mainTexture;
                    Destroy(_streakMaterial);
                    if (tex != null) Destroy(tex);
                }
            }
            catch { /* already gone */ }
            _streakMaterial = null;
            _system = null;
            _renderer = null;
            _built = false;
            _drawn = 0;
        }

        /// <summary>
        /// `wake lines reset`: clear the latch, drop every streak, destroy the emitter and let
        /// the next frame rebuild it with the configured pool. The mod's first console
        /// mutation — local, cosmetic, with no store behind it, which is exactly the self-gating
        /// the console's header asks for.
        /// </summary>
        public string Reset()
        {
            DestroyEmitter();
            _latched = false;
            _lastError = "none";
            _state = null;
            _configuredPool = 0;
            _effectivePool = 0;
            _seaState = 0f;
            _costEma = 0f;
            _overBudget = 0;
            _firstAfloatLogged = false;
            _cache.Clear();
            _camVol = null;
            Array.Clear(_memo, 0, _memo.Length);
            _idle = false;
            _nextIdleProbe = 0f;
            // Every counter the readout prints, or "since build" becomes a lie.
            _wTried = _wSpawned = _wRejSlack = _wRejShallow = _wRejWeak = _wRejNoVolume = 0;
            _lTried = _lSpawned = _lRejSlack = _lRejShallow = _lRejWeak = _lRejNoVolume = 0;
            _retiredLife = _retiredReflect = _retiredNoVolume = _totalSpawned = 0;
            _stateWord = "reset — rebuilding on the next frame";
            return "drift lines reset: latch cleared, emitter destroyed, rebuilding on the next frame.";
        }

        // ---- instrumentation ----------------------------------------------------------------

        private void LogFirstAfloat(int i, float surface, float level)
        {
            _firstAfloatLogged = true;
            var c = CultureInfo.InvariantCulture;
            string volName = _vol[i] != null ? _vol[i].name : "?";
            Undertow.Log.LogInfo(
                $"DriftLines: first streak afloat at ({_x[i].ToString("0", c)}, {_z[i].ToString("0", c)}) — " +
                $"{_speed[i].ToString("0.###", c)} m/s bearing {DriftLineMath.BearingDegrees(_vx[i], _vz[i]).ToString("0", c)}°, " +
                $"surface {surface.ToString("0.00", c)} (flat {level.ToString("0.00", c)}) in volume '{volName}'.");
        }

        private void RollWindow(float now)
        {
            if (now - _windowStart < WindowSeconds) return;
            _lTried = _wTried; _lSpawned = _wSpawned; _lRejSlack = _wRejSlack;
            _lRejShallow = _wRejShallow; _lRejWeak = _wRejWeak; _lRejNoVolume = _wRejNoVolume;
            _wTried = _wSpawned = _wRejSlack = _wRejShallow = _wRejWeak = _wRejNoVolume = 0;
            _windowStart = now;
        }

        private void MeasureCost(float now)
        {
            _sw.Stop();
            // The build frame carries the one-time cost of the shader chain, the texture and the
            // material. Letting it seed the EMA halved the pool two seconds after every login
            // within sight of water (review finding, 2026-09-18). It is not a frame cost.
            if (Time.frameCount == _builtAtFrame) return;
            _lastCost = (float)_sw.Elapsed.TotalMilliseconds;
            // Never seed the EMA from a single frame. Measured 2026-09-18: the first frame after
            // build (cache warm-up, cold memo) read 2.66 ms and the seeded EMA came within ~20
            // frames of a spurious halving. From zero, the average earns its value over a second.
            _costEma += (_lastCost - _costEma) / 60f;
            if (Time.frameCount - _builtAtFrame < WarmupFrames) return;   // no verdicts while cold

            float budget = Mathf.Clamp(ModConfig.DriftLineBudgetMs.Value, 0.1f, 4f);
            if (_costEma > budget) _overBudget++; else _overBudget = 0;

            if (_overBudget >= OverBudgetFrames)
            {
                _overBudget = 0;
                int cap = DriftLineMath.DegradedCap(_effectivePool, _costEma, budget);
                if (cap < _effectivePool)
                {
                    var c = CultureInfo.InvariantCulture;
                    Undertow.Log.LogWarning(
                        $"DriftLines: cost averaged {_costEma.ToString("0.00", c)} ms over {OverBudgetFrames} frames " +
                        $"against a {budget.ToString("0.00", c)} budget — pool {_effectivePool} → {cap}.");
                    for (int i = cap; i < _effectivePool; i++) Retire(i, 0f);
                    _effectivePool = cap;
                }
            }

            if (ModConfig.VerboseLogging.Value && now >= _nextVerbose)
            {
                _nextVerbose = now + VerboseSeconds;
                Undertow.Log.LogInfo(Summary(now));
            }
        }

        private string Summary(float now)
        {
            var c = CultureInfo.InvariantCulture;
            Means(out int active, out float meanBearing, out float meanSpeed, out float meanLength);
            float fieldBearing = 0f, fieldSpeed = 0f;
            if (FieldAt(_centre.x, _centre.z, now, out FieldSample s))
            {
                fieldBearing = DriftLineMath.BearingDegrees(s.X, s.Z);
                fieldSpeed = s.Speed;
            }
            return $"DriftLines: active {active}/{_effectivePool} bearing {meanBearing.ToString("0", c)}° vs {fieldBearing.ToString("0", c)}° " +
                   $"speed {meanSpeed.ToString("0.00", c)} vs {fieldSpeed.ToString("0.00", c)} len {meanLength.ToString("0.0", c)}m " +
                   $"cost {_costEma.ToString("0.00", c)}ms chop {DriftLineMath.Chop(_seaState).ToString("0.00", c)} day {_day.ToString("0.00", c)}";
        }

        private void Means(out int active, out float meanBearing, out float meanSpeed, out float meanLength)
        {
            active = 0;
            float sx = 0f, sz = 0f, sumSpeed = 0f, sumLen = 0f;
            if (_state != null)
            {
                for (int i = 0; i < _effectivePool; i++)
                {
                    if (_state[i] != Slot.Active) continue;
                    active++;
                    sx += _vx[i];
                    sz += _vz[i];
                    sumSpeed += _speed[i];
                    sumLen += _length[i];
                }
            }
            meanBearing = active > 0 ? DriftLineMath.BearingDegrees(sx, sz) : 0f;
            meanSpeed = active > 0 ? sumSpeed / active : 0f;
            meanLength = active > 0 ? sumLen / active : 0f;
        }

        /// <summary>
        /// `wake lines`: everything a person needs to trust the visual without eyes on it —
        /// which shader resolved, whether spawning is alive and WHY attempts fail, that the
        /// streaks carry the field (mean bearing and speed against the field's own), that a
        /// streak's height is the wave and not the flat level, and that OUR resolver agrees
        /// with vanilla's <c>Floating.GetWaterLevel</c> to the millimetre.
        /// </summary>
        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1024);
            float now = Time.time;

            bool on = ModConfig.EnableDriftLines.Value;
            sb.Append($"drift lines {(on ? "ON" : "OFF")} — {_stateWord}");
            if (_built)
                sb.Append($" (shader '{_shaderName}', {(_shaderHasFog ? "shader fog" : "manual fog")})");
            sb.Append('\n');

            Means(out int active, out float meanBearing, out float meanSpeed, out float meanLength);
            int pool = _state != null ? _state.Length : 0;
            sb.Append($"pool {_configuredPool}");
            if (_effectivePool < _configuredPool) sb.Append($" (auto-degraded: {_configuredPool} → {_effectivePool})");
            sb.Append($", active {active}, dormant {Math.Max(0, _effectivePool - active)}, drawn {_drawn} | ");
            sb.Append($"radius {ModConfig.DriftLineRadius.Value.ToString("0", c)}m, opacity {ModConfig.DriftLineOpacity.Value.ToString("0.00", c)}, ");
            sb.Append($"min depth {ModConfig.DriftLineMinDepth.Value.ToString("0.#", c)}m, ");
            // Reported because "the foam hovers" is a bug report this readout should be able to
            // answer on its own: the nearest-streak line below already proves the SAMPLED height
            // matches vanilla exactly, so anything left over is this number.
            sb.Append($"lift {(ModConfig.DriftLineLiftMetres.Value * 100f).ToString("0.#", c)}cm, ");
            sb.Append($"slack floor {ModConfig.DriftLineSlackFloor.Value.ToString("0.##", c)}, ");
            sb.Append($"night floor {ModConfig.DriftLineNightFloor.Value.ToString("0.##", c)}\n");

            float level = SeaContext.WaterLevel;
            sb.Append($"centre ({_centre.x.ToString("0", c)}, {_centre.z.ToString("0", c)})");
            sb.Append(float.IsNaN(_camAbove) ? " camera: no water volume" : $" camera {_camAbove.ToString("0.0", c)}m above water");
            if (SeaContext.TryEvaluate(_centre.x, _centre.z, out FieldSample s))
            {
                sb.Append($" | field here {s.Speed.ToString("0.###", c)} m/s bearing {DriftLineMath.BearingDegrees(s.X, s.Z).ToString("0", c)}° " +
                          $"({s.X.ToString("0.###", c)}, {s.Z.ToString("0.###", c)}) {s.Dominant}, depth {s.Depth.ToString("0.#", c)}m, " +
                          $"surge x{s.StormSurge.ToString("0.00", c)}");
            }
            sb.Append('\n');

            sb.Append($"streaks: mean bearing {meanBearing.ToString("0", c)}°, mean speed {meanSpeed.ToString("0.00", c)}, mean length {meanLength.ToString("0.0", c)}m | ");
            sb.Append($"last {WindowSeconds:0}s window: {_lTried} tried, {_lSpawned} spawned; rejected slack {_lRejSlack}, shallow {_lRejShallow}, weak {_lRejWeak}, no-volume {_lRejNoVolume} | ");
            sb.Append($"retired: life {_retiredLife}, reflected {_retiredReflect}, no-volume {_retiredNoVolume} | spawned {_totalSpawned} since build\n");

            int nearest = -1;
            float nearestD = float.MaxValue;
            if (_state != null)
            {
                for (int i = 0; i < _effectivePool; i++)
                {
                    if (_state[i] != Slot.Active) continue;
                    float dx = _x[i] - _centre.x, dz = _z[i] - _centre.z;
                    float d = dx * dx + dz * dz;
                    if (d < nearestD) { nearestD = d; nearest = i; }
                }
            }
            if (nearest >= 0)
            {
                int i = nearest;
                WaterVolume probe = null;
                float vanilla = Floating.GetWaterLevel(new Vector3(_x[i], level, _z[i]), ref probe);
                sb.Append($"nearest streak {Mathf.Sqrt(nearestD).ToString("0.0", c)}m: surface {_y[i].ToString("0.00", c)} " +
                          $"(flat {level.ToString("0.00", c)}, wave {(_y[i] - level).ToString("+0.00;-0.00", c)}) | ");
                sb.Append(vanilla <= -9999f
                    ? "vanilla Floating.GetWaterLevel: no water volume | "
                    : $"vanilla Floating.GetWaterLevel {vanilla.ToString("0.00", c)} (delta {(_y[i] - vanilla).ToString("0.000", c)}) | ");
                sb.Append($"rot {_rotation[i].ToString("0", c)}° for bearing {DriftLineMath.BearingDegrees(_vx[i], _vz[i]).ToString("0", c)}° | ");
                sb.Append($"len {_length[i].ToString("0.0", c)}m wid {_width[i].ToString("0.00", c)}m | age {_age[i].ToString("0.0", c)}/{_life[i].ToString("0.0", c)}s\n");
            }
            else
            {
                sb.Append("nearest streak: none active\n");
            }

            sb.Append($"sea state {_seaState.ToString("0.00", c)}m (chop {DriftLineMath.Chop(_seaState).ToString("0.00", c)}, chop fade {DriftLineMath.ChopFade(_seaState).ToString("0.00", c)}), ");
            sb.Append($"fog lum {_fogLum.ToString("0.00", c)} -> day {_day.ToString("0.00", c)} (ambient lum {_ambientLum.ToString("0.00", c)}), tint ({_tintR.ToString("0.00", c)},{_tintG.ToString("0.00", c)},{_tintB.ToString("0.00", c)}), ");
            sb.Append(_fogMode == 0 ? "fog off" : $"fog mode {_fogMode} density {_fogDensity.ToString("0.0000", c)}");
            sb.Append($" | volumes cached {_cache.Count} (Instances {_cache.InstancesCount}), rebuilt {_cache.RebuiltAgo(now).ToString("0.0", c)}s ago\n");

            sb.Append($"cost {_lastCost.ToString("0.00", c)} ms last frame ({_costEma.ToString("0.00", c)} EMA, budget {ModConfig.DriftLineBudgetMs.Value.ToString("0.00", c)}), ");
            sb.Append($"surface reads {_surfaceReads}, field evals {_fieldEvals} (memo {MemoLiveCells(now)} cells) | ");
            sb.Append($"last error: {_lastError} (latched: {(_latched ? "yes" : "no")})");

            return sb.ToString();
        }

        private static byte ToByte(float v)
        {
            if (float.IsNaN(v) || v <= 0f) return 0;
            if (v >= 1f) return 255;
            return (byte)(v * 255f + 0.5f);
        }
    }
}
