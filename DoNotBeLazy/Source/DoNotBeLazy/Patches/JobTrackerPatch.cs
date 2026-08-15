using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Components;

namespace DoNotBeLazy.Patches
{
    // Postfix on Pawn_JobTracker.EndCurrentJob - the single point where a
    // sweep pawn's current task finishes. Two responsibilities:
    //
    //  1. Workstation continuation: if the finished job was a DoBill job
    //     that succeeded, ask the SAME WorkGiverDef the sweep is using for
    //     another job on the same bill giver. If it returns one, take it
    //     directly. This is how we override "stop after one bill" without
    //     a custom JobDriver_AreaSweep subclassing JobDriver_DoBill's
    //     internal toils (chosen deliberately - that internal structure
    //     isn't something we can verify without a decompiler in hand).
    //  2. General sweep chaining: anything else (hauling, construction,
    //     mining, cleaning) is handed to SweepManager to assign the next
    //     target in the sweep queue.
    //
    // curJob is cleared (and can already be replaced by a new job) partway
    // through EndCurrentJob's own body, so the Prefix captures it before
    // that happens and the Postfix reads it back.
    //
    // Depends on SweepManager and SweepOrder (Phase 3), which must expose:
    //   bool TryGetActiveSweep(Pawn pawn, out SweepOrder order)
    //   void Notify_JobEnded(Pawn pawn, JobCondition condition)
    // and SweepOrder must expose:
    //   WorkGiverDef WorkGiverDef { get; }
    // Will not compile until those exist.
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class JobTrackerPatch
    {
        private static readonly Dictionary<Pawn_JobTracker, Job> EndingJobs = new Dictionary<Pawn_JobTracker, Job>();

        [HarmonyPriority(Priority.First)]
        public static void Prefix(Pawn_JobTracker __instance)
        {
            EndingJobs[__instance] = __instance.curJob;
        }

        public static void Postfix(Pawn_JobTracker __instance, JobCondition condition, Pawn ___pawn)
        {
            if (!EndingJobs.TryGetValue(__instance, out Job endedJob))
            {
                return;
            }
            EndingJobs.Remove(__instance);

            Pawn pawn = ___pawn;
            if (pawn == null || endedJob == null)
            {
                return;
            }

            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }

            SweepManager sweepManager = map.GetComponent<SweepManager>();
            if (sweepManager == null || !sweepManager.TryGetActiveSweep(pawn, out SweepOrder order))
            {
                return;
            }

            if (condition == JobCondition.Succeeded
                && endedJob.def == JobDefOf.DoBill
                && endedJob.targetA.Thing is Thing billGiver
                && order.WorkGiverDef?.Worker is WorkGiver_Scanner scanner)
            {
                Job nextBillJob = scanner.JobOnThing(pawn, billGiver);
                if (nextBillJob != null)
                {
                    pawn.jobs.TryTakeOrderedJob(nextBillJob, JobTag.Misc);
                    return;
                }
            }

            sweepManager.Notify_JobEnded(pawn, condition);
        }
    }
}
