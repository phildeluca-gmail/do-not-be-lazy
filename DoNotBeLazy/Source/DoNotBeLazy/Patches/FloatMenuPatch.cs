using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
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
        // Hauling/Construction/Cleaning/Mining/Growing per architecture doc
        // section 2's examples. PlantCutting added separately from Growing -
        // cutting/chopping plants (PlantsCut) is its own WorkTypeDef, not
        // part of Growing, and was missing entirely (clicking an
        // already-marked-for-cutting plant did nothing). Workstation bills
        // aren't listed by name here - any WorkGiverDef whose Worker is
        // WorkGiver_DoBill qualifies regardless of WorkTypeDef, since
        // there's one per crafting station and hardcoding them all would
        // break against mod/DLC additions.
        private static readonly HashSet<string> SupportedWorkTypeDefNames = new HashSet<string>
        {
            "Hauling",
            "Construction",
            "Cleaning",
            "Mining",
            "Growing",
            "PlantCutting",
        };

        // CookFillHopper (workType Hauling, vanilla defName) matches on any
        // food item near a hopper that needs fuel - right-clicking food or
        // drugs was offering "* fill food hoppers", an obscure mechanic
        // nobody was asking for in that context. Everything else under
        // Hauling is still fine; this one def just doesn't belong.
        //
        // DeliverResourcesToFrames/Blueprints (workType Hauling) are exact
        // duplicates of ConstructDeliverResourcesToFrames/Blueprints
        // (workType Construction) - same giverClass, registered twice so
        // whichever work priority is higher governs it. Excluding the
        // Hauling-tagged copies specifically, by defName, rather than
        // deduping by giverClass generally: giverClass is NOT a safe
        // uniqueness key across the whole WorkGiverDef set - all ~19
        // workstation bill types (cooking, smithing, tailoring, art,
        // stonecutting, everything) share the single giverClass
        // WorkGiver_DoBill, so a giverClass-based dedup was silently
        // collapsing every workstation type down to just one. Fixed by
        // reverting to this narrow, explicit denylist instead.
        private static readonly HashSet<string> ExcludedDefNames = new HashSet<string>
        {
            "CookFillHopper",
            "DeliverResourcesToFrames",
            "DeliverResourcesToBlueprints",
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

            // not early-returning when this is empty - scanCells defs (sow
            // crops) target the clicked cell itself, which is normally
            // empty by definition
            List<Thing> thingsHere = cell.GetThingList(map);

            // GrowerSow/GrowerHarvest are both scanCells (target the cell
            // itself, not a Thing on it) and neither checks for fire - a
            // burning farm tile with a scorched-but-still-mature plant on
            // it, or one already burnt down to bare ground, still passes
            // HasJobOnCell. Bail on the whole click rather than special-case
            // each WorkGiverDef: nothing we offer is sensible to send pawns
            // into while it's on fire. Firefighting itself needs no manual
            // order - it's emergency/auto-taken (see IsSweepEligible).
            foreach (Thing thing in thingsHere)
            {
                if (thing is Fire)
                {
                    return;
                }
            }

            foreach (WorkGiverDef def in EligibleDefs())
            {
                List<Pawn> eligiblePawns = EligiblePawns(pawns, map, def);
                if (eligiblePawns.Count == 0)
                {
                    continue;
                }

                var scanner = (WorkGiver_Scanner)def.Worker;
                LocalTargetInfo target = FindTargetWithJob(eligiblePawns, def, scanner, cell, thingsHere);
                if (!target.IsValid)
                {
                    continue;
                }

                string label = def.label.NullOrEmpty() ? def.defName : def.label.CapitalizeFirst();

                WorkGiverDef capturedDef = def;
                LocalTargetInfo capturedTarget = target;
                Map capturedMap = map;
                options.Add(new FloatMenuOption(
                    "* " + label + " until done",
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

            AddConsumeOption(pawns, map, thingsHere, options);
        }

        // Eating/drugs aren't WorkGiver-based at all - there's no WorkGiverDef
        // for "ingest", it's a separate system (JobDefOf.Ingest), so this
        // doesn't go through SweepManager or the eligibleDefs loop above.
        // One dose/meal per eligible pawn, issued directly and immediately -
        // not an ongoing sweep. Mirrors what right-clicking "Eat/Smoke/Snort
        // X" does for a single pawn in vanilla, just fanned out to the whole
        // selection. Deliberately ignores Drafted (you can manually order a
        // drafted pawn to eat/take a combat drug in vanilla too) and doesn't
        // check hunger level (a manual order works regardless of need, same
        // as vanilla).
        private static void AddConsumeOption(List<Pawn> pawns, Map map, List<Thing> thingsHere, List<FloatMenuOption> options)
        {
            Thing ingestible = FindIngestibleThing(thingsHere);
            if (ingestible == null)
            {
                return;
            }

            var canEat = new List<Pawn>();
            foreach (Pawn pawn in pawns)
            {
                if (pawn != null && pawn.Map == map && CanConsume(pawn, ingestible))
                {
                    canEat.Add(pawn);
                }
            }
            if (canEat.Count == 0)
            {
                return;
            }

            string commandFormat = ingestible.def.ingestible.ingestCommandString;
            string label = commandFormat.NullOrEmpty()
                ? "Consume " + ingestible.LabelShort
                : string.Format(commandFormat, ingestible.LabelShort);

            Thing capturedThing = ingestible;
            options.Add(new FloatMenuOption(
                "* " + label,
                () => ConsumeAll(canEat, capturedThing),
                MenuOptionPriority.Low));
        }

        private static Thing FindIngestibleThing(List<Thing> thingsHere)
        {
            foreach (Thing thing in thingsHere)
            {
                if (thing?.def?.ingestible != null && thing.def.ingestible.showIngestFloatOption)
                {
                    return thing;
                }
            }
            return null;
        }

        private static bool CanConsume(Pawn pawn, Thing thing)
        {
            if (pawn.Dead || pawn.Downed || pawn.InMentalState)
            {
                return false;
            }
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return false;
            }

            // WillEat is a food-appetite check (preferability/nutrition) and
            // rejects pure drugs outright - a smokeleaf joint or wake-up
            // isn't "food", so every drug was failing this and only
            // unrelated Hauling-category options were left to show. Route
            // drugs through their own, much simpler check instead.
            if (thing.def.ingestible.drugCategory != DrugCategory.None)
            {
                // vanilla *does* let you force-feed a Teetotaler a drug (see
                // the "forced to take drugs" thought - it's allowed, just
                // gives a mood hit) but skip them here rather than force it
                bool isTeetotaler = pawn.story?.traits != null && pawn.story.traits.HasTrait(TraitDefOf.DrugDesire, -1);
                return !isTeetotaler;
            }

            return FoodUtility.WillEat(pawn, thing, pawn);
        }

        // one dose each, taken directly from the clicked stack - not looped
        // like an area sweep, and not tracked by SweepManager (nothing to
        // interrupt or chain, the job either succeeds or it doesn't)
        private static void ConsumeAll(List<Pawn> pawns, Thing thing)
        {
            foreach (Pawn pawn in pawns)
            {
                if (thing.Destroyed || thing.stackCount <= 0)
                {
                    break;
                }

                float nutrition = thing.GetStatValue(StatDefOf.Nutrition);
                Job job = JobMaker.MakeJob(JobDefOf.Ingest, thing);
                job.count = Mathf.Clamp(FoodUtility.WillIngestStackCountOf(pawn, thing.def, nutrition), 1, thing.stackCount);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
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
            if (def == null || ExcludedDefNames.Contains(def.defName))
            {
                return false;
            }
            // directOrderable defaults true and is only set false on defs
            // vanilla deliberately keeps out of the player's hands - e.g.
            // FightFires (emergency-only, auto-taken by any idle pawn
            // regardless of orders; that's why there's no vanilla "put out
            // fire" menu option either). Respecting this generally is safer
            // than denylisting each one we happen to trip over.
            if (!def.directOrderable)
            {
                return false;
            }
            if (!(def.Worker is WorkGiver_Scanner scanner))
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
        // actually do the job on the clicked target - mirror that here so
        // the * option only shows up where the normal one would have.
        // scanCells defs (e.g. GrowerSow - sow crops) have no Thing to
        // check: the target IS the clicked cell itself, empty or not, so
        // that branch runs regardless of what's in thingsHere.
        //
        // forced:true throughout - this is a manually-issued player order,
        // same as vanilla's own float-menu building. WorkGiver_GrowerSow's
        // JobOnCell threads forced straight into its reservation check
        // (ignoreOtherReservations), so leaving it false was silently
        // failing sow/harvest on perfectly valid cells.
        private static LocalTargetInfo FindTargetWithJob(List<Pawn> pawns, WorkGiverDef def, WorkGiver_Scanner scanner, IntVec3 cell, List<Thing> thingsHere)
        {
            if (def.scanCells)
            {
                foreach (Pawn pawn in pawns)
                {
                    try
                    {
                        if (scanner.HasJobOnCell(pawn, cell, true))
                        {
                            return cell;
                        }
                    }
                    catch
                    {
                        // swallow - this def just doesn't offer a sweep here
                    }
                }
            }

            if (def.scanThings)
            {
                foreach (Thing thing in thingsHere)
                {
                    foreach (Pawn pawn in pawns)
                    {
                        // HasJobOnThing on a modded scanner can throw on odd
                        // targets - one bad def shouldn't eat the menu
                        try
                        {
                            if (scanner.HasJobOnThing(pawn, thing, true))
                            {
                                return thing;
                            }
                        }
                        catch
                        {
                            // swallow
                        }
                    }
                }
            }

            return LocalTargetInfo.Invalid;
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
