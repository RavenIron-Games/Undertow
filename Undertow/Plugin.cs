using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using RavenIron.Undertow.Config;
using RavenIron.Undertow.Core;
using RavenIron.Undertow.Net;

namespace RavenIron.Undertow
{
    /// <summary>
    /// Undertow — the sea gets its own motion.
    ///
    /// ROLE-AWARE, AND THE ROLES ARE NOT THE OBVIOUS ONES. This mod's authority split is
    /// different from Ragnarok's Wrath's, and getting it backwards is the first way to waste
    /// a day here:
    ///
    ///   • The current FIELD is a pure function of seed, position, world time and season. It
    ///     needs no authority at all — every machine computes the same vector for the same
    ///     point, which is why there is no sync layer and no save file.
    ///   • The current's EFFECT on a boat is applied by whichever machine owns that boat's
    ///     ZDO, which is a player's client, not the server. See the Ship postfix (task 2).
    ///   • Only ambient world work — flotsam (task 4) — is server-authoritative and driven
    ///     from <see cref="SeaTick"/>.
    ///
    /// So: the DLL is required on the server and on every client, and SeaTick idles on a
    /// pure client rather than being absent from it.
    /// </summary>
    [BepInPlugin(PluginId, PluginName, PluginVersion)]
    [BepInProcess("valheim.exe")]
    [BepInProcess("valheim_server.exe")]
    public class Undertow : BaseUnityPlugin
    {
        public const string PluginId      = "com.raveniron.undertow";
        public const string PluginName    = "Undertow";
        public const string PluginVersion = "1.0.3";

        public static Undertow Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        /// <summary>
        /// True once ZNet exists and this process is the authority for ambient world work.
        /// Null before ZNet.Start — callers must handle "not known yet", which is why this is
        /// a method rather than a bool cached at Awake.
        /// </summary>
        public static bool IsSimulationAuthority()
        {
            ZNet znet = ZNet.instance;
            if (znet == null) return false;
            return znet.IsServer();
        }

        /// <summary>Headless dedicated server: no local player, no presence layer, no rendering.</summary>
        public static bool IsDedicated()
        {
            ZNet znet = ZNet.instance;
            return znet != null && znet.IsDedicated();
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ModConfig.Bind(base.Config);

            // Map the synced keys to their entries once, here, because it is the only moment
            // where every one of them is certain to be bound. It also checks OUR table against
            // OUR bindings and logs an error naming any row that has no entry — a typo there
            // would otherwise cost nothing visible: the key just never syncs, on both ends.
            ConfigSync.Bind();

            _harmony = new Harmony(PluginId);
            _harmony.PatchAll();
            ReportPatches();

            // SeaTick is a plain MonoBehaviour driven from Update — deliberately NOT a
            // coroutine. House style rule 2: every long-lived coroutine in this codebase's
            // lineage independently grew a `continue`-past-`yield` hard-lock, and one of them
            // reached production.
            gameObject.AddComponent<SeaTick>();

            RegisterSystems();

            // Visuals exist only where something renders. GraphicsDeviceType.Null is the
            // headless tell that survives compiling against client reference DLLs (where
            // ZNet.IsDedicated() is a hardcoded false) - the sibling mod's gate, verbatim.
            // Added AFTER the cursor and the systems so nothing a visual does at Awake can
            // stand between them and their registration (house rule 3).
            if (UnityEngine.SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                gameObject.AddComponent<Visuals.DriftLines>();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        /// <summary>
        /// Every ambient system registers here, in one place. Systems are round-robined by
        /// SeaTick's cursor, so ordering is a mild scheduling hint only — no system may depend
        /// on another having ticked first within the same frame.
        ///
        /// Drift (task 2) and swimmers (task 5) are Harmony patches on the machine that OWNS the
        /// hull, not SeaTick systems, and will never appear in this list — that split is the
        /// whole authority story of this mod.
        /// </summary>
        private static void RegisterSystems()
        {
            SeaTick.Register(new Systems.FlotsamSystem());
        }

        /// <summary>
        /// Name every method this mod patched, at boot.
        ///
        /// The drift postfix cannot be verified any other way on a headless server: boat physics
        /// runs only on the peer that owns the hull, so a server with nobody sailing never
        /// executes it once. This at least separates "the patch is armed" from "the patch never
        /// attached", which are indistinguishable from a silent log — and if Valheim ever
        /// renames Ship.CustomFixedUpdate or the private fields behind it, this is the line that
        /// says so instead of a boat that mysteriously stops drifting.
        /// </summary>
        private void ReportPatches()
        {
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (System.Reflection.MethodBase m in _harmony.GetPatchedMethods())
                    names.Add($"{m.DeclaringType?.Name}.{m.Name}");

                if (names.Count == 0)
                    Log.LogWarning("Harmony attached NO patches — the console and the drift will both be absent.");
                else
                    Log.LogInfo($"Harmony patched {names.Count}: {string.Join(", ", names.ToArray())}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"could not enumerate patches: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            Instance = null;
        }
    }
}
