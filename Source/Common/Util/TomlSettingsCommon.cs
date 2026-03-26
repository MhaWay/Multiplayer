using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Multiplayer.Common.Util
{
    /// <summary>
    /// Manual TOML reader/writer for ServerSettings.
    /// Does NOT use Tomlyn to avoid netstandard 2.1 dependency issues on .NET Framework 4.8.
    /// Only supports flat key-value pairs (string, int, float, bool, enum).
    /// </summary>
    public static class TomlSettingsCommon
    {
        public static ServerSettings Load(string filename)
        {
            var scribe = new SimpleTomlScribe();
            scribe.ParseFile(filename);
            scribe.mode = SimpleTomlMode.Loading;

            ScribeLike.provider = scribe;

            var settings = new ServerSettings();
            settings.ExposeData();

            return settings;
        }

        public static void Save(ServerSettings settings, string filename)
        {
            var scribe = new SimpleTomlScribe { mode = SimpleTomlMode.Saving };
            ScribeLike.provider = scribe;

            settings.ExposeData();

            File.WriteAllText(filename, scribe.ToToml());
        }
    }

    internal enum SimpleTomlMode
    {
        Loading, Saving
    }

    internal class SimpleTomlScribe : ScribeLike.Provider
    {
        private readonly Dictionary<string, string> data = new Dictionary<string, string>();
        private readonly List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
        public SimpleTomlMode mode;

        public void ParseFile(string filename)
        {
            foreach (var line in File.ReadAllLines(filename))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    continue;

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx < 0)
                    continue;

                var key = trimmed.Substring(0, eqIdx).Trim();
                var val = trimmed.Substring(eqIdx + 1).Trim();
                data[key] = val;
            }
        }

        public override void Look<T>(ref T value, string label, T defaultValue, bool forceSave)
        {
            if (mode == SimpleTomlMode.Loading)
            {
                if (data.TryGetValue(label, out var raw))
                    value = ParseValue<T>(raw);
                else
                    value = defaultValue;
            }
            else
            {
                entries.Add(new KeyValuePair<string, string>(label, FormatValue(value)));
            }
        }

        private static T ParseValue<T>(string raw)
        {
            var type = typeof(T);

            if (type == typeof(string))
                return (T)(object)Unquote(raw);

            if (type == typeof(bool))
                return (T)(object)(raw.Equals("true", StringComparison.OrdinalIgnoreCase));

            if (type == typeof(int))
                return (T)(object)int.Parse(raw, CultureInfo.InvariantCulture);

            if (type == typeof(float))
                return (T)(object)float.Parse(raw, CultureInfo.InvariantCulture);

            if (type == typeof(double))
                return (T)(object)double.Parse(raw, CultureInfo.InvariantCulture);

            if (type == typeof(long))
                return (T)(object)long.Parse(raw, CultureInfo.InvariantCulture);

            if (type.IsEnum)
                return (T)Enum.Parse(type, Unquote(raw));

            return (T)Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
            {
                s = s.Substring(1, s.Length - 2);
                s = s.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            return s;
        }

        private static string FormatValue<T>(T value)
        {
            if (value == null) return "\"\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString(CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (value is long l) return l.ToString(CultureInfo.InvariantCulture);
            if (typeof(T).IsEnum) return Quote(value.ToString());
            if (value is string s) return Quote(s);
            return Quote(value.ToString());
        }

        private static string Quote(string s)
        {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public string ToToml()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
                sb.AppendLine(entries[i].Key + " = " + entries[i].Value);
            return sb.ToString();
        }
    }
}
