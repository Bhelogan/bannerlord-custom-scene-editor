using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Remembers which prefab the game died on, and refuses to build that one again.
    ///
    /// Some prefabs fault inside the engine when instantiated outside the mission they were authored
    /// for. That is a native access violation, not a managed exception: it cannot be caught, there is
    /// no crash report to send, and the game simply closes. <see cref="PlacedScriptGuard"/> removes
    /// the managed scripts that cause most of these, but engine-side scripts have no managed type at
    /// all and cannot be reached that way - so something has to handle the ones that get through.
    ///
    /// The trick is that a crash leaves evidence if you write it down first. The name of whatever is
    /// about to be built goes to a file before the call and is erased immediately after. Finding that
    /// file still populated at startup means exactly one thing: the last thing this editor tried to
    /// build took the process with it.
    ///
    /// So the first person to hit a bad prefab loses one session, and never hits it again - and the
    /// list they accumulate is a plain text file worth sending back, which is how the shipped guard
    /// gets better.
    /// </summary>
    public static class PrefabCrashGuard {
        private static readonly object Sync = new();

        private static HashSet<string>? _blocked;
        private static bool _checked;

        private static string Directory => TraceLogger.LogDirectoryPathPublic;
        private static string InFlightPath => Path.Combine(Directory, "csc_building_now.txt");
        private static string BlockedPath => Path.Combine(Directory, "csc_unsafe_prefabs.txt");

        /// <summary>
        /// Reads the list, and converts an unfinished build from last session into a new entry.
        /// Called when a scene opens.
        /// </summary>
        public static string? CheckPreviousSession() {
            lock (Sync) {
                _blocked ??= LoadBlocked();
                if (_checked) return null;
                _checked = true;

                string? victim = ReadInFlight();
                if (victim == null) return null;

                ClearInFlight();
                if (!_blocked.Add(victim)) return null;

                SaveBlocked();
                TraceLogger.Write(nameof(PrefabCrashGuard),
                    $"'{victim}' was being built when the game closed last time. It will not be built " +
                    $"again. Remove it from {BlockedPath} to try once more.");
                return victim;
            }
        }

        public static bool IsBlocked(string prefabName) {
            lock (Sync) {
                _blocked ??= LoadBlocked();
                return _blocked.Contains(prefabName);
            }
        }

        /// <summary>Records what is about to be built. Must be paired with <see cref="End"/>.</summary>
        public static void Begin(string prefabName) {
            try {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(InFlightPath, prefabName);
            } catch {
                // Losing the guard is survivable; failing to build because of it is not.
            }
        }

        public static void End() => ClearInFlight();

        private static void ClearInFlight() {
            try {
                if (File.Exists(InFlightPath)) File.Delete(InFlightPath);
            } catch { }
        }

        private static string? ReadInFlight() {
            try {
                if (!File.Exists(InFlightPath)) return null;
                string name = File.ReadAllText(InFlightPath).Trim();
                return name.Length > 0 ? name : null;
            } catch {
                return null;
            }
        }

        private static HashSet<string> LoadBlocked() {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try {
                if (!File.Exists(BlockedPath)) return result;
                foreach (string line in File.ReadAllLines(BlockedPath)) {
                    string name = line.Trim();
                    if (name.Length > 0 && !name.StartsWith("#", StringComparison.Ordinal)) result.Add(name);
                }
                if (result.Count > 0) {
                    TraceLogger.Write(nameof(PrefabCrashGuard),
                        $"{result.Count} prefab(s) blocked from earlier crashes: {string.Join(", ", result.Take(20))}");
                }
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PrefabCrashGuard), $"Could not read the blocked list: {ex.Message}");
            }
            return result;
        }

        private static void SaveBlocked() {
            try {
                var lines = new List<string> {
                    "# Prefabs that crashed the game when this editor tried to build them.",
                    "# Added automatically. Delete a line to allow that prefab to be tried again.",
                };
                lines.AddRange(_blocked!.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
                File.WriteAllLines(BlockedPath, lines);
            } catch (Exception ex) {
                TraceLogger.Write(nameof(PrefabCrashGuard), $"Could not write the blocked list: {ex.Message}");
            }
        }
    }
}
