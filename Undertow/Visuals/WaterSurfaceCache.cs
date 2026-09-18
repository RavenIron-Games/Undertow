using System.Collections.Generic;
using UnityEngine;

namespace RavenIron.Undertow.Visuals
{
    /// <summary>
    /// A read-only resolver from a world (x, z) to the live <see cref="WaterVolume"/> under it,
    /// and that volume's wave surface height.
    ///
    /// WHY NOT <c>Floating.GetWaterLevel</c> ON THE PER-FRAME PATH. Its body (decompiled
    /// 2026-09-18) is fine and it IS what the console cross-checks against — but its cached
    /// fast path calls <c>GetComponent&lt;Collider&gt;()</c> on every call and its miss path is
    /// a physics overlap query, and the drift lines ask for a surface height up to eighty
    /// times a frame. So this class snapshots <c>WaterVolume.Instances</c> (public static,
    /// maintained by the volumes' own OnEnable/OnDisable), memoises each volume's collider
    /// bounds ONCE per volume lifetime (water volumes never move), and answers containment
    /// with an XZ compare. Zero GetComponent and zero physics queries per streak per frame.
    ///
    /// EVERYTHING HERE IS A READ. <c>WaterVolume.Instances</c>, <c>GetLiquidType()</c> and
    /// <c>GetWaterSurface()</c> are all public in the shipping assembly (verified against the
    /// NON-publicized DLL, house rule 5); the private <c>m_collider</c> is never named — the
    /// collider comes from the volume's own GameObject. Nothing is written back to any volume,
    /// which is house rule 4 by construction.
    ///
    /// A destroyed volume reads as Unity-null (the overloaded operator) and is skipped; a
    /// volume that unloads leaves <c>Instances</c> through OnDisable, which changes the count
    /// and triggers a rebuild. Belt and braces: the snapshot is also rebuilt on a timer.
    /// </summary>
    internal sealed class WaterSurfaceCache
    {
        private struct Entry
        {
            public WaterVolume Volume;
            public Bounds Bounds;
        }

        public const float RebuildSeconds = 2f;

        private readonly List<Entry> _entries = new List<Entry>(128);
        private Dictionary<WaterVolume, Bounds> _boundsMemo = new Dictionary<WaterVolume, Bounds>(128);
        private int _cachedCount = -1;
        private float _lastRebuild = -1f;

        /// <summary>Volumes in the current snapshot (water only).</summary>
        public int Count => _entries.Count;

        /// <summary>What vanilla currently lists, for the console to compare against.</summary>
        public int InstancesCount => WaterVolume.Instances.Count;

        public int Rebuilds { get; private set; }

        public float RebuiltAgo(float now) => _lastRebuild < 0f ? -1f : now - _lastRebuild;

        /// <summary>
        /// Refresh the snapshot when vanilla's list changed size, when forced, or when the
        /// timer says so. Cheap when nothing changed: one int compare and one float compare.
        /// </summary>
        public void Refresh(float now, bool force)
        {
            List<WaterVolume> instances = WaterVolume.Instances;
            bool rebuild = force
                           || instances.Count != _cachedCount
                           || _lastRebuild < 0f
                           || now - _lastRebuild >= RebuildSeconds;
            if (!rebuild) return;

            _entries.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                WaterVolume v = instances[i];
                if (v == null) continue;                                  // Unity-null: destroyed
                // Inert today: GetLiquidType() is hardcoded to Water in the shipping build
                // (decompiled 2026-09-18) and nothing subclasses WaterVolume — tar is a separate
                // component, not a WaterVolume flavour. Kept in case it ever becomes real; it
                // costs one call per volume per rebuild.
                if (v.GetLiquidType() != LiquidType.Water) continue;

                if (!_boundsMemo.TryGetValue(v, out Bounds b))
                {
                    Collider collider = v.GetComponent<Collider>();
                    if (collider == null) continue;
                    b = collider.bounds;
                    _boundsMemo[v] = b;
                }
                _entries.Add(new Entry { Volume = v, Bounds = b });
            }

            // The memo outlives volumes on purpose (a destroyed key is harmless), but not
            // forever: prune once it holds far more dead entries than live ones.
            if (_boundsMemo.Count > _entries.Count * 2 + 32)
            {
                var pruned = new Dictionary<WaterVolume, Bounds>(_entries.Count * 2);
                for (int i = 0; i < _entries.Count; i++)
                    pruned[_entries[i].Volume] = _entries[i].Bounds;
                _boundsMemo = pruned;
            }

            _cachedCount = instances.Count;
            _lastRebuild = now;
            Rebuilds++;
        }

        public void Clear()
        {
            _entries.Clear();
            _boundsMemo.Clear();
            _cachedCount = -1;
            _lastRebuild = -1f;
        }

        public static bool ContainsXZ(Bounds b, float x, float z)
        {
            Vector3 min = b.min;
            Vector3 max = b.max;
            return x >= min.x && x <= max.x && z >= min.z && z <= max.z;
        }

        /// <summary>
        /// The wave surface height at (x, z). <paramref name="volume"/> and
        /// <paramref name="bounds"/> are the caller's cached pair for this point: validated
        /// first (alive, still contains the point), re-resolved on a miss. Where more than one
        /// water volume contains the point, the HIGHEST surface wins — vanilla's own rule in
        /// <c>Floating.GetWaterLevel</c>. Returns false, and nulls the pair, when no water
        /// volume contains the point at all; a streak can never sit on nothing.
        /// </summary>
        public bool TryGetSurface(float x, float z, float waterLevel,
                                  ref WaterVolume volume, ref Bounds bounds, out float surface)
        {
            var point = new Vector3(x, waterLevel, z);

            if (volume != null && ContainsXZ(bounds, x, z))
            {
                surface = volume.GetWaterSurface(point);
                return true;
            }

            volume = null;
            surface = 0f;
            bool found = false;

            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (!ContainsXZ(e.Bounds, x, z)) continue;
                if (e.Volume == null) continue;

                float s = e.Volume.GetWaterSurface(point);
                if (!found || s > surface)
                {
                    surface = s;
                    volume = e.Volume;
                    bounds = e.Bounds;
                    found = true;
                }
            }
            return found;
        }
    }
}
