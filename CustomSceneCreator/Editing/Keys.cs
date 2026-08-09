using TaleWorlds.InputSystem;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Editor key bindings.
    ///
    /// These are the DEFAULTS. Most are rebindable in MCM's options screen; the values here are
    /// what applies when MCM is not installed, and the fallback for any single binding that cannot
    /// be read. See Settings.KeyBindings. Deliberately kept off WASD, Shift and the
    /// mouse buttons so ordinary movement and looking still work while editing, and matched to the
    /// original mod's layout where possible so muscle memory carries over.
    ///
    /// Note InputKey values are US-layout POSITIONS, not the character printed on the key: on AZERTY,
    /// InputKey.Q is the key labelled A.
    /// </summary>
    public static class Keys {
        // Chosen to sit where a building game usually puts them, and to stay off keys the game has
        // already claimed - P is the pick-up-item bind, which is why edit mode is not on it.
        //   BackSlash - edit mode
        //   Q / E     - yaw the object (the rotation used constantly)
        //   G / H     - ground snap / ground-follow toggle
        //   LMB       - place          (RMB held + mouse = tilt and roll)
        public static InputKey EditMode => Settings.KeyBindings.EditMode;
        public static InputKey CameraMode => Settings.KeyBindings.CameraMode;
        /// <summary>Opens the asset picker.</summary>
        public static InputKey AssetPicker => Settings.KeyBindings.AssetPicker;
        /// <summary>Lists everything placed in the scene. L for list.</summary>
        public static InputKey Outliner => Settings.KeyBindings.Outliner;
        /// <summary>
        /// Save is Alt+S, not Ctrl+S: Ctrl is reset-rotation, so Ctrl+S would clear the rotation of
        /// whatever you are holding every time you saved. K remains as a single-key alternative.
        /// </summary>
        public static InputKey SaveModifier => Settings.KeyBindings.SaveModifier;
        public const InputKey SaveWithModifier = InputKey.S;
        public static InputKey Save => Settings.KeyBindings.Save;
        /// <summary>Alt+E opens the export dialog - deliberate and occasional, unlike saving.</summary>
        public const InputKey ExportWithModifier = InputKey.E;

        /// <summary>Primary place action. Read through the scene layer so Gauntlet does not eat it.</summary>
        public const InputKey Place         = InputKey.LeftMouseButton;
        /// <summary>Keyboard fallback. Also the only way to place while a player-attached camera has
        /// the cursor captured.</summary>
        public static InputKey PlaceAlt => Settings.KeyBindings.PlaceAlt;

        /// <summary>Held to rotate the held object with horizontal mouse movement.</summary>
        public const InputKey RotateDrag    = InputKey.RightMouseButton;

        public static InputKey PrevPlaceable => Settings.KeyBindings.PrevPlaceable;    // [
        public static InputKey NextPlaceable => Settings.KeyBindings.NextPlaceable;   // ]
        public static InputKey NextCategory => Settings.KeyBindings.NextCategory;    // '

        public static InputKey RotateTurnLeft => Settings.KeyBindings.RotateTurnLeft;
        public static InputKey RotateTurnRight => Settings.KeyBindings.RotateTurnRight;

        public static InputKey SnapToGround => Settings.KeyBindings.SnapToGround;
        public static InputKey ToggleGroundLock => Settings.KeyBindings.ToggleGroundLock;

        /// <summary>Clears rotation and height offset in one press. Reached often enough that a
        /// numpad key was the wrong home for it.</summary>
        public static InputKey ResetRotation => Settings.KeyBindings.ResetRotation;
        public const InputKey RotateTiltUp    = InputKey.Numpad8;
        public const InputKey RotateTiltDown  = InputKey.Numpad2;
        public const InputKey RotateRollLeft  = InputKey.Numpad4;
        public const InputKey RotateRollRight = InputKey.Numpad6;

        public static InputKey MoveUp => Settings.KeyBindings.MoveUp;
        public static InputKey MoveDown => Settings.KeyBindings.MoveDown;

        /// <summary>Readable label for on-screen prompts.</summary>
        public static string Describe(InputKey key) {
            switch (key) {
                case InputKey.OpenBraces:  return "[";
                case InputKey.CloseBraces: return "]";
                case InputKey.Apostrophe:  return "'";
                case InputKey.Slash:       return "/";
                case InputKey.BackSlash:   return "\\";
                case InputKey.LeftMouseButton:  return "LMB";
                case InputKey.RightMouseButton: return "RMB";
                case InputKey.LeftControl:      return "Left Ctrl";
                case InputKey.Tilde:            return "`";
                case InputKey.LeftAlt:          return "Alt";
                case InputKey.Numpad0: return "Num0";
                case InputKey.Numpad1: return "Num1";
                case InputKey.Numpad2: return "Num2";
                case InputKey.Numpad4: return "Num4";
                case InputKey.Numpad5: return "Num5";
                case InputKey.Numpad6: return "Num6";
                case InputKey.Numpad7: return "Num7";
                case InputKey.Numpad8: return "Num8";
                case InputKey.Numpad9: return "Num9";
                default: return key.ToString();
            }
        }
    }
}
