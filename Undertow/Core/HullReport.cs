using System.Globalization;
using System.Text;
using UnityEngine;

namespace RavenIron.Undertow.Core
{
    /// <summary>
    /// One line at boot naming every vanilla hull's force constants — the numbers the drift push
    /// is actually measured against, and which the class defaults in a decompile do NOT show
    /// because the prefabs override them.
    ///
    /// Why it exists (2026-09-21, first bug report after 1.0.0): a paddled hull could not make
    /// headway against 0.18 m/s of water without a fivefold paddle force. Our push against a hull
    /// driving upstream is an ACCELERATION of `water × DriftStrength`, and whether that stops a
    /// hull depends on the hull's own thrust per tick (`m_backwardForce × dt` when rowing) and
    /// not on its speed. Nobody could say what a raft's thrust IS without this line, and a
    /// diagnosis that needs a number nobody has is a round-trip nobody should spend twice.
    ///
    /// One-shot, INFO, every role. Retries until ZNetScene holds prefabs, for the same reason
    /// <see cref="FloatScan"/> does. Every member named here is public in the shipping assembly
    /// (read out of the real `assembly_valheim.dll` the same day — rule 5).
    /// </summary>
    internal static class HullReport
    {
        private static bool _done;

        public static void MaybeLog()
        {
            if (_done) return;

            ZNetScene scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null || scene.m_prefabs.Count == 0) return;

            try
            {
                StringBuilder sb = new StringBuilder(
                    "Hulls (vanilla prefab constants; rowing dv per tick = backwardForce x 0.02, " +
                    "our upstream push per tick = water x DriftStrength x 0.02): ");
                int n = 0;
                foreach (GameObject go in scene.m_prefabs)
                {
                    if (go == null) continue;
                    Ship ship = go.GetComponent<Ship>();
                    if (ship == null) continue;
                    Rigidbody rb = go.GetComponent<Rigidbody>();

                    if (n++ > 0) sb.Append(" | ");
                    sb.Append(go.name)
                      .Append(": backwardForce ").Append(F(ship.m_backwardForce))
                      .Append(", sailForceFactor ").Append(F(ship.m_sailForceFactor))
                      .Append(", dampingForward ").Append(F(ship.m_dampingForward))
                      .Append(", dampingSideway ").Append(F(ship.m_dampingSideway))
                      .Append(", damping ").Append(F(ship.m_damping))
                      .Append(", force ").Append(F(ship.m_force))
                      .Append(", forceDistance ").Append(F(ship.m_forceDistance))
                      .Append(", stearForce ").Append(F(ship.m_stearForce))
                      .Append(", mass ").Append(rb != null ? F(rb.mass) : "?")
                      .Append(", rbLinearDamping ").Append(rb != null ? F(rb.linearDamping) : "?")
                      .Append(", rbAngularDamping ").Append(rb != null ? F(rb.angularDamping) : "?");
                }

                // ZNetScene's list is complete from Awake, so no hull at all is an answer too —
                // and one worth latching, or this would poll every frame for the whole session.
                _done = true;
                if (n == 0) { Undertow.Log.LogInfo("Hulls: no prefab with a Ship component in ZNetScene."); return; }
                Undertow.Log.LogInfo(sb.ToString());
            }
            catch (System.Exception e)
            {
                _done = true;
                Undertow.Log.LogWarning("HullReport failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
