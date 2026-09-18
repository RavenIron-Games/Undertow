// Hand-written stand-ins for the handful of BepInEx types the tested source mentions in its
// signatures. Deliberately minimal: the stub surface is almost always smaller than it looks.
// Nothing here needs to behave like BepInEx — it only needs to compile and let the real logic
// run.
//
// It grew once, for the config migration (0.7.1). That needed three things the counting stub
// could not do: remember what a file already held, hand a bound entry back by section and key,
// and reproduce BepInEx's own habit of SWALLOWING a value it cannot parse. The last one is the
// point rather than a detail — ConfigMigration checks that a backfill actually landed, and an
// assertion about that is worthless against a stub that throws where the real one shrugs.

using System;
using System.Collections.Generic;
using System.Globalization;

// ---- the plugin's logger -----------------------------------------------------------
// The real Undertow class derives from BepInEx's BaseUnityPlugin, which cannot run off-game.
// This stand-in supplies only the static Log surface ConfigMigration uses, routed to the
// console so a failing test shows the boot line it would have written.

namespace RavenIron.Undertow
{
    /// <summary>
    /// Routes the plugin's log to the console so a failing test shows the boot line it would have
    /// written — and RECORDS it, because several of this migration's guarantees ARE the log line.
    /// A backfill that BepInEx clamped still changes the entry, so "did the value move" cannot tell
    /// it from one that landed; the only difference the outside world can see is that the mod said
    /// so. An assertion about a silent failure has to be able to hear the noise.
    /// </summary>
    public class TestLog
    {
        public readonly List<string> Messages = new List<string>();

        public void LogInfo(object o)    { Record("info", o); }
        public void LogWarning(object o) { Record("warn", o); }
        public void LogError(object o)   { Record("error", o); }

        private void Record(string level, object o)
        {
            string line = $"[{level}] {o}";
            Messages.Add(line);
            Console.WriteLine($"      {line}");
        }

        public void Clear() => Messages.Clear();

        public bool Said(string fragment)
            => Messages.Exists(m => m.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static class Undertow
    {
        public static readonly TestLog Log = new TestLog();
    }
}

namespace BepInEx.Configuration
{
    public class AcceptableValueRange<T>
    {
        public readonly T MinValue, MaxValue;
        public AcceptableValueRange(T min, T max) { MinValue = min; MaxValue = max; }
    }

    public class ConfigDescription
    {
        public readonly string Description;
        public readonly object AcceptableValues;

        public ConfigDescription(string description, object acceptableValues = null)
        {
            Description = description;
            AcceptableValues = acceptableValues;
        }
    }

    /// <summary>
    /// Enough of BepInEx's ConfigDefinition to address a key. ORDINAL AND CASE-SENSITIVE, because
    /// the real one is: `Equals` is `string.Equals(Key, other.Key) && string.Equals(Section,
    /// other.Section)` — the two-argument overload — over a case-sensitive `GetHashCode` (read out
    /// of libs\BepInEx.dll 2026-09-18).
    ///
    /// This stub was written case-INSENSITIVE first, with a comment asserting that matched the real
    /// one. It did not. A mis-cased ledger row would have found its entry here and missed it in
    /// game, so every Apply assertion would have passed for a reason that does not hold on a real
    /// server — the confident, well-formed, wrong instrument this repo's discipline names.
    /// </summary>
    public class ConfigDefinition
    {
        public readonly string Section;
        public readonly string Key;

        public ConfigDefinition(string section, string key) { Section = section; Key = key; }

        public override bool Equals(object obj)
            => obj is ConfigDefinition d
               && string.Equals(d.Section, Section, StringComparison.Ordinal)
               && string.Equals(d.Key, Key, StringComparison.Ordinal);

        public override int GetHashCode()
            => ((Key ?? "").GetHashCode() * 397) ^ (Section ?? "").GetHashCode();
    }

    public abstract class ConfigEntryBase
    {
        public abstract object BoxedValue { get; set; }
        public abstract object DefaultValue { get; }
        public abstract string GetSerializedValue();
        public abstract void SetSerializedValue(string value);
    }

    public class ConfigEntry<T> : ConfigEntryBase
    {
        private readonly T _default;
        private readonly ConfigDescription _description;
        private T _value;

        /// <summary>
        /// CLAMPS on the way in, because the real one does: BepInEx's setter runs
        /// `value = ClampValue(value)` against the entry's AcceptableValueRange, and
        /// AcceptableValueRange.Clamp returns MinValue or MaxValue rather than refusing. Without
        /// this a test could not tell a backfill that landed from one that landed CLAMPED, and the
        /// assertion guarding against exactly that would pass for the wrong reason.
        /// </summary>
        public T Value
        {
            get => _value;
            set => _value = Clamp(value);
        }

        public ConfigEntry(T defaultValue, ConfigDescription description = null)
        {
            _default = defaultValue;
            _description = description;
            _value = defaultValue;
        }

        private T Clamp(T candidate)
        {
            if (_description?.AcceptableValues is AcceptableValueRange<T> range && candidate is IComparable<T> c)
            {
                if (c.CompareTo(range.MinValue) < 0) return range.MinValue;
                if (c.CompareTo(range.MaxValue) > 0) return range.MaxValue;
            }
            return candidate;
        }

        public override object BoxedValue { get => Value; set => Value = (T)value; }
        public override object DefaultValue => _default;

        /// <summary>
        /// Invariant culture, and lower-case for a bool, because that is how BepInEx's own
        /// TomlTypeConverter spells a config value. A comma-decimal machine writing "0,5" where
        /// the shipping mod writes "0.5" is exactly the locale bug the working agreement names,
        /// and here it would also make every text comparison in ConfigLedger disagree.
        /// </summary>
        public override string GetSerializedValue()
            => Value is bool b ? (b ? "true" : "false") : Convert.ToString(Value, CultureInfo.InvariantCulture);

        /// <summary>
        /// SWALLOWS a value it cannot parse and leaves the entry untouched — which is not
        /// laziness, it is what the real one does. BepInEx's ConfigEntryBase.SetSerializedValue
        /// catches every exception itself, logs its own warning and returns (read out of
        /// libs\BepInEx.dll with ilspycmd, 2026-09-18). So a mistyped ledger value is a SILENT
        /// no-op in game, and ConfigMigration.ApplyBackfill exists to catch it. A stub that threw
        /// here would let that check pass for the wrong reason.
        /// </summary>
        public override void SetSerializedValue(string value)
        {
            try { Value = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture); }
            catch { }
        }
    }

    /// <summary>
    /// Records what was bound rather than only counting it. The harness asserts on the
    /// recorded entries — a duplicate section/key pair is a real and silent bug in BepInEx
    /// (the second Bind returns the FIRST entry, so two config fields quietly share one
    /// value), and it is invisible without a record of what was asked for.
    ///
    /// Since 0.7.1 it also REMEMBERS: a stored value read from a real file on disk is applied
    /// over the shipped default, and a repeated Bind returns the entry it made the first time.
    /// Both are what the real ConfigFile does, and without them a migration that trampled a
    /// setting somebody had already chosen would pass against a stub pretending nobody ever
    /// chose one.
    /// </summary>
    public class ConfigFile
    {
        public sealed class BoundEntry
        {
            public string Section;
            public string Key;
            public object DefaultValue;
            public ConfigDescription Description;
            public string Path => Section + "/" + Key;
        }

        public readonly List<BoundEntry> Bound = new List<BoundEntry>();

        private readonly Dictionary<ConfigDefinition, ConfigEntryBase> _entries =
            new Dictionary<ConfigDefinition, ConfigEntryBase>();

        /// <summary>
        /// Values already in the file, loaded once on the first Bind. Parsed with the SHIPPING
        /// ConfigLedger.ParseIni rather than a second parser, per the working agreement — a
        /// harness that duplicates logic proves nothing and drifts. That parser cannot quietly
        /// agree with itself here, because its own tests pin it against hand-written expectations.
        /// </summary>
        private Dictionary<string, string> _stored;

        /// <summary>
        /// Slots that are IN the file and that nothing has bound — BepInEx calls these orphans,
        /// keeps them in a private dictionary, and writes every one of them back out on each Save.
        /// That is the entire reason a retirement has to do real work instead of just not binding
        /// the key: an unbound line rides along forever. Modelled here so the retirement test
        /// measures that mechanism rather than asserting a key is absent that was never present.
        /// </summary>
        private readonly HashSet<string> _orphans = new HashSet<string>(StringComparer.Ordinal);

        public int OrphanCount => _orphans.Count;
        public bool HasOrphan(string section, string key) => _orphans.Contains(section + "::" + key);

        public int BoundCount => Bound.Count;

        /// <summary>How many times Save() was called. The stamp-only boot writes nothing else, so this is the only proof it wrote at all.</summary>
        public int SaveCount { get; private set; }

        private string _configFilePath = "";

        /// <summary>
        /// Where the pre-bind snapshot is read from. Settable so a test can point it at a real
        /// temp file — and instrumented, because WHEN the migration reads it is the mechanism.
        /// A backfill acts on a key being absent, and BepInEx's own Bind makes it present at its
        /// shipped default, so a snapshot taken after any bind can never see an absence again.
        /// With every shipped ledger table empty, no assertion about a backfill's RESULT can
        /// catch that ordering slipping; the read itself is the only observable, so it is watched.
        /// </summary>
        public string ConfigFilePath
        {
            get
            {
                if (BindCountAtPathRead < 0) BindCountAtPathRead = Bound.Count;
                return _configFilePath;
            }
            set { _configFilePath = value; }
        }

        /// <summary>How many keys were already bound the first time anything asked where the file is. -1 means nothing ever asked.</summary>
        public int BindCountAtPathRead { get; private set; } = -1;

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue,
                                      ConfigDescription description = null)
            => BindCore(section, key, defaultValue, description);

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
            => BindCore(section, key, defaultValue, new ConfigDescription(description));

        public ConfigEntry<T> Bind<T>(ConfigDefinition definition, T defaultValue,
                                      ConfigDescription description = null)
            => BindCore(definition.Section, definition.Key, defaultValue, description);

        private ConfigEntry<T> BindCore<T>(string section, string key, T defaultValue,
                                           ConfigDescription description)
        {
            Bound.Add(new BoundEntry
            {
                Section = section,
                Key = key,
                DefaultValue = defaultValue,
                Description = description
            });

            if (_stored == null)
            {
                // The backing field, not the property: the instrumentation above is there to
                // measure when the MIGRATION looked at the file, and the stub's own bookkeeping
                // must not blur that reading.
                _stored = !string.IsNullOrEmpty(_configFilePath) && System.IO.File.Exists(_configFilePath)
                    ? RavenIron.Undertow.Core.ConfigLedger.ParseIni(System.IO.File.ReadAllLines(_configFilePath))
                    : new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (string slot in _stored.Keys) _orphans.Add(slot);
            }

            // Binding a key is what takes it OUT of the orphan set, in the real ConfigFile as here.
            _orphans.Remove(section + "::" + key);

            var def = new ConfigDefinition(section, key);
            if (_entries.TryGetValue(def, out ConfigEntryBase existing)) return (ConfigEntry<T>)existing;

            var entry = new ConfigEntry<T>(defaultValue, description);
            if (_stored.TryGetValue(section + "::" + key, out string raw)) entry.SetSerializedValue(raw);

            _entries[def] = entry;
            return entry;
        }

        public bool ContainsKey(ConfigDefinition def) => _entries.ContainsKey(def);
        public ConfigEntryBase this[ConfigDefinition def] => _entries[def];
        public bool Remove(ConfigDefinition def) => _entries.Remove(def);
        public void Save() { SaveCount++; }
    }
}
