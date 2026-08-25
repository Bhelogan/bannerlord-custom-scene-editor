using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using CustomSceneCreator.Catalog;
using CustomSceneCreator.Editing;
using TaleWorlds.Library;

namespace CustomSceneCreator.IO {
    /// <summary>
    /// Converts a Custom Scene Creator project into a complete, uniquely named derived SceneObj
    /// folder that the official Modding Kit can open. CSC bakes every authored cutout, ground
    /// addition, and elevated route into the copied navmesh; the Kit remains an optional fallback.
    /// </summary>
    internal static class ModdingKitSceneExporter {
        private const string WorkbenchModule = "CustomSceneCreator";

        public static ExportResult Export(SceneProject project, string requestedName) {
            ExportResult result = ExportAtRoot(project, requestedName, BasePath.Name);
            if (result.Success) CreateLaunchProject(project, Path.GetFileName(result.Path));
            return result;
        }

        /// <summary>
        /// The settlement entry opens Saved Projects first. A derived scene used to be reachable
        /// only through New - Pick a Scene or csc.open, which made a successful export look absent.
        /// Give it an empty launch project: its objects are already baked into scene.xscene and must
        /// not be instantiated a second time by the project layer.
        /// </summary>
        private static void CreateLaunchProject(SceneProject source, string sceneId) {
            if (string.IsNullOrWhiteSpace(sceneId) || ProjectSerializer.Load(sceneId) != null) return;
            var launchProject = new SceneProject {
                Name = sceneId,
                TargetScene = sceneId,
                SceneLevels = source.SceneLevels,
            };
            if (!ProjectSerializer.Save(launchProject)) {
                TraceLogger.Write(nameof(ModdingKitSceneExporter),
                    $"Derived scene '{sceneId}' was created, but its Saved Projects launcher was not.");
            }
        }

        /// <summary>Root-injected seam used by the filesystem smoke test and future tooling.</summary>
        internal static ExportResult ExportAtRoot(
            SceneProject project, string requestedName, string gameRoot) {
            string sceneId = ToSceneId(requestedName);
            if (sceneId.Length == 0)
                return Fail("Give the Modding Kit scene a name containing letters or numbers.");

            string modulesRoot = Path.Combine(gameRoot, "Modules");
            string workbenchRoot = Path.Combine(modulesRoot, WorkbenchModule);
            string sceneObjRoot = Path.Combine(workbenchRoot, "SceneObj");
            string sceneEditRoot = Path.Combine(workbenchRoot, "SceneEditData");
            string destination = Path.Combine(sceneObjRoot, sceneId);
            string editDestination = Path.Combine(sceneEditRoot, sceneId);

            string navmeshNote = "";

            try {
                string? source = FindSourceSceneFolder(modulesRoot, project.TargetScene, out string sourceProblem);
                if (source == null) return Fail(sourceProblem);

                // Never overwrite a prior export: that folder may now contain hours of Modding Kit
                // navmesh work. A different export name is cheaper than an accidental data loss.
                if (Directory.Exists(destination) || Directory.Exists(editDestination)) {
                    return Fail(
                        $"Scene '{sceneId}' already exists in the CustomSceneCreator workbench. " +
                        "Choose a new name; existing Modding Kit work is never overwritten.");
                }

                Directory.CreateDirectory(sceneObjRoot);
                string temporary = Path.Combine(
                    sceneObjRoot, $".{sceneId}.exporting-{Guid.NewGuid():N}");

                try {
                    Directory.CreateDirectory(temporary);
                    CopyPublishedScene(source, temporary);
                    WriteDerivedScene(project, source, temporary, sceneId);
                    NavMeshCutoutManifestExporter.WriteIntoFolder(project, temporary);
                    navmeshNote = BakeExportedNavMesh(project, temporary);
                    WriteHandoffReceipt(project, source, temporary, sceneId, navmeshNote);

                    ValidateDerivedScene(temporary, sceneId);
                    Directory.Move(temporary, destination);
                } catch {
                    TryDeleteTemporary(temporary, sceneObjRoot);
                    throw;
                }

                bool copiedEditData = CopySceneEditDataIfPresent(
                    source, project.TargetScene, editDestination, sceneEditRoot);

                try {
                    // Catalog registration is a convenience for the current browser session. The
                    // completed files are the authoritative result and must not be reported as a
                    // failed export if catalog refresh encounters a damaged optional catalog.
                    SceneCatalog.RegisterDerived(
                        sceneId,
                        project.SceneLevels,
                        noNavMesh: !File.Exists(Path.Combine(destination, "navmesh.bin")));
                } catch (Exception ex) {
                    TraceLogger.Write(nameof(ModdingKitSceneExporter),
                        $"Derived scene was created, but browser registration failed: {ex.Message}");
                }

                if (navmeshNote.Length == 0) {
                    navmeshNote = File.Exists(Path.Combine(destination, "navmesh.bin"))
                        ? "Its navmesh is the source scene's, unchanged."
                        : "The source had no navmesh; generate one in the Modding Kit.";
                }
                string editDataNote = copiedEditData
                    ? " Source SceneEditData was copied too."
                    : " Source SceneEditData was not available; the Kit may create editor data on save.";

                TraceLogger.Write(nameof(ModdingKitSceneExporter),
                    $"Exported Modding Kit scene '{sceneId}' from '{source}' to '{destination}'.");

                return new ExportResult {
                    Success = true,
                    Path = destination,
                    Message =
                        $"Modding Kit scene '{sceneId}' created in CustomSceneCreator/SceneObj. " +
                        navmeshNote + editDataNote,
                };
            } catch (Exception ex) {
                TraceLogger.WriteException(nameof(ModdingKitSceneExporter),
                    $"Failed exporting Modding Kit scene '{sceneId}'", ex);
                return Fail($"Modding Kit scene export failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Bakes the exported folder's own navmesh so the scene leaves here complete.
        ///
        /// An exported SceneObj is meant to be dropped into a module and used. Shipping it with the
        /// source scene's navmesh would mean buildings the author placed are still walkable and
        /// ground they opened up still is not.
        /// </summary>
        private static string BakeExportedNavMesh(SceneProject project, string sceneFolder) {
            if (!SceneNavMeshBaker.HasWork(project)) return "";

            SceneNavMeshBaker.Result result = SceneNavMeshBaker.BakeExportedScene(project, sceneFolder);
            if (!result.Attempted) return "";

            return result.Success
                ? result.Message + " (baked into this scene folder)"
                : result.Message + " Its navmesh is the source scene's, unchanged.";
        }

        /// <summary>Bannerlord scene identifiers are safest as lowercase ASCII plus underscores.</summary>
        internal static string ToSceneId(string requestedName) {
            if (string.IsNullOrWhiteSpace(requestedName)) return "";

            var result = new StringBuilder();
            bool previousUnderscore = false;
            foreach (char raw in requestedName.Trim().ToLowerInvariant()) {
                char c = raw;
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (valid) {
                    result.Append(c);
                    previousUnderscore = false;
                } else if (!previousUnderscore && result.Length > 0) {
                    result.Append('_');
                    previousUnderscore = true;
                }
            }

            string id = result.ToString().Trim('_');
            if (id.Length == 0) return "";
            if (!id.StartsWith("csc_", StringComparison.Ordinal)) id = "csc_" + id;
            return id.Length <= 80 ? id : id.Substring(0, 80).TrimEnd('_');
        }

        private static string? FindSourceSceneFolder(
            string modulesRoot, string sceneName, out string problem) {
            problem = "";
            if (string.IsNullOrWhiteSpace(sceneName)) {
                problem = "The project does not name a source scene.";
                return null;
            }

            SceneEntry? catalogEntry = SceneCatalog.Find(sceneName);
            if (catalogEntry != null && !string.IsNullOrWhiteSpace(catalogEntry.Module)) {
                string catalogPath = Path.Combine(
                    modulesRoot, catalogEntry.Module, "SceneObj", sceneName);
                if (HasSceneXml(catalogPath)) return catalogPath;
            }

            var candidates = new List<string>();
            if (Directory.Exists(modulesRoot)) {
                foreach (string module in Directory.GetDirectories(modulesRoot)) {
                    string candidate = Path.Combine(module, "SceneObj", sceneName);
                    if (HasSceneXml(candidate)) candidates.Add(candidate);
                }
            }

            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count == 0) {
                problem =
                    $"Could not find SceneObj/{sceneName}/scene.xscene in any installed module.";
                return null;
            }

            problem =
                $"More than one installed module provides scene '{sceneName}'. Remove the shadowing " +
                "copy or regenerate the scene catalog so the source module is unambiguous: " +
                string.Join(", ", candidates.Select(Path.GetDirectoryName));
            return null;
        }

        private static bool HasSceneXml(string directory) =>
            Directory.Exists(directory) && File.Exists(Path.Combine(directory, "scene.xscene"));

        private static void CopyPublishedScene(string source, string destination) {
            foreach (string file in Directory.GetFiles(source)) {
                if (string.Equals(Path.GetFileName(file), "scene.xscene", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }

            foreach (string directory in Directory.GetDirectories(source)) {
                // ShaderCache is generated for a specific scene identity and graphics setup. Carrying
                // it into a renamed scene is unnecessary and has caused scene-shadowing confusion.
                if (string.Equals(Path.GetFileName(directory), "ShaderCache", StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private static void CopyDirectory(string source, string destination) {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            foreach (string child in Directory.GetDirectories(source))
                CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }

        private static void WriteDerivedScene(
            SceneProject project, string source, string destination, string sceneId) {
            string sourceXml = Path.Combine(source, "scene.xscene");
            var document = new XmlDocument { PreserveWhitespace = true };
            document.Load(sourceXml);

            XmlElement root = document.DocumentElement
                ?? throw new InvalidDataException("Source scene.xscene has no root element.");
            if (!string.Equals(root.Name, "scene", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source scene.xscene root is not <scene>.");
            root.SetAttribute("name", sceneId);

            XmlElement? entities = root.SelectSingleNode("entities") as XmlElement;
            if (entities == null) {
                entities = document.CreateElement("entities");
                XmlNode? before = root.SelectSingleNode("terrain");
                if (before != null) root.InsertBefore(entities, before);
                else root.AppendChild(entities);
            }

            entities.AppendChild(document.CreateWhitespace("\n\t\t"));
            entities.AppendChild(document.CreateComment(
                " Added by Custom Scene Creator; matching authored navmesh changes are baked during export. "));

            string fragmentXml = SceneExporter.BuildSceneEntityFragment(project, indent: "\t\t");
            if (!string.IsNullOrWhiteSpace(fragmentXml)) {
                entities.AppendChild(document.CreateWhitespace("\n"));
                XmlDocumentFragment fragment = document.CreateDocumentFragment();
                fragment.InnerXml = fragmentXml;
                entities.AppendChild(fragment);
            }
            entities.AppendChild(document.CreateWhitespace("\n\t"));

            var settings = new XmlWriterSettings {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                NewLineHandling = NewLineHandling.None,
            };
            using (XmlWriter writer = XmlWriter.Create(
                Path.Combine(destination, "scene.xscene"), settings)) {
                document.Save(writer);
            }
        }

        private static void ValidateDerivedScene(string directory, string expectedName) {
            string xmlPath = Path.Combine(directory, "scene.xscene");
            if (!File.Exists(xmlPath))
                throw new InvalidDataException("Derived scene.xscene was not written.");

            var document = new XmlDocument();
            document.Load(xmlPath);
            XmlElement root = document.DocumentElement
                ?? throw new InvalidDataException("Derived scene has no root element.");
            if (!string.Equals(root.GetAttribute("name"), expectedName, StringComparison.Ordinal))
                throw new InvalidDataException("Derived scene internal name does not match its folder.");
            if (root.SelectSingleNode("entities") == null)
                throw new InvalidDataException("Derived scene has no <entities> block.");
        }

        private static bool CopySceneEditDataIfPresent(
            string sourceSceneFolder,
            string sourceName,
            string destination,
            string sceneEditRoot) {
            string temporary = destination + $".exporting-{Guid.NewGuid():N}";
            try {
                DirectoryInfo? sceneObj = Directory.GetParent(sourceSceneFolder);
                DirectoryInfo? module = sceneObj?.Parent;
                if (module == null) return false;

                string source = Path.Combine(module.FullName, "SceneEditData", sourceName);
                if (!Directory.Exists(source)) return false;

                Directory.CreateDirectory(sceneEditRoot);
                CopyDirectory(source, temporary);
                Directory.Move(temporary, destination);
                return true;
            } catch (Exception ex) {
                // Published SceneObj is already complete. Missing edit data should not discard it;
                // report the limitation and let the Kit recreate what it can.
                TraceLogger.Write(nameof(ModdingKitSceneExporter),
                    $"SceneEditData copy skipped: {ex.GetType().Name}: {ex.Message}");
                TryDeleteTemporary(temporary, sceneEditRoot);
                return false;
            }
        }

        private static void WriteHandoffReceipt(
            SceneProject project, string source, string destination, string sceneId,
            string navmeshNote) {
            bool hasNavmesh = File.Exists(Path.Combine(destination, "navmesh.bin"));
            string navmesh = !hasNavmesh
                ? "NOT PRESENT - GENERATE IN THE MODDING KIT"
                : navmeshNote.Length > 0
                    ? navmeshNote
                    : "SOURCE NAVMESH COPIED UNCHANGED (THE PROJECT CONTAINED NO NAVMESH AUTHORING)";
            string text =
                "CUSTOM SCENE CREATOR - MODDING KIT HANDOFF\r\n" +
                "===========================================\r\n" +
                $"Derived scene: {sceneId}\r\n" +
                $"Source scene:  {project.TargetScene}\r\n" +
                $"Source folder: {source}\r\n" +
                $"Scene levels used while laying out: {project.SceneLevels}\r\n" +
                $"Placed objects inserted: {project.Entities.Count}\r\n" +
                $"Created (UTC): {DateTime.UtcNow:O}\r\n" +
                $"Navmesh status: {navmesh}\r\n\r\n" +
                "NEXT STEPS\r\n" +
                "1. Read the navmesh status above. Resolve every skipped operation before release.\r\n" +
                "2. Reopen the saved project and use Walk Around to test followers on tunnels,\r\n" +
                "   stairs, ramps, platforms, and routes around solid objects.\r\n" +
                "3. Use Raid-Scale Battle when this scene is intended for combat.\r\n" +
                "4. The Modding Kit is optional when every authored operation applied. Use it only\r\n" +
                "   for terrain work or manual navmesh edits CSC cannot express. If only navigation\r\n" +
                "   changed, use File > Save Navigation Mesh; broad Save Scene may stage terrain.\r\n" +
                "5. Move the complete SceneObj folder to the final owning module before release.\r\n" +
                "   Move SceneEditData too only if you want future Modding Kit editing; the retail\r\n" +
                "   game does not require it. Keep the folder and internal scene names identical.\r\n\r\n" +
                "The .navcut.json beside this receipt is authoring/debug data. Bannerlord does not\r\n" +
                "load it directly. The runtime navmesh is this folder's navmesh.bin.\r\n";
            File.WriteAllText(Path.Combine(destination, "CSC_MODDING_KIT_HANDOFF.txt"), text, Encoding.UTF8);
        }

        private static void TryDeleteTemporary(string temporary, string sceneObjRoot) {
            try {
                string fullTemporary = Path.GetFullPath(temporary);
                string fullRoot = Path.GetFullPath(sceneObjRoot)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (fullTemporary.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(fullTemporary)) {
                    Directory.Delete(fullTemporary, recursive: true);
                }
            } catch { }
        }

        private static ExportResult Fail(string message) =>
            new ExportResult { Success = false, Message = message };
    }
}
