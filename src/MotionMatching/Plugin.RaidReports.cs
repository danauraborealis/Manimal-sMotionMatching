using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx.Configuration;
using EFT;
using UnityEngine;
using ZLinq;

namespace Manimal.MotionMatching
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> _automaticRaids, _developerControls;
        private ConfigEntry<float> _animationDistance;
        private ConfigEntry<bool> _animationVisibility;
        internal float AnimationDistanceValue => _animationDistance.Value;
        internal bool AnimationVisibilityValue => _animationVisibility.Value;
        internal ConfigEntry<bool> _raidReactions;
        private RaidReportRecorder _raidRecorder;
        private bool _raidAttempted;
        private int _raidRenderFrame = -1;
        private string _lastRaidReport;
        private readonly List<Task<string>> _raidSaves = new List<Task<string>>();

        internal object RaidReportStatus() => new { Active = _raidRecorder != null,
            Attempted = _raidAttempted, Bots = _fleet?.Attached ?? 0,
            Saving = _raidSaves.Count, LastReport = _lastRaidReport,
            Automatic = _automaticRaids.Value, DeveloperHotkeys = _developerControls.Value };

        private void BindRaidSettings()
        {
            _automaticRaids = Config.Bind("Player test", "Automatic raids", true,
                "Apply locomotion and collect a bounded local diagnostic report during normal solo raids. No keys or test routes required.");
            _raidReactions = Config.Bind("Player test", "Hit reactions", true,
                "Enable damage reactions and subdued moving-jump landing recovery for normal bots.");
            _developerControls = Config.Bind("Player test", "Developer hotkeys", false,
                "Enable the optional capture/puppet keyboard shortcuts. Keep disabled for normal play.");
            _animationDistance = Config.Bind("Performance", "Animation distance", 80f,
                new ConfigDescription("Maximum distance in metres to activate normal-raid animation. Active bots get a 15 m exit margin. Bots outside range use native Tarkov animation.",
                    new AcceptableValueRange<float>(20f, 300f)));
            _animationVisibility = Config.Bind("Performance", "Cull unseen bots", true,
                "Outside 20 m, use Tarkov's visibility flag to limit normal-raid animation. Allow 2 seconds before deactivating an unseen bot. This is not a separate line-of-sight raycast.");
        }

        private void TickRaidReports()
        {
            if (!_automaticRaids.Value)
            {
                if (_raidRecorder != null) StopFleet("Automatic raids disabled");
                return;
            }
            if (_raidAttempted || !_hooksReady || _fleet != null || _capture != null || _puppet != null) return;
            _raidAttempted = true;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
            {
                Report("Automatic locomotion test supports solo SPT; Fika replication is not implemented.");
                return;
            }
            string error = null;
            var db = LoadPoseDatabase(ref error);
            string folder = Path.GetDirectoryName(Info.Location);
            // Snapshot only deliberately selected environment fields; never serialize a player/profile or config file.
            var metadata = new
            {
                schema = "motion-raid-v1", version = ModInfo.Version, map = _world.LocationId,
                unity = Application.unityVersion, game = Application.version,
                gameAssembly = typeof(Player).Assembly.ManifestModule.ModuleVersionId.ToString(),
                sptReflection = typeof(SPT.Reflection.Patching.ModulePatch).Assembly.GetName().Version.ToString(),
                dllSha256 = FullFileHash(Info.Location),
                poseSha256 = FullFileHash(Path.Combine(folder, _poseDatabasePath.Value)),
                reactionSha256 = FullFileHash(Path.Combine(folder, "reaction_posedb.json")),
                reactionPolicy = new { baseChance = HitReactionPolicy.BaseChance,
                    chancePerDamage = HitReactionPolicy.ChancePerDamage,
                    maximumChance = HitReactionPolicy.MaximumChance,
                    windowSeconds = HitReactionPolicy.QuietResetSeconds,
                    cooldownSeconds = HitReactionPolicy.CooldownSeconds,
                    weightModifiers = false, bossModifiers = false },
                plugins = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.AsValueEnumerable()
                    .Select(p => new { guid = p.Metadata.GUID, version = p.Metadata.Version.ToString() }).ToArray(),
                settings = new { clip = _poseClip.Value, footPlacer = _footPlacerConfig.Value,
                    animationDistance = _animationDistance.Value, cullUnseenBots = _animationVisibility.Value,
                    footLock = _footLockConfig.Value, lean = _bodyLeanConfig.Value,
                    leanStrength = _bodyLeanStrength.Value, weaponLevel = _bodyLeanWeaponLevel.Value,
                    hipShift = _hipShift.Value, pelvisTilt = _pelvisTilt.Value,
                    runBand = _runBandLegs.Value, sprintLegs = _sprintLegs.Value,
                    phaseLock = _animatorPhaseLock.Value, variations = _variations.Value,
                    inertia = _botInertia.Value, acceleration = _accelerationLimit.Value,
                    sprintInertia = _nativeSprintInertia.Value, reactions = _raidReactions.Value,
                    preferGrunt = _preferGrunt.Value, clipDrivenStarts = _clipDrivenStarts.Value,
                    strideWarp = _strideWarp.Value, sprintTransitions = _sprintTransitions.Value,
                    sprintCycleBridge = _sprintCycleBridge.Value, walkFamily = _walkFamily.Value,
                    alyxCycles = _alyxCycles.Value, kneeBendFloor = _kneeBendFloor.Value,
                    turningStarts = _turningStarts.Value, turnInPlace = _turnInPlace.Value,
                    repickStart = _repickStart.Value, inertialize = _inertialize.Value }
            };
            string directory = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput", ModInfo.Name, "Reports");
            _raidRecorder = new RaidReportRecorder(directory, metadata);
            if (db == null)
            {
                _raidRecorder.Event(new { type = "initialization_failed", reason = "pose_database_unavailable" });
                FinishRaidReport("pose_database_unavailable");
                Report("Automatic raid did not start: " + error);
                return;
            }
            try
            {
                _fleet = new Fleet(this, db, _poseClip.Value, directory, 1, _raidRecorder)
                { MaxBots = 128, FollowViewer = false };
                _fleetStarted = Time.realtimeSinceStartup;
                Report("Automatic raid test active. A diagnostic ZIP will be saved when the raid ends.");
            }
            catch
            {
                FinishRaidReport("initialization_failed");
                throw;
            }
        }

        private void YieldRaidToDeveloper()
        {
            _raidAttempted = true;
            if (_raidRecorder != null) StopFleet("Developer test started");
        }

        private void FinishRaidReport(string reason)
        {
            var recorder = _raidRecorder;
            _raidRecorder = null;
            if (recorder != null) _raidSaves.Add(recorder.Finish(reason));
        }

        private void PollRaidReports()
        {
            for (int i = _raidSaves.Count - 1; i >= 0; i--)
            {
                var task = _raidSaves[i];
                if (!task.IsCompleted) continue;
                _raidSaves.RemoveAt(i);
                if (task.IsFaulted) Logger.LogError("Raid report save failed: " + task.Exception);
                else if (!task.IsCanceled)
                {
                    _lastRaidReport = task.Result;
                    Logger.LogInfo("Raid report saved: " + task.Result);
                }
            }
        }

        private static string FullFileHash(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }
    }
}
