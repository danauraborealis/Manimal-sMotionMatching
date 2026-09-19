using System;
using System.IO;
using System.Text;
using BepInEx;
using Manimal.MotionMatching;

internal static class Program
{
    private static int Main()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "runtime_identity_migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ImportsOnlyWhenTheNewConfigIsAbsentAndRewritesExactDefaults(root);
            ExistingNewConfigIsPreserved(root);
            ARepeatedMigrationDoesNotOverwriteTheImportedConfig(root);
            NoLegacyConfigMeansNoMigration(root);
            Console.WriteLine("runtime_identity_migration: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void ImportsOnlyWhenTheNewConfigIsAbsentAndRewritesExactDefaults(string root)
    {
        Fixture fixture = CreateFixture(root, "import", includeLegacy: true);
        string legacyRoot = Path.Combine(Paths.BepInExRootPath, "plugins", "Manimal-MotionMatching");
        string currentRoot = Path.Combine(Paths.BepInExRootPath, "plugins", "Manimal-MotionMagic");
        string absoluteLegacy = Path.Combine(legacyRoot, "reaction_posedb.json");
        string absoluteCurrent = Path.Combine(currentRoot, "reaction_posedb.json");

        string legacyContents = string.Join("\r\n", new[]
        {
            "## Settings file was created by plugin Manimal-MotionMatching",
            "## Plugin GUID: com.manimal.motionmatching",
            "[Pose playback]",
            "Database =   BepInEx/plugins/Manimal-MotionMatching/alyx_posedb.json   ",
            "Reaction = BepInEx\\plugins\\Manimal-MotionMatching\\reaction_posedb.json",
            "Absolute = " + absoluteLegacy,
            "Custom = BepInEx/plugins/Manimal-MotionMatching/custom.json",
            "NearMiss = BepIn/plugins/Manimal-MotionMatching/alyx_posedb.json",
            "Unknown = BepInEx/plugins/Manimal-MotionMatching/alyx_posedb.json.bak",
            "Plain = custom/alyx_posedb.json"
        }) + "\r\n";
        File.WriteAllText(fixture.LegacyPath, legacyContents, new UTF8Encoding(false));

        var plugin = new Plugin(fixture.Config, fixture.Logger);
        plugin.InvokeMigrateLegacySettingsForTest();

        Check(File.Exists(fixture.CurrentPath), "migration did not create the new config file");
        string migrated = File.ReadAllText(fixture.CurrentPath);
        string expected = string.Join("\r\n", new[]
        {
            "## Settings file was created by plugin Manimal-MotionMagic",
            "## Plugin GUID: com.manimal.motionmagic",
            "[Pose playback]",
            "Database =   BepInEx/plugins/Manimal-MotionMagic/alyx_posedb.json   ",
            "Reaction = BepInEx\\plugins\\Manimal-MotionMagic\\reaction_posedb.json",
            "Absolute = " + absoluteCurrent,
            "Custom = BepInEx/plugins/Manimal-MotionMatching/custom.json",
            "NearMiss = BepIn/plugins/Manimal-MotionMatching/alyx_posedb.json",
            "Unknown = BepInEx/plugins/Manimal-MotionMatching/alyx_posedb.json.bak",
            "Plain = custom/alyx_posedb.json"
        }) + "\r\n";
        Equal(expected, migrated, "rewritten config");
        Equal(legacyContents, File.ReadAllText(fixture.LegacyPath), "legacy config must remain byte-for-byte unchanged");
        Check(fixture.Config.ReloadCount == 1, "successful migration should reload config exactly once");
        Check(fixture.Logger.Infos.Count == 1 && fixture.Logger.Infos[0].Contains("Manimal-MotionMagic", StringComparison.Ordinal),
            "successful migration should log the new plugin name");
        Check(!File.Exists(fixture.CurrentPath + ".motionmagic-migration.tmp"), "migration temporary file was left behind");
    }

    private static void ExistingNewConfigIsPreserved(string root)
    {
        Fixture fixture = CreateFixture(root, "existing", includeLegacy: true);
        string existing = "## current settings\r\n[General]\r\nEnabled = false\r\n";
        string legacy = "## legacy settings\r\nEnabled = true\r\n";
        File.WriteAllText(fixture.CurrentPath, existing, new UTF8Encoding(false));
        File.WriteAllText(fixture.LegacyPath, legacy, new UTF8Encoding(false));

        new Plugin(fixture.Config, fixture.Logger).InvokeMigrateLegacySettingsForTest();

        Equal(existing, File.ReadAllText(fixture.CurrentPath), "existing new config");
        Equal(legacy, File.ReadAllText(fixture.LegacyPath), "legacy config after existing-config check");
        Check(fixture.Config.ReloadCount == 0, "existing new config must not trigger a reload");
        Check(fixture.Logger.Infos.Count == 0, "existing new config must not report an import");
        Check(!File.Exists(fixture.CurrentPath + ".motionmagic-migration.tmp"), "existing-config check left a temporary file");
    }

    private static void ARepeatedMigrationDoesNotOverwriteTheImportedConfig(string root)
    {
        Fixture fixture = CreateFixture(root, "repeat", includeLegacy: true);
        string firstLegacy = "## Settings file was created by plugin Manimal-MotionMatching\n" +
            "## Plugin GUID: com.manimal.motionmatching\n" +
            "Database = BepInEx/plugins/Manimal-MotionMatching/alyx_posedb.json\n";
        File.WriteAllText(fixture.LegacyPath, firstLegacy, new UTF8Encoding(false));

        new Plugin(fixture.Config, fixture.Logger).InvokeMigrateLegacySettingsForTest();
        string imported = File.ReadAllText(fixture.CurrentPath);
        Check(fixture.Config.ReloadCount == 1, "first migration should reload once");

        string secondLegacy = "## changed after initial import\nEnabled = false\n";
        File.WriteAllText(fixture.LegacyPath, secondLegacy, new UTF8Encoding(false));
        var secondConfig = new MigrationConfig(fixture.CurrentPath);
        var secondLogger = new MigrationLogger();
        new Plugin(secondConfig, secondLogger).InvokeMigrateLegacySettingsForTest();

        Equal(imported, File.ReadAllText(fixture.CurrentPath), "repeated migration must preserve imported config");
        Check(secondConfig.ReloadCount == 0, "repeated migration must not reload an existing new config");
        Check(secondLogger.Infos.Count == 0, "repeated migration must not report a second import");
        Equal(secondLegacy, File.ReadAllText(fixture.LegacyPath), "legacy config after repeated migration");
    }

    private static void NoLegacyConfigMeansNoMigration(string root)
    {
        Fixture fixture = CreateFixture(root, "missing", includeLegacy: false);
        new Plugin(fixture.Config, fixture.Logger).InvokeMigrateLegacySettingsForTest();

        Check(!File.Exists(fixture.CurrentPath), "missing legacy config should not create a new config");
        Check(fixture.Config.ReloadCount == 0, "missing legacy config should not reload config");
        Check(fixture.Logger.Infos.Count == 0, "missing legacy config should not report an import");
    }

    private static Fixture CreateFixture(string root, string name, bool includeLegacy)
    {
        string fixtureRoot = Path.Combine(root, name);
        string bepinRoot = Path.Combine(fixtureRoot, "BepInEx");
        string configRoot = Path.Combine(bepinRoot, "config");
        Directory.CreateDirectory(configRoot);
        Paths.BepInExRootPath = bepinRoot;
        Paths.ConfigPath = configRoot;

        string legacyPath = Path.Combine(configRoot, "com.manimal.motionmatching.cfg");
        string currentPath = Path.Combine(configRoot, "com.manimal.motionmagic.cfg");
        if (includeLegacy)
            File.WriteAllText(legacyPath, "placeholder\n", new UTF8Encoding(false));
        return new Fixture(legacyPath, currentPath, new MigrationConfig(currentPath), new MigrationLogger());
    }

    private sealed class Fixture
    {
        internal Fixture(string legacyPath, string currentPath, MigrationConfig config, MigrationLogger logger)
        {
            LegacyPath = legacyPath;
            CurrentPath = currentPath;
            Config = config;
            Logger = logger;
        }

        internal string LegacyPath { get; }
        internal string CurrentPath { get; }
        internal MigrationConfig Config { get; }
        internal MigrationLogger Logger { get; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            int index = 0;
            int limit = Math.Min(expected.Length, actual.Length);
            while (index < limit && expected[index] == actual[index]) index++;
            string difference = index < limit
                ? " first difference at " + index + " (expected U+" + ((int)expected[index]).ToString("X4") + ", got U+" + ((int)actual[index]).ToString("X4") + ", expected context '" + Escape(Context(expected, index)) + "', got context '" + Escape(Context(actual, index)) + "')"
                : " length differs (expected " + expected.Length + ", got " + actual.Length + ")";
            throw new InvalidOperationException(label + difference + ": expected " + Escape(expected) + ", got " + Escape(actual));
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string Context(string value, int index)
    {
        int start = Math.Max(0, index - 24);
        int length = Math.Min(value.Length - start, 48);
        return value.Substring(start, length);
    }
}
