using System;
using TaleWorlds.Engine;
using System.Collections.Generic;
using System.Linq;
using CustomSceneCreator.Api;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;

namespace CustomSceneCreator.UI {
    /// <summary>Small cycle-based material/image picker; avoids another dense two-list dialog.</summary>
    public class TexturePanelVM : ViewModel {
        private readonly PlacedEntity _entity;
        private readonly Action _close;
        private readonly Action _changed;
        private List<TextureOverrideApplicator.TextureSurface> _surfaces = new();
        private List<string> _images = new();
        private int _materialIndex, _imageIndex;

        public TexturePanelVM(PlacedEntity entity, Action close, Action changed) {
            _entity = entity; _close = close; _changed = changed; Refresh();
        }

        [DataSourceProperty] public string TitleText => "Texture Override";
        [DataSourceProperty] public string SubjectText => Placeable.ToDisplayName(_entity.PrefabName);
        [DataSourceProperty] public string HintText => "Put an 8-bit RGB or RGBA PNG in Documents\\Mount and Blade II Bannerlord\\CustomSceneCreator\\textures, then choose one surface. A new override changes only that surface; older material-wide overrides remain supported.";
        [DataSourceProperty] public string MaterialText => _surfaces.Count == 0 ? "No mesh surfaces found" : _surfaces[_materialIndex].DisplayName;
        [DataSourceProperty] public string ImageText => _images.Count == 0 ? "No PNG files found" : _images[_imageIndex];
        [DataSourceProperty] public string StatusText {
            get {
                int total = _entity.TextureOverrides?.Count ?? 0;
                int legacy = _entity.TextureOverrides?.Count(t => t.MeshIndex < 0) ?? 0;
                return legacy > 0
                    ? $"{total} override(s) saved. {legacy} legacy material-wide override(s): remove and reapply a Surface for precise results."
                    : $"{total} surface override(s) saved on this object. Visible diffuse slot(s); transparent pixels depend on the original material.";
            }
        }
        [DataSourceProperty] public bool CanApply => _surfaces.Count > 0 && _images.Count > 0;
        [DataSourceProperty] public string ApplyText => "Apply Preview";
        [DataSourceProperty] public string RemoveText => "Remove Surface Override";
        [DataSourceProperty] public string RefreshText => "Refresh PNGs";
        [DataSourceProperty] public string CloseText => "Close";

        public void ExecutePreviousMaterial() => Cycle(ref _materialIndex, _surfaces, -1, nameof(MaterialText));
        public void ExecuteNextMaterial() => Cycle(ref _materialIndex, _surfaces, 1, nameof(MaterialText));
        public void ExecutePreviousImage() => Cycle(ref _imageIndex, _images, -1, nameof(ImageText));

        /// <summary>
        /// What the selected surface's material is, not just what it is called.
        ///
        /// <para>A material name says nothing about how an image will be used. This line says
        /// whether the alpha channel is opacity or the specular level, and which maps the material
        /// carries - the two things that decide what a texture actually does here.</para>
        /// </summary>
        [DataSourceProperty] public string MaterialInfoText {
            get {
                if (_surfaces.Count == 0) return "";
                try {
                    return TextureOverrideApplicator.DescribeMaterial(
                        TextureOverrideApplicator.MaterialOfSurface(
                            _entity.SceneEntity, _surfaces[_materialIndex].MeshIndex));
                } catch { return ""; }
            }
        }

        /// <summary>-1 means "use the PNG as authored"; 0-255 forces every alpha byte.</summary>
        private int _alpha = -1;

        /// <summary>
        /// Always "Opacity". The material's UsingSpecularAlpha flag is NOT a safe guide.
        ///
        /// <para>This used to read "Gloss (alpha)" whenever that flag was set, on the reading that
        /// such a material treats the diffuse alpha as its specular term. Homesteads tested exactly
        /// that on dog_a - a material whose flag IS set - by writing a low value into every alpha
        /// byte. The coats did not go matte, they went nearly invisible, and read as black over a
        /// dim interior. The flag may well mean what it says to the shader; what a user gets when
        /// they drag this slider down is transparency.</para>
        ///
        /// <para>So the label describes the observed effect rather than the flag. The flag is still
        /// reported verbatim in the material probe line, where it belongs as a fact rather than as
        /// advice about a control.</para>
        /// </summary>
        [DataSourceProperty] public string AlphaLabelText => _surfaces.Count == 0 ? "Alpha" : "Opacity (alpha)";

        [DataSourceProperty] public string AlphaText =>
            _alpha < 0 ? "From image" : (int)Math.Round(_alpha * 100.0 / 255.0) + "%";

        public void ExecuteLessAlpha() => StepAlpha(-13);
        public void ExecuteMoreAlpha() => StepAlpha(13);

        /// <summary>-1 keeps the material's own cutoff; 0-100 is a percentage.</summary>
        private int _cutoff = -1;

        [DataSourceProperty] public string CutoffText => _cutoff < 0 ? "From material" : _cutoff + "%";

        public void ExecuteLessCutoff() => StepCutoff(-5);
        public void ExecuteMoreCutoff() => StepCutoff(5);

        private void StepCutoff(int delta) {
            if (_cutoff < 0) _cutoff = delta > 0 ? 0 : -1;
            else {
                _cutoff += delta;
                if (_cutoff < 0) _cutoff = -1;
                else if (_cutoff > 100) _cutoff = 100;
            }
            OnPropertyChangedWithValue(CutoffText, nameof(CutoffText));
        }

        /// <summary>
        /// Blend modes worth offering, rather than the whole engine enum.
        ///
        /// <para>MBAlphaBlendMode has fourteen entries, most of them internal G-buffer and
        /// no-write variants that would only give an author a way to break a prop. These four
        /// cover what a texture override actually needs: leave it alone, make it solid, blend it,
        /// or add it as light.</para>
        /// </summary>
        private static readonly (int Value, string Label)[] BlendModes = {
            (-1, "From material"),
            ((int)Material.MBAlphaBlendMode.NoAlphaBlend, "Opaque"),
            ((int)Material.MBAlphaBlendMode.Modulate, "Transparent"),
            ((int)Material.MBAlphaBlendMode.Add, "Additive"),
        };

        private int _blendIndex;

        [DataSourceProperty] public string BlendText => BlendModes[_blendIndex].Label;

        public void ExecutePreviousBlend() => StepBlend(-1);
        public void ExecuteNextBlend() => StepBlend(1);

        private void StepBlend(int delta) {
            _blendIndex = (_blendIndex + delta + BlendModes.Length) % BlendModes.Length;
            OnPropertyChangedWithValue(BlendText, nameof(BlendText));
        }

        /// <summary>
        /// Steps the alpha level, with "From image" sitting just below zero.
        ///
        /// <para>Leaving the PNG untouched has to stay reachable: forcing alpha is right for a
        /// material that reads it as gloss and wrong for one that reads it as opacity, and the panel
        /// cannot decide that for the author.</para>
        /// </summary>
        private void StepAlpha(int delta) {
            if (_alpha < 0) _alpha = delta > 0 ? 0 : -1;
            else {
                _alpha += delta;
                if (_alpha < 0) _alpha = -1;
                else if (_alpha > 255) _alpha = 255;
            }
            OnPropertyChanged(nameof(AlphaText));
        }
        public void ExecuteNextImage() => Cycle(ref _imageIndex, _images, 1, nameof(ImageText));
        public void ExecuteRefresh() { TextureOverrideApplicator.InvalidateImageCache(); Refresh(); }
        public void ExecuteApply() {
            if (!CanApply) return;
            _entity.TextureOverrides ??= new List<TextureOverride>();
            TextureOverrideApplicator.TextureSurface surface = _surfaces[_materialIndex];
            _entity.TextureOverrides.RemoveAll(t => t.MeshIndex == surface.MeshIndex);
            _entity.TextureOverrides.Add(new TextureOverride {
                Material = surface.Material, MeshIndex = surface.MeshIndex, Image = _images[_imageIndex],
                Alpha = _alpha, AlphaCutoff = _cutoff, BlendMode = BlendModes[_blendIndex].Value
            });
            _changed(); Notify();
        }
        public void ExecuteRemove() {
            if (_surfaces.Count == 0 || _entity.TextureOverrides == null) return;
            TextureOverrideApplicator.TextureSurface surface = _surfaces[_materialIndex];
            _entity.TextureOverrides.RemoveAll(t => t.MeshIndex == surface.MeshIndex ||
                (t.MeshIndex < 0 && string.Equals(t.Material, surface.Material, StringComparison.OrdinalIgnoreCase)));
            _changed(); Notify();
        }
        public void ExecuteClose() => _close();

        private void Refresh() {
            _surfaces = TextureOverrideApplicator.Surfaces(_entity.SceneEntity);
            _images = TextureOverrideApplicator.ImageFiles();
            _materialIndex = Math.Min(_materialIndex, Math.Max(0, _surfaces.Count - 1));
            _imageIndex = Math.Min(_imageIndex, Math.Max(0, _images.Count - 1)); Notify();
        }
        private void Notify() {
            OnPropertyChangedWithValue(MaterialText, nameof(MaterialText));
            OnPropertyChangedWithValue(ImageText, nameof(ImageText));
            OnPropertyChangedWithValue(StatusText, nameof(StatusText));
            OnPropertyChangedWithValue(CanApply, nameof(CanApply));
            NotifySurface();
        }

        /// <summary>
        /// The material readout and the alpha label both describe the SELECTED surface, so they have
        /// to be re-read whenever the selection moves - otherwise the panel keeps describing the
        /// previous surface while showing the name of the new one, which is worse than showing
        /// nothing.
        /// </summary>
        private void NotifySurface() {
            OnPropertyChangedWithValue(MaterialInfoText, nameof(MaterialInfoText));
            OnPropertyChangedWithValue(AlphaLabelText, nameof(AlphaLabelText));
            OnPropertyChangedWithValue(AlphaText, nameof(AlphaText));
            OnPropertyChangedWithValue(CutoffText, nameof(CutoffText));
            OnPropertyChangedWithValue(BlendText, nameof(BlendText));
        }
        private void Cycle<T>(ref int index, List<T> values, int delta, string property) {
            if (values.Count == 0) return;
            index = (index + delta + values.Count) % values.Count;
            OnPropertyChangedWithValue(property == nameof(MaterialText) ? MaterialText : ImageText, property);
            if (property == nameof(MaterialText)) NotifySurface();
        }
    }
}
