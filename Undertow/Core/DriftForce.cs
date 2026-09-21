namespace RavenIron.Undertow.Core
{
    /// <summary>
    /// What the current does to a floating body. Pure arithmetic, no Unity, no game types, so
    /// the harness tests the shipping source.
    ///
    /// THE MODEL IS ENTRAINMENT, NOT DRAG, AND THE DIFFERENCE IS THE WHOLE FILE.
    ///
    /// The obvious model is drag toward the water: force proportional to (water - hull), so a
    /// boat asymptotically takes up the speed of the water it sits in. It is wrong here, and
    /// wrong in a way that only shows up under sail. Vanilla ALREADY models hull-water
    /// resistance — `m_damping`, `m_dampingForward`, `m_dampingSideway` in the method this
    /// feeds — computed against the hull's absolute velocity, i.e. assuming the water is still.
    /// Adding a second drag term against the hull's full velocity double-counts it. A karve
    /// making 6 m/s through water moving at 0.3 would receive
    /// `0.6 * (0.3 - 6) = -3.4 m/s^2` — the sea as a brake on every boat under way, which is
    /// both unphysical and would read to a player as the mod breaking sailing.
    ///
    /// So the current is applied as a PUSH along the water's own direction, saturating as the
    /// hull takes up the water's speed. It carries a boat and never fights the sail.
    ///
    /// WHY SATURATION RATHER THAN A CALIBRATED PUSH — measured on a live server 2026-08-28, and
    /// this killed two earlier models in one reading. The first attempt matched a push against
    /// vanilla's quadratic damping so the two would balance at the water's speed. That requires
    /// knowing the hull's damping coefficient, and `m_dampingForward` is a SERIALIZED UNITY
    /// FIELD — every boat prefab overrides it. The class default read off the decompile (0.01)
    /// applies to no actual boat: a VikingShip measured ~0.0053 effective, and settled at 1.38x
    /// the water's speed instead of 1.0. A raft, a karve and a longship would each land on a
    /// different multiple, so NO single constant can be right.
    ///
    /// Fading the push out as the hull approaches the water's speed sets the equilibrium
    /// directly, by construction, without knowing anything about how the hull damps. Every boat
    /// type converges on the same answer, and the number `wake here` prints is the speed a
    /// sailor will actually drift, on any hull.
    ///
    /// The hull's velocity is a parameter again — but only its component ALONG the current, and
    /// only through a term clamped to [0,1]. That clamp is the anti-braking guarantee that the
    /// earlier signature enforced by omission: the result can never oppose the current, so a
    /// boat under sail is never slowed, it merely stops being helped.
    ///
    ///
    /// The result is a VELOCITY CHANGE per tick — an impulse per unit mass. The caller
    /// multiplies by the body's mass and hands it to ForceMode.Impulse, which is the convention
    /// vanilla itself uses in the method this patches.
    /// </summary>
    public static class DriftForce
    {
        /// <summary>
        /// Where the current starts fading out near the world edge.
        ///
        /// Vanilla's own <c>Ship.ApplyEdgeForce</c> pushes hulls back inland from 10420m, ramping
        /// to 10500m. Undertow is fully faded by 10420 so the two never argue: a mod current
        /// fighting the game's own boundary force is a boat juddering on the rim of the world,
        /// and whichever won would look like a bug.
        /// </summary>
        public const float EdgeFadeStart = 10200f;
        public const float EdgeFadeEnd = 10420f;

        /// <summary>1 well inside the world, 0 at and beyond the boundary force's start.</summary>
        public static float EdgeFade(float distanceFromCentre)
        {
            if (distanceFromCentre <= EdgeFadeStart) return 1f;
            if (distanceFromCentre >= EdgeFadeEnd) return 0f;
            return 1f - (distanceFromCentre - EdgeFadeStart) / (EdgeFadeEnd - EdgeFadeStart);
        }

        /// <summary>
        /// Velocity change to apply this tick, horizontal only.
        /// </summary>
        /// <param name="hullAlongCurrent">
        /// The hull's velocity component ALONG the current, m/s. Used only to fade the push out
        /// as the hull takes up the water's speed, through a term clamped to [0,1] - so it can
        /// never turn the push into a brake. Negative when the hull drives upstream.
        /// </param>
        /// <param name="strength">
        /// How hard the water grips, per second. Sets how QUICKLY a hull takes up the water's
        /// speed; WHERE it settles is set by the saturation term, not by this.
        /// </param>
        /// <param name="crewFactor">
        /// Scales the whole effect. 1 for a crewed boat; the configured unattended fraction,
        /// DEFAULT ZERO, for an empty one — vanilla already damps an unmanned hull's horizontal
        /// velocity to a tenth per tick and forces it to Stop, and losing a moored longship to a
        /// mod is a one-star review.
        /// </param>
        public static void Compute(
            float waterX, float waterZ,
            float hullAlongCurrent,
            float strength, float dt,
            float crewFactor, float edgeFade,
            out float dvx, out float dvz)
        {
            dvx = 0f;
            dvz = 0f;

            float waterSpeed = (float)System.Math.Sqrt(waterX * waterX + waterZ * waterZ);
            if (waterSpeed <= 1e-5f) return;

            // SATURATION, and it is what makes the mod hull-independent.
            //
            // Full push at rest, fading to nothing as the hull's speed ALONG THE CURRENT reaches
            // the water's own. The equilibrium is therefore set by this term rather than by a
            // race between our push and vanilla's damping, which is the only way to get every
            // hull to the same answer — see the class summary for why calibrating against
            // damping cannot work.
            //
            // CLAMPED TO [0,1], which is the anti-braking guarantee — and the guarantee is
            // narrower than it first reads, so state it exactly. The result can never be negative,
            // so the force can never point AGAINST the water: there is no drag term keyed to hull
            // speed, and at worst this stops helping. What it does NOT promise is that your speed
            // over the ground is unaffected. Sail into a current and it costs you roughly the
            // water's own speed, because the push points at you — which is correct, is what a
            // current is, and was confirmed as wanted by the owner on 2026-09-19. An earlier
            // wording here claimed this "never slows a boat under sail", and the README repeated
            // it; both were read as a promise about ground speed and misled the owner in game.
            // A hull moving AGAINST the current gets a value above 1 before clamping and is capped
            // at a full push rather than an amplified one.
            float head = 1f - (hullAlongCurrent / waterSpeed);
            if (head > 1f) head = 1f;
            else if (head < 0f) head = 0f;

            float k = strength * dt * head;

            // NEVER HAND OVER MORE THAN THE WATER'S OWN SPEED IN ONE TICK. Without this, a long
            // frame — a lag spike, a loading hitch, a breakpoint — multiplies coupling by a large
            // dt and delivers a velocity change many times the current itself, launching the
            // hull. Clamping before the other factors keeps one tick bounded by the thing it is
            // modelling.
            if (k > 1f) k = 1f;
            else if (k < 0f) k = 0f;

            k *= crewFactor * edgeFade;

            dvx = waterX * k;
            dvz = waterZ * k;
        }

        /// <summary>
        /// What the current does to a hull UNDER WAY — paddle or sail set. Pure, in the hull's
        /// own axes (forward, right), the axes vanilla damps along.
        ///
        /// THE PUSH ABOVE IS WRONG FOR A HULL UNDER PROPULSION, AND 1.0.0 SHIPPED WITH IT.
        /// Reported the day 1.0.0 went up (2026-09-21, Grishak, a paddled karve): below a
        /// `MaxCurrentSpeed` of 0.25 he could paddle through anything; at 0.3 the water held him
        /// or pushed him backward. Read out of the prefabs with <c>HullReport</c> the same hour:
        /// a karve's paddle is `m_backwardForce 0.2`, i.e. 0.004 m/s of speed per tick, and the
        /// push above against a hull driving upstream is `water × strength × dt` = 0.004 per
        /// tick at 0.2 m/s of water. Break-even at 0.2 m/s, to the decimal he found. The push is
        /// an ACCELERATION compared against the hull's THRUST, so whether a current stops a boat
        /// depended on the boat's engine and not on the water — a leaf's worth of current
        /// stopped a karve, and a fivefold paddle beat a race. Nobody had seen it because every
        /// hull measurement was either drifting, which the push handles well, or under Njord and
        /// Sailing, whose thrust is many times vanilla's.
        ///
        /// WHAT A CURRENT ACTUALLY COSTS is a SPEED: drag acts on velocity relative to the water,
        /// so a hull that makes v0 through still water makes v0 − w over the ground upstream and
        /// v0 + w downstream, whatever its thrust. Vanilla's damping is quadratic in the hull's
        /// ABSOLUTE velocity, per axis, scaled by how deep the hull sits:
        /// `dv = −d × v|v| × submergence` with `d = m_dampingForward` / `m_dampingSideway`. The
        /// exact correction that turns it into drag relative to the water is the difference
        /// `−d × sub × [(v − w)|v − w| − v|v|]` per axis — which is what this returns. Every
        /// hull's Rigidbody has ZERO linear drag (read the same day), so this quadratic term IS
        /// the hull's whole resistance and the correction is complete, not approximate.
        ///
        /// Worked for a karve (d 0.001, submergence ≈ 0.2 at rest, paddle 0.004/tick): still
        /// water 4.47 m/s; into 0.182 m/s of water 4.29; into a 1.2 m/s race 3.27, and 5.67
        /// with it. Under the push above the same karve made 1.34 m/s into 0.182 and went
        /// backward in the race. The harness simulates exactly this and pins both numbers.
        ///
        /// WHY THE PUSH STAYS FOR A HULL ADRIFT. Vanilla's drag is so small that the true
        /// relative-drag coupling would take a free hull minutes to carry; the saturating push
        /// carries it in seconds, was measured at 0.99 of the water's speed on three hulls, and
        /// is the behaviour the owner watched and kept. So: adrift → the push; under way → this.
        /// The caller decides from the ship's speed setting, which is exactly "is anything
        /// propelling it".
        ///
        /// `strength` scales this the way it scales the push — 1 is the water's own drag; a
        /// server can make the sea grip harder, never differently. Each axis is clamped to
        /// ±1 m/s per tick, as vanilla clamps its own damping.
        /// </summary>
        public static void ComputeUnderWay(
            float waterForward, float waterRight,
            float hullForward, float hullRight,
            float dampingForward, float dampingSideway,
            float submergence,
            float strength, float crewFactor, float edgeFade,
            out float dvForward, out float dvRight)
        {
            dvForward = 0f;
            dvRight = 0f;
            if (submergence <= 0f) return;
            if (waterForward == 0f && waterRight == 0f) return;

            float dF = dampingForward * submergence;
            float dS = dampingSideway * submergence;

            dvForward = -dF * (Signed2(hullForward - waterForward) - Signed2(hullForward));
            dvRight   = -dS * (Signed2(hullRight - waterRight)     - Signed2(hullRight));

            float scale = strength * crewFactor * edgeFade;
            dvForward = Clamp1(dvForward * scale);
            dvRight   = Clamp1(dvRight * scale);
        }

        /// <summary>x·|x| — the signed square vanilla's damping is built on.</summary>
        private static float Signed2(float x) => x < 0f ? -(x * x) : x * x;

        private static float Clamp1(float x) => x > 1f ? 1f : (x < -1f ? -1f : x);
    }
}
