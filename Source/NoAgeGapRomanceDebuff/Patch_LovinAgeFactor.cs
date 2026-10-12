using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace NoAgeGapRomanceDebuff
{
    /// <summary>
    /// Removes the age-gap romance debuff from <c>Pawn_RelationsTracker.LovinAgeFactor</c>.
    ///
    /// Two mechanisms, because the method may or may not actually run:
    ///
    /// 1. <see cref="Transpiler"/> handles the vanilla case. RimWorld applies the debuff as a
    ///    <c>GenMath.FlatHill</c> trapezoid over the two pawns' biological ages (male:
    ///    -30/-10/+3/+10, female: -10/-3/+10/+30, floored at 0.2f), then multiplies it by two
    ///    <c>Mathf.InverseLerp(16f, 18f, age)</c> terms. Those terms are a separate young-pawn
    ///    ramp-in, NOT part of the gap debuff, so only the FlatHill calls are redirected --
    ///    the ramp stays vanilla code and keeps whatever values Ludeon ships.
    ///
    /// 2. <see cref="Postfix"/> handles the case where another mod has taken the prefix slot
    ///    and returns false, skipping the original body entirely. Way Better Romance
    ///    (divineDerivative.Romance) does this, substituting its own configurable
    ///    curve. The transpiled body never executes, so there is nothing for mechanism 1 to
    ///    have rewritten. Harmony's "return false" skips the original and any remaining
    ///    prefixes, but never postfixes, so this still lands.
    ///
    /// Neither mechanism touches a minimum-age gate. Vanilla's hard "&lt; 16f returns 0f" lives
    /// upstream in SecondaryLovinChanceFactor; Way Better Romance replaces that method too and
    /// enforces its own "&lt; MinAgeForSex() returns 0f" in the same place. This patch only ever
    /// affects the gap between two pawns already old enough to qualify.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.LovinAgeFactor))]
    public static class Patch_Pawn_RelationsTracker_LovinAgeFactor
    {
        private const int ExpectedFlatHillCalls = 2; // There are two FlatHill calls in the vanilla method, male and female

        private static readonly MethodInfo Target = AccessTools.Method(
            typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.LovinAgeFactor)); // find the target method

        private static readonly MethodInfo FlatHill = AccessTools.Method(
            typeof(GenMath),
            nameof(GenMath.FlatHill),
            new[]
            {
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float)
            }); // find the FlatHill method

        private static readonly MethodInfo NoGapMethod = AccessTools.Method(
            typeof(Patch_Pawn_RelationsTracker_LovinAgeFactor), nameof(NoGap)); // new stand-in method to replace FlatHill calls with

        private static bool transpilerApplied; // True if the transpiler successfully replaced all expected FlatHill calls; false if it failed and the postfix is needed instead.

        private static bool? overrideResult; // True if some other mod has a prefix that skips the original body, making the transpiler useless. false if there is no prefix exists. Cached..

        /// <summary>
        /// Harmony skips this whole class if Prepare returns false. Without it, a RimWorld update
        /// that renames or removes LovinAgeFactor makes PatchAll throw, and the player gets a red
        /// error on startup instead of a mod that quietly does nothing.
        /// </summary>
        private static bool Prepare()
        {
            if (Target != null)
            {
                return true;
            }

            Log.Warning("[NoAgeGapRomanceDebuff] Pawn_RelationsTracker.LovinAgeFactor not found. Nothing was patched.");
            return false;
        }

        /// <summary>
        /// True when some other mod is prefixing the LovinAgeFactor method, which
        /// would make the transpiler useless.
        /// </summary>
        private static bool OverrideResult
        {
            get
            {
                if (overrideResult.HasValue)
                {
                    return overrideResult.Value; // cached value, don't recalc
                }

                // This class registers only a transpiler and a postfix, therefore any prefixes are not ours.
                // Only a bool-returning prefix can skip the original.
                // If there is a prefix that returns false, the original body never runs, so the transpiler never runs either. The postfix still runs, so we need to override the result.
                var patches = Target == null ? null : Harmony.GetPatchInfo(Target);
                var foreign = patches?.Prefixes != null && patches.Prefixes.Any(p => p.PatchMethod?.ReturnType == typeof(bool));

                // if the transpiler fails for an unknown reason, the postfix is still needed to override the result. The transpiler sets this flag if it succeeds.
                overrideResult = foreign || !transpilerApplied;

                if (overrideResult.Value)
                {
                    Log.Message("[NoAgeGapRomanceDebuff] LovinAgeFactor is being overridden by postfix" + (foreign ? " (another mod prefixes it)." : " (transpiler did not apply)."));
                }

                return overrideResult.Value;
            }
        }

        [ThreadStatic] private static bool bypass; // Set only while RawLovinAgeFactor runs, so the stand-in and the postfix both stand down.

        /// <summary>
        /// The value LovinAgeFactor would return without this mod, for the debug readout.
        /// The transpiler removed a call, not a calculation, so vanilla still works out all seven FlatHill
        /// arguments and hands them to NoGap, which normally ignores them. Passing them back to the real
        /// FlatHill reproduces vanilla's answer without copying any of its constants here.
        /// If another mod prefixes the method the original body never runs and the postfix stands down
        /// instead, so their value comes through, which is the "before" that matters in that case.
        /// </summary>
        public static float RawLovinAgeFactor(Pawn pawn, Pawn otherPawn)
        {
            bypass = true;
            try
            {
                return pawn.relations.LovinAgeFactor(otherPawn);
            }
            finally
            {
                bypass = false;
            }
        }

        /// <summary>
        /// Which mechanism is actually doing the work, for the debug readout.
        /// </summary>
        public static string StatusLine => $"transpiler applied: {transpilerApplied}; postfix override active: {OverrideResult}";

        /// <summary>
        /// Signature-identical stand-in for the seven-argument <c>GenMath.FlatHill</c>.
        /// Allows us to replace the FlatHill calls in LovinAgeFactor with a constant 1f, removing the age-gap debuff entirely.
        /// Returns the real FlatHill result while bypass is set, so RawLovinAgeFactor can read the unpatched value.
        /// </summary>
        private static float NoGap(float minY, float min, float lower, float upper, float max, float maxY, float x)
        {
            return bypass ? GenMath.FlatHill(minY, min, lower, upper, max, maxY, x) : 1f;
        } 


        /// <summary>
        /// Transpiler that replaces the two <c>GenMath.FlatHill</c> calls in <c>Pawn_RelationsTracker.LovinAgeFactor</c> with a constant 1f, removing the age-gap debuff entirely.
        /// </summary>
        [HarmonyPriority(Priority.Last)] // Go last so other mods' transpilers still see the vanilla FlatHill calls, rather than our stand-in.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            if (FlatHill == null || NoGapMethod == null)
            {
                Log.Warning("[NoAgeGapRomanceDebuff] Could not resolve GenMath.FlatHill(float x7). Falling back to the postfix.");
                return code;
            }

            var replaced = 0;
            // we have to replace 2 calls to GenMath.FlatHill
            for (var i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(FlatHill))
                {
                    continue;
                }

                code[i].operand = NoGapMethod;
                replaced++;
            }

            // Assigned every run, not just on success. Harmony re-runs all transpilers whenever
            // another patch is added to the method, so a later mod can strip the FlatHill calls out
            // from under us. Latching true on the first run would leave the postfix standing down
            // and the debuff still applied.
            transpilerApplied = replaced == ExpectedFlatHillCalls;

            if (!transpilerApplied)
            {
                Log.Warning($"[NoAgeGapRomanceDebuff] Expected {ExpectedFlatHillCalls} GenMath.FlatHill calls in LovinAgeFactor but redirected {replaced}. Falling back to the postfix.");
            }

            return code;
        }

        /// <summary>
        /// Postfix that overrides the result of <c>Pawn_RelationsTracker.LovinAgeFactor</c> to 1f if some other mod has a prefix that skips the original body, making the transpiler useless. This is a fallback for when the transpiler cannot run.
        /// </summary>
        [HarmonyPriority(Priority.Last)] // Go last so a competing postfix cannot overwrite the result after us. Without this the winner depends on mod load order.
        private static void Postfix(ref float __result)
        {
            if (!bypass && OverrideResult)
            {
                __result = 1f;
            }
        }
    }
}