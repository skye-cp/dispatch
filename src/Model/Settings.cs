using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Dispatch.Model
{
    /// <summary>Remembered filters and window placement, kept in the user's own app-data folder.</summary>
    internal sealed class Settings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dispatch", "settings.ini");

        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    s.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, Serialize(), new UTF8Encoding(false));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public void Parse(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).TrimEnd('\r');
                }
            }
        }

        public string Serialize() =>
            string.Join("\n", values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\n";

        public string Get(string key, string fallback = "") => values.TryGetValue(key, out var v) ? v : fallback;

        public void Set(string key, string value) => values[key] = (value ?? "").Replace("\n", " ").Replace("\r", " ");

        public IEnumerable<string> GetList(string key) =>
            Get(key).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0);

        public void SetList(string key, IEnumerable<string> items) =>
            Set(key, string.Join(",", items.Where(i => i.IndexOf(',') < 0)));

        /// <summary>Lists of records whose fields may hold any text (host names), escaped so separators can't clash.</summary>
        public IEnumerable<string[]> GetRecords(string key) =>
            GetList(key).Select(r => r.Split('|').Select(Uri.UnescapeDataString).ToArray());

        public void SetRecords(string key, IEnumerable<string[]> records) =>
            Set(key, string.Join(",", records.Select(r => string.Join("|", r.Select(f => Uri.EscapeDataString(f ?? ""))))));

        public double GetDouble(string key, double fallback) =>
            double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

        public void SetDouble(string key, double value) => Set(key, value.ToString("0.##", CultureInfo.InvariantCulture));
    }
}
