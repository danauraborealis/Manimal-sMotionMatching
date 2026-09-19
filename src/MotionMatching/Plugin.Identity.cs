using System;
using System.IO;
using System.Text;
using BepInEx;
using ZLinq;

namespace Manimal.MotionMatching
{
    public sealed partial class Plugin
    {
        // Keep these values stable so an installation can move from the old plugin
        // without making a user's settings or local test databases undiscoverable.
        internal const string LegacyModGuid = "com.manimal.motionmatching";
        internal const string LegacyDisplayName = "Manimal-MotionMatching";
        internal const string LegacyPluginDirectory = "Manimal-MotionMatching";

        // Call this before BindRaidSettings (and before any other Config.Bind call).
        // BepInEx creates Config with the new GUID, so replacing its file and
        // reloading here makes the subsequent binds consume the legacy values.
        private void MigrateLegacySettings()
        {
            try
            {
                string currentPath = Config.ConfigFilePath;
                string legacyPath = Path.Combine(BepInEx.Paths.ConfigPath, LegacyModGuid + ".cfg");
                if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(legacyPath)) return;
                if (File.Exists(currentPath) || string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(legacyPath), StringComparison.OrdinalIgnoreCase)) return;

                string directory = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);

                string temporaryPath = currentPath + ".motionmagic-migration.tmp";
                string contents = RewriteLegacyConfig(File.ReadAllText(legacyPath));
                File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
                if (File.Exists(currentPath))
                {
                    File.Delete(temporaryPath);
                    return;
                }

                File.Move(temporaryPath, currentPath);
                Config.Reload();
                Logger.LogInfo($"Imported settings from {LegacyDisplayName} into {ModInfo.Name}.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not import {LegacyDisplayName} settings; keeping the new defaults. {ex.Message}");
            }
        }

        private static string RewriteLegacyConfig(string contents)
        {
            if (contents == null) return string.Empty;
            string newline = contents.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string normalized = contents.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            return string.Join(newline, lines.AsValueEnumerable().Select(RewriteLegacyConfigLine).ToArray());
        }

        private static string RewriteLegacyConfigLine(string line)
        {
            if (line.StartsWith("## Settings file was created by plugin ", StringComparison.Ordinal))
                return "## Settings file was created by plugin " + ModInfo.Name;
            if (line.StartsWith("## Plugin GUID: ", StringComparison.Ordinal))
                return "## Plugin GUID: " + ModInfo.Guid;

            int equals = line.IndexOf('=');
            if (equals < 0) return line;
            string valueWithWhitespace = line.Substring(equals + 1);
            string value = valueWithWhitespace.Trim();
            if (!TryRewriteDefaultDataPath(value, out string replacement)) return line;
            int leading = valueWithWhitespace.Length - valueWithWhitespace.TrimStart().Length;
            int trailing = valueWithWhitespace.Length - valueWithWhitespace.TrimEnd().Length;
            return line.Substring(0, equals + 1)
                + new string(' ', leading)
                + replacement
                + new string(' ', trailing);
        }

        private static bool TryRewriteDefaultDataPath(string value, out string replacement)
        {
            string legacyRoot = Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins", LegacyPluginDirectory);
            string currentRoot = Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins", ModInfo.Name);
            string legacyForwardRoot = "BepInEx/plugins/" + LegacyPluginDirectory;
            string currentForwardRoot = "BepInEx/plugins/" + ModInfo.Name;
            string legacyBackRoot = "BepInEx\\plugins\\" + LegacyPluginDirectory;
            string currentBackRoot = "BepInEx\\plugins\\" + ModInfo.Name;
            var relative = new[] { "alyx_posedb.json", "reaction_posedb.json" }
                .AsValueEnumerable()
                .Select(fileName => new
                {
                    ForwardLegacy = legacyForwardRoot + "/" + fileName,
                    ForwardCurrent = currentForwardRoot + "/" + fileName,
                    BackLegacy = legacyBackRoot + "\\" + fileName,
                    BackCurrent = currentBackRoot + "\\" + fileName,
                    AbsoluteLegacy = Path.Combine(legacyRoot, fileName),
                    AbsoluteCurrent = Path.Combine(currentRoot, fileName)
                })
                .FirstOrDefault(candidate =>
                    string.Equals(value, candidate.ForwardLegacy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, candidate.BackLegacy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, candidate.AbsoluteLegacy, StringComparison.OrdinalIgnoreCase));
            if (relative != null)
            {
                replacement = string.Equals(value, relative.ForwardLegacy, StringComparison.OrdinalIgnoreCase)
                    ? relative.ForwardCurrent
                    : string.Equals(value, relative.BackLegacy, StringComparison.OrdinalIgnoreCase)
                        ? relative.BackCurrent
                        : relative.AbsoluteCurrent;
                return true;
            }

            replacement = null;
            return false;
        }
    }
}
