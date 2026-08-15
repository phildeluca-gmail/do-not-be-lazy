using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using DoNotBeLazy.Components;
using DoNotBeLazy.Utility;
// UnityEngine has its own Logger and this file needs UnityEngine for Vector3
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Patches
{
    // Postfixes on the two float menu builders in FloatMenuMakerMap.
    // Confirmed against the actual game DLL for this version
    // (1.5.9214.33606) via reflection rather than guessed - there is no
    // GetOptions method on FloatMenuMakerMap in this build. The real
    // entry points are:
    //
    //   ChoicesAtFor(Vector3, Pawn, bool)        - exactly 1 pawn selected
    //   ChoicesAtForMultiSelect(Vector3, List<Pawn>)  - 2+ pawns selected
    //
    // Both are patched. The single-pawn one just wraps its pawn in a
    // one-element list and hands off to the same builder, so the
    // eligibility scan lives in exactly one place.
    //
    // clickPos is a world-space Vector3, not a cell or thing - vanilla
    // resolves it internally and we don't have access to that. We convert
    // to a cell ourselves and look at what's there to decide which
    // sweep-eligible WorkGiverDefs apply, rather than trying to pull a
    // WorkGiverDef back out of the FloatMenuOptions vanilla already built
    // (they don't carry one anywhere accessible).
    public static class FloatMenuPatch
    {
        // Hauling/Construction/Cleaning/Mining per architecture doc section
        // 2's examples. Workstation bills aren't listed by name here - any
        // WorkGiverDef whose Worker is WorkGiver_DoBill qualifies regardless
        // of WorkTypeDef, since there's one per crafting station and
        // hardcoding them all would break against mod/DLC additions.
        private static readonly HashSet<string> SupportedWorkTypeDefNames = new HashSet<string>
        {
            "Hauling",
            "Construction",
            "Cleaning",
            "Mining",
        };

        // Built once on first right-click instead of walking the whole
        // DefDatabase every time the menu opens. def.Worker instantiates the
        // worker, so doing it per click on a 200-def modlist is wasted work.
        private static List<WorkGiverDef> eligibleDefs;

        // 1 pawn selected
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ChoicesAtFor))]
        public static class SingleSelect
        {
            public static void Postfix(Vector3 clickPos, Pawn pawn, List<FloatMenuOption> __result)
            {
                if (pawn == null)
                {
                    return;
                }
                AddSweepOptions(clickPos, new List<Pawn> { pawn }, __result);
            }
        }

        // 2+ pawns selected
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ChoicesAtForMultiSelect))]
        public static class MultiSelect
        {
            public static void Postfix(Vector3 clickPos, List<Pawn> pawns, List<FloatMenuOption> __result)
            {
                AddSweepOptions(clickPos, pawns, __result);
            }
        }

        // Whole body is wrapped - an exception escaping a float menu postfix
        // takes the right-click menu down for every other mod in the chain
        // too (Achtung patches the same area). Better to silently lose our
        // * entries than to break the menu.
        private static void AddSweepOptions(Vector3 clickPos, List<Pawn> pawns, List<FloatMenuOption> options)
        {
            if (pawns == null || pawns.Count == 0 || options == null)
            {
                return;
            }

            try
            {
                Build(clickPos, pawns, options);
            }
            catch (Exception e)
            {
                Logger.Error("float menu postfix blew up, skipping sweep options: " + e);
            }
        }

        private static void Build(Vector3 clickPos, List<Pawn> pawns, List<FloatMenuOption> options)
        {
            // don't trust pawns[0] to be spawned - caravan/world pawns can
            // sit in a selection and their Map is null
            Map map = null;
            foreach (Pawn p in pawns)
            {
                if (p != null && p.Spawned && p.Map != null)
                {
                    map = p.Map;
                    break;
                }
            }
            if (map == null)
            {
                return;
            }

            IntVec3 cell = clickPos.ToIntVec3();
            if (!cell.InBounds(map))
            {
                return;
            }

            List<Thing> thingsHere = cell.GetThingList(map);
            if (thingsHere.Count == 0)
            {
                return;
            }

            foreach (WorkGiverDef def in EligibleDefs())
            {
                List<Pawn> eligiblePawns = EligiblePawns(pawns, map, def);
                if (eligiblePawns.Count == 0)
                {
                    continue;
                }

                var scanner = (WorkGiver_Scanner)def.Worker;
                Thing target = FindThingWithJob(eligiblePawns, scanner, thingsHere);
                if (target == null)
                {
                    continue;
                }

                string label = def.label.NullOrEmpty() ? def.defName : def.label.CapitalizeFirst();

                WorkGiverDef capturedDef = def;
                Thing capturedTarget = target;
                Map capturedMap = map;
                options.Add(new FloatMenuOption(
                    "* " + label,
                    () =>
                    {
                        SweepManager mgr = capturedMap.GetComponent<SweepManager>();
                        if (mgr == null)
                        {
                            Logger.Warning("no SweepManager on map, sweep ignored");
                            return;
                        }
                        mgr.BeginSweep(eligiblePawns, capturedTarget, capturedDef);
                    },
                    MenuOptionPriority.Low));
            }
        }

        private static List<WorkGiverDef> EligibleDefs()
        {
            if (eligibleDefs != null)
            {
                return eligibleDefs;
            }

            eligibleDefs = new List<WorkGiverDef>();
            foreach (WorkGiverDef def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                // a broken mod def can throw straight out of the Worker
                // getter - don't let one bad def kill the whole list
                try
                {
                    if (IsSweepEligible(def))
                    {
                        eligibleDefs.Add(def);
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning($"skipping WorkGiverDef {def?.defName}: {e.Message}");
                }
            }

            return eligibleDefs;
        }

        private static bool IsSweepEligible(WorkGiverDef def)
        {
            if (!(def?.Worker is WorkGiver_Scanner scanner))
            {
                return false;
            }
            if (scanner is WorkGiver_DoBill)
            {
                return true;
            }
            return def.workType != null && SupportedWorkTypeDefNames.Contains(def.workType.defName);
        }

        // vanilla only shows the base option when some selected pawn can
        // actually do the job on the clicked thing - mirror that here so
        // the * option only shows up where the normal one would have
        private static Thing FindThingWithJob(List<Pawn> pawns, WorkGiver_Scanner scanner, List<Thing> thingsHere)
        {
            foreach (Thing thing in thingsHere)
            {
                foreach (Pawn pawn in pawns)
                {
                    // HasJobOnThing on a modded scanner can throw on odd
                    // targets - one bad def shouldn't eat the menu
                    try
                    {
                        if (scanner.HasJobOnThing(pawn, thing))
                        {
                            return thing;
                        }
                    }
                    catch
                    {
                        // swallow - this def just doesn't offer a sweep here
                    }
                }
            }
            return null;
        }

        private static List<Pawn> EligiblePawns(List<Pawn> pawns, Map map, WorkGiverDef def)
        {
            var result = new List<Pawn>();
            foreach (Pawn pawn in pawns)
            {
                // same map only - BeginSweep runs against one map's component
                if (pawn == null || pawn.Map != map)
                {
                    continue;
                }
                if (PawnValidator.CanSweep(pawn, def))
                {
                    result.Add(pawn);
                }
            }
            return result;
        }
    }
}
