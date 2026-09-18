import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PLAYBACK = ROOT / "src" / "MotionMatching" / "PosePlayback.cs"


def extract_method(source: str, signature: str) -> str:
    """Return one complete C# method from the current production source."""
    start = source.index(signature)
    opening = source.index("{", start)
    depth = 0
    for position in range(opening, len(source)):
        character = source[position]
        if character == "{":
            depth += 1
        elif character == "}":
            depth -= 1
            if depth == 0:
                return source[start : position + 1]
    raise AssertionError(f"unterminated method: {signature}")


def latest_ref_pack(dotnet_root: Path) -> Path:
    packs = dotnet_root / "packs" / "Microsoft.NETCore.App.Ref"
    versions = sorted(
        (path for path in packs.iterdir() if path.is_dir()),
        key=lambda path: tuple(int(part) if part.isdigit() else part for part in path.name.split(".")),
        reverse=True,
    )
    if not versions:
        raise AssertionError(f"no .NET reference pack under {packs}")
    return versions[0] / "ref" / f"net{versions[0].name.split('.')[0]}.0"


def ramp(frames: int, first: int, value: float) -> list[float]:
    values = [0.0] * frames
    for index in range(max(0, first), frames):
        values[index] = value
    return values


def csharp_harness(helper: str, variation: str, use_zlinq: bool) -> str:
    # The production methods are inserted verbatim. Everything around them is a
    # deliberately tiny stand-in for Unity/runtime state needed by those methods.
    zlinq_fallback = "" if use_zlinq else textwrap.dedent(
        """
        namespace ZLinq
        {
            public static class HarnessValueEnumerable
            {
                public static System.Collections.Generic.IEnumerable<T> AsValueEnumerable<T>(
                    this System.Collections.Generic.IEnumerable<T> source) => source;
            }
        }
        """
    )
    template = textwrap.dedent(
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using ZLinq;
        using UnityEngine;

        __ZLINQ_FALLBACK__

        namespace UnityEngine
        {
            internal static class Mathf
            {
                public static float Abs(float value) => Math.Abs(value);
                public static float Max(float left, float right) => Math.Max(left, right);
                public static int Max(int left, int right) => Math.Max(left, right);
                public static float Min(float left, float right) => Math.Min(left, right);
                public static int Min(int left, int right) => Math.Min(left, right);
                private static float Repeat(float value, float length) => value - (float)Math.Floor(value / length) * length;
                public static float DeltaAngle(float current, float target)
                {
                    float delta = Repeat(target - current, 360f);
                    if (delta > 180f) delta -= 360f;
                    return delta;
                }
            }

            internal static class Time
            {
                public static float time => 12.5f;
            }
        }

        namespace Manimal.MotionMatching
        {
            internal sealed class PoseClip
            {
                public string Name;
                public int Frames;
                public int DesiredFeetFrame;
            }

            internal sealed class VariationPlaybackHarness
            {
                private sealed class MoveSet
                {
                    public float Yaw;
                    public float YawChange;
                    public PoseClip Start;
                    public float StartSpeed;
                    public float[] StartPeak;
                }

                private enum Phase { Start }

                private readonly List<MoveSet> _sets = new List<MoveSet>();
                private readonly List<string> _events = new List<string>();
                private float _speed;
                private float _travel;
                private float _phaseFor;
                private MoveSet _set;
                private bool _variation;
                private bool _startRepicked;
                private PoseClip _begunClip;
                private int _begunEntry = -1;
                private const float VariationDirectionError = 35f;

                public int Variations { get; set; } = 2;
                public Func<bool> InCombat;
                public IReadOnlyList<string> Events => _events;
                public string BegunName => _begunClip == null ? "" : _begunClip.Name;
                public int BegunEntry => _begunEntry;

                __VARIATION_METHOD__

                __HELPER_METHOD__

                private float TravelYaw() => _travel;

                private static bool IsHop(PoseClip clip) =>
                    (clip == null ? "" : clip.Name).IndexOf("hop", StringComparison.OrdinalIgnoreCase) >= 0;

                private static float FeetCost(PoseClip clip, int frame) =>
                    Math.Abs(frame - clip.DesiredFeetFrame) * 0.001f;

                private void Begin(Phase phase, PoseClip clip, int entry)
                {
                    _begunClip = clip;
                    _begunEntry = entry;
                }

                public void Configure(float speed, float travel, float phaseFor = 10f)
                {
                    _speed = speed;
                    _travel = travel;
                    _phaseFor = phaseFor;
                    InCombat = () => false;
                }

                public void Add(string name, int frames, float startSpeed, float yaw, float[] peak, int feetFrame)
                {
                    _sets.Add(new MoveSet
                    {
                        Yaw = yaw,
                        YawChange = 0f,
                        StartSpeed = startSpeed,
                        StartPeak = peak,
                        Start = new PoseClip { Name = name, Frames = frames, DesiredFeetFrame = feetFrame }
                    });
                }

                public bool Run() => TryBeginVariation();

                public static int ExactFirstPeak(int frames, float[] peak, float speed, int from)
                {
                    return FirstPeakAtOrAbove(new MoveSet
                    {
                        Start = new PoseClip { Frames = frames },
                        StartPeak = peak
                    }, speed, from);
                }

                public static float LegacyFeetCost(int previousFrames, float[] previousPeak, int shortFrames, float speed)
                {
                    int oldEntry = ExactFirstPeak(previousFrames, previousPeak, speed, 0);
                    float cost = float.MaxValue;
                    for (int frame = Mathf.Max(0, oldEntry - 4);
                         frame < Mathf.Min(shortFrames - 8, oldEntry + 12);
                         frame++)
                    {
                        // This is the minimal old cross-clip loop: with an entry
                        // past the short clip it never executes, leaving MaxValue.
                        cost = Math.Min(cost, 0.001f);
                    }
                    return cost;
                }
            }

            internal static class Program
            {
                private static void Require(bool condition, string message)
                {
                    if (!condition) throw new InvalidOperationException(message);
                }

                private static float[] Ramp(int frames, int first, float value)
                {
                    float[] result = new float[frames];
                    for (int frame = Math.Max(0, first); frame < frames; frame++) result[frame] = value;
                    return result;
                }

                private static void SelectionUsesFeasibleRunnerUpAndOwnCurve()
                {
                    const float speed = 2f;
                    float[] previousLong = Ramp(120, 110, speed);
                    float[] shortHop = Ramp(61, 35, speed);
                    int legacyEntry = VariationPlaybackHarness.ExactFirstPeak(120, previousLong, speed, 0);
                    float legacyFeet = VariationPlaybackHarness.LegacyFeetCost(120, previousLong, 61, speed);
                    Require(legacyEntry == 110, "legacy fixture must reproduce entry 110");
                    Require(legacyFeet == float.MaxValue, "legacy fixture must reproduce feet=float.MaxValue");

                    var playback = new VariationPlaybackHarness();
                    playback.Configure(speed, 0f);
                    // Both of these score better on direction than the runner-up,
                    // but one cannot attain speed and the other has fewer than nine
                    // frames left after attaining it. They must be rejected before
                    // ordering so the feasible short hop is still selected.
                    playback.Add("hop_unreachable", 60, speed, 1f, Ramp(60, 0, 1.5f), 35);
                    playback.Add("hop_tail", 60, speed, 0f, Ramp(60, 54, speed), 35);
                    playback.Add("short_hop", 61, speed, 20f, shortHop, 37);
                    Require(playback.Run(), "feasible runner-up should start a variation");
                    Require(playback.BegunName == "short_hop", "selected hop must be the feasible runner-up");
                    Require(playback.BegunEntry == 37, "entry should come from the selected short hop's speed curve");
                    Require(playback.Events.Count == 1 && playback.Events[0].Contains("feet 0.000"),
                        "selected variation must report a finite feet cost");
                    Console.WriteLine("selected feasible runner-up: legacyEntry=" + legacyEntry
                        + " legacyFeet=" + legacyFeet + " currentClip=" + playback.BegunName
                        + " currentEntry=" + playback.BegunEntry);
                }

                private static void UnreachableSpeedSkips()
                {
                    var playback = new VariationPlaybackHarness();
                    playback.Configure(2f, 0f);
                    playback.Add("hop_unreachable", 60, 2f, 0f, Ramp(60, 0, 1.5f), 30);
                    Require(!playback.Run(), "unreachable speed must skip the variation");
                    Require(playback.BegunName == "" && playback.BegunEntry == -1,
                        "unreachable speed must not call Begin");
                    Console.WriteLine("unreachable speed: skipped");
                }

                private static void TooShortTailSkips()
                {
                    var playback = new VariationPlaybackHarness();
                    playback.Configure(2f, 0f);
                    playback.Add("hop_tail", 60, 2f, 0f, Ramp(60, 54, 2f), 54);
                    Require(!playback.Run(), "too-short tail must skip the variation");
                    Require(playback.BegunName == "" && playback.BegunEntry == -1,
                        "too-short tail must not call Begin");
                    Console.WriteLine("too-short tail: skipped");
                }

                private static int Main()
                {
                    try
                    {
                        SelectionUsesFeasibleRunnerUpAndOwnCurve();
                        UnreachableSpeedSkips();
                        TooShortTailSkips();
                        Console.WriteLine("variation_entry: all checks passed");
                        return 0;
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error.Message);
                        return 1;
                    }
                }
            }
        }
        """
    )
    return template.replace("__ZLINQ_FALLBACK__", zlinq_fallback).replace(
        "__VARIATION_METHOD__", variation
    ).replace("__HELPER_METHOD__", helper)


class VariationEntryRuntimeTests(unittest.TestCase):
    def test_current_csharp_variation_methods_choose_a_feasible_short_hop(self):
        source = PLAYBACK.read_text(encoding="utf-8")
        helper = extract_method(source, "private static int FirstPeakAtOrAbove")
        variation = extract_method(source, "private bool TryBeginVariation()")

        dotnet = Path(shutil.which("dotnet") or r"C:\Program Files\dotnet\dotnet.exe")
        self.assertTrue(dotnet.exists(), f"dotnet is required for this C# regression harness: {dotnet}")
        sdk_root = dotnet.parent / "sdk"
        csc_candidates = sorted(sdk_root.glob("*/Roslyn/bincore/csc.dll"), reverse=True)
        self.assertTrue(csc_candidates, f"Roslyn csc.dll not found under {sdk_root}")
        csc = csc_candidates[0]
        ref_dir = latest_ref_pack(dotnet.parent)
        self.assertTrue(ref_dir.exists(), f"reference assemblies not found: {ref_dir}")

        # UnityToolkit is installed in the SPT development tree. If a checkout
        # lacks it, the generated harness uses an identity adapter with the same
        # AsValueEnumerable call so this remains a no-download test.
        zlinq = Path(r"D:\SPT41Dev\BepInEx\plugins\UnityToolkit\ZLinq.dll")
        use_zlinq = zlinq.exists()
        harness_source = csharp_harness(helper, variation, use_zlinq)

        with tempfile.TemporaryDirectory(prefix="mm-variation-entry-") as temporary:
            work = Path(temporary)
            program = work / "Program.cs"
            output = work / "variation_entry.exe"
            runtime_config = work / "variation_entry.runtimeconfig.json"
            program.write_text(harness_source, encoding="utf-8")

            references = [f"-r:{path}" for path in (ref_dir.glob("*.dll"))]
            if use_zlinq:
                references.append(f"-r:{zlinq}")
                shutil.copy2(zlinq, work / "ZLinq.dll")
            compile_command = [
                str(dotnet),
                str(csc),
                "-nologo",
                "-target:exe",
                f"-out:{output}",
                "-nostdlib+",
                "-langversion:latest",
                *references,
                str(program),
            ]
            compiled = subprocess.run(
                compile_command,
                cwd=ROOT,
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(
                compiled.returncode,
                0,
                "C# variation harness did not compile.\n"
                + compiled.stdout
                + compiled.stderr,
            )

            runtime_version = ref_dir.parent.parent.name
            runtime_config.write_text(
                "{\n"
                '  "runtimeOptions": {\n'
                f'    "tfm": "net{runtime_version.split(".")[0]}.0",\n'
                '    "framework": {\n'
                '      "name": "Microsoft.NETCore.App",\n'
                f'      "version": "{runtime_version}"\n'
                "    }\n"
                "  }\n"
                "}\n",
                encoding="utf-8",
            )
            executed = subprocess.run(
                [str(dotnet), str(output)],
                cwd=ROOT,
                text=True,
                capture_output=True,
                check=False,
            )
            self.assertEqual(
                executed.returncode,
                0,
                "C# variation harness failed.\nstdout:\n"
                + executed.stdout
                + "stderr:\n"
                + executed.stderr,
            )
            self.assertIn("legacyEntry=110", executed.stdout)
            self.assertIn("legacyFeet=3.4028235E+38", executed.stdout)
            self.assertIn("currentClip=short_hop currentEntry=37", executed.stdout)
            self.assertIn("unreachable speed: skipped", executed.stdout)
            self.assertIn("too-short tail: skipped", executed.stdout)
            self.assertIn("variation_entry: all checks passed", executed.stdout)


if __name__ == "__main__":
    unittest.main()
