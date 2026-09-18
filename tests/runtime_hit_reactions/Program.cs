using System;
using Manimal.MotionMatching;
static class Program
{
    static int checks;
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    static bool Near(float a, float b) => Math.Abs(a-b) < .00001f;
    static void Main()
    {
        var p = new HitReactionPolicy();
        Check(!p.AddDamage(0,45,.99f) && Near(p.LastChance,.325f), "45 damage probability");
        var q = new HitReactionPolicy();
        Check(!q.AddDamage(0,50,.99f) && Near(q.LastChance,.35f), "50 damage probability");
        Check(q.LastChance > p.LastChance, "greater damage greater probability");
        Check(!p.AddDamage(.1f,45,.99f) && Near(p.LastChance,.55f), "recent damage stacks");
        Check(p.AddDamage(.2f,1,.5f), "successful accumulated roll");
        Check(!p.AddDamage(.21f,1,0), "only one pending request");
        p.Resolve(.3f,false);
        Check(p.AddDamage(.4f,1,0), "rejection does not consume cooldown");
        p.Resolve(.5f,true);
        Check(!p.AddDamage(.6f,500,0), "cooldown prevents success");
        Check(!p.AddDamage(2.99f,500,0), "cooldown begins at playback time");
        Check(!p.AddDamage(3,1,.2f) && Near(p.LastChance,.105f), "cooldown damage discarded");
        p = new HitReactionPolicy();
        Check(!p.AddDamage(0,45,.99f), "first old hit");
        Check(!p.AddDamage(1,50,.99f), "second old hit");
        Check(!p.AddDamage(2,10,.99f) && Near(p.LastChance,.4f), "sliding window expires first hit");
        Check(!p.AddDamage(4,10,.99f) && Near(p.LastChance,.15f), "quiet window resets history");
        Check(!p.AddDamage(4.1f,10000,.99f) && Near(p.LastChance,.85f), "chance capped");
        p = new HitReactionPolicy();
        Check(p.AddDamage(0,.001f,0), "tiny damage can succeed without threshold");
        p.Resolve(0,true);
        Check(!p.AddDamage(float.NaN,1,0), "invalid clock");
        Check(!p.AddDamage(10,0,0), "zero damage");
        Check(!p.AddDamage(10,-1,0), "negative damage");
        Check(!p.AddDamage(10,float.PositiveInfinity,0), "infinite damage");
        Check(!p.AddDamage(10,1,float.NaN), "invalid roll");
        Check(!p.AddDamage(10,1,-1), "negative roll");
        Check(!p.AddDamage(-1,10,.99f) && Near(p.LastChance,.15f), "clock reset clears cooldown and history");
        Console.WriteLine($"PASS: {checks} weighted chance policy checks");
    }
}
