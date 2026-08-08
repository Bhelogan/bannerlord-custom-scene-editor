using TaleWorlds.InputSystem;

namespace CustomSceneCreator.Editing {
    /// <summary>
    /// Editor key bindings.
    ///
    /// Hardcoded for now; these become MCM settings later. Deliberately kept off WASD, Shift and the
    /// mouse buttons so ordinary movement and looking still work while editing, and matched to the
    /// original mod's layout where possible so muscle memory carries over.
    ///
    /// Note InputKey values are US-layout POSITIONS, not the character printed on the key: on AZERTY,
    /// InputKey.Q is the key labelled A.
    /// </summary>
    public static class Keys {
        // Matched to Homesteads Reloaded's shipped scheme wherever it has one, so anyone coming from
        // the RTS builder there does not have to relearn the editor.
        //   BackSlash - edit mode      (P was the game's own pick-up-item bind)
        //   Q / E     - yaw the object (the rotation people actually use constantly)
        //   G / H     - ground snap / ground-follow toggle
        //   LMB       - place          (RMB held + mouse = rotate)
        public const InputKey EditMode      = InputKey.BackSlash;
        public const InputKey CameraMode    = InputKey.V;
        /// <summary>Opens the asset picker. Tilde is what Homesteads uses for its building picker.</summary>
        public const InputKey AssetPicker   = InputKey.Tilde;
        public const InputKey Save          = InputKey.K;

        /// <summary>Primary place action. Read through the scene layer so Gauntlet does not eat it.</summary>
        public const InputKey Place         = InputKey.LeftMouseButton;
        /// <summary>Keyboard fallback, and what Homesteads binds by default. Also the only way to
        /// place while a player-attached camera has the cursor captured.</summary>
        public const InputKey PlaceAlt      = InputKey.F;

        /// <summary>Held to rotate the held object with horizontal mouse movement.</summary>
        public const InputKey RotateDrag    = InputKey.RightMouseButton;

        public const InputKey PrevPlaceable = InputKey.OpenBraces;    // [
        public const InputKey NextPlaceable = InputKey.CloseBraces;   // ]
        public const InputKey NextCategory  = InputKey.Apostrophe;    // '

        public const InputKey RotateTurnLeft  = InputKey.Q;
        public const InputKey RotateTurnRight = InputKey.E;

        public const InputKey SnapToGround     = InputKey.G;
        public const InputKey ToggleGroundLock = InputKey.H;

        /// <summary>Clears rotation and height offset in one press - Homesteads binds this to Left
        /// Ctrl, and it is reached often enough that a numpad key was the wrong home for it.</summary>
        public const InputKey ResetRotation = InputKey.LeftControl;
        public const InputKey RotateTiltUp    = InputKey.Numpad8;
        public const InputKey RotateTiltDown  = InputKey.Numpad2;
        public const InputKey RotateRollLeft  = InputKey.Numpad4;
        public const InputKey RotateRollRight = InputKey.Numpad6;

        public const InputKey MoveUp   = InputKey.Numpad5;
        public const InputKey MoveDown = InputKey.Numpad1;

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
