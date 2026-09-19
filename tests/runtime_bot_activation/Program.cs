using System;
using Manimal.MotionMatching;
internal static class Program
{
    private static int Main()
    {
        try
        {
            Expect(null, false, 80, true, false, true, 0, true);
            Expect("distance", false, 80.01f, true, false, true, 0, true);
            Expect(null, true, 95, true, false, true, 0, true);
            Expect("distance", true, 95.01f, true, false, true, 0, true);
            Expect(null, false, 20, false, false, true, 9, true);
            Expect("unseen", false, 20.01f, false, false, true, 0, true);
            Expect(null, true, 40, false, false, true, 1.99f, true);
            Expect("unseen", true, 40, false, false, true, 2, true);
            Expect(null, false, 70, false, false, true, 100, false);
            Expect("distance", true, 100, false, false, true, 0, false);
            Expect("simplified", true, 1, true, true, true, 0, false);
            Expect("no_viewer", true, 1, true, false, false, 0, false);
            Expect("distance", true, float.NaN, true, false, true, 0, false);
            Expect("distance", true, float.PositiveInfinity, true, false, true, 0, false);
            Expect("unseen", true, 40, false, false, true, float.NaN, true);
            Expect("unseen", true, 40, false, false, true, -1, true);
            // Crossing the exit margin must prevent immediate readmission until the entry boundary.
            Expect("distance", true, 96, true, false, true, 0, true);
            Expect("distance", false, 90, true, false, true, 0, true);
            Expect(null, false, 79, true, false, true, 0, true);
            if (BotActivationPolicy.IneligibleReason(false, 21*21, 20, true, false, true, 0, true) != "distance")
                throw new Exception("Configured range must govern admission");
            Console.WriteLine("runtime_bot_activation: 20 boundary/grace/fallback checks passed");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Expect(string expected, bool active, float distance, bool visible, bool simplified,
        bool viewer, float unseen, bool cull)
    {
        string actual = BotActivationPolicy.IneligibleReason(active, distance * distance, 80, visible, simplified, viewer, unseen, cull);
        if (actual != expected) throw new Exception($"active={active}, distance={distance}, unseen={unseen}: expected {expected ?? "eligible"}, got {actual ?? "eligible"}");
    }
}
