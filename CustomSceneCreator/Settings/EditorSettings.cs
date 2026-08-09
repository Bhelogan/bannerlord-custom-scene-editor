using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace CustomSceneCreator.Settings {
    /// <summary>
    /// The MCM options screen: key bindings, and the detection mode that tells you what to type
    /// into them.
    ///
    /// IMPORTANT: nothing may touch this type unless MCM is actually loaded. It derives from an MCM
    /// base class, so merely resolving it forces the CLR to load MCM's assembly - and a mod that
    /// hard-crashes for everyone without an optional dependency installed is worse than one with
    /// fixed keys. <see cref="KeyBindings"/> is the only thing allowed to reach it, and it checks
    /// first.
    ///
    /// Bindings are typed as TEXT rather than picked from a list because InputKey has hundreds of
    /// members and MCM's dropdowns are painful at that length. The cost is that a name can be typed
    /// wrong, which is what the detection mode and the fallback logging are for.
    /// </summary>
    public class EditorSettings : AttributeGlobalSettings<EditorSettings> {
        private const string KeyGroup = "{=CSC_Group_Keys}Key Bindings";
        private const string ModeGroup = "{=CSC_Group_Modes}Editing";

        public override string Id => "CustomSceneCreator";
        public override string DisplayName =>
            new TaleWorlds.Localization.TextObject("{=CSC_Mod_Name}Custom Scene Creator").ToString();
        public override string FolderName => "CustomSceneCreator";
        public override string FormatType => "xml";

        // -- editing ---------------------------------------------------------------------------

        [SettingPropertyBool("{=CSC_KeyDetection_Name}Key Detection Mode",
            Order = 0, RequireRestart = false,
            HintText = "{=CSC_KeyDetection_Hint}Turn this on, open a scene, and press any key: the " +
                       "editor tells you the exact name to type into the boxes below. Needed on " +
                       "non-US keyboards, where the game reads a key's PHYSICAL POSITION rather " +
                       "than the letter printed on it - so the name is often not the character you " +
                       "see. Turn it off when you are done.")]
        [SettingPropertyGroup(ModeGroup, GroupOrder = 0)]
        public bool KeyDetectionMode { get; set; } = false;

        // -- key bindings ----------------------------------------------------------------------
        //
        // Defaults match Keys.cs. Anything left blank falls back to the default rather than
        // unbinding, so an empty box can never leave the editor unusable.

        [SettingPropertyText("{=CSC_Key_EditMode_Name}Cycle Edit Mode", Order = 1, RequireRestart = false,
            HintText = "{=CSC_Key_EditMode_Hint}Steps through Off, Build, Delete, Move and Script.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyEditMode { get; set; } = "\\";

        [SettingPropertyText("{=CSC_Key_AssetPicker_Name}Asset Picker", Order = 2, RequireRestart = false,
            HintText = "{=CSC_Key_AssetPicker_Hint}Opens the searchable list of everything you can build.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyAssetPicker { get; set; } = "`";

        [SettingPropertyText("{=CSC_Key_Outliner_Name}Scene Contents List", Order = 3, RequireRestart = false,
            HintText = "{=CSC_Key_Outliner_Hint}Lists everything you have placed, with its exact position.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyOutliner { get; set; } = "L";

        [SettingPropertyText("{=CSC_Key_Place_Name}Place (keyboard)", Order = 4, RequireRestart = false,
            HintText = "{=CSC_Key_Place_Hint}Does what a left-click does. The mouse button is not rebindable.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyPlace { get; set; } = "F";

        [SettingPropertyText("{=CSC_Key_Camera_Name}Cycle Camera", Order = 5, RequireRestart = false,
            HintText = "{=CSC_Key_Camera_Hint}Overhead, third person, first person.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyCamera { get; set; } = "V";

        [SettingPropertyText("{=CSC_Key_RotateLeft_Name}Rotate Left", Order = 6, RequireRestart = false,
            HintText = "{=CSC_Key_RotateLeft_Hint}Turns the held object anticlockwise.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyRotateLeft { get; set; } = "Q";

        [SettingPropertyText("{=CSC_Key_RotateRight_Name}Rotate Right", Order = 7, RequireRestart = false,
            HintText = "{=CSC_Key_RotateRight_Hint}Turns the held object clockwise.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyRotateRight { get; set; } = "E";

        [SettingPropertyText("{=CSC_Key_ResetRotation_Name}Reset Rotation", Order = 8, RequireRestart = false,
            HintText = "{=CSC_Key_ResetRotation_Hint}Clears rotation and height offset on the held object.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyResetRotation { get; set; } = "LeftControl";

        [SettingPropertyText("{=CSC_Key_SnapToGround_Name}Drop To Ground", Order = 9, RequireRestart = false,
            HintText = "{=CSC_Key_SnapToGround_Hint}Drops the held object to the terrain and re-enables ground follow.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeySnapToGround { get; set; } = "G";

        [SettingPropertyText("{=CSC_Key_GroundLock_Name}Toggle Ground Follow", Order = 10, RequireRestart = false,
            HintText = "{=CSC_Key_GroundLock_Hint}Switches between hugging the terrain and holding a fixed height.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyGroundLock { get; set; } = "H";

        [SettingPropertyText("{=CSC_Key_PrevPlaceable_Name}Previous Object", Order = 11, RequireRestart = false,
            HintText = "{=CSC_Key_PrevPlaceable_Hint}Steps back through the current list without opening the picker.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyPrevPlaceable { get; set; } = "[";

        [SettingPropertyText("{=CSC_Key_NextPlaceable_Name}Next Object", Order = 12, RequireRestart = false,
            HintText = "{=CSC_Key_NextPlaceable_Hint}Steps forward through the current list.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyNextPlaceable { get; set; } = "]";

        [SettingPropertyText("{=CSC_Key_NextCategory_Name}Next Category", Order = 13, RequireRestart = false,
            HintText = "{=CSC_Key_NextCategory_Hint}Moves to the next category, leaving any search behind.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyNextCategory { get; set; } = "'";

        [SettingPropertyText("{=CSC_Key_Save_Name}Save", Order = 14, RequireRestart = false,
            HintText = "{=CSC_Key_Save_Hint}Single-key save. The modifier combination below also works.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeySave { get; set; } = "K";

        [SettingPropertyText("{=CSC_Key_Modifier_Name}Save/Export Modifier", Order = 15, RequireRestart = false,
            HintText = "{=CSC_Key_Modifier_Hint}Held with S to save and E to export. Alt by default, " +
                       "because Ctrl is reset-rotation and Ctrl+S would clear your rotation every time you saved.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyModifier { get; set; } = "LeftAlt";

        [SettingPropertyText("{=CSC_Key_MoveUp_Name}Raise Object", Order = 16, RequireRestart = false,
            HintText = "{=CSC_Key_MoveUp_Hint}Raises the held object. The mouse wheel does this too.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyMoveUp { get; set; } = "Numpad5";

        [SettingPropertyText("{=CSC_Key_MoveDown_Name}Lower Object", Order = 17, RequireRestart = false,
            HintText = "{=CSC_Key_MoveDown_Hint}Lowers the held object.")]
        [SettingPropertyGroup(KeyGroup, GroupOrder = 10)]
        public string KeyMoveDown { get; set; } = "Numpad1";
    }
}
