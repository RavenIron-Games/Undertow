using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using RavenIron.Undertow.Config;
using RavenIron.Undertow.Core;
using UnityEngine;

namespace RavenIron.Undertow.Net
{
    /// <summary>
    /// The server's sea, on every client. The engine half of <see cref="ConfigWire"/>, which holds
    /// the decisions; this file is the part that touches ZNet, BepInEx and a real config file, and
    /// is therefore the part the harness cannot compile — the same split, and the same reason, as
    /// <see cref="ConfigLedger"/> against <c>Config/ConfigMigration</c>.
    ///
    /// SERVER WINS, AND AN ADMIN CAN PUSH. Two directions, deliberately asymmetric:
    ///
    ///   - DOWN. The server publishes its gameplay dials to everybody, on a slow heartbeat and
    ///     immediately whenever the peer list changes so a joiner does not sail a stale sea. A
    ///     client ADOPTS them IN MEMORY for the session. Its config file is never written, never
    ///     backed up and never migrated by this — leave a server and your own settings are exactly
    ///     as you left them. That is the whole reason the override below exists rather than the
    ///     obvious <c>entry.Value = incoming</c>: BepInEx saves on set by default, and a config
    ///     manager mod would hand the server a pen to edit the player's file with.
    ///
    ///   - UP. An admin who changes a synced value on their own client sends that one value to the
    ///     server, which applies it to its OWN entry — so it persists in the server's file, the
    ///     way an admin change should — and re-publishes. A non-admin's edits never leave their
    ///     machine.
    ///
    /// WHAT THIS IS NOT. It is not a second source of truth for the sea. <see cref="CurrentField"/>
    /// is still a pure function of seed, position, time and season, with no persistence and no
    /// per-tick traffic; all this does is make sure the two ends agree about the CONSTANTS they
    /// feed it. Nothing here is saved, nothing is sent per frame, and with the mod alone on a
    /// machine it never speaks at all.
    /// </summary>
    public static class ConfigSync
    {
        /// <summary>Server to everybody: the whole synced set.</summary>
        private const string RpcPublish = "com.raveniron.undertow.cfg";

        /// <summary>An admin's client to the server: one value.</summary>
        private const string RpcPush = "com.raveniron.undertow.cfgpush";

        /// <summary>
        /// A client to the server: "I have just arrived, send it now."
        ///
        /// The peer-change broadcast below covers a join too, but it fires the moment the SOCKET
        /// connects, which can be well before the joining client has built its own handler table —
        /// and a payload that lands before the handler exists is simply gone. That would leave the
        /// joiner computing its own ocean until the next heartbeat, and the whole point of the
        /// heartbeat being slow is that nothing should depend on it. Asking, once, the moment this
        /// end is definitely ready removes the race rather than widening the window around it.
        /// </summary>
        private const string RpcRequest = "com.raveniron.undertow.cfgwant";

        /// <summary>
        /// The server back to ONE client: what became of the value you pushed.
        ///
        /// Exists because the push is now sent optimistically — see OnLocalSettingChanged — so
        /// without an answer a non-admin would log "pushed" and watch nothing happen, which is
        /// the silent no-op this project refuses everywhere else. Carries a plain sentence, not a
        /// status code: it is read by a human in a log, once, when something surprised them.
        /// </summary>
        private const string RpcAck = "com.raveniron.undertow.cfgack";

        /// <summary>
        /// The heartbeat. Slow on purpose: the payload is a few hundred bytes and the peer-change
        /// trigger below is what actually makes a joiner current, so this exists only to heal a
        /// message that went missing and to carry a server-side config edit within half a minute.
        /// </summary>
        private const float BroadcastIntervalSeconds = 30f;

        /// <summary>The shortest gap between two answers to a client's request. See OnRequested.</summary>
        private const float RequestFloorSeconds = 1f;

        // ---- registration ------------------------------------------------------------------

        /// <summary>
        /// The instance we registered against, NOT a bool. ZRoutedRpc is rebuilt for each session,
        /// so a latched "registered" flag survives into a new session whose handler table is empty
        /// and the mod goes quiet for the rest of the process. Same pattern as Ragnarok's Wrath's
        /// VersionSync, for the same reason.
        /// </summary>
        private static ZRoutedRpc _registeredWith;

        private static float _nextBroadcast;
        private static float _lastBroadcast = float.NegativeInfinity;

        /// <summary>When a client's REQUEST was last answered. Separate from <see cref="_lastBroadcast"/> — see OnRequested.</summary>
        private static float _lastRequestAnswered = float.NegativeInfinity;

        private static int _lastPeerCount = -1;
        private static bool _hooked;

        /// <summary>Whether this client has already asked the server for its config this session.</summary>
        private static bool _asked;

        /// <summary>
        /// True while an admin's push is being written to the server's own entries.
        ///
        /// Without it the server publishes TWICE per pushed key: once because SetSerializedValue
        /// raises SettingChanged and the handler treats a local edit on the authority as a reason
        /// to broadcast, and again when OnPushed finishes. Not a loop — a client never writes an
        /// entry, so there is nothing on the far end to bounce back — but two payloads and two log
        /// lines for one change is the kind of noise that makes a real fault hard to see later.
        /// </summary>
        private static bool _applyingPush;

        // ---- the override layer ------------------------------------------------------------

        /// <summary>
        /// The values in force, when they are not this machine's own. Boxed and keyed by wire
        /// address, which is fine because of <see cref="_any"/>.
        /// </summary>
        private static readonly Dictionary<string, object> _overrides =
            new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>
        /// Short-circuits the read path when nothing is overridden — which is EVERY read on a
        /// server, in single player, and on any client before the first payload lands. The drift
        /// postfix reads config four times per hull per physics tick, so the common case is one
        /// bool test rather than a string hash. Measured discipline, not a guess: the profiling on
        /// 2026-09-19 established that the field's cost is its nine terrain probes and that
        /// everything around them is noise, so the only thing worth protecting here is the path
        /// that runs when the feature is not in use at all.
        /// </summary>
        private static bool _any;

        /// <summary>Every synced key this build actually binds, by wire address.</summary>
        private static readonly Dictionary<string, ConfigEntryBase> _bound =
            new Dictionary<string, ConfigEntryBase>(StringComparer.Ordinal);

        // ---- what `wake status` reads ------------------------------------------------------

        /// <summary>How many values this machine is currently sailing by that are not its own.</summary>
        public static int AdoptedCount => _overrides.Count;

        /// <summary>
        /// How many keys this build actually BOUND, which is what a server really publishes.
        ///
        /// Not the same as <c>ConfigWire.SyncedKeys.Length</c>, and `wake status` printed that
        /// constant until 2026-09-19. <see cref="Bind"/> logs an error and DROPS a row it cannot
        /// resolve rather than throwing, and <see cref="Broadcast"/> returns silently when nothing
        /// is bound — so in the one state worth detecting, the command the protocol tells you to
        /// run to confirm health reported a confident "12 published" while publishing none.
        /// </summary>
        public static int BoundCount => _bound.Count;

        /// <summary>One line for the console. Never null.</summary>
        public static string LastEvent = "no config has arrived from a server";

        // ------------------------------------------------------------------------------------
        // Binding
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Map every key on the wire to the entry it names, once, after ModConfig has bound.
        ///
        /// The loop at the end is a guard on OUR OWN table rather than on anything a server sends:
        /// a row in <see cref="ConfigWire.SyncedKeys"/> that names a key this build does not bind
        /// is a typo that would otherwise cost nothing visible — the key simply never syncs, on
        /// both ends, forever. Ordinal and case-sensitive matching makes that failure mode one
        /// wrong letter away, so it is worth an error that names the row.
        /// </summary>
        public static void Bind()
        {
            _bound.Clear();

            Add(ModConfig.MaxCurrentSpeed);
            Add(ModConfig.TidePeriodSeconds);
            Add(ModConfig.TideAmplitude);
            Add(ModConfig.CoastalStrength);
            Add(ModConfig.StormSurgeMultiplier);
            Add(ModConfig.EnableWrathBridge);
            Add(ModConfig.EnableDrift);
            Add(ModConfig.DriftStrength);
            Add(ModConfig.UnattendedDriftFactor);
            Add(ModConfig.EnableSwimmers);
            Add(ModConfig.SwimmerDriftFactor);
            Add(ModConfig.SwimmerMaxShareOfSwimSpeed);

            for (int i = 0; i < ConfigWire.SyncedKeys.Length; i++)
            {
                if (_bound.ContainsKey(ConfigWire.SyncedKeys[i])) continue;
                Undertow.Log.LogError(
                    "ConfigSync: '" + ConfigWire.SyncedKeys[i] + "' is on the wire but nothing " +
                    "binds it — that key will never sync in either direction. Fix ConfigWire.SyncedKeys.");
            }
        }

        private static void Add(ConfigEntryBase entry)
        {
            if (entry?.Definition == null) return;
            string address = ConfigWire.Address(entry.Definition.Section, entry.Definition.Key);

            if (!ConfigWire.IsSynced(entry.Definition.Section, entry.Definition.Key))
            {
                // The mirror of the guard above: an entry wired up here that the table does not
                // list would be sent by nobody and accepted by nobody, quietly.
                Undertow.Log.LogError(
                    "ConfigSync: '" + address + "' is bound for sync but is absent from " +
                    "ConfigWire.SyncedKeys — it will never travel. Fix one of the two.");
                return;
            }

            _bound[address] = entry;
        }

        // ------------------------------------------------------------------------------------
        // The read path — what every consumer calls instead of entry.Value
        // ------------------------------------------------------------------------------------

        /// <summary>The float in force here: the server's if one arrived, otherwise this machine's.</summary>
        public static float Live(this ConfigEntry<float> entry)
        {
            if (!_any || entry?.Definition == null) return entry?.Value ?? 0f;
            return _overrides.TryGetValue(
                       ConfigWire.Address(entry.Definition.Section, entry.Definition.Key),
                       out object boxed) && boxed is float f
                ? f
                : entry.Value;
        }

        /// <summary>The bool in force here: the server's if one arrived, otherwise this machine's.</summary>
        public static bool Live(this ConfigEntry<bool> entry)
        {
            if (!_any || entry?.Definition == null) return entry != null && entry.Value;
            return _overrides.TryGetValue(
                       ConfigWire.Address(entry.Definition.Section, entry.Definition.Key),
                       out object boxed) && boxed is bool b
                ? b
                : entry.Value;
        }

        // ------------------------------------------------------------------------------------
        // Lifecycle, driven from SeaTick
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Called every frame from the one cursor, on EVERY role — house rule 2, no timer of our
        /// own. Cheap: a null check and a float compare in the common case.
        /// </summary>
        public static void Tick(ZNet znet)
        {
            if (znet == null) return;

            try
            {
                EnsureRegistered();
                HookLocalEdits();

                if (!znet.IsServer())
                {
                    ZRoutedRpc rpc = ZRoutedRpc.instance;
                    if (rpc != null) MaybeAsk(znet, rpc);
                    return;
                }

                // A peer list that changed means somebody just joined (or left), and a joiner
                // holding its own MaxCurrentSpeed is sailing a different ocean until it hears
                // otherwise. Cheaper and far more responsive than shortening the heartbeat.
                int peers = znet.GetPeerConnections();
                float now = Time.realtimeSinceStartup;

                if (peers != _lastPeerCount)
                {
                    _lastPeerCount = peers;

                    // Nobody to tell. A routed RPC to Everybody with no peers still round-trips
                    // through this machine's own handler, which then stands down on IsServer —
                    // work for nothing, on every empty server and in every single-player session.
                    if (peers > 0) Broadcast("peer list changed");
                    return;
                }

                if (now < _nextBroadcast) return;
                _nextBroadcast = now + BroadcastIntervalSeconds;   // also when there is nobody to tell
                if (peers > 0) Broadcast("heartbeat");
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: tick failed — " + ex.Message);
            }
        }

        /// <summary>
        /// Leaving a server hands the player their own sea back. Idempotent, so SeaTick can call
        /// it on every frame ZNet is absent without thinking about it.
        /// </summary>
        /// <summary>
        /// The wire-version refusal is said ONCE per session. Measured 2026-09-21, the first time
        /// two Undertow versions met: a /1 client on a /2 server logged the refusal for each of the
        /// two join-time publishes and would have logged it again on every 30 s heartbeat — 120
        /// warnings an hour for a condition that does not change until somebody updates. Warn
        /// once, never kick, and keep `wake status` honest through LastEvent, which is set every time.
        /// </summary>
        private static bool _refusalLogged;

        public static void Reset()
        {
            _registeredWith = null;
            _lastPeerCount = -1;
            _nextBroadcast = 0f;
            _lastRequestAnswered = float.NegativeInfinity;
            _asked = false;
            _refusalLogged = false;

            if (_overrides.Count == 0)
            {
                // Still clear the note, and that is not tidiness. A payload this build could not
                // read sets LastEvent WITHOUT ever filling the table, so a refusal on one server
                // followed by a clean disconnect left `wake status` reading "refused a payload
                // this build cannot read" at the main menu, about a server the player had left.
                LastEvent = "no config has arrived from a server";
                return;
            }

            _overrides.Clear();
            _any = false;
            LastEvent = "left the server — back on local values";
            Undertow.Log.LogInfo("ConfigSync: disconnected — local config back in force.");
        }

        private static void EnsureRegistered()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(rpc, _registeredWith)) return;

            rpc.Register<string>(RpcPublish, OnPublished);
            rpc.Register<string>(RpcPush, OnPushed);
            rpc.Register<string>(RpcRequest, OnRequested);
            rpc.Register<string>(RpcAck, OnAcknowledged);
            _registeredWith = rpc;

            Undertow.Log.LogInfo("ConfigSync: RPCs registered (" + ConfigWire.Header + ").");
        }

        /// <summary>
        /// Ask the server for the sea, once, as soon as there is a server to ask.
        ///
        /// THIS USED TO LIVE IN <see cref="EnsureRegistered"/> AND WAS DEAD THERE. The
        /// two-argument <c>InvokeRoutedRPC</c> resolves its target through vanilla's private
        /// <c>GetServerPeerID()</c>, whose body returns **0L when the client's peer list is still
        /// empty** (read out of the real assembly, 2026-09-19). <c>ZRoutedRpc.AddPeer</c> is
        /// called at the very END of <c>ZNet.RPC_PeerInfo</c>, after the whole handshake, while
        /// registration happens on the first frame <c>ZNet.instance</c> exists — so the request
        /// was addressed to 0, handled locally by this same client (which stands down on
        /// <c>!IsServer()</c>), and routed onward to an empty peer list. Nothing reached the wire,
        /// nothing threw, and <c>_registeredWith</c> was already latched so it never retried.
        ///
        /// MEASURED, not merely reasoned: across two separate client joins on 2026-09-19 with
        /// VerboseLogging on, the server logged `published (peer list changed)` and heartbeats and
        /// **never once logged `published (peer N asked)`**. The feature had never worked and the
        /// only reason nothing was wrong is that the peer-change broadcast covers the same case.
        ///
        /// So: gate on <c>GetServerPeer()</c> being non-null, which is exactly the condition that
        /// was missing, and keep a one-shot flag rather than relying on registration happening once.
        /// </summary>
        private static void MaybeAsk(ZNet znet, ZRoutedRpc rpc)
        {
            if (_asked || znet.IsServer()) return;
            if (znet.GetServerPeer() == null) return;

            _asked = true;
            rpc.InvokeRoutedRPC(RpcRequest, ConfigWire.Header);

            if (ModConfig.VerboseLogging.Value)
                Undertow.Log.LogInfo("ConfigSync: asked the server for its config.");
        }

        /// <summary>A client has just arrived and wants the sea it is joining.</summary>
        private static void OnRequested(long sender, string payload)
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet == null || !znet.IsServer()) return;

                // One answer a second, at most. A request costs the server a payload to EVERY
                // client, so an asking client spends somebody else's bandwidth, and a client that
                // asks in a loop must not be able to turn that into traffic for the whole lobby.
                //
                // Its OWN timestamp, not _lastBroadcast. Sharing that one meant the peer-change
                // broadcast fired by a join suppressed the catch-up request sent by that same
                // join, moments later — so the floor swallowed precisely the case the request
                // exists for. A joiner asks once, and now that ask is answered.
                float now = Time.realtimeSinceStartup;
                if (now - _lastRequestAnswered < RequestFloorSeconds)
                {
                    if (ModConfig.VerboseLogging.Value)
                        Undertow.Log.LogInfo(
                            "ConfigSync: peer " + sender + " asked again within " +
                            RequestFloorSeconds + "s — not answered twice.");
                    return;
                }

                _lastRequestAnswered = now;
                Broadcast("peer " + sender + " asked");
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: could not answer a config request — " + ex.Message);
            }
        }

        /// <summary>
        /// Watch this machine's own config for edits. On a server that means re-publishing; on an
        /// admin's client it means pushing the one value up.
        /// </summary>
        private static void HookLocalEdits()
        {
            if (_hooked || _bound.Count == 0) return;

            ConfigFile file = ModConfig.MaxCurrentSpeed?.ConfigFile;
            if (file == null) return;

            file.SettingChanged += OnLocalSettingChanged;
            _hooked = true;
        }

        // ------------------------------------------------------------------------------------
        // Down: the server publishes
        // ------------------------------------------------------------------------------------

        private static void Broadcast(string why)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || _bound.Count == 0) return;

            var pairs = new List<KeyValuePair<string, string>>(_bound.Count);
            foreach (KeyValuePair<string, ConfigEntryBase> kv in _bound)
            {
                string value = Serialise(kv.Value);
                if (value != null) pairs.Add(new KeyValuePair<string, string>(kv.Key, value));
            }

            rpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcPublish, ConfigWire.Format(pairs));

            // Every send pushes the heartbeat out, wherever it was called from. Otherwise a join
            // or an admin edit is followed moments later by an identical heartbeat payload, and
            // the log reads as though something were retrying.
            _lastBroadcast = Time.realtimeSinceStartup;
            _nextBroadcast = _lastBroadcast + BroadcastIntervalSeconds;

            if (ModConfig.VerboseLogging.Value)
                Undertow.Log.LogInfo("ConfigSync: published (" + why + ") — " + ConfigWire.Describe(pairs));
        }

        /// <summary>
        /// A client hears the server. Also fires on the server itself, because Everybody includes
        /// the sender — which is why the first thing it does is stand down on the authority.
        /// </summary>
        private static void OnPublished(long sender, string payload)
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet == null) return;

                // The server IS the source; adopting its own broadcast would put a copy of every
                // value in the override table and make `wake status` lie about where they came from.
                if (znet.IsServer()) return;

                // Only from the server. A routed RPC aimed at Everybody is relayed by the server,
                // but any peer may ASK for that relay — so without this check one modded client
                // could hand the whole lobby a different ocean.
                ZNetPeer server = znet.GetServerPeer();
                if (server == null)
                {
                    // Mid-connect. Not a fault and not worth a warning: the request this client
                    // sends on registering, and the heartbeat behind it, both bring the payload
                    // back once there is a server to attribute it to.
                    return;
                }

                if (server.m_uid != sender)
                {
                    Undertow.Log.LogWarning(
                        "ConfigSync: ignored a config payload from peer " + sender +
                        ", which is not this server. Local values stand.");
                    return;
                }

                List<KeyValuePair<string, string>> pairs = ConfigWire.Parse(payload);
                if (pairs == null)
                {
                    LastEvent = "refused a payload this build cannot read";
                    if (!_refusalLogged)
                    {
                        _refusalLogged = true;
                        Undertow.Log.LogWarning(
                            "ConfigSync: the server's config payload is not '" + ConfigWire.Header +
                            "' — the server runs a different Undertow, and its sea may differ from " +
                            "this build's. Sailing on local values. Said once per session; " +
                            "`wake status` keeps reporting it. Update both ends to the same version.");
                    }
                    return;
                }

                Adopt(pairs);
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: could not read the server's config — " + ex.Message);
            }
        }

        private static void Adopt(List<KeyValuePair<string, string>> pairs)
        {
            var changed = new List<string>();
            int unknown = 0;

            for (int i = 0; i < pairs.Count; i++)
            {
                string address = pairs[i].Key;

                // Not on OUR list, so this server runs a build that syncs something we do not.
                // Ordinary across versions; drop the line and keep the rest.
                if (!_bound.TryGetValue(address, out ConfigEntryBase entry)) { unknown++; continue; }

                if (TryOverride(entry, address, pairs[i].Value, out string before, out string after))
                    changed.Add(ShortName(address) + " " + before + " -> " + after);
            }

            _any = _overrides.Count > 0;

            // Set whether or not anything MOVED. A server whose config matches yours exactly still
            // leaves twelve values in force here, and `wake status` reporting twelve adopted values
            // beside "no config has arrived from a server" would be a straight contradiction — the
            // sort of thing that costs an hour when somebody is trying to work out why the sea
            // looks wrong.
            if (_any) LastEvent = "the server set " + _overrides.Count + " value(s)";

            if (changed.Count > 0)
                Undertow.Log.LogInfo(
                    "ConfigSync: the server's sea is in force — " + string.Join(", ", changed.ToArray()) + ".");

            if (unknown > 0 && ModConfig.VerboseLogging.Value)
                Undertow.Log.LogInfo(
                    "ConfigSync: ignored " + unknown + " value(s) this build does not know. " +
                    "That is the server running a different Undertow, and is harmless.");
        }

        /// <summary>
        /// Park one value in the override table, CLAMPED to what this build allows.
        ///
        /// Clamping against the local entry's own AcceptableValueRange rather than trusting the
        /// wire is the safety property that makes "server wins" safe to say: a server can move a
        /// player anywhere inside the range this build ships, and nowhere outside it. A typo in a
        /// server's config file — or a hostile one — can therefore make the sea faster, never
        /// physically impossible.
        /// </summary>
        private static bool TryOverride(ConfigEntryBase entry, string address, string text,
                                        out string before, out string after)
        {
            before = null;
            after = null;

            object incoming;
            if (entry is ConfigEntry<float> f)
            {
                if (!ConfigWire.TryReadFloat(text, out float v)) return Reject(address, text);
                before = ConfigWire.Write(f.Live());
                incoming = Clamp(entry, v);
                after = ConfigWire.Write((float)incoming);

                // SAY SO when the clamp moved it. The wire carries no range, so the clamp is
                // always the LOCAL build's — which is the safety property, and also means a newer
                // server whose range is wider gets silently substituted here. Without this the
                // diff line reads "1.2 -> 3" whether the server said 3 or said 9, and the player
                // and the server owner both believe they agree about the sea when they do not.
                if (Math.Abs(v - (float)incoming) > 1e-6f)
                    Undertow.Log.LogWarning(
                        "ConfigSync: the server set " + ShortName(address) + " to " +
                        ConfigWire.Write(v) + ", which is outside what this build allows — using " +
                        after + " instead. The sea here is NOT what that server intended; the two " +
                        "ends are probably running different versions of Undertow.");
            }
            else if (entry is ConfigEntry<bool> b)
            {
                if (!ConfigWire.TryReadBool(text, out bool v)) return Reject(address, text);
                before = ConfigWire.Write(b.Live());
                incoming = v;
                after = ConfigWire.Write(v);
            }
            else
            {
                // Unreachable while SyncedKeys is floats and bools, and a loud failure the day it
                // is not — rather than a value silently left alone.
                Undertow.Log.LogWarning(
                    "ConfigSync: '" + address + "' is a " + entry.SettingType.Name +
                    ", which this build cannot carry. Local value stands.");
                return false;
            }

            _overrides[address] = incoming;
            return !string.Equals(before, after, StringComparison.Ordinal);
        }

        private static bool Reject(string address, string text)
        {
            Undertow.Log.LogWarning(
                "ConfigSync: could not read '" + text + "' for " + address + ". Local value stands.");
            return false;
        }

        private static object Clamp(ConfigEntryBase entry, object value)
        {
            AcceptableValueBase range = entry.Description?.AcceptableValues;
            return range == null ? value : range.Clamp(value);
        }

        // ------------------------------------------------------------------------------------
        // Up: an admin pushes
        // ------------------------------------------------------------------------------------

        private static void OnLocalSettingChanged(object _, SettingChangedEventArgs args)
        {
            try
            {
                if (_applyingPush) return;

                ConfigEntryBase entry = args?.ChangedSetting;
                if (entry?.Definition == null) return;
                if (!ConfigWire.IsSynced(entry.Definition.Section, entry.Definition.Key)) return;

                ZNet znet = ZNet.instance;
                if (znet == null) return;

                string address = ConfigWire.Address(entry.Definition.Section, entry.Definition.Key);
                string value = Serialise(entry);
                if (value == null) return;

                if (znet.IsServer())
                {
                    // The server's own file changed — everybody should hear it now rather than in
                    // up to thirty seconds.
                    Broadcast("config edited on the server");
                    return;
                }

                // NO CLIENT-SIDE ADMIN GATE, and removing it is a FIX rather than a loosening.
                //
                // This used to refuse the push unless `ZNet.LocalPlayerIsAdminOrHost()` said yes,
                // documented as a known limit on the grounds that it only matches the full
                // "Steam_7656…" form. MEASURED 2026-09-19 and it is worse than that: the owner is
                // in adminlist.txt in all three forms (bare, "V_" and "Steam_"), and the gate
                // still refused them, so the admin push could not be used at all. The likeliest
                // cause is that the server was started with -crossplay, which gives the local user
                // a PLAYFAB identity (the handshake logged `playfab/8BF4F5368AF43770`) while the
                // admin list holds Steam ids. Vanilla's own server-side check copes with that;
                // PlayerIsAdmin does not, because in vanilla it only drives cosmetic UI hints and
                // a false negative there costs nothing.
                //
                // The client was never the right place to decide this anyway. The SERVER holds the
                // admin list, and `ZNet.IsAdmin(hostName)` is vanilla's own authoritative answer,
                // handling every id form and every socket type. So: send optimistically, let the
                // server judge, and have it ACKNOWLEDGE either way — see RpcAck. That removes the
                // false negative, keeps the security decision on the authority where it belongs,
                // and needs no reference to Splatform.dll.
                //
                // The cost is one small RPC when a non-admin edits one of the twelve synced keys
                // by hand, which is a human-speed event, and they get told why nothing happened
                // instead of watching a silent no-op.
                ZRoutedRpc rpc = ZRoutedRpc.instance;
                if (rpc == null) return;

                var one = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(address, value) };
                rpc.InvokeRoutedRPC(RpcPush, ConfigWire.Format(one));

                // "sent", not "set". Whether it took is the server's to say, and it will.
                Undertow.Log.LogInfo(
                    "ConfigSync: sent " + ShortName(address) + "=" + value +
                    " to the server; waiting for it to say whether that was allowed.");
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: could not push a config change — " + ex.Message);
            }
        }

        /// <summary>
        /// The server hears an admin. This is the only place anything reaches a real config entry
        /// because of the network, and it is the server's own file — which is the point: an admin
        /// change should still be there after a restart.
        /// </summary>
        private static void OnPushed(long sender, string payload)
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet == null || !znet.IsServer()) return;

                if (!SenderIsAdmin(znet, sender))
                {
                    Undertow.Log.LogWarning(
                        "ConfigSync: refused a config push from peer " + sender + " — not an admin.");
                    Acknowledge(sender,
                        "the server refused it — you are not an admin on this server. Your own " +
                        "config file keeps the value; the sea does not.");
                    return;
                }

                List<KeyValuePair<string, string>> pairs = ConfigWire.Parse(payload);
                if (pairs == null || pairs.Count == 0)
                {
                    // Say so. The pushing client already logged "pushed X to the server" and gets
                    // no acknowledgement back, so a bare return here means an admin watches their
                    // own success message and nothing happens, with both logs silent about why.
                    // OnPublished's mirror of this branch has always named the failure; this one
                    // did not, and the admin push is exactly where somebody meets it.
                    Undertow.Log.LogWarning(
                        "ConfigSync: a config push arrived that this build cannot read — " +
                        (pairs == null
                            ? "it is not '" + ConfigWire.Header + "'."
                            : "it carried no usable values.") +
                        " Nothing was changed. Check that both ends run the same Undertow.");
                    return;
                }

                bool any = false;
                _applyingPush = true;
                try
                {
                    for (int i = 0; i < pairs.Count; i++)
                    {
                        if (!_bound.TryGetValue(pairs[i].Key, out ConfigEntryBase entry))
                        {
                            Undertow.Log.LogWarning(
                                "ConfigSync: an admin pushed '" + pairs[i].Key + "', which this build " +
                                "does not bind. Ignored.");
                            continue;
                        }

                        string was = entry.GetSerializedValue();
                        entry.SetSerializedValue(pairs[i].Value);
                        string now = entry.GetSerializedValue();

                        // SetSerializedValue SWALLOWS a value it cannot parse — its whole body is a
                        // try/catch that logs a BepInEx warning and leaves the entry untouched (read
                        // out of libs\BepInEx.dll 2026-09-18). So the only way to know whether this
                        // worked is to read it back, exactly as the migration's backfill does.
                        if (string.Equals(now, was, StringComparison.Ordinal) &&
                            !string.Equals(pairs[i].Value, was, StringComparison.Ordinal))
                        {
                            Undertow.Log.LogWarning(
                                "ConfigSync: '" + pairs[i].Value + "' did not take on " +
                                ShortName(pairs[i].Key) + " — it is still " + was +
                                ". Out of range, or not a " + entry.SettingType.Name + ".");
                            continue;
                        }

                        any = true;
                        Undertow.Log.LogInfo(
                            "ConfigSync: admin (peer " + sender + ") set " + ShortName(pairs[i].Key) +
                            " " + was + " -> " + now + ".");
                    }
                }
                finally
                {
                    _applyingPush = false;
                }

                // Re-publish so every other client sails the new sea at once. The push itself only
                // reached the server.
                if (any)
                {
                    Broadcast("an admin changed a value");
                    Acknowledge(sender, "the server accepted it and told every client.");
                }
                else
                {
                    Acknowledge(sender,
                        "the server read it but nothing changed — the value was already set, or " +
                        "it would not take. See the server log.");
                }
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: could not apply a config push — " + ex.Message);
            }
        }

        /// <summary>
        /// Is this peer an admin? Vanilla's own answer, asked of vanilla.
        ///
        /// **`ZNet.IsAdmin(string hostName)` IS PUBLIC** in the shipping assembly — a one-line
        /// forwarder to the private `ListContainsId(m_adminList, hostName)` (read out of the REAL
        /// assembly_valheim.dll, line 3078, 2026-09-19). An earlier version of this method
        /// reimplemented that comparison from the decompiled body, on a comment asserting the
        /// check was private and unreachable under house rule 5. `ListContainsId` is private;
        /// `IsAdmin` is not, and it is the whole answer. The reimplementation was both unnecessary
        /// and WRONG, in a way no single-machine test could show:
        ///
        ///   - `ISocket.GetHostName()` returns a DIFFERENT SHAPE per socket type.
        ///     `ZSteamSocket` returns `m_peerID.GetSteamID().ToString()` — bare numeric, NO
        ///     underscore — while `ZPlayFabSocket` returns the prefixed `Steam_…` form. So the
        ///     old underscore-strip was dead code on a direct Steam connection and live only
        ///     under crossplay, which is an accident of how the server was started.
        ///   - Vanilla also accepts a THIRD form. `ListContainsId` runs the id through
        ///     `PlatformUserID.FilterPlatformUserID`, which maps Steam to the display prefix "V",
        ///     so `V_76561198…` is admin to vanilla and was unreachable here.
        ///
        /// Calling `IsAdmin` also keeps this correct for free the day Valheim adds a platform.
        ///
        /// SERVER ONLY. `m_adminList` is constructed exclusively inside `if (m_isServer)`
        /// (ZNet lines 356-367), so it is null on a client and `IsAdmin` would throw there. The
        /// only caller checks `IsServer()` first, and must keep doing so.
        ///
        /// Deny by default: no peer, no socket or no host name all mean no.
        /// </summary>
        private static bool SenderIsAdmin(ZNet znet, long sender)
        {
            string id = znet.GetPeer(sender)?.m_socket?.GetHostName();
            return !string.IsNullOrEmpty(id) && znet.IsAdmin(id);
        }

        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Tell ONE peer what became of its push. Server side only; best effort.
        ///
        /// Routed to that peer alone rather than Everybody, because it is an answer to a question
        /// nobody else asked.
        /// </summary>
        private static void Acknowledge(long peer, string what)
        {
            try
            {
                ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcAck, what ?? "");
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: could not acknowledge peer " + peer + " — " + ex.Message);
            }
        }

        /// <summary>
        /// The server's answer to something this client pushed.
        ///
        /// Only from the server, for the same reason OnPublished checks: any peer may ask for a
        /// relay, so without this one modded client could tell another that its change was
        /// accepted when it was not. The payload is a sentence written by the server's own build
        /// and is logged as text; it is never parsed and never drives behaviour.
        /// </summary>
        private static void OnAcknowledged(long sender, string what)
        {
            try
            {
                ZNet znet = ZNet.instance;
                if (znet == null || znet.IsServer()) return;

                ZNetPeer server = znet.GetServerPeer();
                if (server == null || server.m_uid != sender) return;

                LastEvent = "the server answered a pushed value";
                Undertow.Log.LogInfo("ConfigSync: " + what);
            }
            catch (Exception ex)
            {
                Undertow.Log.LogWarning("ConfigSync: could not read the server's answer — " + ex.Message);
            }
        }

        private static string Serialise(ConfigEntryBase entry)
        {
            if (entry is ConfigEntry<float> f) return ConfigWire.Write(f.Value);
            if (entry is ConfigEntry<bool> b) return ConfigWire.Write(b.Value);
            return null;
        }

        private static string ShortName(string address)
        {
            int bar = address.IndexOf(ConfigWire.SectionSeparator);
            return bar >= 0 ? address.Substring(bar + 1) : address;
        }
    }
}
