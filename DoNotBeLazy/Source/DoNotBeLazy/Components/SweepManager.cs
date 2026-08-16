using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;
using DoNotBeLazy.Utility;

namespace DoNotBeLazy.Components
{
    // Holds everything needed to keep chaining a pawn through a sweep once
    // they've been assigned into one. WorkGiverDef stays fixed per order;
    // SharedPool is the live, shared candidate list for the whole sweep -
    // every pawn in the same sweep call (BeginSweep) points at the same
    // List instance, so claiming a target for one pawn removes it for
    // everyone else. Workstation orders get an empty pool - bill
    // continuation while it's the same bill happens entirely in
    // JobTrackerPatch re-asking the scanner for the same bill giver, but
    // WorkstationTarget is still needed here to resume that same station
    // after a need-pause (see AssignNextTask).
    public class SweepOrder
    {
        public WorkGiverDef WorkGiverDef { get; }
        public List<LocalTargetInfo> SharedPool { get; }
        public Thing WorkstationTarget { get; }

        public SweepOrder(WorkGiverDef workGiverDef, List<LocalTargetInfo> sharedPool, Thing workstationTarget = null)
        {
            WorkGiverDef = workGiverDef;
            SharedPool = sharedPool;
            WorkstationTarget = workstationTarget;
        }
    }

    // MapComponent tracking active area sweeps. Job-to-job chaining is
    // event driven (JobTrackerPatch's postfix on EndCurrentJob calls
    // Notify_JobEnded when a sweep pawn's job wraps up) rather than
    // polled here - avoids reassigning on a delay and avoids doing the
    // same completion check twice. Tick() is only for the things nothing
    // else observes: a pawn going down, dying, drafting, or leaving the
    // map mid-sweep (architecture doc section 4 edge cases).
    //
    // Need interrupts pause rather than cancel (per explicit user request -
    // this reverses the original "don't auto-resume" design): NeedMonitor
    // calls PauseForNeed instead of RemoveSweep, and Notify_JobEnded checks
    // pausedForNeed first, so once the pawn's own eat/sleep/joy job wraps up
    // they're handed the next sweep task (or, for a workstation order, sent
    // back to the same station) automatically.
    //
    // No ExposeData - per architecture doc 4, sweeps are cleared on
    // load rather than persisted. Simplest option for v1 and avoids
    // re-deriving stale reservations against a changed map state.
    public class SweepManager : MapComponent
    {
        private const int StateCheckIntervalTicks = 60;

        private readonly Dictionary<Pawn, SweepOrder> activeSweeps = new Dictionary<Pawn, SweepOrder>();

        // pawns pulled out of their sweep job for a critical need but still
        // tracked in activeSweeps - NeedMonitor adds them here via
        // PauseForNeed instead of RemoveSweep, so the sweep can resume once
        // whatever job vanilla AI gives them (eat/sleep/joy) ends on its own
        private readonly HashSet<Pawn> pausedForNeed = new HashSet<Pawn>();

        // TryTakeOrderedJob interrupts whatever the pawn is doing right now,
        // and that fires EndCurrentJob(InterruptForced) -> JobTrackerPatch's
        // postfix -> Notify_JobEnded -> RemoveSweep. So handing a pawn a
        // sweep job was cancelling the sweep that handed it out. Verified
        // against the DLL: TryTakeOrderedJob -> StartJob -> EndCurrentJob.
        // This flag lets JobTrackerPatch ignore the job end it caused itself.
        public static bool AssigningJob;

        public SweepManager(Map map) : base(map)
        {
        }

        // every TryTakeOrderedJob in the mod goes through here - see AssigningJob
        public static void GiveJob(Pawn pawn, Job job)
        {
            AssigningJob = true;
            try
            {
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            finally
            {
                AssigningJob = false;
            }
        }

        public override void MapComponentTick()
        {
            if (activeSweeps.Count == 0 || Find.TickManager.TicksGame % StateCheckIntervalTicks != 0)
            {
                return;
            }

            // snapshot keys - RemoveSweep mutates activeSweeps mid-loop otherwise
            var pawns = new List<Pawn>(activeSweeps.Keys);
            foreach (Pawn pawn in pawns)
            {
                if (pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.Drafted || pawn.Map != map)
                {
                    RemoveSweep(pawn);
                }
            }
        }

        public bool TryGetActiveSweep(Pawn pawn, out SweepOrder order)
        {
            return activeSweeps.TryGetValue(pawn, out order);
        }

        // copy, not the live key collection - NeedMonitor calls RemoveSweep
        // while walking this and was throwing "collection was modified"
        public IEnumerable<Pawn> GetSweptPawns()
        {
            return new List<Pawn>(activeSweeps.Keys);
        }

        public void RemoveSweep(Pawn pawn)
        {
            activeSweeps.Remove(pawn);
            pausedForNeed.Remove(pawn);
        }

        public bool IsPaused(Pawn pawn)
        {
            return pausedForNeed.Contains(pawn);
        }

        // Called by NeedMonitor when a swept pawn's need goes critical.
        // Keeps the sweep order alive (does NOT RemoveSweep) so
        // Notify_JobEnded can resume it once the pawn's own need-driven job
        // (eat/sleep/joy, picked by vanilla AI once we let go) finishes on
        // its own. Uses the same self-caused-job-end suppression as
        // GiveJob - without it, this EndCurrentJob call would trip
        // JobTrackerPatch's postfix immediately and read as "sweep task
        // ended", removing the sweep before the pawn even gets to eat.
        public void PauseForNeed(Pawn pawn)
        {
            if (!activeSweeps.ContainsKey(pawn))
            {
                return;
            }

            pausedForNeed.Add(pawn);

            if (pawn.jobs?.curJob == null)
            {
                return;
            }

            AssigningJob = true;
            try
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            finally
            {
                AssigningJob = false;
            }
        }

        // Entry point from FloatMenuPatch. eligible pawns only - caller has
        // already run them through PawnValidator. Workstation WorkGivers
        // (WorkGiver_DoBill) get single-pawn best-of selection per
        // architecture doc 2; everything else fans the group out across
        // targets found within sweepRadius of clickedTarget.
        public void BeginSweep(List<Pawn> eligiblePawns, LocalTargetInfo clickedTarget, WorkGiverDef workGiverDef)
        {
            if (map == null || eligiblePawns == null || eligiblePawns.Count == 0)
            {
                return;
            }

            if (!(workGiverDef?.Worker is WorkGiver_Scanner scanner))
            {
                return;
            }

            if (scanner is WorkGiver_DoBill)
            {
                BeginWorkstationSweep(eligiblePawns, clickedTarget.Thing, workGiverDef, scanner);
            }
            else
            {
                BeginAreaSweep(eligiblePawns, clickedTarget.Cell, workGiverDef, scanner);
            }
        }

        private void BeginWorkstationSweep(List<Pawn> eligiblePawns, Thing billGiver, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner)
        {
            if (billGiver == null)
            {
                return;
            }

            // was: single PickBestWorkstationPawn call, then bail if
            // JobOnThing came back null. Problem is the best pawn can fail
            // for reasons that don't apply to the others (can't reach the
            // station, bill has a skill floor they miss) and the whole
            // command then silently did nothing. Rank them and take the
            // first that actually gets a job.
            List<Pawn> ranked = RankForWorkstation(eligiblePawns, workGiverDef);

            foreach (Pawn pawn in ranked)
            {
                Job job = scanner.JobOnThing(pawn, billGiver);
                if (job == null)
                {
                    continue;
                }

                // empty pool - see SweepOrder comment. billGiver is kept so
                // AssignNextTask can re-ask this same station after a
                // need-pause; a fresh assignment always clears any leftover
                // pause state from a previous order
                pausedForNeed.Remove(pawn);
                activeSweeps[pawn] = new SweepOrder(workGiverDef, new List<LocalTargetInfo>(), billGiver);
                GiveJob(pawn, job);
                return;
            }
        }

        // Skill level on the WorkGiver's work type first, WorkSpeedGlobal to
        // break ties, MoveSpeed after that. Doc calls out a specific stat
        // per trade (SmithingSpeed etc.) for the second tiebreak - using the
        // global stat here instead since resolving "the specific stat for
        // this work type" generically isn't a straight lookup. Good enough
        // for picking a winner in the common case; revisit if it matters.
        private static List<Pawn> RankForWorkstation(List<Pawn> eligiblePawns, WorkGiverDef workGiverDef)
        {
            SkillDef relevantSkill = null;
            if (workGiverDef.workType?.relevantSkills != null && workGiverDef.workType.relevantSkills.Count > 0)
            {
                relevantSkill = workGiverDef.workType.relevantSkills[0];
            }

            var ranked = new List<Pawn>(eligiblePawns);
            ranked.Sort((a, b) =>
            {
                int cmp = SkillLevelOf(b, relevantSkill).CompareTo(SkillLevelOf(a, relevantSkill));
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = b.GetStatValue(StatDefOf.WorkSpeedGlobal).CompareTo(a.GetStatValue(StatDefOf.WorkSpeedGlobal));
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.GetStatValue(StatDefOf.MoveSpeed).CompareTo(a.GetStatValue(StatDefOf.MoveSpeed));
            });

            return ranked;
        }

        private static int SkillLevelOf(Pawn pawn, SkillDef skillDef)
        {
            if (skillDef == null || pawn.skills == null)
            {
                return 0;
            }
            SkillRecord record = pawn.skills.GetSkill(skillDef);
            return record == null || record.TotallyDisabled ? 0 : record.Level;
        }

        private void BeginAreaSweep(List<Pawn> eligiblePawns, IntVec3 clickCell, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner)
        {
            // TaskScanner is pawn-scoped (PotentialWorkThingsGlobal takes a
            // pawn) so we build the shared pool off whichever eligible pawn
            // happens to be first - fine since the checks it applies
            // (forbidden, reservable, radius) don't vary by which pawn asked
            Pawn driver = eligiblePawns[0];
            List<LocalTargetInfo> pool = TaskScanner.FindTargets(clickCell, DoNotBeLazyMod.Settings.sweepRadius, map, workGiverDef, driver);
            if (pool.Count == 0)
            {
                return;
            }

            var order = new SweepOrder(workGiverDef, pool);

            foreach (Pawn pawn in eligiblePawns)
            {
                if (order.SharedPool.Count == 0)
                {
                    break;
                }
                pausedForNeed.Remove(pawn);
                activeSweeps[pawn] = order;
                AssignNextTask(pawn, order);
            }
        }

        public void Notify_JobEnded(Pawn pawn, JobCondition condition)
        {
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            if (pausedForNeed.Remove(pawn))
            {
                // this wasn't a sweep task ending - it's the pawn's own
                // need-driven job (eat/sleep/joy) wrapping up on its own.
                // Resume regardless of how it ended (succeeded, or
                // interrupted by something else entirely) - if they're free
                // again, they go back to the last-ordered work.
                AssignNextTask(pawn, order);
                return;
            }

            // workstation orders end here unconditionally - JobTrackerPatch
            // already tried requeuing the same bill giver and came up empty,
            // or the job didn't succeed. Either way there's no pool to draw
            // the next target from.
            if (condition != JobCondition.Succeeded || order.WorkGiverDef.Worker is WorkGiver_DoBill)
            {
                RemoveSweep(pawn);
                return;
            }

            AssignNextTask(pawn, order);
        }

        private void AssignNextTask(Pawn pawn, SweepOrder order)
        {
            if (!(order.WorkGiverDef.Worker is WorkGiver_Scanner scanner))
            {
                RemoveSweep(pawn);
                return;
            }

            // state can change between the tick check and here (downed by a
            // roof collapse mid-mining sweep is the obvious one)
            if (!PawnValidator.CanSweep(pawn, order.WorkGiverDef) || !pawn.Spawned || pawn.Map != map)
            {
                RemoveSweep(pawn);
                return;
            }

            if (order.WorkstationTarget != null)
            {
                // resuming (or continuing) a workstation order - always the
                // same station, never a pool to draw from
                if (order.WorkstationTarget.Destroyed)
                {
                    RemoveSweep(pawn);
                    return;
                }

                Job resumeJob = scanner.JobOnThing(pawn, order.WorkstationTarget);
                if (resumeJob == null)
                {
                    RemoveSweep(pawn);
                    return;
                }

                GiveJob(pawn, resumeJob);
                return;
            }

            while (order.SharedPool.Count > 0)
            {
                // pull by index - Remove(target) on a struct list was doing a
                // linear equality scan for something we'd just found the
                // position of
                int i = NearestTargetIndex(pawn.Position, order.SharedPool);
                LocalTargetInfo target = order.SharedPool[i];
                order.SharedPool.RemoveAt(i);

                if (!TargetStillValid(pawn, target))
                {
                    continue;
                }

                Job job = target.HasThing ? scanner.JobOnThing(pawn, target.Thing) : scanner.JobOnCell(pawn, target.Cell);
                if (job == null)
                {
                    continue;
                }

                GiveJob(pawn, job);
                return;
            }

            // pool's empty - pawn's done, nothing left in this sweep for them
            RemoveSweep(pawn);
        }

        private bool TargetStillValid(Pawn pawn, LocalTargetInfo target)
        {
            // pool was built against one driver pawn's allowed area - the
            // rest of the group can have different zones, so re-check here
            Area allowed = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;

            if (target.HasThing)
            {
                Thing thing = target.Thing;
                if (thing == null || thing.Destroyed || thing.Map != map)
                {
                    return false;
                }
                if (thing.IsForbidden(pawn))
                {
                    return false;
                }
                if (allowed != null && !allowed[thing.Position])
                {
                    return false;
                }
                return map.reservationManager.CanReserve(pawn, thing);
            }

            // cell target (e.g. an empty tile waiting to be sown) - no
            // Thing to check Destroyed/forbidden on, just bounds + area +
            // reservation
            IntVec3 cell = target.Cell;
            if (!cell.InBounds(map))
            {
                return false;
            }
            if (allowed != null && !allowed[cell])
            {
                return false;
            }
            return map.reservationManager.CanReserve(pawn, target);
        }

        private static int NearestTargetIndex(IntVec3 from, List<LocalTargetInfo> pool)
        {
            int nearest = 0;
            float nearestDistSq = (pool[0].Cell - from).LengthHorizontalSquared;

            for (int i = 1; i < pool.Count; i++)
            {
                float distSq = (pool[i].Cell - from).LengthHorizontalSquared;
                if (distSq < nearestDistSq)
                {
                    nearest = i;
                    nearestDistSq = distSq;
                }
            }

            return nearest;
        }
    }
}
