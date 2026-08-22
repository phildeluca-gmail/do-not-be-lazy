using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Patches
{
    // Instrument 2 of the standing-still diagnostics. Postfix on
    // Pawn_JobTracker.StartJob, logging only the idle-shaped jobs.
    //
    // What it's for: "standing still" covers two completely different
    // states that look identical in game.
    //   - the pawn keeps getting Wait/wander jobs -> the think tree is fine
    //     and JobGiver_Work is declining the work, so the question is which
    //     WorkGiver said no
    //   - no lines at all for an idle pawn -> DetermineNextJob returned
    //     NoJob, and TryFindAndStartJob drops it on the floor without a word
    //     (checked in the decompile - there is no log line on that path)
    // Different causes, opposite fixes, and nothing in vanilla tells them
    // apart.
    //
    // Job.jobGiver is the ThinkNode that issued the job, so its declaring
    // type names the mod responsible whenever it isn't a vanilla node.
    [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
    public static class JobSourcePatch
    {
        // No throttle on purpose. A pawn cycling the same wait job every few
        // ticks is exactly the report ("started 10 jobs in 10 ticks"), and
        // deduping would hide the thing being looked for. The setting is off
        // by default; that's the volume control.
        private static readonly HashSet<string> IdleJobDefNames = new HashSet<string>
        {
            "Wait",
            "Wait_Wander",
            "Wait_MaintainPosture",
            "Wait_Downed",
            "Wait_WithSleeping",
            "GotoWander",
            "Goto",
            "LayDown",
        };

        public static void Postfix(Pawn ___pawn, Job newJob, ThinkNode jobGiver, ThinkTreeDef thinkTree)
        {
            if (!Logger.JobDiagnostics || newJob?.def == null || ___pawn == null)
            {
                return;
            }

            try
            {
                // colonists only - raiders and animals idle constantly and
                // none of it is the report
                if (!___pawn.IsColonistPlayerControlled)
                {
                    return;
                }
                if (!IdleJobDefNames.Contains(newJob.def.defName))
                {
                    return;
                }

                string source = jobGiver != null ? jobGiver.GetType().Name : "none";
                string tree = thinkTree != null ? thinkTree.defName : "?";

                // the giver's own assembly is what names the mod when the
                // node isn't vanilla - Verse/RimWorld types come out of
                // Assembly-CSharp
                string asm = jobGiver != null ? jobGiver.GetType().Assembly.GetName().Name : "-";

                Logger.Diag($"job {___pawn.LabelShort}: {newJob.def.defName} from {source} [{asm}] (tree {tree})");
            }
            catch (Exception e)
            {
                // a diagnostic must never be the thing that breaks the game
                Logger.Warning("job trace blew up, ignoring: " + e.Message);
            }
        }
    }
}
