using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TaleWorlds.InputSystem;

namespace CustomSceneCreator.Settings {
    /// <summary>
    /// The editor's key bindings, from MCM if it is installed and from the defaults if it is not.
    ///
    /// MCM is optional on purpose. It is a big ask for someone who only wants to try a scene editor,
    /// and the editor is perfectly usable on its defaults - so this is the wall between the two. The
    /// MCM settings type is never named outside <see cref="ReadSettings"/>, which is only called
    /// after MCM's assembly is confirmed loaded: naming the type is enough to make the CLR load MCM,
    /// and that would crash for everyone who does not have it.
    /// </summary>
    public static class KeyBindings {
        /// <summary>Typed as InputKey NAMES because that is what the settings screen takes.</summary>
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase) {
            ["-"] = nameof(InputKey.Minus),
            ["="] = nameof(InputKey.Equals),
            ["["] = nameof(InputKey.OpenBraces),
            ["]"] = nameof(InputKey.CloseBraces),
            ["'"] = nameof(InputKey.Apostrophe),
            ["\""] = nameof(InputKey.Apostrophe),
            [";"] = nameof(InputKey.SemiColon),
            [","] = nameof(InputKey.Comma),
            ["."] = nameof(InputKey.Period),
            ["/"] = nameof(InputKey.Slash),
            ["\\"] = nameof(InputKey.BackSlash),
            ["`"] = nameof(InputKey.Tilde),
            ["~"] = nameof(InputKey.Tilde),
            ["ctrl"] = nameof(InputKey.LeftControl),
            ["alt"] = nameof(InputKey.LeftAlt),
            ["shift"] = nameof(InputKey.LeftShift),
        };

        // Defaults. These are the bindings when MCM is absent, and the fallback for any single value
        // that cannot be parsed.
        public static InputKey EditMode      = InputKey.BackSlash;
        public static InputKey CameraMode    = InputKey.V;
        public static InputKey AssetPicker   = InputKey.Tilde;
        public static InputKey Outliner      = InputKey.L;
        public static InputKey SaveModifier  = InputKey.LeftAlt;
        public static InputKey Save          = InputKey.K;
        public static InputKey PlaceAlt      = InputKey.F;
        public static InputKey PrevPlaceable = InputKey.OpenBraces;
        public static InputKey NextPlaceable = InputKey.CloseBraces;
        public static InputKey NextCategory  = InputKey.Apostrophe;
        public static InputKey RotateTurnLeft  = InputKey.Q;
        public static InputKey RotateTurnRight = InputKey.E;
        public static InputKey SnapToGround     = InputKey.G;
        public static InputKey ToggleGroundLock = InputKey.H;
        public static InputKey ResetRotation = InputKey.LeftControl;
        public static InputKey MoveUp   = InputKey.Numpad5;
        public static InputKey MoveDown = InputKey.Numpad1;

        /// <summary>Set from the settings screen; makes the editor report the name of any key pressed.</summary>
        public static bool KeyDetectionMode { get; private set; }

        public static bool UsingMcm { get; private set; }

        private static bool _loaded;

        /// <summary>
        /// Re-reads the settings. Called when a scene opens, so changing a binding in the options
        /// screen and reopening the editor is enough - no restart.
        /// </summary>
        public static void Refresh() {
            if (!IsMcmLoaded()) {
                if (!_loaded) {
                    TraceLogger.Write(nameof(KeyBindings),
                        "MCM not installed - using default key bindings.");
                    _loaded = true;
                }
                return;
            }

            try {
                ReadSettings();
                UsingMcm = true;
                TraceLogger.Write(nameof(KeyBindings), "Key bindings loaded from MCM.");
            } catch (Exception ex) {
                // Defaults are already in place, so a broken settings file costs the rebinds and
                // nothing else.
                TraceLogger.WriteException(nameof(KeyBindings),
                    "Could not read MCM settings - keeping default bindings", ex);
            }
            _loaded = true;
        }

        /// <summary>
        /// True only when MCM's assembly is really in the process.
        ///
        /// Checked by name rather than by catching a load failure: a missing-assembly exception from
        /// a JIT-ed method is not reliably catchable, which is exactly the crash this avoids.
        /// </summary>
        private static bool IsMcmLoaded() {
            try {
                return AppDomain.CurrentDomain.GetAssemblies().Any(assembly => {
                    string name = assembly.GetName().Name ?? "";
                    return name.StartsWith("MCM", StringComparison.OrdinalIgnoreCase);
                });
            } catch {
                return false;
            }
        }

        /// <summary>
        /// The only method that names the MCM settings type.
        ///
        /// NoInlining matters: inlined into Refresh, the type reference would be resolved when
        /// Refresh is JIT-ed - before the MCM check has run - and the guard would be worthless.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadSettings() {
            EditorSettings? settings = EditorSettings.Instance;
            if (settings == null) return;

            KeyDetectionMode = settings.KeyDetectionMode;

            EditMode        = Parse(settings.KeyEditMode, InputKey.BackSlash);
            CameraMode      = Parse(settings.KeyCamera, InputKey.V);
            AssetPicker     = Parse(settings.KeyAssetPicker, InputKey.Tilde);
            Outliner        = Parse(settings.KeyOutliner, InputKey.L);
            PlaceAlt        = Parse(settings.KeyPlace, InputKey.F);
            Save            = Parse(settings.KeySave, InputKey.K);
            SaveModifier    = Parse(settings.KeyModifier, InputKey.LeftAlt);
            PrevPlaceable   = Parse(settings.KeyPrevPlaceable, InputKey.OpenBraces);
            NextPlaceable   = Parse(settings.KeyNextPlaceable, InputKey.CloseBraces);
            NextCategory    = Parse(settings.KeyNextCategory, InputKey.Apostrophe);
            RotateTurnLeft  = Parse(settings.KeyRotateLeft, InputKey.Q);
            RotateTurnRight = Parse(settings.KeyRotateRight, InputKey.E);
            SnapToGround    = Parse(settings.KeySnapToGround, InputKey.G);
            ToggleGroundLock = Parse(settings.KeyGroundLock, InputKey.H);
            ResetRotation   = Parse(settings.KeyResetRotation, InputKey.LeftControl);
            MoveUp          = Parse(settings.KeyMoveUp, InputKey.Numpad5);
            MoveDown        = Parse(settings.KeyMoveDown, InputKey.Numpad1);
        }

        /// <summary>
        /// Turns a typed name into a key. Blank means "use the default" rather than "unbind", so an
        /// empty box can never leave part of the editor unreachable.
        /// </summary>
        private static InputKey Parse(string value, InputKey fallback) {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            string name = value.Trim();
            if (Aliases.TryGetValue(name, out string? alias)) name = alias;
            else if (name.Length == 1) name = name.ToUpperInvariant();

            try {
                return (InputKey)Enum.Parse(typeof(InputKey), name, ignoreCase: true);
            } catch {
                // Said out loud rather than swallowed. Someone on AZERTY may type a character that
                // exists on their keycaps but has no InputKey name at all, and silence would leave
                // them thinking the editor ignored them.
                TraceLogger.Write(nameof(KeyBindings),
                    $"'{value}' is not a key name - keeping '{fallback}'. Use an InputKey name " +
                    "(A-Z, D0-D9, F1-F12, Numpad0-Numpad9, OpenBraces, CloseBraces, Apostrophe, " +
                    "SemiColon, Comma, Period, Slash, BackSlash, Tilde), or turn on Key Detection " +
                    "Mode and press the key to be told its name.");
                return fallback;
            }
        }
    }
}
