using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace NoAgeGapRomanceDebuff
{
    [StaticConstructorOnStartup]
    public class NoAgeGapRomanceDebuffMod : Mod
    {
        public NoAgeGapRomanceDebuffMod(ModContentPack content) : base(content)
        {
            var harmony = new Harmony("jomar.noagegapromancedebuff");
            harmony.PatchAll();
        }
    }
}