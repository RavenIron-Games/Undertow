using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using RavenIron.Undertow.Core;

namespace RavenIron.Undertow.Config
{
    /// <summary>
    /// The engine-facing half of the config migration; the DECISIONS are
    /// <see cref="ConfigLedger"/> (pure, off-game, under test). Ported 2026-09-18 from Ragnarok's
    /// Wrath and FireFront, which both took the shape that day from Valkyrie's Cargo, itself from
    /// Wu'barrk's WingsoftheValkyrie: snapshot the raw file BEFORE any bind, back it up beside
    /// itself, apply after every bind, stamp a version, and never let a failed migration stop the
    /// mod loading.
    ///
    /// <see cref="Begin"/> runs before the first <c>cfg.Bind</c>; <see cref="Finish"/> after the
    /// last. THE ORDER IS THE WHOLE MECHANISM, not a convention: a backfill acts on a key being
    /// ABSENT, and the moment BepInEx binds that key it is present at its shipped default.
    /// Snapshot after binding and every backfill silently becomes a no-op — the migration would
    /// run, log happily, stamp its version, and change nothing, forever, because the next boot
    /// reads a current file and never retries.
    ///
    /// HOUSE RULE 5 DOES NOT BITE HERE, and nobody should reach for reflection out of caution.
    /// The rule is about PUBLICIZED reference assemblies, where the compile-time surface is wider
    /// than the runtime one. <c>libs\BepInEx.dll</c> is copied verbatim from the game's
    /// <c>BepInEx\core</c> by fetch-libs and is not publicized, so what compiles is what runs.
    /// Every member below was read out of it with ilspycmd on 2026-09-18 and is public:
    /// <c>ConfigFile.ConfigFilePath</c>, <c>ContainsKey</c>, the <c>ConfigDefinition</c> indexer,
    /// <c>Remove</c>, <c>Save</c>, <c>Bind&lt;T&gt;</c>, and on <c>ConfigEntryBase</c>
    /// <c>BoxedValue</c>, <c>DefaultValue</c>, <c>GetSerializedValue</c> and
    /// <c>SetSerializedValue</c>. The one exception is <c>ConfigFile.OrphanedEntries</c>, which
    /// really is PRIVATE — it is never named here, and must not be; see
    /// <see cref="ConsumeRetiredKey"/> for the public route to the same effect.
    /// </summary>
    public static class ConfigMigration
    {
        /// <summary>
        /// What <see cref="Begin"/> decided, so <see cref="Finish"/> can tell "nothing to do" from
        /// "tried and could not". Stamping a version on a state the mod never reached makes the
        /// failure PERMANENT — the next boot sees a current file and never retries — so only
        /// <see cref="Failed"/> withholds the stamp outright.
        ///
        /// Fresh = no file existed to migrate (a first install, where every shipped default is
        /// exactly right). AlreadyCurrent = the file's own stamp already meets
        /// <see cref="ConfigLedger.CurrentVersion"/>. Planned = a plan was built. Failed = the
        /// <c>try</c> threw before a plan existed — on Windows, a sharing violation on the cfg at
        /// boot is the realistic one.
        /// </summary>
        private enum MigrationState { Fresh, AlreadyCurrent, Planned, Failed }

        private static Dictionary<string, string> _snapshot;
        private static ConfigLedger.MigrationPlan _plan;
        private static string _path;
        private static MigrationState _state = MigrationState.Fresh;

        /// <summary>
        /// Whether <see cref="Backup"/> landed a copy — or found an identical one already there —
        /// for THIS boot. Only consulted when the plan is destructive; see
        /// <see cref="ConfigLedger.MigrationPlan.IsDestructive"/> for why that is a property of
        /// the plan rather than a house opinion.
        /// </summary>
        private static bool _backedUp;

        /// <summary>The last migration's boot line, read back by `wake status`. Empty when nothing has run.</summary>
        public static string LastSummary { get; private set; } = "";

        /// <summary>
        /// How many steps <see cref="Apply"/> REFUSED this boot. A refusal is almost always a bug
        /// in our own ledger rather than in the user's file — a row naming a key this build does
        /// not bind, or retiring one it still does — plus the one that is nobody's bug, a drop that
        /// threw. Counted because <see cref="LastSummary"/> is written in <see cref="Begin"/> from
        /// the plan's INTENT, before a single step has run, and the console command reads it back
        /// verbatim. Without this it says "1 retired key dropped" for a key still sitting in the
        /// file, and the only contradiction is a warning several hundred log lines earlier.
        /// </summary>
        private static int _refused;

        /// <summary>Call BEFORE the first cfg.Bind. Never throws.</summary>
        public static void Begin(ConfigFile cfg)
        {
            Reset();
            LastSummary = "";

            try
            {
                if (cfg == null) return;

                string path = cfg.ConfigFilePath;

                // A fresh install has no file, and that is not a failure — it is the case where
                // every shipped default is exactly right and a migration would be wrong.
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

                _snapshot = ConfigLedger.ParseIni(ReadLinesShared(path));

                int fileVersion = ConfigLedger.ReadVersion(_snapshot);
                if (fileVersion >= ConfigLedger.CurrentVersion)
                {
                    _state = MigrationState.AlreadyCurrent;
                    return;
                }

                _path = path;
                _plan = ConfigLedger.Plan(_snapshot, fileVersion);
                _state = MigrationState.Planned;
                LastSummary = ConfigLedger.Describe(_plan);

                // Only a plan that CHANGES something needs the previous file kept. A stamp-only
                // boot leaves every value exactly as it was, so there is nothing to back up and a
                // .bak would be a byte-identical copy of a file nobody touched.
                _backedUp = _plan.IsEmpty || Backup(path, fileVersion);

                if (_plan.IsEmpty)
                {
                    Undertow.Log.LogInfo(LastSummary + " (stamping the layout version)");
                }
                else if (_backedUp)
                {
                    Undertow.Log.LogWarning(
                        LastSummary + " (your previous config is backed up beside it, .v" +
                        fileVersion.ToString(CultureInfo.InvariantCulture) + ".bak)");
                }
                else if (!_plan.IsDestructive)
                {
                    Undertow.Log.LogWarning(
                        LastSummary + " (no backup could be written; nothing in this plan removes " +
                        "a setting, so the migration proceeds anyway)");
                }
                else
                {
                    Undertow.Log.LogWarning(
                        LastSummary + " (no backup could be written, and this plan would overwrite " +
                        "or drop a stored value, so NOTHING is changed and the version is left " +
                        "unstamped; it retries on the next boot)");
                }
            }
            catch (Exception ex)
            {
                // Never stop the mod loading. Every value binds exactly as it always did; the file
                // is left unstamped, which is precisely what makes the next boot retry.
                _state = MigrationState.Failed;
                Undertow.Log.LogError(
                    "Config migration could not start. Every value binds as it always did and nothing " +
                    "has been changed; the config is left unstamped so this retries on the next boot. " +
                    "Reason: " + ex);
            }
        }

        /// <summary>
        /// Call AFTER the last cfg.Bind, with the bound version entry, so every entry this might
        /// touch already exists. Applies the plan, stamps the version and saves. Never throws.
        ///
        /// Both the apply and the stamp are gated on <see cref="_backedUp"/> when the plan is
        /// destructive: overwriting an admin's stored value or dropping their only copy of a key
        /// with no backup on disk destroys the one thing this machinery promises never to lose.
        /// Skipping is always safe — an unstamped file IS a pre-migration file, so the next boot's
        /// <see cref="Begin"/> retries the whole thing from scratch.
        /// </summary>
        public static void Finish(ConfigFile cfg, ConfigEntry<int> versionEntry)
            => Finish(cfg, versionEntry, _plan);

        /// <summary>
        /// The plan is a PARAMETER so the version-stamp gate can be measured. Undertow's shipped
        /// Retirements table is empty, so no step built from the real ledger can fail, so the one
        /// branch that withholds the stamp is unreachable through <see cref="Begin"/> today —
        /// exactly the reason <c>ConfigLedger.Plan</c> takes its tables. An empty table must not
        /// leave live code unmeasured until the first rung that uses it ships, because that rung
        /// is a release, on somebody else's config file. The sibling mods have a live retirement
        /// and drive this same branch through their real ledger.
        /// </summary>
        internal static void Finish(ConfigFile cfg, ConfigEntry<int> versionEntry, ConfigLedger.MigrationPlan plan)
        {
            try
            {
                bool migrationAttempted = _path != null;
                bool destructive = plan != null && plan.IsDestructive;
                bool safeToFinish = _state != MigrationState.Failed &&
                                    (!migrationAttempted || !destructive || _backedUp);

                // A step that failed for a reason that could succeed next time must WITHHOLD the
                // stamp, or the retry it deserves never happens: a stamped file takes the
                // AlreadyCurrent path on every future boot.
                bool appliedCleanly = true;
                if (plan != null && safeToFinish && cfg != null) appliedCleanly = Apply(cfg, plan);

                if (_state == MigrationState.Failed)
                {
                    Undertow.Log.LogWarning(
                        "Config migration did not run this boot; your config is unchanged and unstamped, " +
                        "and the next boot retries.");
                }

                if (versionEntry != null)
                {
                    // THE STAMP ONLY EVER GOES UP. A file carrying a HIGHER version was written by
                    // a newer build whose rungs have already run, and this build knows nothing
                    // about them — lowering it would make the next upgrade replay those rungs
                    // against values the user has since chosen, and a rebase cannot tell a
                    // deliberate choice from the old default it happens to equal. Rolling a mod
                    // back for an afternoon is ordinary; losing a setting to it is not.
                    if (safeToFinish && appliedCleanly)
                    {
                        if (versionEntry.Value < ConfigLedger.CurrentVersion)
                            versionEntry.Value = ConfigLedger.CurrentVersion;
                    }
                    else
                        Undertow.Log.LogWarning(
                            "Config migration did not finish cleanly; the layout version is left unstamped " +
                            "so the next boot retries instead of treating this one as done.");
                }

                // Not redundant, though not for the obvious reason: BepInEx's own Bind already
                // saves after each newly created entry while SaveOnConfigSet is on. What it does
                // NOT save is a value assignment that changes nothing (the setter compares first
                // and fires no event), or a `Remove` — and a retirement is exactly a Remove. So
                // this is the call that guarantees the file on disk matches what was decided.
                // LastSummary was written in Begin, from the plan's INTENT, before anything ran.
                // The console command reads it back verbatim, so a refused step has to reach it or
                // the one line the user actually looks at is confidently wrong.
                if (_refused > 0)
                    LastSummary += " — but " + _refused.ToString(CultureInfo.InvariantCulture) +
                                   " step(s) were REFUSED; see the warnings in the log";

                if (cfg != null) cfg.Save();
            }
            catch (Exception ex)
            {
                Undertow.Log.LogError(
                    "Config migration could not finish — check the backup beside your config file. Reason: " + ex);
            }
            finally
            {
                Reset();
            }
        }

        /// <summary>
        /// Apply a plan to the bound entries. Separated from <see cref="Finish"/> so the harness
        /// can drive it with a synthetic plan — every shipped ledger table is empty today, so the
        /// real boot path exercises none of these three loops, and "the first rung that does real
        /// work is the first time this code has ever run anywhere" is precisely the risk this repo
        /// refuses to take. Internal rather than public: the test project compiles the shipping
        /// source into its own assembly, so it can reach this, and nothing outside the mod can.
        /// </summary>
        /// <returns>
        /// False when a step failed for a reason that could SUCCEED NEXT TIME — today only a
        /// retirement that threw. That answer gates the version stamp, because a stamped file never
        /// migrates again and a transient file lock must not become permanent. A ledger row naming
        /// a key this build does not bind is deliberately NOT counted: it cannot succeed next time
        /// either, so withholding the stamp would re-run the migration on every boot forever.
        /// </returns>
        internal static bool Apply(ConfigFile cfg, ConfigLedger.MigrationPlan plan)
        {
            if (cfg == null || plan == null) return true;
            bool allRetriableStepsSucceeded = true;

            foreach (string slot in plan.ResetToDefault)
            {
                ConfigEntryBase entry = Lookup(cfg, slot);
                if (entry == null) { WarnUnknownSlot(slot); continue; }
                entry.BoxedValue = entry.DefaultValue;
            }

            foreach (ConfigLedger.BackfilledSlot b in plan.Backfilled)
            {
                ConfigEntryBase entry = Lookup(cfg, b.Slot);
                if (entry == null) { WarnUnknownSlot(b.Slot); continue; }
                ApplyBackfill(entry, b);
            }

            foreach (string slot in plan.Retired)
            {
                string section, key;
                if (!ConfigLedger.SplitSlot(slot, out section, out key)) continue;

                // A RETIREMENT MUST NEVER TOUCH A KEY THIS BUILD STILL BINDS. The other two loops
                // warn when a slot is unknown; this is the mirror mistake, and it is the dangerous
                // one — ConsumeRetiredKey would bind the live entry and then remove it, deleting
                // the user's value outright. Relying on Bind's cast to throw is not protection:
                // BepInEx returns the EXISTING entry for an already-bound definition, so the cast
                // only fails when the type differs, and Undertow's three string keys
                // (FlotsamCommon, FlotsamRare, FlotsamWreckage) would be deleted in silence.
                if (Lookup(cfg, slot) != null)
                {
                    _refused++;
                    Undertow.Log.LogWarning(
                        "Config migration wanted to retire " + slot + ", but this build still binds that key. " +
                        "Nothing was removed — retiring a live setting would delete your value. This is a bug " +
                        "in ConfigLedger, not in your file.");
                    continue;
                }

                if (!ConsumeRetiredKey(cfg, section, key)) allRetriableStepsSucceeded = false;
            }

            return allRetriableStepsSucceeded;
        }

        /// <summary>
        /// Drops the working state. Deliberately does NOT touch <see cref="LastSummary"/>, which
        /// has to outlive the boot that produced it — `wake status` reads it minutes later, and a
        /// migration nobody can read back is one nobody can debug. Only <see cref="Begin"/> clears
        /// it, at the start of the next attempt.
        /// </summary>
        private static void Reset()
        {
            _refused = 0;
            _snapshot = null;
            _plan = null;
            _path = null;
            _state = MigrationState.Fresh;
            _backedUp = false;
        }

        /// <summary>
        /// Write a backfill and CHECK IT LANDED, which neither sibling does and both needed to.
        ///
        /// <c>ConfigEntryBase.SetSerializedValue</c> catches every exception itself and logs a
        /// BepInEx warning, leaving the value untouched (read out of libs\BepInEx.dll, 2026-09-18)
        /// — so a try/catch around it is unreachable code, and a mistyped LegacyValue is a silent
        /// no-op followed by a confident version stamp. That is exactly the "confident,
        /// well-formed, wrong measurement" this repo's debugging discipline names as the most
        /// expensive failure available, and it would be permanent: the stamp means it never runs
        /// again. So the value is read back, and a set that moved nothing while the entry does not
        /// already hold what was asked for says so by name.
        /// </summary>
        private static void ApplyBackfill(ConfigEntryBase entry, ConfigLedger.BackfilledSlot b)
        {
            entry.SetSerializedValue(b.Value);
            string landed = SerializedOrNull(entry);

            // ASK WHETHER IT LANDED ON THE REQUESTED VALUE, not merely whether it moved. Two
            // different failures hide behind "it moved": BepInEx refuses the text and leaves the
            // entry alone, or its setter CLAMPS the parsed value into the entry's
            // AcceptableValueRange and silently stores something else. The second one moves the
            // entry, so a did-it-move test reports success for a value nobody asked for.
            if (ValuesAgree(b.Value, landed)) return;

            Undertow.Log.LogWarning(
                "Config migration set " + b.Slot + " to '" + (landed ?? "?") + "' rather than the '" + b.Value +
                "' it intended — BepInEx either would not parse that text for this setting's type or clamped it " +
                "into the setting's allowed range. This world may behave differently from before. This is a bug " +
                "in ConfigLedger, not in your file.");
        }

        /// <summary>
        /// Whether a config entry ended up holding what the ledger asked for. Numbers are compared
        /// as numbers, because a round trip legitimately reformats them ("0.50" comes back "0.5")
        /// and a text-only test would cry wolf on every float. Everything else is ordinal
        /// ignore-case, which covers a bool's "true"/"True". InvariantCulture throughout: this is
        /// text that came off a disk.
        /// </summary>
        private static bool ValuesAgree(string requested, string actual)
        {
            if (requested == null || actual == null) return false;

            double a, c;
            if (double.TryParse(requested.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a) &&
                double.TryParse(actual.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out c))
            {
                return Math.Abs(a - c) <= 1e-6 * Math.Max(1.0, Math.Abs(a));
            }

            return string.Equals(requested.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string SerializedOrNull(ConfigEntryBase entry)
        {
            try { return entry.GetSerializedValue(); }
            catch { return null; }
        }

        /// <summary>
        /// The ledger names a key this build does not bind. Only reachable by editing one file and
        /// not the other, and silence would make it permanent — the version stamps and the step
        /// never runs again.
        /// </summary>
        private static void WarnUnknownSlot(string slot)
        {
            _refused++;
            Undertow.Log.LogWarning(
                "Config migration wanted to touch " + slot + " but this build binds no such key. " +
                "Nothing was changed for it. This is a bug in ConfigLedger, not in your file.");
        }

        /// <summary>The bound entry for a "Section::Key" slot, or null when this build does not bind it.</summary>
        private static ConfigEntryBase Lookup(ConfigFile cfg, string slot)
        {
            string section, key;
            if (!ConfigLedger.SplitSlot(slot, out section, out key)) return null;

            // ConfigDefinition's constructor THROWS on null, on leading or trailing whitespace, and
            // on = \n \t \ " ' [ ] in either part — so a malformed ledger row would otherwise take
            // the whole of Finish down rather than just its own step.
            try
            {
                var def = new ConfigDefinition(section, key);
                return cfg.ContainsKey(def) ? cfg[def] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Drop a retired key through PUBLIC ConfigFile API alone. BepInEx keeps an unbound line in
        /// <c>OrphanedEntries</c> and writes it back out on every Save, so hoping it falls off does
        /// not work — but that property is private, and naming it would reproduce the
        /// <c>Terminal.commands</c> failure exactly: Mono resolves field access when a method is
        /// JIT'd, so the whole method would throw on entry and never reach its own catch.
        ///
        /// The public route has the same effect. <c>Bind</c> under a throwaway default pulls the
        /// key OUT of the orphan set (BepInEx's own Bind removes it there), and <c>Remove</c> then
        /// takes the now-bound entry out of the live set too, so neither collection carries it into
        /// the next Save.
        /// </summary>
        /// <returns>
        /// False when the drop THREW. Bind and Remove share one try block and Bind does real file
        /// I/O (BepInEx saves after each newly created entry), so a transient lock — antivirus,
        /// cloud sync, a config manager, a second process in the same directory — leaves the key
        /// bound and never removed. Reporting that upward is what stops <see cref="Finish"/>
        /// stamping the version as though the retirement had happened, which would make a
        /// one-second lock permanent.
        /// </returns>
        private static bool ConsumeRetiredKey(ConfigFile cfg, string section, string key)
        {
            try
            {
                var def = new ConfigDefinition(section, key);
                cfg.Bind(def, "");
                cfg.Remove(def);
                return true;
            }
            catch (Exception ex)
            {
                _refused++;
                Undertow.Log.LogError(
                    "Could not drop the retired key " + section + "." + key + " from the config file. " +
                    "The layout version is left unstamped so the next boot tries again. Reason: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// The raw file, line by line, opened with FileShare.ReadWrite so another process holding
        /// the cfg open for writing — a config manager, an editor, a sync tool, the realistic
        /// Windows case — does not fail the migration the way File.ReadAllLines would.
        /// </summary>
        private static List<string> ReadLinesShared(string path)
        {
            var lines = new List<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }
            return lines;
        }

        /// <summary>
        /// Copies <paramref name="path"/> beside itself as <c>.v{fromVersion}.bak</c>, never
        /// overwriting — a half-finished earlier run's backup is somebody's only clean copy, so a
        /// second attempt falls back to a timestamped name rather than clobbering it. Returns true
        /// when a copy now exists on disk for this migration (the fresh one landed, or an identical
        /// one was already there). Never throws.
        ///
        /// Internal for the same reason as <see cref="Apply"/>: every shipped ledger table is empty,
        /// so `_plan.IsEmpty` short-circuits this on every real boot and it would otherwise first
        /// execute on the day it is most needed.
        /// </summary>
        internal static bool Backup(string path, int fromVersion)
        {
            try
            {
                string stem = path + ".v" + fromVersion.ToString(CultureInfo.InvariantCulture);
                string bak = stem + ".bak";

                if (File.Exists(bak))
                {
                    if (BytesEqual(bak, path)) return true; // already backed up, byte for byte

                    // InvariantCulture on the stamp is not decoration: "yyyy" under a culture with
                    // its own calendar writes a different year (th-TH gives the Buddhist era), and
                    // this is a filename on someone else's disk.
                    bak = stem + "." +
                          DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".bak";
                }

                File.Copy(path, bak, overwrite: false);
                return true;
            }
            catch (Exception ex)
            {
                Undertow.Log.LogError(
                    "Could not back up the config before migrating (" + ex.Message + "). If the migration " +
                    "would change a stored value, nothing is changed and the version is left unstamped.");
                return false;
            }
        }

        private static bool BytesEqual(string pathA, string pathB)
        {
            try
            {
                byte[] a = File.ReadAllBytes(pathA);
                byte[] b = File.ReadAllBytes(pathB);
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i]) return false;
                return true;
            }
            catch
            {
                // Can't prove they match — treat as different, which routes to the timestamped
                // fallback rather than overwriting a copy that might be the only good one.
                return false;
            }
        }
    }
}
