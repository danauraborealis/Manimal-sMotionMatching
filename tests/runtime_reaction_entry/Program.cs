using System;
using Manimal.MotionMatching;
using UnityEngine;
static class Program
{
    static void Check(bool good) { if (!good) throw new Exception("Reaction entry assertion failed"); }
    static void Main()
    {
        var zero = new Vector3(); float blend; string reason;
        Check(ReactionEntry.Foot(zero, new Vector3(.2f,0,0), true,true,out blend,out reason));
        Check(!ReactionEntry.Foot(zero, new Vector3(.21f,0,0), true,true,out blend,out reason));
        Check(!ReactionEntry.Foot(zero, zero, true,false,out blend,out reason));
        Check(ReactionEntry.Foot(zero, new Vector3(.4f,0,0), false,false,out blend,out reason) && blend > .12f);
        Check(!ReactionEntry.Foot(zero, new Vector3(.4f,0,0), false,true,out blend,out reason));
        Check(ReactionEntry.Foot(zero, new Vector3(0,.85f,0), false,false,out blend,out reason) && blend <= .3f);
        Check(!ReactionEntry.Foot(zero, new Vector3(.56f,0,0), false,false,out blend,out reason));
        Check(!ReactionEntry.Foot(zero, new Vector3(.9f,0,0), false,false,out blend,out reason));
        Check(!ReactionEntry.Foot(null,zero,false,false,out blend,out reason));
        Check(!ReactionEntry.Foot(zero,zero,null,false,out blend,out reason));
        Check(!ReactionEntry.Foot(zero,new Vector3(float.NaN,0,0),false,false,out blend,out reason));
        Console.WriteLine("PASS: 11 reaction entry checks");
    }
}
