using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>
    /// The top-left readout: what the editor is about to act on.
    ///
    /// The message log was enough to prove the editor worked, but not to use it - by the time you
    /// have cycled a few assets the line that told you what is selected has scrolled away. This
    /// stays on screen and answers the one question every mode raises: what happens if I click now.
    ///
    /// In Build that is the asset being placed. In Delete and Move it is the object under the
    /// cursor - and in Move it holds onto the object being carried, so it keeps naming what you are
    /// moving rather than flickering to whatever the cursor passes over mid-drag.
    /// </summary>
    public class EditorStatusVM : ViewModel {
        private bool _isVisible;
        private string _modeText = "";
        private string _primaryText = "";
        private string _detailText = "";
        private string _modeColor = ColorNeutral;

        // Kept as hex strings because Gauntlet brushes take colours that way.
        private const string ColorNeutral = "#D8D0BEFF";
        private const string ColorBuild   = "#9BE08AFF";
        private const string ColorDelete  = "#E58A8AFF";
        private const string ColorMove    = "#8ABEE5FF";

        [DataSourceProperty]
        public bool IsVisible {
            get => _isVisible;
            set { if (value != _isVisible) { _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); } }
        }

        [DataSourceProperty]
        public string ModeText {
            get => _modeText;
            set { if (value != _modeText) { _modeText = value; OnPropertyChangedWithValue(value, nameof(ModeText)); } }
        }

        /// <summary>The asset or object name - the thing you actually look at.</summary>
        [DataSourceProperty]
        public string PrimaryText {
            get => _primaryText;
            set { if (value != _primaryText) { _primaryText = value; OnPropertyChangedWithValue(value, nameof(PrimaryText)); } }
        }

        /// <summary>Category, position in the cycle set, or a short instruction.</summary>
        [DataSourceProperty]
        public string DetailText {
            get => _detailText;
            set { if (value != _detailText) { _detailText = value; OnPropertyChangedWithValue(value, nameof(DetailText)); } }
        }

        [DataSourceProperty]
        public string ModeColor {
            get => _modeColor;
            set { if (value != _modeColor) { _modeColor = value; OnPropertyChangedWithValue(value, nameof(ModeColor)); } }
        }

        public void Set(string mode, string primary, string detail, StatusTone tone) {
            ModeText = mode;
            PrimaryText = primary;
            DetailText = detail;
            ModeColor = tone switch {
                StatusTone.Build => ColorBuild,
                StatusTone.Delete => ColorDelete,
                StatusTone.Move => ColorMove,
                _ => ColorNeutral,
            };
        }
    }

    public enum StatusTone { Neutral, Build, Delete, Move }
}
