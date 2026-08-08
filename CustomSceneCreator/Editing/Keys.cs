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
        // Slash, not P: P is the game's own "pick up item" bind, so using it meant every mode
        // switch also grabbed equipment off the ground.
        public const InputKey EditMode      = InputKey.Slash;
        public const InputKey Place         = InputKey.Q;
        public const InputKey CameraMode    = InputKey.V;
        public const InputKey Save          = InputKey.K;

        public const InputKey PrevPlaceable = InputKey.OpenBraces;    // [
        public const InputKey NextPlaceable = InputKey.CloseBraces;   // ]
        public const InputKey NextCategory  = InputKey.Apostrophe;    // '

        public const InputKey ResetRotation = InputKey.Numpad0;
        public const InputKey RotateTiltUp    = InputKey.Numpad8;
        public const InputKey RotateTiltDown  = InputKey.Numpad2;
        public const InputKey RotateRollLeft  = InputKey.Numpad4;
        public const InputKey RotateRollRight = InputKey.Numpad6;
        public const InputKey RotateTurnLeft  = InputKey.Numpad7;
        public const InputKey RotateTurnRight = InputKey.Numpad9;

        public const InputKey MoveUp   = InputKey.Numpad5;
        public const InputKey MoveDown = InputKey.Numpad1;

        /// <summary>Readable label for on-screen prompts.</summary>
        public static string Describe(InputKey key) {
            switch (key) {
                case InputKey.OpenBraces:  return "[";
                case InputKey.CloseBraces: return "]";
                case InputKey.Apostrophe:  return "'";
                case InputKey.Slash:       return "/";
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
