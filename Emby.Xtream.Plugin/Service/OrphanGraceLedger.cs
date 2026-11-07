using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// When each orphan candidate was first seen missing from the catalogue (ADR-C007). One file
    /// per library root, under the records root, keyed by the path relative to that root.
    /// </summary>
    /// <remarks>
    /// Every failure here can only delay a deletion, never bring one forward. An unreadable or
    /// unknown-format file loads as empty, so every candidate starts its grace period again; a
    /// failed write means the next run does the same. Nothing in this class deletes anything.
    /// </remarks>
    internal static class OrphanGraceLedger
    {
        private const int FormatVersion = 1;

        private sealed class LedgerFile
        {
            public int Version { get; set; }

            /// <summary>Relative path → first-missing time, ISO 8601 UTC.</summary>
            public Dictionary<string, string> Pending { get; set; }
        }

        /// <summary>Reads the ledger. Missing, unreadable or newer-format files read as empty.</summary>
        public static Dictionary<string, DateTime> Load(string path, Action<string> warn)
        {
            var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return result;
            }

            try
            {
                var file = STJ.JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(path));
                if (file == null || file.Version != FormatVersion || file.Pending == null)
                {
                    warn?.Invoke(string.Format(
                        CultureInfo.InvariantCulture,
                        "Orphan grace ledger {0} is in an unknown format; every missing file starts its grace period again",
                        path));
                    return result;
                }

                foreach (var entry in file.Pending)
                {
                    DateTime when;
                    if (DateTime.TryParse(entry.Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
                    {
                        result[entry.Key] = when;
                    }
                }
            }
            catch (Exception ex)
            {
                warn?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    "Orphan grace ledger {0} could not be read ({1}); every missing file starts its grace period again",
                    path, ex.Message));
                result.Clear();
            }

            return result;
        }

        /// <summary>Replaces the ledger with <paramref name="pending"/>. Never throws.</summary>
        public static void Save(string path, IDictionary<string, DateTime> pending, Action<string> warn)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                var file = new LedgerFile
                {
                    Version = FormatVersion,
                    Pending = new Dictionary<string, string>(StringComparer.Ordinal),
                };
                foreach (var entry in pending)
                {
                    file.Pending[entry.Key] = entry.Value.ToUniversalTime()
                        .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                }

                // Temp file + replace, as DecisionStore does: a torn ledger would read as empty
                // and only restart the clocks, but there is no reason to risk even that.
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, STJ.JsonSerializer.Serialize(file));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (IOException)
                    {
                        // Some bind mounts refuse File.Replace. Copy over instead; the temp file
                        // stays behind and the next save overwrites it.
                        File.Copy(temp, path, true);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                warn?.Invoke(string.Format(
                    CultureInfo.InvariantCulture,
                    "Orphan grace ledger {0} could not be written ({1}); missing files will start their grace period again next run",
                    path, ex.Message));
            }
        }
    }
}
