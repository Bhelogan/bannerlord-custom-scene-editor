using System.Globalization;
using TaleWorlds.MountAndBlade.GauntletUI.TextureProviders;

namespace CustomSceneCreator.UI.PrefabPreview {
    /// <summary>Texture provider discovered by Gauntlet for the asset picker preview widget.</summary>
    public sealed class CSCPrefabPreviewTextureProvider : SceneTextureProvider {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        private readonly CSCPrefabPreviewScene _preview = new CSCPrefabPreviewScene();
        private bool _cleared;

        public CSCPrefabPreviewTextureProvider() {
            // The TextureWidget initializes lazily. Give its tableau a valid empty scene immediately
            // so its first sizing pass can never receive null.
            if (_preview.EnsureScene()) base.Scene = _preview.Scene;
        }

        public string PrefabName { set { _preview.Show(value); } }
        public string Yaw { set { if (float.TryParse(value, NumberStyles.Float, Culture, out float v)) _preview.SetYaw(v); } }
        public string Pitch { set { if (float.TryParse(value, NumberStyles.Float, Culture, out float v)) _preview.SetPitch(v); } }
        public string Zoom { set { if (float.TryParse(value, NumberStyles.Float, Culture, out float v)) _preview.SetZoom(v); } }

        public override void Tick(float dt) {
            _preview.Tick(dt);
            base.Tick(dt);
        }

        public override void Clear(bool clearNextFrame) {
            if (!_cleared) {
                _cleared = true;
                try { base.Scene = null; } finally { _preview.Dispose(); }
            }
            base.Clear(clearNextFrame);
        }
    }
}
