using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RavenIron.Undertow.Core
{
    /// <summary>
    /// What the server tells a client about the sea, and how that is written down.
    ///
    /// PURE, and the same split as the config migration for the same reason:
    /// <see cref="ConfigLedger"/> holds the decisions and <c>Config/ConfigMigration</c> the file
    /// handling. Here the decisions are WHICH keys travel and WHAT the payload looks like, both
    /// of which are string work with no Unity, no BepInEx, no ZNet and no clock in them — so the
    /// harness compiles this file and exercises every rule below, including the ones that only
    /// matter when one version of this mod meets a different one across a wire.
    ///
    /// THE PROBLEM THIS SOLVES. <see cref="CurrentField"/> is a pure function of seed, position,
    /// world time and season, which is why the sea needs no persistence and no sync: every
    /// machine computes the same water from facts they all already have. That argument holds only
    /// while the TUNING is also the same. A server that raised MaxCurrentSpeed and a client that
    /// did not are computing two different oceans from one seed, and because drift is applied by
    /// the peer that OWNS each hull, those two players genuinely sail different seas. Nothing
    /// desyncs, nothing errors, and nobody can tell — which is the worst shape a disagreement can
    /// take. So the server publishes its gameplay dials and a client sails by those.
    ///
    /// WHAT DOES NOT TRAVEL, and why the list below is short. A key belongs here only when a
    /// client READS it and a disagreement changes the water. Everything else is the player's own
    /// machine: the drift lines are cosmetic and client-only, the tick budget and the refresh
    /// cadence are performance dials, verbose logging is a debugging choice, and the flotsam keys
    /// drive an ambient system that only ever runs on the authority. Sending those would be a
    /// server owner reaching into a player's frame rate, which is not what this is for.
    /// </summary>
    public static class ConfigWire
    {
        /// <summary>
        /// Stamped at the head of every payload, and a mismatch is refused WHOLESALE.
        ///
        /// Bump this when the payload's SHAPE changes — never when a key joins or leaves, those
        /// are handled per line by <see cref="Parse"/>, which is what lets two builds agree about
        /// the keys they both know — AND when the FIELD MATHS changes (owner's rule, 2026-09-21).
        /// The header is the one thing both ends compare before trusting each other's sea, and
        /// the sync's whole argument is "same constants into the same pure function"; a build
        /// whose function differs must not adopt the other's constants and believe it agrees. A
        /// mismatch is refused wholesale and logged, and the client sails its own tuning.
        ///
        /// /1 → /2: the coastal term's onshore push now points toward land on the ebb as well as
        /// the flood (it used to reverse with the stream), so a /1 build computes different
        /// coastal water for half of every tide.
        /// </summary>
        public const string Header = "undertow-cfg/2";

        /// <summary>Separates a key's section from its name on the wire: "section|key=value".</summary>
        public const char SectionSeparator = '|';

        /// <summary>
        /// Every key the server is allowed to speak for, addressed as the config file addresses it.
        ///
        /// Case-SENSITIVE and ordinal throughout, matching BepInEx's own ConfigDefinition.Equals
        /// (read out of libs\BepInEx.dll 2026-09-18) — a mis-cased row here would name a key
        /// nobody can ever set, and it would fail silently.
        ///
        /// The five field terms, the bridge switch and the two force groups. Read the class
        /// comment for what is deliberately absent.
        /// </summary>
        public static readonly string[] SyncedKeys =
        {
            // The field itself. Every one of these feeds SeaContext.BuildSettings, so a
            // disagreement means two machines computing different water from the same seed.
            "3 - The current" + SectionSeparator + "MaxCurrentSpeed",
            "3 - The current" + SectionSeparator + "TidePeriodSeconds",
            "3 - The current" + SectionSeparator + "TideAmplitude",
            "3 - The current" + SectionSeparator + "CoastalStrength",
            "3 - The current" + SectionSeparator + "StormSurgeMultiplier",

            // Whether Ragnarok's Wrath is consulted at all. It gates BOTH the season the field is
            // evaluated for and the storm surge, so it is a field term in disguise.
            "2 - Systems" + SectionSeparator + "EnableWrathBridge",

            // The force on a hull. Applied by whoever owns the boat, i.e. usually a client, so
            // without this one crew's longship answers the sea differently from another's.
            "2 - Systems" + SectionSeparator + "EnableDrift",
            "4 - Drift" + SectionSeparator + "DriftStrength",
            "4 - Drift" + SectionSeparator + "UnattendedDriftFactor",

            // The same argument for a body in the water.
            "2 - Systems" + SectionSeparator + "EnableSwimmers",
            "6 - Swimmers" + SectionSeparator + "SwimmerDriftFactor",
            "6 - Swimmers" + SectionSeparator + "SwimmerMaxShareOfSwimSpeed",
        };

        /// <summary>The wire address of a key: "section|key".</summary>
        public static string Address(string section, string key)
            => (section ?? "") + SectionSeparator + (key ?? "");

        /// <summary>
        /// Whether this key is one the server speaks for. Ordinal, for the reason on
        /// <see cref="SyncedKeys"/>.
        /// </summary>
        public static bool IsSynced(string section, string key)
        {
            string address = Address(section, key);
            for (int i = 0; i < SyncedKeys.Length; i++)
                if (string.Equals(SyncedKeys[i], address, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// A float on the wire. Round-trip format, invariant culture.
        ///
        /// Invariant is not a preference here. BepInEx's own float converter is
        /// <c>ToString(NumberFormatInfo.InvariantInfo)</c> and <c>float.Parse(str,
        /// NumberFormatInfo.InvariantInfo)</c> (read out of libs\BepInEx.dll 2026-09-19), so a
        /// server owner on a comma-decimal locale already has a config file full of dots. Writing
        /// the wire any other way would make the two disagree on that owner's machine alone — and
        /// SetSerializedValue SWALLOWS a value it cannot parse, so the disagreement would be a
        /// silent no-op rather than an error anybody could see.
        /// </summary>
        public static string Write(float value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>A bool on the wire, lower case — the spelling BepInEx writes to the file.</summary>
        public static string Write(bool value) => value ? "true" : "false";

        public static bool TryReadFloat(string text, out float value)
            => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        public static bool TryReadBool(string text, out bool value)
        {
            value = false;
            if (text == null) return false;
            string t = text.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
            return false;
        }

        /// <summary>
        /// Build a payload. Header first, then one key per line.
        ///
        /// No escaping, and that is a property of the key list rather than an oversight: every
        /// synced key is a float or a bool, so no value can contain a newline or an '='.
        /// <see cref="Parse"/> refuses anything that does anyway, so if a string key is ever added
        /// to <see cref="SyncedKeys"/> the result is a dropped line, not a mangled value silently
        /// applied to somebody's sea.
        /// </summary>
        public static string Format(IList<KeyValuePair<string, string>> pairs)
        {
            var sb = new StringBuilder();
            sb.Append(Header);
            if (pairs == null) return sb.ToString();

            for (int i = 0; i < pairs.Count; i++)
            {
                string address = pairs[i].Key;
                string value = pairs[i].Value;
                if (string.IsNullOrEmpty(address) || value == null) continue;
                sb.Append('\n').Append(address).Append('=').Append(value);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Read a payload back. Returns null if the HEADER does not match, and skips any single
        /// line it cannot use.
        ///
        /// The asymmetry is deliberate and is the forward-compatibility rule. A key this build
        /// does not know means the other end is a different version of this mod, which is ordinary
        /// and must not cost the payload's other eleven values — so an unknown line is dropped and
        /// the rest applied. A wrong header means the payload is not shaped the way this code
        /// reads it at all, so there is nothing to salvage and guessing would be worse than
        /// sailing on local values.
        ///
        /// Note what this does NOT do: check the address against <see cref="SyncedKeys"/>. That
        /// belongs to the caller, because the two directions need it at different moments — see
        /// ConfigSync.
        /// </summary>
        public static List<KeyValuePair<string, string>> Parse(string payload)
        {
            if (payload == null) return null;

            string[] lines = payload.Split('\n');
            if (lines.Length == 0) return null;
            if (!string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal)) return null;

            var result = new List<KeyValuePair<string, string>>();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0 || eq == line.Length - 1) continue;

                string address = line.Substring(0, eq);
                string value = line.Substring(eq + 1);

                // An address must name a section AND a key, or it addresses nothing.
                int bar = address.IndexOf(SectionSeparator);
                if (bar <= 0 || bar == address.Length - 1) continue;

                result.Add(new KeyValuePair<string, string>(address, value));
            }
            return result;
        }

        /// <summary>
        /// Describe a payload for a log line: how many values, and the first few by name.
        ///
        /// A sync nobody can read is a sync nobody can debug, and this mod's whole discipline is
        /// that a silent success and a silent no-op look identical from outside the game. Kept
        /// here rather than in the engine so the harness can pin the wording.
        /// </summary>
        public static string Describe(IList<KeyValuePair<string, string>> pairs)
        {
            if (pairs == null || pairs.Count == 0) return "nothing";

            var sb = new StringBuilder();
            sb.Append(pairs.Count.ToString(CultureInfo.InvariantCulture)).Append(" value(s): ");

            int shown = pairs.Count < 3 ? pairs.Count : 3;
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                string address = pairs[i].Key ?? "";
                int bar = address.IndexOf(SectionSeparator);
                sb.Append(bar >= 0 ? address.Substring(bar + 1) : address);
                sb.Append('=').Append(pairs[i].Value);
            }
            if (pairs.Count > shown)
                sb.Append(" and ").Append((pairs.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more");

            return sb.ToString();
        }
    }
}
