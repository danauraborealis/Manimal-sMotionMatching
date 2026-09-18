using System;
using System.IO;
using System.Text.Json;
using Manimal.MotionMatching;
using UnityEngine;

internal static class Program
{
    private static int Main()
    {
        try
        {
            IntersectingSegmentsHaveZeroDistance();
            ParallelSegmentsKeepTheirGap();
            DegenerateSegmentsReduceToPointDistance();
            EndpointClampingAndSymmetryAreStable();
            MinimumChecksAllFourOppositePairs();
            RequiredUsesTheBaselineRelativeThreshold();
            RecordedFleetCaseMatchesTheClearanceRegression();
            Console.WriteLine("runtime_leg_clearance: all checks passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void IntersectingSegmentsHaveZeroDistance()
    {
        Near(LegClearance.SegmentDistance(P(0, 0, 0), P(1, 0, 0), P(.5f, -1, 0), P(.5f, 1, 0)), 0f, "intersection");
    }

    private static void ParallelSegmentsKeepTheirGap()
    {
        Near(LegClearance.SegmentDistance(P(0, 0, 0), P(1, 0, 0), P(0, 1, 0), P(1, 1, 0)), 1f, "parallel gap");
    }

    private static void DegenerateSegmentsReduceToPointDistance()
    {
        Near(LegClearance.SegmentDistance(P(0, 0, 0), P(0, 0, 0), P(-1, 0, 0), P(1, 0, 0)), 0f, "point on segment");
        Near(LegClearance.SegmentDistance(P(0, 2, 0), P(0, 2, 0), P(-1, 0, 0), P(1, 0, 0)), 2f, "point to segment");
        Near(LegClearance.SegmentDistance(P(1, 2, 3), P(1, 2, 3), P(4, 6, 3), P(4, 6, 3)), 5f, "point to point");
    }

    private static void EndpointClampingAndSymmetryAreStable()
    {
        float forward = LegClearance.SegmentDistance(P(0, 0, 0), P(1, 0, 0), P(2, 1, 0), P(2, 2, 0));
        float reverse = LegClearance.SegmentDistance(P(2, 1, 0), P(2, 2, 0), P(0, 0, 0), P(1, 0, 0));
        Near(forward, (float)Math.Sqrt(2), "endpoint clamp");
        Near(reverse, forward, "segment symmetry");

        float finite = LegClearance.SegmentDistance(P(float.MaxValue, 0, 0), P(-float.MaxValue, 0, 0), P(0, float.MaxValue, 0), P(0, -float.MaxValue, 0));
        if (float.IsNaN(finite) || float.IsInfinity(finite))
            throw new InvalidOperationException("finite inputs must yield finite clearance");

        float invalid = LegClearance.Minimum(P(float.NaN, 0, 0), P(0, 0, 0), P(0, 1, 0), P(1, 0, 0), P(1, 1, 0), P(1, 2, 0));
        Near(invalid, float.MaxValue, "invalid geometry sentinel");
    }

    private static void MinimumChecksAllFourOppositePairs()
    {
        // The left shin and right thigh meet; the other three pairings remain separated.
        float minimum = LegClearance.Minimum(P(-1, 0, 0), P(0, 0, 0), P(1, 0, 0), P(.5f, -1, 0), P(.5f, 1, 0), P(2, 2, 0));
        Near(minimum, 0f, "four-pair minimum");
    }

    private static void RequiredUsesTheBaselineRelativeThreshold()
    {
        Near(LegClearance.Required(.1814075f), .11f, "cap threshold");
        Near(LegClearance.Required(.08f), .06f, "baseline fraction threshold");
        Near(LegClearance.Required(0f), 0f, "source crossing threshold");
        Near(LegClearance.Required(float.NaN), 0f, "invalid threshold");
    }

    private static void RecordedFleetCaseMatchesTheClearanceRegression()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fleet-20260917-005029.jsonl");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("stream").GetString() != "fleet-20260917-005029.jsonl" || root.GetProperty("bot").GetInt32() != -1403596 || root.GetProperty("frame").GetInt32() != 10687)
            throw new InvalidOperationException("fixture identity changed");

        float before = Minimum(root.GetProperty("before"));
        float after = Minimum(root.GetProperty("after"));
        Near(before, root.GetProperty("expected").GetProperty("before").GetSingle(), "recorded before clearance");
        Near(after, root.GetProperty("expected").GetProperty("after").GetSingle(), "recorded after clearance");
        if (!(before > .11f && after < LegClearance.Required(before)))
            throw new InvalidOperationException("recorded placement loss must cross the relative safeguard threshold");
    }

    private static float Minimum(JsonElement sides)
    {
        var l = Points(sides.GetProperty("L"));
        var r = Points(sides.GetProperty("R"));
        return LegClearance.Minimum(l[0], l[1], l[2], r[0], r[1], r[2]);
    }

    private static Vector3[] Points(JsonElement array)
    {
        var points = new Vector3[array.GetArrayLength()];
        int index = 0;
        foreach (var item in array.EnumerateArray())
        {
            points[index++] = P(item[0].GetSingle(), item[1].GetSingle(), item[2].GetSingle());
        }
        return points;
    }

    private static Vector3 P(float x, float y, float z) => new Vector3(x, y, z);

    private static void Near(float actual, float expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.000002f)
            throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
    }
}
