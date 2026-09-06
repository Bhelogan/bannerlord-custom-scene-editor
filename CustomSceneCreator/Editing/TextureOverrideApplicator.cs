using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomSceneCreator.Api;
using TaleWorlds.Engine;
using IOPath = System.IO.Path;

namespace CustomSceneCreator.Editing {
    /// <summary>Applies project PNGs to named materials on one runtime prefab instance.</summary>
    internal static class TextureOverrideApplicator {
        internal sealed class TextureSurface {
            public int MeshIndex;
            public string Material = "";
            public string DisplayName = "";
        }
        private static readonly List<Material> Materials = new();
        private static readonly Dictionary<string, Texture> Textures = new(StringComparer.OrdinalIgnoreCase);

        public static List<string> ImageFiles() {
            try {
                Directory.CreateDirectory(ProjectSerializer.TexturesPath);
                return Directory.GetFiles(ProjectSerializer.TexturesPath, "*.png")
                    .Select(IOPath.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Cast<string>().ToList();
            } catch { return new List<string>(); }
        }

        /// <summary>
        /// Forget file-to-texture lookups before the user refreshes the PNG list. Existing material
        /// copies retain their Texture references, while the next preview reads changed pixels from
        /// disk instead of silently reusing the old image.
        /// </summary>
        public static void InvalidateImageCache() => Textures.Clear();

        /// <summary>
        /// A portable template travels with a sibling &lt;name&gt;_textures directory. Import those
        /// images into CSC's shared texture library before previewing or placing the template.
        /// Existing files are left alone so importing someone else's template cannot overwrite a
        /// texture the author already uses in other projects.
        /// </summary>
        public static int ImportTemplateImages(string templateProject) {
            try {
                if (string.IsNullOrWhiteSpace(templateProject)) return 0;
                string? directory = IOPath.GetDirectoryName(templateProject);
                string name = IOPath.GetFileNameWithoutExtension(templateProject);
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name)) return 0;
                string sourceDirectory = IOPath.Combine(directory, name + "_textures");
                if (!Directory.Exists(sourceDirectory)) return 0;

                Directory.CreateDirectory(ProjectSerializer.TexturesPath);
                int copied = 0;
                foreach (string source in Directory.GetFiles(sourceDirectory, "*.png")) {
                    string destination = IOPath.Combine(ProjectSerializer.TexturesPath, IOPath.GetFileName(source));
                    if (File.Exists(destination)) continue;
                    File.Copy(source, destination, false);
                    copied++;
                }
                if (copied > 0) InvalidateImageCache();
                return copied;
            } catch (Exception ex) {
                TraceLogger.Write(nameof(TextureOverrideApplicator),
                    $"Template texture import skipped: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        public static List<string> MaterialNames(GameEntity? root) {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root != null) foreach (Mesh mesh in Meshes(root)) {
                try { string name = mesh.GetMaterial()?.Name ?? ""; if (name.Length > 0) names.Add(name); } catch { }
            }
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Gives the UI a distinct target for every mesh instead of grouping by material name.
        /// Many shipped prefabs reuse one material across walls, roofs and trim; selecting by
        /// name made a picture texture leak onto all of those surfaces.
        /// </summary>
        public static List<TextureSurface> Surfaces(GameEntity? root) {
            var result = new List<TextureSurface>();
            if (root == null) return result;
            int index = 0;
            foreach (Mesh mesh in Meshes(root)) {
                try {
                    string material = mesh.GetMaterial()?.Name ?? "(unnamed material)";
                    result.Add(new TextureSurface {
                        MeshIndex = index,
                        Material = material,
                        DisplayName = $"Surface {index + 1}: {material}",
                    });
                } catch { }
                index++;
            }
            return result;
        }

        public static int ApplyAll(GameEntity? root, PlacedEntity placed, out string error) {
            error = "";
            if (root == null || placed?.TextureOverrides == null || placed.TextureOverrides.Count == 0) return 0;
            int applied = 0;
            foreach (TextureOverride item in placed.TextureOverrides) {
                if (string.IsNullOrWhiteSpace(item.Image) || string.IsNullOrWhiteSpace(item.Material)) continue;
                string path = IOPath.Combine(ProjectSerializer.TexturesPath, IOPath.GetFileName(item.Image));
                Texture? texture = LoadTexture(path, item.Alpha, out string loadError);
                if (texture == null) { error = loadError; continue; }
                int meshIndex = 0;
                foreach (Mesh mesh in Meshes(root)) {
                    try {
                        Material original = mesh.GetMaterial();
                        bool legacyMaterialMatch = item.MeshIndex < 0 &&
                            original != null && string.Equals(original.Name, item.Material, StringComparison.OrdinalIgnoreCase);
                        bool exactSurfaceMatch = item.MeshIndex == meshIndex;
                        if (!legacyMaterialMatch && !exactSurfaceMatch) { meshIndex++; continue; }
                        if (original == null) { meshIndex++; continue; }
                        // A saved surface index must still name the same material. If a prefab was
                        // altered after authoring, skipping it is safer than painting a different
                        // surface that merely inherited the old index.
                        if (exactSurfaceMatch && !string.Equals(original.Name, item.Material, StringComparison.OrdinalIgnoreCase)) {
                            error = $"Surface {meshIndex + 1} no longer matches material '{item.Material}'; it was skipped.";
                            meshIndex++;
                            continue;
                        }
                        Texture? originalDiffuse = original.GetTexture(Material.MBTextureType.DiffuseMap);
                        Texture? originalDiffuse2 = original.GetTexture(Material.MBTextureType.DiffuseMap2);
                        Material custom = original.CreateCopy();
                        custom.SetTexture(Material.MBTextureType.DiffuseMap, texture);
                        // A sizeable number of Bannerlord materials render their visible colour from
                        // DiffuseMap2 (tableau/tint/blended materials) even though DiffuseMap is also
                        // present. The first implementation replaced only slot 0 and then counted the
                        // SetMaterial call as success, so these assets reported success without changing
                        // on screen. Preserve a genuinely unused second slot, but replace it whenever the
                        // source material actually has one.
                        bool replacedDiffuse2 = originalDiffuse2 != null;
                        if (replacedDiffuse2)
                            custom.SetTexture(Material.MBTextureType.DiffuseMap2, texture);

                        // Carry over the maps that light the surface. Assigning DiffuseMap clears the
                        // material's UsingSpecularMap flag and nothing puts it back - measured on
                        // Homesteads' dog material, where the specular texture survived in its slot
                        // while the flag read False. With that flag off the shader falls back to the
                        // diffuse alpha for specular, which is how an untouched alpha channel could
                        // turn a prop almost black in ambient light. Re-assigning the slots does not
                        // restore the flag, but it keeps the textures, and it is a texture write
                        // rather than a shader-flag write - the latter can request an uncompiled
                        // permutation and lock the renderer.
                        foreach (Material.MBTextureType slot in new[] {
                                     Material.MBTextureType.SpecularMap,
                                     Material.MBTextureType.BumpMap,
                                     Material.MBTextureType.EnvironmentMap }) {
                            try {
                                Texture? kept = original.GetTexture(slot);
                                if (kept != null) custom.SetTexture(slot, kept);
                            } catch {
                                // A slot the material does not use is not an error.
                            }
                        }
                        // Preserve the source material's shader and transparency state. The HSR
                        // picture frame deliberately enables alpha for its one known material, but
                        // doing that to an arbitrary Bannerlord asset can change opaque meshes and
                        // request a shader permutation the game did not compile.
                        // Per-material state, applied before the material goes on the mesh.
                        // These two are the safe pair: they do not touch shader flags, so they
                        // cannot ask the renderer for a permutation the game never compiled.
                        if (item.BlendMode >= 0 && item.BlendMode < (int)Material.MBAlphaBlendMode.Total) {
                            try { custom.SetAlphaBlendMode((Material.MBAlphaBlendMode)item.BlendMode); }
                            catch (Exception ex) {
                                TraceLogger.Write(nameof(TextureOverrideApplicator),
                                    "Blend mode " + item.BlendMode + " refused: " + ex.GetType().Name);
                            }
                        }
                        if (item.AlphaCutoff >= 0) {
                            try { custom.SetAlphaTestValue(Math.Min(100, item.AlphaCutoff) / 100f); }
                            catch (Exception ex) {
                                TraceLogger.Write(nameof(TextureOverrideApplicator),
                                    "Alpha cutoff " + item.AlphaCutoff + " refused: " + ex.GetType().Name);
                            }
                        }

                        mesh.SetMaterial(custom);

                        // Do not treat the temporary material copy as proof that the mesh accepted it.
                        // Mesh.SetMaterial can silently retain the original native material on some
                        // prefabs; checking <c>custom</c> alone made the UI report a successful
                        // texture assignment even though nothing on screen changed.
                        Material active = mesh.GetMaterial();
                        bool materialAssigned = SameNativeMaterial(active, custom);
                        Texture? verifiedDiffuse = active?.GetTexture(Material.MBTextureType.DiffuseMap);
                        Texture? verifiedDiffuse2 = replacedDiffuse2
                            ? active?.GetTexture(Material.MBTextureType.DiffuseMap2)
                            : null;
                        bool slot0Changed = SameNativeTexture(verifiedDiffuse, texture);
                        bool slot1Changed = !replacedDiffuse2 || SameNativeTexture(verifiedDiffuse2, texture);
                        TraceLogger.Write(nameof(TextureOverrideApplicator),
                            $"Applied '{IOPath.GetFileName(path)}' ({texture.Width}x{texture.Height}) to surface {meshIndex + 1}, material " +
                            $"'{original.Name}', slots DiffuseMap{(replacedDiffuse2 ? "+DiffuseMap2" : "")}; " +
                            $"source textures [{TextureName(originalDiffuse)}, {TextureName(originalDiffuse2)}], " +
                            $"active '{active?.Name ?? "none"}', assigned {materialAssigned}, " +
                            $"verified [{slot0Changed}, {slot1Changed}].");
                        if (!materialAssigned || !slot0Changed || !slot1Changed)
                            error = $"Bannerlord did not retain the requested texture on material '{original.Name}'.";
                        else {
                            // Count only a material whose native slots can be read back successfully.
                            // Keeping the material alive also keeps its dynamically-created PNG alive.
                            Materials.Add(custom);
                            applied++;
                        }
                    } catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    meshIndex++;
                }
            }
            return applied;
        }

        /// <summary>
        /// Reads a PNG and, when <paramref name="alpha"/> is 0-255, rewrites every alpha byte.
        ///
        /// <para>The cache key carries the alpha because the same PNG at two levels is two different
        /// textures; keying on the path alone would hand the second surface the first one's look.</para>
        ///
        /// <para>A forced alpha has to go through the manual decode - the engine loader returns the
        /// file as authored - so that faster route is only taken for an unmodified image.</para>
        /// </summary>
        private static Texture? LoadTexture(string path, int alpha, out string error) {
            error = "";
            string key = alpha < 0 ? path : path + "|a=" + alpha;
            if (Textures.TryGetValue(key, out Texture cached)) return cached;

            // The engine's asset loader is NOT used here, and that is a correction.
            //
            // It was tried first, on the theory that a texture built by CreateFromByteArray cannot be
            // marked sRGB and would therefore read too dark in ambient light. That theory was wrong -
            // it made no visible difference - and the real cause turned out to be the specular flag.
            //
            // What the attempt DID do was swap red and blue: LoadTextureFromPath hands back its
            // channels in the opposite order to CreateFromByteArray, which is the one that matches
            // PngDecoder. Measured on a dog coat - 167/167/26 yellow in the file arrived as cyan.
            //
            // It was also gated on "alpha < 0", so an override WITH an alpha value took the manual
            // path and one without took the engine's. Two textures from the same folder therefore
            // came out in different channel orders depending on an unrelated setting, which is a
            // worse bug than the one the gate was written around.

            byte[]? pixels = PngDecoder.ReadRgba(path, out int w, out int h, out error);
            if (pixels == null) return null;
            if (alpha >= 0) {
                byte level = (byte)(alpha > 255 ? 255 : alpha);
                for (int a = 3; a < pixels.Length; a += 4) pixels[a] = level;
            }
            try {
                Texture made = Texture.CreateFromByteArray(pixels, w, h);
                if (made == null) { error = "Bannerlord could not create " + IOPath.GetFileName(path) + "."; return null; }
                // Pinned against engine eviction. A cached CreateFromByteArray texture has no file to
                // reload from, and the engine WILL stream it down - measured on Homesteads' dog coats,
                // where a cached 2048x2048 coat exported as a blank 512x512 once it had gone black.
                try { made.SetTextureAsAlwaysValid(); made.PreloadTexture(true); } catch { }
                Textures[key] = made; return made;
            } catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>
        /// One line describing what a surface's material actually is, for the panel.
        ///
        /// <para>The panel used to show only a material NAME, which says nothing about how the image
        /// will be used - in particular whether the alpha channel is opacity or the specular level.
        /// That difference changes what a texture does and cost real time to work out by experiment.
        /// Reading these flags is free; writing them is what locks the renderer, and is not done
        /// anywhere.</para>
        /// </summary>
        internal static string DescribeMaterial(Material? material) {
            if (material == null) return "no material";
            try {
                var slots = new System.Text.StringBuilder();
                var wanted = new System.Collections.Generic.List<System.Tuple<Material.MBTextureType, string>> {
                    System.Tuple.Create(Material.MBTextureType.DiffuseMap, "Diffuse"),
                    System.Tuple.Create(Material.MBTextureType.DiffuseMap2, "Diffuse2"),
                    System.Tuple.Create(Material.MBTextureType.BumpMap, "Bump"),
                    System.Tuple.Create(Material.MBTextureType.SpecularMap, "Specular"),
                };
                foreach (var pair in wanted) {
                    bool set;
                    try { set = material.GetTexture(pair.Item1) != null; } catch { set = false; }
                    if (!set) continue;
                    if (slots.Length > 0) slots.Append("+");
                    slots.Append(pair.Item2);
                }
                // The flag, reported as a flag. Asserting what it MEANS for the alpha channel is
                // what led Homesteads to write a low value into every alpha byte of a dog coat and
                // get a transparent dog rather than a matte one.
                string meaning = material.UsingSpecularAlpha
                    ? "specularAlpha=True (alpha still behaves as opacity when written)"
                    : "alpha = opacity";
                return meaning + "   maps: " + (slots.Length == 0 ? "none" : slots.ToString());
            } catch (Exception ex) {
                return "material state unreadable (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>The material currently on a given surface of a placed entity.</summary>
        internal static Material? MaterialOfSurface(GameEntity? root, int meshIndex) {
            if (root == null) return null;
            int index = 0;
            foreach (Mesh mesh in Meshes(root)) {
                if (index++ != meshIndex) continue;
                try { return mesh.GetMaterial(); } catch { return null; }
            }
            return null;
        }

        private static bool SameNativeTexture(Texture? first, Texture? second) {
            if (first == null || second == null) return false;
            try { return first.Pointer == second.Pointer; }
            catch { return ReferenceEquals(first, second); }
        }

        private static bool SameNativeMaterial(Material? first, Material? second) {
            if (first == null || second == null) return false;
            try { return first.Pointer == second.Pointer; }
            catch { return ReferenceEquals(first, second); }
        }

        private static string TextureName(Texture? texture) {
            if (texture == null) return "none";
            try {
                string name = texture.Name;
                return string.IsNullOrWhiteSpace(name) ? $"unnamed {texture.Width}x{texture.Height}" : name;
            } catch { return "unreadable"; }
        }

        private static IEnumerable<Mesh> Meshes(GameEntity root) {
            foreach (GameEntity entity in root.GetEntityAndChildren()) {
                var seen = new HashSet<Mesh>();
                for (int m = 0; m < entity.MultiMeshComponentCount; m++) {
                    MetaMesh meta = entity.GetMetaMesh(m); if (meta == null) continue;
                    for (int i = 0; i < meta.MeshCount; i++) {
                        Mesh mesh = meta.GetMeshAtIndex(i); if (mesh != null && seen.Add(mesh)) yield return mesh;
                    }
                }
                if (seen.Count == 0) { Mesh only = entity.GetFirstMesh(); if (only != null) yield return only; }
            }
        }
    }
}
