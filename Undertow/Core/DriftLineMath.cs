using System;

namespace RavenIron.Undertow.Core
{
    /// <summary>
    /// The maths behind the drift lines — the foam streaks that show the current on the water.
    ///
    /// PURE, like <see cref="CurrentField"/>: no Unity, no config, no clock, no game types.
    /// Every number the renderer needs is a function of its inputs, so the harness can pin the
    /// properties that would otherwise fail silently in-game: foam in slack water, a visible
    /// disc edge, a streak that pops in at full alpha, a white sprite glowing at midnight, a
    /// texture with a head end that turns a sea phenomenon into an arrow.
    ///
    /// <c>Visuals/DriftLines.cs</c> is the only consumer. It owns the arrays and the
    /// ParticleSystem; this file owns every rule about what a streak looks like and where one
    /// may exist. The design premise lives here too: NOTHING in this file reads the field out
    /// for the player. A streak's length grows with speed and its line lies along the flow,
    /// but it is end-to-end symmetric, so a still frame gives the line and never the sense.
    /// The sense comes from watching foam pass the hull, which is how a sailor reads a set.
    /// </summary>
    public static class DriftLineMath
    {
        // ---- spawn --------------------------------------------------------------------------

        /// <summary>Share of MaxSpeed at which the acceptance weight reaches 1.</summary>
        public const float FullSpeedShare = 0.6f;

        /// <summary>Metres past MinDepth over which acceptance ramps from 0 to 1.</summary>
        public const float DepthRampMetres = 6f;

        /// <summary>
        /// 0..1 chance that a candidate point earns a streak. Exactly zero at or below the
        /// share of MaxSpeed that <see cref="CurrentField"/> itself calls Slack — the same
        /// constant, not a copy of its value — because slack water is glassy, and that absence
        /// is the contrast flotsam's slack-water story depends on. Zero again shallower than
        /// <paramref name="minDepth"/>, which is what actually keeps foam off a beach: the
        /// field's own shallow fade is a ramp to the waterline, not a cutoff.
        /// </summary>
        public static float SpawnWeight(float speed, float depth, float maxSpeed, float minDepth)
        {
            if (float.IsNaN(speed) || float.IsNaN(depth) || float.IsNaN(maxSpeed) || float.IsNaN(minDepth))
                return 0f;
            if (maxSpeed <= 0f) return 0f;

            float slack = CurrentField.SlackShare * maxSpeed;
            float span  = (FullSpeedShare - CurrentField.SlackShare) * maxSpeed;
            float bySpeed = Clamp01((speed - slack) / span);
            float byDepth = Clamp01((depth - minDepth) / DepthRampMetres);
            return bySpeed * byDepth;
        }

        /// <summary>Foam arrives in patches: 1 (50%), 2 (30%) or 3 (20%) streaks per accepted point.</summary>
        public static int ClusterSize(float roll01)
        {
            if (float.IsNaN(roll01) || roll01 < 0.5f) return 1;
            return roll01 < 0.8f ? 2 : 3;
        }

        public const float MinLifeSeconds = 6f;
        public const float MaxLifeSeconds = 14f;

        public static float LifetimeSeconds(float roll01)
            => MinLifeSeconds + (MaxLifeSeconds - MinLifeSeconds) * Clamp01(roll01);

        /// <summary>
        /// Uniform-by-area point in a disc from two 0..1 rolls. The square root is the classic
        /// bug: without it streaks clump at the centre and thin outward, which reads as a
        /// personal cloud that follows the player.
        /// </summary>
        public static void DiscPoint(float u1, float u2, float radius, out float dx, out float dz)
        {
            float r = Math.Max(0f, radius) * (float)Math.Sqrt(Clamp01(u1));
            double theta = 2.0 * Math.PI * Clamp01(u2);
            dx = r * (float)Math.Cos(theta);
            dz = r * (float)Math.Sin(theta);
        }

        // ---- life and the disc --------------------------------------------------------------

        public const float FadeInShare  = 0.20f;
        public const float FadeOutShare = 0.35f;

        /// <summary>
        /// Alpha over a streak's life: smoothstep in over the first fifth, hold, smoothstep out
        /// over the last third. Zero at both ends and outside [0,1]. Foam forms and dissipates;
        /// a streak that pops is the one thing that would betray a particle system.
        /// </summary>
        public static float LifeEnvelope(float age01)
        {
            if (float.IsNaN(age01) || age01 <= 0f || age01 >= 1f) return 0f;
            if (age01 < FadeInShare) return SmoothStep(age01 / FadeInShare);
            if (age01 > 1f - FadeOutShare) return SmoothStep((1f - age01) / FadeOutShare);
            return 1f;
        }

        public const float EdgeSolidShare = 0.65f;

        /// <summary>
        /// 1 inside 65% of the radius, quadratic to 0 at the radius, 0 beyond. The disc
        /// boundary must never be visible, or the effect reveals that it is attached to the
        /// player rather than to the sea.
        /// </summary>
        public static float EdgeFade(float distance, float radius)
        {
            if (float.IsNaN(distance) || float.IsNaN(radius) || radius <= 0f) return 0f;
            float solid = EdgeSolidShare * radius;
            if (distance <= solid) return 1f;
            if (distance >= radius) return 0f;
            float t = (distance - solid) / (radius - solid);
            return (1f - t) * (1f - t);
        }

        public const float ReflectBeyondShare = 1.15f;
        public const float ReflectToShare     = 0.98f;

        /// <summary>
        /// A streak the player has moved away from is mirrored through the centre to just
        /// inside the rim, where the caller re-rolls its acceptance. Keeps the disc uniformly
        /// filled under the player's own motion with no velocity estimate: what falls astern of
        /// a hull under sail reappears ahead of it. Returns true when it moved the offset.
        /// </summary>
        public static bool Reflect(ref float dx, ref float dz, float radius)
        {
            if (float.IsNaN(dx) || float.IsNaN(dz) || float.IsNaN(radius) || radius <= 0f) return false;
            float d = (float)Math.Sqrt((double)dx * dx + (double)dz * dz);
            if (d <= ReflectBeyondShare * radius) return false;
            float scale = -ReflectToShare * radius / d;
            dx *= scale;
            dz *= scale;
            return true;
        }

        // ---- geometry -----------------------------------------------------------------------

        /// <summary>
        /// Length and width in metres. Length grows with speed and a little with chop; width
        /// stays a hand's breadth. The ranges are what reads as foam from a deck: nothing
        /// degenerates into a blob or stretches into a kilometre under any config.
        /// </summary>
        public static void StreakSize(float speed, float maxSpeed, float chop, float r1, float r2,
                                      out float length, out float width)
        {
            float sp = 0f;
            if (!float.IsNaN(speed) && !float.IsNaN(maxSpeed) && maxSpeed > 0f)
                sp = Clamp01(speed / (FullSpeedShare * maxSpeed));

            length = (1.2f + 3.3f * sp) * (0.75f + 0.5f * Clamp01(r1)) * (1f + 0.3f * Clamp01(chop));
            width  = 0.22f + 0.22f * Clamp01(r2);
        }

        /// <summary>
        /// Compass bearing of a velocity in degrees, 0 = +z north, 90 = +x east — the same
        /// convention <c>wake here</c> prints, inlined so the streaks and the console cannot
        /// disagree. A zero vector answers 0.
        /// </summary>
        public static float BearingDegrees(float vx, float vz)
        {
            if (float.IsNaN(vx) || float.IsNaN(vz) || (vx == 0f && vz == 0f)) return 0f;
            float deg = (float)(Math.Atan2(vx, vz) * (180.0 / Math.PI));
            return Normalise360(deg);
        }

        /// <summary>
        /// The ONE place the horizontal-billboard handedness lives, and it is MEASURED, not
        /// derived. MEASURED ON STORM10, 2026-09-18: in uniform water running 192° with 70–95
        /// streaks whose mean bearing matched the field to a degree, a rotation of
        /// <c>−bearing</c> laid every line at 102° — a quarter turn across the flow. Of Unity's
        /// four possible conventions only one puts it there: the quad's long (U) axis lies along
        /// world +x at rotation 0 and a positive rotation turns it clockwise seen from above
        /// (east toward north, i.e. heading DEcreasing), so the line's heading is
        /// <c>90 − rotation</c> and the rotation for a bearing is <c>90 − bearing</c>. That is
        /// also what Unity's "positive rotates clockwise as seen from the front" gives for a
        /// quad whose front faces +y. An earlier "90° off" report against this same formula
        /// came from the slack node at spawn, where the two streaks in view carried sampled
        /// bearings of 196° and 118° against a centre reading of 8° — a confounded reading,
        /// not a measurement. <paramref name="flip"/> remains the one-constant fix for a
        /// mirror; the texture is symmetric end to end, so only the LINE matters.
        /// </summary>
        public static float QuadRotationDegrees(float bearingDegrees, bool flip)
        {
            if (float.IsNaN(bearingDegrees)) return 0f;
            return Normalise360(flip ? bearingDegrees - 90f : 90f - bearingDegrees);
        }

        // ---- light --------------------------------------------------------------------------

        /// <summary>Rec. 709 luminance, never negative.</summary>
        public static float Luminance(float r, float g, float b)
        {
            if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)) return 0f;
            return Math.Max(0f, 0.2126f * r + 0.7152f * g + 0.0722f * b);
        }

        public const float DayFloorLuminance = 0.05f;
        public const float DaySlopeLuminance = 0.30f;

        /// <summary>1 by day, ~0 at night, in between at dusk and under a storm sky.</summary>
        public static float DayFactor(float ambientLuminance)
        {
            if (float.IsNaN(ambientLuminance)) return 0f;
            return Clamp01((ambientLuminance - DayFloorLuminance) / DaySlopeLuminance);
        }

        /// <summary>
        /// Streak colour from the scene: a little over halfway from the fog colour to white,
        /// then scaled by the day. At midnight that is a grey a shade above black water, never
        /// a glow — the shaders that ship are unlit, and an unlit white quad at night is a UI
        /// element. The alpha scale dims the night further.
        /// </summary>
        public static void Tint(float fogR, float fogG, float fogB, float day,
                                out float r, out float g, out float b, out float alphaScale)
        {
            day = Clamp01(day);
            float bright = 0.12f + 0.88f * day;
            r = Clamp01(Lerp(Clamp01(fogR), 1f, 0.55f) * bright);
            g = Clamp01(Lerp(Clamp01(fogG), 1f, 0.55f) * bright);
            b = Clamp01(Lerp(Clamp01(fogB), 1f, 0.55f) * bright);
            alphaScale = 0.35f + 0.65f * day;
        }

        /// <summary>
        /// Unity's own fog curves, so a streak sinks into the mist with the water when the
        /// resolved shader has no fog pass of its own. Modes follow UnityEngine.FogMode:
        /// 1 linear, 2 exponential, 3 exponential squared. Anything else, a non-positive density
        /// or distance, or a degenerate linear range answers "fully visible".
        /// </summary>
        public static float FogVisibility(float distance, float density, int fogMode,
                                          float linearStart, float linearEnd)
        {
            if (float.IsNaN(distance) || distance <= 0f) return 1f;
            switch (fogMode)
            {
                case 1:
                    if (float.IsNaN(linearStart) || float.IsNaN(linearEnd) || linearEnd <= linearStart) return 1f;
                    return Clamp01((linearEnd - distance) / (linearEnd - linearStart));
                case 2:
                    if (float.IsNaN(density) || density <= 0f) return 1f;
                    return (float)Math.Exp(-distance * density);
                case 3:
                    if (float.IsNaN(density) || density <= 0f) return 1f;
                    float k = distance * density;
                    return (float)Math.Exp(-k * k);
                default:
                    return 1f;
            }
        }

        // ---- sea state ----------------------------------------------------------------------

        /// <summary>
        /// Exponential moving average of the frame's largest wave deviation across the streaks
        /// we already ride. The chop signal costs nothing — the samples are taken anyway — and
        /// reads nothing from the environment.
        /// </summary>
        public static float SeaState(float previous, float observedMax, float dt, float tau)
        {
            if (float.IsNaN(observedMax)) return previous;
            if (float.IsNaN(previous) || tau <= 0f || float.IsNaN(tau)) return observedMax;
            if (float.IsNaN(dt) || dt <= 0f) return previous;
            float a = 1f - (float)Math.Exp(-dt / tau);
            return previous + (observedMax - previous) * a;
        }

        /// <summary>0 in a calm, 1 in a full storm; lengthens, brightens and lifts the streaks.</summary>
        public static float Chop(float seaState)
            => float.IsNaN(seaState) ? 0f : Clamp01((seaState - 0.3f) / 2.0f);

        /// <summary>
        /// A mild attenuation in a big sea — half at 3 m, never below a fifth in any Valheim
        /// water — so a storm reads as spume rather than a carpet, and is never erased.
        /// </summary>
        public static float ChopFade(float seaState)
        {
            if (float.IsNaN(seaState) || seaState <= 1.5f) return 1f;
            float t = (seaState - 1.5f) / 1.5f;
            return 1f / (1f + t * t);
        }

        // ---- the texture --------------------------------------------------------------------

        /// <summary>
        /// Alpha of the streak texture at (u, v) in 0..1: feathered ends along u, soft edges
        /// across v, and an elongated mottle so it reads as a run of bubbles rather than a
        /// brushstroke. SYMMETRIC under u → 1−u and v → 1−v by construction — the grain is
        /// sampled on |2u−1| and |2v−1| — so there is no head and no tail. That symmetry is the
        /// one property separating foam from an arrow, and the harness holds it.
        /// </summary>
        public static float StreakAlpha(float u, float v, int seed)
        {
            if (float.IsNaN(u) || float.IsNaN(v) || u <= 0f || u >= 1f || v <= 0f || v >= 1f) return 0f;

            float along  = (float)Math.Pow(Math.Sin(Math.PI * u), 0.6);
            float t      = 2f * v - 1f;
            float across = (float)Math.Pow(Math.Max(0f, 1f - t * t), 1.5);

            float gu = Math.Abs(2f * u - 1f) * 3f;
            float gv = Math.Abs(2f * v - 1f);
            float noise = 0.65f * ValueNoise(gu, gv, seed) + 0.35f * ValueNoise(2f * gu, 2f * gv, seed + 1);
            float grain = 0.65f + 0.35f * noise;

            return Clamp01(along * across * grain);
        }

        /// <summary>
        /// The texel version the rasteriser calls: the outer one-pixel ring is forced to zero
        /// so Clamp wrapping can never bleed a hard edge, and each texel samples its own centre.
        /// </summary>
        public static float StreakAlphaAt(int px, int py, int width, int height, int seed)
        {
            if (width < 3 || height < 3) return 0f;
            if (px <= 0 || py <= 0 || px >= width - 1 || py >= height - 1) return 0f;
            return StreakAlpha((px + 0.5f) / width, (py + 0.5f) / height, seed);
        }

        /// <summary>
        /// Integer-avalanche hash to 0..1 — the same idiom <see cref="CurrentField"/> uses for
        /// its stream function, copied rather than shared: the pure field file does not move
        /// for a cosmetic's convenience. No state, byte-identical on every machine.
        /// </summary>
        public static float Hash01(int a, int b, int seed)
        {
            unchecked
            {
                uint h = (uint)a * 2654435761u ^ (uint)b * 2246822519u ^ (uint)seed * 3266489917u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                h *= 3266489917u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / (float)0x1000000;
            }
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int ix = (int)Math.Floor(x);
            int iy = (int)Math.Floor(y);
            float fx = SmoothStep(x - ix);
            float fy = SmoothStep(y - iy);

            float a = Hash01(ix,     iy,     seed);
            float b = Hash01(ix + 1, iy,     seed);
            float c = Hash01(ix,     iy + 1, seed);
            float d = Hash01(ix + 1, iy + 1, seed);

            return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
        }

        // ---- rolls and keys -----------------------------------------------------------------

        /// <summary>
        /// xorshift32, 0..1. Never UnityEngine.Random: that is shared global state (vanilla's
        /// own wind octaves save and restore it around their draws), and a cosmetic must not
        /// perturb anything that reads it. A zero state is reseeded so it cannot stick.
        /// </summary>
        public static float NextRoll(ref uint state)
        {
            uint x = state;
            if (x == 0u) x = 0x9E3779B9u;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return (x & 0xFFFFFFu) / (float)0x1000000;
        }

        /// <summary>
        /// Memo-cache key for a field sample: the cell's x index in the high 32 bits and its z
        /// index in the low 32, so it is injective over any span the game can reach, negative
        /// coordinates included.
        /// </summary>
        public static long CellKey(float x, float z, float cellSize)
        {
            if (float.IsNaN(x) || float.IsNaN(z) || float.IsNaN(cellSize) || cellSize <= 0f) return 0L;
            int ix = (int)Math.Floor(x / cellSize);
            int iz = (int)Math.Floor(z / cellSize);
            return ((long)ix << 32) | (uint)iz;
        }

        // ---- riding and self-protection -----------------------------------------------------

        public const float VerticalVelocityCap = 5f;

        /// <summary>
        /// Slope for the frames between two surface reads, clamped so a bad sample can never
        /// launch a streak skyward.
        /// </summary>
        public static float VerticalVelocity(float yPrev, float yNow, float dt)
        {
            if (float.IsNaN(yPrev) || float.IsNaN(yNow) || float.IsNaN(dt) || dt <= 1e-4f) return 0f;
            float v = (yNow - yPrev) / dt;
            if (v > VerticalVelocityCap) return VerticalVelocityCap;
            if (v < -VerticalVelocityCap) return -VerticalVelocityCap;
            return v;
        }

        public const float MaxDeltaSeconds = 0.25f;

        /// <summary>A loading hitch may not teleport a streak.</summary>
        public static float ClampDt(float dt)
        {
            if (float.IsNaN(dt) || dt < 0f) return 0f;
            return dt > MaxDeltaSeconds ? MaxDeltaSeconds : dt;
        }

        public const int DegradeFloor = 16;

        /// <summary>
        /// The self-protection step: over budget halves the pool, with a floor so the feature
        /// can never silently remove itself. The caller logs every step; nothing here is quiet.
        /// </summary>
        public static int DegradedCap(int cap, float avgMs, float budgetMs)
        {
            if (float.IsNaN(avgMs) || float.IsNaN(budgetMs)) return cap;
            if (avgMs <= budgetMs) return cap;
            return Math.Max(DegradeFloor, cap / 2);
        }

        // ---- helpers ------------------------------------------------------------------------

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v) || v <= 0f) return 0f;
            return v >= 1f ? 1f : v;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static float SmoothStep(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float Normalise360(float deg)
        {
            deg %= 360f;
            if (deg < 0f) deg += 360f;
            return deg >= 360f ? deg - 360f : deg;
        }
    }
}
