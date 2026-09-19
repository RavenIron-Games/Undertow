using System;
using System.Collections.Generic;
using System.Globalization;

namespace RavenIron.Undertow.Core
{
    /// <summary>
    /// The config migration's DECISIONS, pure and off-game — no BepInEx, no Unity, no clock, so
    /// the harness compiles it and runs it in a second, exactly like <see cref="CurrentField"/>
    /// and <see cref="DriftLineMath"/>. The engine-facing half is
    /// <c>Config/ConfigMigration.cs</c>, which is the only part that touches a real file.
    ///
    /// Ported 2026-09-18 from Ragnarok's Wrath and FireFront, which both took the shape the same
    /// day from Valkyrie's Cargo, which took it from Wu'barrk's WingsoftheValkyrie (the family's,
    /// alongside TortalPortal and Fatty). Do not fork it: a reader who knows one of these should
    /// be able to read the others without relearning anything.
    ///
    /// WHY A MIGRATION AT ALL, when BepInEx already merges new keys into an existing file.
    /// Because merging is not the problem. BepInEx appends a new key at its SHIPPED default, and
    /// a shipped default is chosen for a fresh install — it is a statement about what a new world
    /// should feel like, not a promise about what an existing one already feels like. When those
    /// two differ, an owner who changed nothing gets a sea that behaves differently, with no error
    /// and nothing in the log. That is this codebase's named enemy arriving through the front door.
    ///
    /// Three shapes, and the whole file is these three plus the plumbing to read a file:
    ///
    ///   REBASE — the key is PRESENT and still holds an old shipped default. The value was never
    ///   the admin's, so it moves to the new default. Anything else stored there is the admin's
    ///   work: it is kept untouched and named in the log, so they can see the migration read it
    ///   and leave it.
    ///
    ///   BACKFILL — the key is ABSENT, and the shipped default would change how an existing world
    ///   behaves. The file gets a stated legacy value instead, chosen to reproduce exactly what
    ///   that world already did. A fresh install never sees this and gets the shipped default,
    ///   which is the point: new worlds get the new feeling, old worlds keep theirs until somebody
    ///   decides otherwise.
    ///
    ///   RETIRE — the key is PRESENT and no current build binds it. It is dropped, rather than
    ///   riding along forever as a BepInEx orphan that an owner keeps editing to no effect.
    ///
    /// ALL THREE TABLES ARE EMPTY TODAY, AND THAT IS A MEASURED FACT RATHER THAN AN OVERSIGHT.
    /// Undertow's config history is append-only across its entire life — checked on 2026-09-18
    /// against three independent evidence lines that agree: the source at all four commits that
    /// ever touched ModConfig.cs, the shipped 0.5.1 and 0.6.0 DLLs' own string tables, and nine
    /// real config files on the owner's machines spanning 0.5.1, 0.6.0 and 0.7.0. No key was ever
    /// removed, renamed or retyped; no shipped default ever moved; no section string ever changed;
    /// no AcceptableValueRange was ever narrowed, so nothing on disk can be silently clamped on
    /// upgrade. The union of section|key pairs across all nine files is exactly the set this build
    /// binds. And because CHANGELOG.md records 0.5.1 as the first release, the retire surface is
    /// CLOSED rather than merely unobserved: no stranger can hold a key this repo never published.
    ///
    /// So version 1 is the ladder, not a rung — the machinery and the stamp, shipped before the
    /// change that needs them, because the alternative is writing it under pressure on the day a
    /// default has to move. The one judgement call available was 0.7.0's `EnableDriftLines`, and
    /// it is deliberately NOT backfilled: drift lines are client-side cosmetics that touch no
    /// world state, they are double-gated off on a dedicated server anyway, they are the
    /// advertised feature of the release an owner chose to install, and turning them off by
    /// default would quietly overturn the locked "visible current — diegetic only" decision.
    ///
    /// Version numbers (backfilled — nothing before this cut ever stamped one):
    ///   0 = any unstamped file: every config Undertow has ever written, up to and including
    ///       0.7.0, whatever it carries.
    ///   1 = 0.7.1, the ladder itself. Current.
    /// </summary>
    public static class ConfigLedger
    {
        /// <summary>
        /// Where the stamp lives. NUMBERED, unlike the siblings' bare "Meta", for a reason worth
        /// keeping: BepInEx's <c>ConfigFile.Save</c> groups by section and orders by the section
        /// name, so "Meta" would sort BELOW "1 - Core" and land at the bottom of the file. (Read
        /// out of libs\BepInEx.dll on 2026-09-18; Ragnarok's Wrath states the opposite as fact in
        /// a comment and is wrong about it — harmlessly, since nothing depends on where it sits.)
        /// "0 - Meta" both sorts first and matches Undertow's own numbered sections. It is a
        /// migration slot string forever after, so it is not something to tidy later.
        /// </summary>
        public const string MetaSection = "0 - Meta";
        public const string VersionKey = "ConfigVersion";

        /// <summary>
        /// Version 1 stamped and did nothing. **Version 2 is the first rung this mod has ever
        /// had** — see <see cref="Rebases"/> — so it is also the first time the destructive path
        /// (a backup beside the file before anything moves) runs anywhere outside the harness.
        /// </summary>
        public const int CurrentVersion = 2;

        /// <summary>
        /// One slot's every old shipped default. A stored value equal to ANY of them is the mod's
        /// own, not the admin's. Compared as TEXT, never as a number — spell the default exactly
        /// as net472 writes it invariantly ("0.45", not "0.450000"; "false", not "False"), or the
        /// rung silently never matches.
        /// </summary>
        public sealed class Rebase
        {
            public string Section;
            public string Key;
            public string[] OldDefaults;
            /// <summary>Used only in the boot line, so an owner reads why their value moved.</summary>
            public string Because;
        }

        /// <summary>
        /// A key ABSENT from an existing file, given a value that preserves how that world already
        /// behaved rather than the shipped default meant for new ones. Same text rule as
        /// <see cref="Rebase.OldDefaults"/>.
        /// </summary>
        public sealed class Backfill
        {
            public string Section;
            public string Key;
            /// <summary>The legacy value, serialised exactly as the config file spells it.</summary>
            public string LegacyValue;
            public string Because;
        }

        /// <summary>A key an old build wrote that no current build binds. Present, it is dropped; absent, nothing happens.</summary>
        public sealed class Retire
        {
            public string Section;
            public string Key;
            public string Because;
        }

        /// <summary>
        /// Keyed by the version the step PRODUCES: <c>Rebases[n]</c> takes a file at n-1 up to n.
        ///
        /// **NO LONGER EMPTY, and the claim the empty version made turned out to be exactly
        /// right.** The ladder was built before any rung needed it, precisely so that the first
        /// default Undertow ever moved would be a data edit here rather than new code on the boot
        /// path. That is what version 2 is: one row, no new machinery.
        /// </summary>
        private static readonly Dictionary<int, Rebase[]> Rebases = new Dictionary<int, Rebase[]>
        {
            {
                2, new[]
                {
                    // WHY THIS IS A REBASE AND NOT A SHRUG. DriftLineMinDepth has been bound since
                    // 0.7.0, so BepInEx has written it to every existing config file at its shipped
                    // 10. BepInEx never rewrites a value already present in a file — so moving the
                    // C# default alone would ship this fix DISABLED for every existing install,
                    // with nothing in the log and no way for an owner to know. That is the exact
                    // failure this whole ledger exists for, met for the first time.
                    //
                    // "10" is MEASURED, not assumed: both Storm10's config and a pre-session backup
                    // of a real client profile read `DriftLineMinDepth = 10` literally (2026-09-19).
                    // The comparison is ordinal TEXT, so "10.0" here would be a silent no-op.
                    new Rebase
                    {
                        Section = "7 - Drift lines",
                        Key = "DriftLineMinDepth",
                        OldDefaults = new[] { "10" },
                        Because =
                            "0.7 shipped this at 10 m, which excluded every race, strait, shelf and " +
                            "coastal set — the open ocean is a flat 30 m, so the 'fast water between " +
                            "islands' the drift lines exist to show was gated out of its own habitat. " +
                            "The new floor of 2 m ends its ramp exactly where the current's own " +
                            "shallow fade ends. A value you chose yourself is kept and named."
                    }
                }
            }
        };

        /// <summary>Same keying, same reason. Empty.</summary>
        private static readonly Dictionary<int, Backfill[]> Backfills = new Dictionary<int, Backfill[]>();

        /// <summary>
        /// Same keying. Empty, and provably so: 0.5.1 was the first public release and all 27 keys
        /// it published are still bound today, so no file in the world holds a key this build does
        /// not know. A future RENAME is what fills this — and it must land in the same commit as
        /// the rename, or BepInEx keeps the old line as an orphan forever while the admin's value
        /// sits visibly in the file doing nothing.
        /// </summary>
        private static readonly Dictionary<int, Retire[]> Retirements = new Dictionary<int, Retire[]>();

        /// <summary>One slot whose stored value was NOT an old default — real admin work, kept and named.</summary>
        public struct KeptSlot
        {
            public string Slot;
            public string Value;
        }

        /// <summary>A key this migration writes because it was absent and the shipped default would have changed behaviour.</summary>
        public struct BackfilledSlot
        {
            public string Slot;
            public string Value;
            public string Because;
        }

        /// <summary>The whole plan a snapshot produces. Nothing to do plans an empty one.</summary>
        public sealed class MigrationPlan
        {
            public int FromVersion;
            public int ToVersion;
            public List<string> ResetToDefault = new List<string>();
            public List<KeptSlot> Kept = new List<KeptSlot>();
            public List<BackfilledSlot> Backfilled = new List<BackfilledSlot>();
            public List<string> Retired = new List<string>();

            /// <summary>True when the plan would change nothing on disk beyond the version stamp.</summary>
            public bool IsEmpty =>
                ResetToDefault.Count == 0 && Kept.Count == 0 && Backfilled.Count == 0 && Retired.Count == 0;

            /// <summary>
            /// True when applying this plan would LOSE something an owner could otherwise recover:
            /// a rebase overwrites a stored value, a retirement deletes a line. A backfill writes a
            /// key that was absent and so destroys nothing.
            ///
            /// This is the one place the two sibling mods disagreed, and the disagreement was about
            /// their own plans rather than about the rule: FireFront blocks the version stamp on a
            /// failed backup because its plan retires a key and the .bak is the admin's only
            /// remaining copy; Ragnarok's Wrath proceeds because its plan only backfills. Expressed
            /// as a property of the PLAN, both are right, and Undertow becomes strict the first
            /// time a destructive rung is added rather than when somebody remembers to.
            /// </summary>
            public bool IsDestructive => ResetToDefault.Count > 0 || Retired.Count > 0;
        }

        public static string Slot(string section, string key) => section + "::" + key;

        /// <summary>The inverse of <see cref="Slot"/>. False for anything that is not "Section::Key".</summary>
        public static bool SplitSlot(string slot, out string section, out string key)
        {
            section = null;
            key = null;
            if (slot == null) return false;

            int i = slot.IndexOf("::", StringComparison.Ordinal);
            if (i <= 0 || i + 2 >= slot.Length) return false;

            section = slot.Substring(0, i);
            key = slot.Substring(i + 2);
            return true;
        }

        /// <summary>
        /// BepInEx config files are plain INI: `[Section]` headers, `#` comments, blank lines, and
        /// `Key = value` where the value may itself contain `=` or `,` (Undertow's flotsam tables
        /// are comma lists, so the value is taken whole after the FIRST `=` and never split).
        /// Keyed "Section::Key"; the last duplicate wins; values trimmed. NEVER THROWS — a config
        /// this cannot read must not stop the mod loading; it must look like a fresh install,
        /// which plans nothing and changes nothing.
        ///
        /// ORDINAL, CASE-SENSITIVE, and that is not a default — it is a match to BepInEx.
        /// `ConfigDefinition.Equals` is `string.Equals(Key, other.Key) && string.Equals(Section,
        /// other.Section)`, the two-argument overload, with a case-sensitive `GetHashCode` behind
        /// it (read out of libs\BepInEx.dll 2026-09-18). So `enabledriftlines` and
        /// `EnableDriftLines` are two DIFFERENT keys to BepInEx: one binds, the other becomes an
        /// orphan. An ignore-case snapshot would answer "present" for a key BepInEx considers
        /// absent, which silently cancels the one step whose entire safety is the absence test —
        /// and the stamp would then make that permanent. This dictionary has to model BepInEx's
        /// view of the file, not a friendlier one.
        /// </summary>
        public static Dictionary<string, string> ParseIni(IEnumerable<string> lines)
        {
            var into = new Dictionary<string, string>(StringComparer.Ordinal);
            if (lines == null) return into;

            string section = "";
            foreach (string rawLine in lines)
            {
                if (rawLine == null) continue;
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                into[Slot(section, key)] = value;
            }
            return into;
        }

        /// <summary>
        /// The stamped version, or 0 when absent or unparseable — which is exactly what a
        /// pre-migration file is. InvariantCulture because this crosses a disk.
        /// </summary>
        public static int ReadVersion(Dictionary<string, string> snapshot)
        {
            string raw;
            if (snapshot != null && snapshot.TryGetValue(Slot(MetaSection, VersionKey), out raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
            {
                return version;
            }
            return 0;
        }

        /// <summary>
        /// What migrating <paramref name="snapshot"/> from <paramref name="fileVersion"/> to
        /// <see cref="CurrentVersion"/> does, against the shipped tables. An empty snapshot (a
        /// fresh install, or a file that could not be read) plans nothing, and so does a file
        /// already at or beyond the current version. NEVER THROWS.
        /// </summary>
        public static MigrationPlan Plan(Dictionary<string, string> snapshot, int fileVersion) =>
            Plan(snapshot, fileVersion, CurrentVersion, Rebases, Backfills, Retirements);

        /// <summary>
        /// The same thing against tables supplied by the caller, and the whole implementation —
        /// the overload above is one line of delegation, so this is not a parallel code path.
        ///
        /// It exists because every shipped table is empty today, and a `Plan` tested only against
        /// empty tables proves that nothing happens, which is the one thing that needs no proof.
        /// The harness passes synthetic tables through THIS method, so the matching rules are
        /// already measured on the day the first real rung lands, rather than running for the
        /// first time on somebody's server.
        /// </summary>
        public static MigrationPlan Plan(
            Dictionary<string, string> snapshot,
            int fileVersion,
            int toVersion,
            Dictionary<int, Rebase[]> rebases,
            Dictionary<int, Backfill[]> backfills,
            Dictionary<int, Retire[]> retirements)
        {
            var plan = new MigrationPlan { FromVersion = fileVersion, ToVersion = toVersion };
            if (snapshot == null || snapshot.Count == 0) return plan;
            if (fileVersion >= toVersion) return plan;

            // A NEGATIVE stamp is a hand-edited or corrupt file, and it must not become a loop
            // bound. ConfigVersion is bound without an AcceptableValueRange on purpose (see
            // ModConfig), so nothing stops an owner typing -2000000000 into it — and without this
            // the window below would run two billion times on the boot thread before the game
            // finished loading. A file that claims to predate version 0 simply IS a version 0 file.
            int from = fileVersion < 0 ? 0 : fileVersion;

            // One slot, one decision. Without this a key whose default moves in two consecutive
            // releases is judged twice against the SAME unchanged snapshot, so it can be reported
            // (and reset) once per rung, and a value already claimed by an earlier rung can be
            // re-classified as the admin's by a later one. The first rung that matches owns it.
            var decided = new HashSet<string>(StringComparer.Ordinal);

            for (int version = from + 1; version <= toVersion; version++)
            {
                Rebase[] rebaseSteps;
                if (rebases != null && rebases.TryGetValue(version, out rebaseSteps) && rebaseSteps != null)
                {
                    foreach (Rebase r in rebaseSteps)
                    {
                        if (r == null) continue;
                        string slot = Slot(r.Section, r.Key);
                        if (decided.Contains(slot)) continue;

                        string stored;
                        if (!snapshot.TryGetValue(slot, out stored)) continue;

                        decided.Add(slot);

                        bool wasOldDefault = false;
                        if (r.OldDefaults != null)
                        {
                            foreach (string oldDefault in r.OldDefaults)
                            {
                                if (string.Equals(stored.Trim(), oldDefault, StringComparison.Ordinal))
                                {
                                    wasOldDefault = true;
                                    break;
                                }
                            }
                        }

                        if (wasOldDefault) plan.ResetToDefault.Add(slot);
                        else plan.Kept.Add(new KeptSlot { Slot = slot, Value = stored });
                    }
                }

                Backfill[] backfillSteps;
                if (backfills != null && backfills.TryGetValue(version, out backfillSteps) && backfillSteps != null)
                {
                    foreach (Backfill b in backfillSteps)
                    {
                        if (b == null) continue;
                        string slot = Slot(b.Section, b.Key);
                        if (decided.Contains(slot)) continue;

                        // ABSENT ONLY, and this is the whole safety of a backfill. A key already in
                        // the file carries either the admin's choice or a value an earlier partial
                        // run wrote, and overwriting either would make this migration the thing
                        // that loses settings. The snapshot is taken BEFORE any bind for exactly
                        // this reason: once BepInEx has bound the key it is present at its shipped
                        // default and absence can no longer be observed.
                        if (snapshot.ContainsKey(slot)) continue;

                        decided.Add(slot);
                        plan.Backfilled.Add(new BackfilledSlot
                        {
                            Slot = slot,
                            Value = b.LegacyValue,
                            Because = b.Because,
                        });
                    }
                }

                Retire[] retireSteps;
                if (retirements != null && retirements.TryGetValue(version, out retireSteps) && retireSteps != null)
                {
                    foreach (Retire r in retireSteps)
                    {
                        if (r == null) continue;
                        string slot = Slot(r.Section, r.Key);
                        if (decided.Contains(slot)) continue;

                        // PRESENT ONLY. A retirement of a key that is not there is not a failure,
                        // it is the ordinary case for anyone who installed after the rename.
                        if (!snapshot.ContainsKey(slot)) continue;

                        decided.Add(slot);
                        plan.Retired.Add(slot);
                    }
                }
            }
            return plan;
        }

        /// <summary>
        /// One boot line an owner can act on: what moved, what was kept as theirs and what was
        /// dropped, each named. NEVER THROWS. InvariantCulture, because this is the same text that
        /// ends up in a log an owner pastes into a bug report.
        /// </summary>
        public static string Describe(MigrationPlan plan)
        {
            if (plan == null) return "config: nothing to migrate";

            string head = "config: version " + plan.FromVersion.ToString(CultureInfo.InvariantCulture) +
                          " -> " + plan.ToVersion.ToString(CultureInfo.InvariantCulture) + ": ";

            if (plan.IsEmpty) return head + "nothing to migrate";

            var parts = new List<string>();

            foreach (BackfilledSlot b in plan.Backfilled)
                parts.Add(Dotted(b.Slot) + " set to " + b.Value + " (" + b.Because + ")");

            if (plan.ResetToDefault.Count > 0)
                parts.Add(plan.ResetToDefault.Count.ToString(CultureInfo.InvariantCulture) +
                          " value(s) moved to their new defaults: " + Names(plan.ResetToDefault));

            if (plan.Kept.Count > 0)
            {
                var kept = new List<string>();
                foreach (KeptSlot k in plan.Kept) kept.Add(Dotted(k.Slot) + "=" + k.Value);
                parts.Add(kept.Count.ToString(CultureInfo.InvariantCulture) +
                          " kept as yours: " + string.Join(", ", kept.ToArray()));
            }

            if (plan.Retired.Count > 0)
                parts.Add(plan.Retired.Count.ToString(CultureInfo.InvariantCulture) +
                          " retired key(s) dropped: " + Names(plan.Retired));

            return head + string.Join("; ", parts.ToArray());
        }

        private static string Names(List<string> slots)
        {
            var names = new List<string>();
            foreach (string s in slots) names.Add(Dotted(s));
            return string.Join(", ", names.ToArray());
        }

        private static string Dotted(string slot) => slot == null ? "" : slot.Replace("::", ".");
    }
}
