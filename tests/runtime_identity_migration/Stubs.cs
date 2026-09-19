using System.Collections.Generic;

namespace BepInEx
{
    public static class Paths
    {
        public static string ConfigPath;
        public static string BepInExRootPath;
    }
}

namespace Manimal.MotionMatching
{
    internal static class ModInfo
    {
        internal const string Guid = "com.manimal.motionmagic";
        internal const string Name = "Manimal-MotionMagic";
    }

    internal sealed class MigrationConfig
    {
        internal MigrationConfig(string configFilePath)
        {
            ConfigFilePath = configFilePath;
        }

        internal string ConfigFilePath { get; }
        internal int ReloadCount { get; private set; }

        internal void Reload()
        {
            ReloadCount++;
        }
    }

    internal sealed class MigrationLogger
    {
        internal readonly List<string> Infos = new List<string>();
        internal readonly List<string> Warnings = new List<string>();

        internal void LogInfo(string message) => Infos.Add(message);
        internal void LogWarning(string message) => Warnings.Add(message);
    }

    public sealed partial class Plugin
    {
        internal MigrationConfig Config { get; }
        internal MigrationLogger Logger { get; }

        internal Plugin(MigrationConfig config, MigrationLogger logger)
        {
            Config = config;
            Logger = logger;
        }

        internal void InvokeMigrateLegacySettingsForTest()
        {
            MigrateLegacySettings();
        }
    }
}
