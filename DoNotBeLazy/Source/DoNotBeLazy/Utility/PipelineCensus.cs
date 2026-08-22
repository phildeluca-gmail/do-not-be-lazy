using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Utility
{
    // Instrument 1 of the standing-still diagnostics: who else is patching
    // the job pipeline. Reads Harmony's own registry - no patching, no
    // reflection into game state, nothing that can change behaviour.
    //
    // Point of it: three sessions were spent theorising about which mod
    // interferes with job assignment. Harmony already knows, and every mod
    // that patches anything has to register its owner id (usually the
    // packageId) to do it.
    public static class PipelineCensus
    {
        // Run twice at most in a session - once at startup if diagnostics
        // were already on, once more if the checkbox gets switched on later.
        // Nothing changes in between; the patch list is fixed by the time
        // the main menu appears.
        private static bool alreadyRan;

        public static void Run()
        {
            if (alreadyRan || !Logger.JobDiagnostics)
            {
                return;
            }
            alreadyRan = true;

            Report(AccessTools.Method(typeof(Pawn_JobTracker), "TryFindAndStartJob"), "Pawn_JobTracker.TryFindAndStartJob");
            Report(AccessTools.Method(typeof(Pawn_JobTracker), "StartJob"), "Pawn_JobTracker.StartJob");
            Report(AccessTools.Method(typeof(Pawn_JobTracker), "EndCurrentJob"), "Pawn_JobTracker.EndCurrentJob");
            Report(AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), "Pawn_JobTracker.DetermineNextJob");
            Report(AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage"), "JobGiver_Work.TryIssueJobPackage");
        }

        private static void Report(MethodBase method, string label)
        {
            if (method == null)
            {
                Logger.Diag("pipeline " + label + ": METHOD NOT FOUND (renamed in this build?)");
                return;
            }

            HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                Logger.Diag("pipeline " + label + ": unpatched");
                return;
            }

            var owners = new List<string>();
            Collect(owners, info.Prefixes, "prefix");
            Collect(owners, info.Postfixes, "postfix");
            Collect(owners, info.Transpilers, "transpiler");
            Collect(owners, info.Finalizers, "finalizer");

            Logger.Diag("pipeline " + label + ": "
                + (owners.Count == 0 ? "unpatched" : string.Join(", ", owners.ToArray())));
        }

        private static void Collect(List<string> into, IList<Patch> patches, string kind)
        {
            if (patches == null)
            {
                return;
            }
            foreach (Patch patch in patches)
            {
                into.Add(kind + "=" + patch.owner);
            }
        }
    }

    // Fires after every mod's constructor has run, which is what makes the
    // census worth anything - patching from a Mod ctor is the normal case,
    // so counting at our own ctor would miss everyone loaded after us.
    [StaticConstructorOnStartup]
    public static class PipelineCensusStartup
    {
        static PipelineCensusStartup()
        {
            PipelineCensus.Run();
        }
    }
}
