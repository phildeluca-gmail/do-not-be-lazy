<!-- Pickup context for a fresh session. Updated 2026-08-18. Read this first, then CLAUDE.md's referenced docs as usual. -->

# Pickup: Do Not Be Lazy

Resume **this** conversation (fire sweeps / need-pause fix / menu feedback) with:

```
claude --resume 9fe56f23-dbd5-4515-818c-6170fe4921d1
```

Earlier sessions, for reference only:

```
OLD: claude --resume 88fc941c-80ed-4d29-b235-7b39abac91ce   (consume/log triage)
OLD: claude --resume cc6c6703-86ad-4821-85ea-64813ca0b8ec   (sow fix)
OLD: claude --resume 18d354df-c62b-4ef8-805c-7cbd58244e51
OLD: claude --resume bd100a68-9153-4629-abf2-f0045dc3b922
```

Read `CLAUDE.md` first (project instructions), then this file, then
`DoNotBeLazy_Architecture.md` section 0 for the detailed current-state
log. This file is the fast-orientation summary.

## How the user wants to be talked to

**Brief text when possible.** Short answers, no restating what was just
asked, no padding around a result. Depth on request. This applies to
chat replies - the docs in this repo stay dense on purpose, that's their
job.

Also still true: don't paste whole logs (see the extraction workflow
below), and only commit when explicitly asked.

## What this is

A RimWorld 1.5 Harmony mod. Right-click a target with pawns selected,
get `*` sweep options (haul/build/mine/clean/sow/harvest/cut/workstation
bills/**fight fires**) that send eligible pawns to do that task
repeatedly across a radius until done. Also has a `* Consume` option for
eating/drugs (separate system, not WorkGiver-based) and pause/resume on
critical needs (hunger/rest/joy/mood).

## State right now - READ THIS FIRST

- Builds clean: `cd DoNotBeLazy/Source/DoNotBeLazy && dotnet build`
  (0 errors, 0 warnings), verified at the end of this session.
- Everything below is **committed and pushed**. Check `git log` rather
  than trusting this line - a previous version of this file was stale
  about commit state and it cost time.
- **NOTHING FROM 2026-08-18 HAS BEEN TESTED IN GAME.** Three changes
  landed this session (below) and all three are static analysis plus a
  clean compile only. The user left the machine right after asking for
  them.
- Still untested from before: **the sow fixes** (`cc502c9`). Phase 1 of
  the playtest plan has still not been run.
- **What IS verified in game (2026-08-17):** verbose logging works, and
  a `HaulMerge` sweep runs end to end. That's still the only confirmed
  working sweep from a real save.

## What changed 2026-08-18 (this session) - all untested

Three user-reported items, all implemented, doc'd, committed, pushed.

**1. `* Fight fires until done` - group firefighting now exists.**
Reported as "cannot group-select to put out fires." Three gates were
blocking it, all deliberate at the time: `FightFires` is
`directOrderable:false` and `IsSweepEligible` respected that flag;
`FloatMenuPatch.Build` bailed out of the *whole* right-click on a
burning cell; and `TaskScanner.TargetIsBurning` drops every burning
target, which for a fire sweep is every target there is. All three now
check `FireCompat.IsFirefighting(def)` (keyed on
`workType.defName == "Firefighter"`, not the defName, so modded
firefighting WorkGivers get the same treatment).

Clicking a burning cell now offers the fire sweep **and nothing else** -
the old "don't send pawns into a burning tile" rule still holds for
every other def, and `* Consume` is suppressed there too.

**Vanilla's home-area restriction is deliberately overridden**, per the
user's explicit decision. `WorkGiver_FightFires.HasJobOnThing` refuses
any fire outside `areaManager.Home`; `FireCompat.HasFireJob`
re-implements that method without that one gate. It keeps everything
else: pawn-attached-fire rules, `WorkTags.Firefighting`, the
past-15-tiles reservation check, reachability, and a re-implementation
of `FireIsBeingHandled` (the worker class is `internal`, so even its
`public static` helper is unreachable from our assembly). That last one
is what makes a group fan out over separate fires instead of piling onto
one.

Fire sweeps are the first **rescannable** order: `SweepOrder` now
carries the scan origin/radius and `AssignNextTask` re-scans once when
the pool empties, because fires spread and a pool frozen at
`BeginSweep` time is stale immediately. One rescan per call, so no loop.

**2. Fixed: the need pause/resume loop (the top bug from last
session).** Reported this session as "assign to task with asterisk, they
take break and do not return to that task." Root cause is what the
2026-08-17 entry described, plus a second symptom worse than the logged
one:

- `Notify_JobEnded` consumed `pausedForNeed` on the *first* job end
  after a pause. `PauseForNeed` calls `EndCurrentJob(InterruptForced)`,
  which defaults `startNewJob:true`, so the first thing to end is
  usually vanilla's replacement job, not a meal - the pawn got dragged
  back mid-break.
- **And then the sweep died for good:** with the pause flag spent, the
  *next* interrupt (usually the genuine one, vanilla forcing them to go
  eat) arrived unpaused, hit `TargetFailureIsRecoverable(InterruptForced)`
  = false, and `RemoveSweep`d the order. That's the "never comes back"
  half, and it means the observed behaviour was a lost sweep, not just a
  noisy one.

Fix: a job end is now only a *prompt to re-check*. `Notify_JobEnded`
calls `NeedMonitor.NeedsSatisfied(pawn)` and stays paused if any need is
still under threshold, without touching the failure counter or ending
the sweep. `NeedMonitor`'s existing `IsPaused` guard then prevents any
re-interrupt, so the 60-tick loop can't form. Two supporting details:
`NeedMonitor.ResumeMargin` (0.05) gives hysteresis so a need sitting on
the line doesn't thrash, and `SweepManager.MaxPauseTicks` (30,000, half
a day) ends a sweep whose need never recovers instead of holding the
pool forever. `pausedForNeed` went from `HashSet<Pawn>` to
`Dictionary<Pawn,int>` to carry the pause tick.

**3. New: the menu says why a sweep isn't on offer.** Requested as
"when ordered to do something like haul with no valid targets, provide
feedback in the form of a float menu that describes the error
completely." Built on vanilla's own mechanism rather than invented -
`FloatMenuMakerMap.AddJobGiverWorkOrders` already does exactly this and
was read from the decompiled source:

- `Verse.AI.JobFailReason` is a static WorkGivers write into while
  answering `HasJobOn*`. Clear it before the probe, read
  `HaveReason`/`Reason` after. `HaulAIUtility` sets it for every haul
  refusal there is, including `NoEmptyPlaceLower` - "no empty place to
  put it", which is very likely the answer to both the **stone blocks**
  and **wood** reports still listed as open below.
- Failing defs get a **disabled** (greyed, null action) entry reading
  `* <label> until done - <thing>: <reason>`.
- Scoped like vanilla scopes it: only defs whose own
  `PotentialWorkThingRequest.Accepts(thing)` is true, and only when a
  reason actually exists. No reason, no entry. Capped at
  `MaxFeedbackOptions` (3) so the menu can't fill with grey.
- Only the `scanThings` branch reports. A cell-scanned def refusing bare
  ground is the normal case, and "* Sow until done - not a growing zone"
  on every dirt click would be noise.
- Second surface: if a sweep starts but the radius scan finds nothing,
  `BeginAreaSweep` raises a `Messages.Message(..., RejectInput)`. The
  menu is gone by then, so a message is the only channel left.

## What to do next session

1. **Test the three changes above in game.** Nothing is verified. The
   fire sweep is the most likely to surprise - it's the first sweep type
   that overrides a vanilla precondition rather than restoring one.
2. Specifically worth watching for the fire sweep: do pawns fan out over
   separate fires (that's `FireIsBeingHandled`) or pile onto one; does
   the rescan pick up spread; does anything odd happen when a fire is
   extinguished by someone else mid-walk.
3. For the feedback entries: right-click stone blocks and wood with a
   group selected. If the greyed entry says "no empty place to put it",
   both long-standing open reports below are answered and can be closed.
4. Then Phase 1 of the sow test plan, still unrun.

## Decision left open - needs the user

**Drafted pawns are still excluded from fire sweeps.** This was asked
and not answered before the user left, so it was left consistent with
the rest of the codebase rather than changed unilaterally. Vanilla
disagrees: `FightFires` is `canBeDoneWhileDrafted: true` with
`autoTakeablePriorityDrafted: 20`, and a fire during a raid is exactly
when everyone is drafted. Including them means a per-WorkGiver exception
in **two** places - `PawnValidator.CanSweep` (rejects `pawn.Drafted`)
and `SweepManager.MapComponentTick` (drops a pawn the moment they're
drafted). **For now: undraft before ordering a fire sweep.** Ask before
building it.

## Test plan

A 26-test playtest plan is published here:

```
https://claude.ai/code/artifact/e60cfd11-1f82-46a8-9111-a25d9352a2dd
```

Phase 0 (prove the DLL is current, the logger is live, and the
`wantedPlantDef` reflection resolved) passes as of 2026-08-17. **Phase 1
- the four sow tests - has still not been run.** The plan predates this
session, so it has no fire-sweep or menu-feedback tests in it.

One correction to that plan: T3.5 says food yields `* Eat meal`. Wrong.
Only drugs set `ingestCommandString` in Core, so food and corpses fall
through to our hardcoded `"Consume " + LabelShort` and read
`* Consume fine meal`.

## The float-menu path is still mostly untraced

`AddConsumeOption`, `FindTargetWithJob`, `IsSweepEligible` and the
option-building path in `FloatMenuPatch` contain **zero**
`Logger.Message` calls - only `Error`/`Warning`. Only `SweepManager` and
`TaskScanner` are traced, and those only run *after* an option is
picked. So any "wrong/missing/duplicate menu option" bug is invisible to
the trace.

Partly mitigated now: the disabled feedback entries surface refusal
reasons in the menu itself, which is player-visible rather than
log-visible. Still worth a verbose line per offered option next time
this class of bug comes up.

## Corpse / `* Consume` - investigated, decision is LEAVE AS-IS

Reported: harvesting a corpse at a harvesting table should say "harvest",
not "consume". **User decided to leave the code alone.** Recorded so
nobody re-derives it:

- The harvesting table is **Reclaim, Reuse, Recycle (Continued)**
  (`Mlie.ReclaimReuseRecycle`, Workshop `2567364887`).
- Its `R3_DoWorkHarvestCorpse` is `giverClass WorkGiver_DoBill`,
  `workType Doctor`, label "harvest corpse", and crucially
  `fixedBillGiverDefs: R3_TableHarvesting`.
- We already offer it correctly as `* Harvest corpse until done` -
  `IsSweepEligible` takes any `WorkGiver_DoBill` regardless of workType.
  But `fixedBillGiverDefs` means `HasJobOnThing` is only true for the
  **table**, and `FindTargetWithJob` only looks at things on the clicked
  cell. So clicking the *corpse* can only ever produce `* Consume ...`;
  clicking the *table* produces the harvest option.
- `RimWorld.IngestibleProperties` (note: `RimWorld`, not `Verse`) -
  verified by reflection: `showIngestFloatOption` defaults **true**,
  `ingestCommandString` defaults **empty**. Only drugs set that string
  in Core. So corpses get our hardcoded `"Consume " + LabelShort`.
- The label matches vanilla exactly - base RimWorld also says
  "Consume human corpse". Not a divergence, a design question.

## The logging trap - resolved, but know the history

`Logger.VerboseLogging` was hardcoded `false` from Phase 1 until
2026-08-16, making every `Logger.Message` a no-op. Several playtest logs
showed zero `[DoNotBeLazy]` lines and were read as "nothing fired". It's
now driven by a settings checkbox and **confirmed working in game**.

Still: before concluding anything from an absence of log lines, check
whether that code path logs at all (see the float-menu note above).

## Workflow: log extraction

1. Options > Mod Settings > Do Not Be Lazy > tick "Verbose logging".
   Close the settings window - that's what writes it to disk.
2. Reproduce.
3. Extract (Claude can run this directly, no need to paste):

```
Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log" -Pattern '\[DoNotBeLazy\]' | ForEach-Object { $_.Line }
```

RimWorld truncates `Player.log` on launch - extract before restarting.

Trace lines emitted: `BeginSweep <def>: N targets, M pawns` /
`scan <def> r=N at <cell> for <pawn>: N targets` /
`<pawn>: <JobDef> on <target> plant=<def> (N left)` /
`<pawn>: no job for <target>` / `<pawn>: job ended <condition>` /
`<pawn> paused from sweep: ...` / **new this session:**
`<pawn>: needs satisfied, resuming sweep (<def>)` and
`<pawn>: still under threshold after N ticks paused, ending sweep.`

Those two new lines are the ones to grep for when checking the
pause/resume fix - the old bug showed as a pause line after *every*
task with no resume line between them.

## Modlist

**~60 mods**, RimWorld 1.5.4409, no DLC. Authoritative list comes from
the `Loading game from file ... with mods:` block in any log.

On our code paths: **Performance Fish** patches
`WorkGiverDef::get_Worker()` (which `EligibleDefs()` calls on every def),
`ListerThings.ThingsMatching`, `WorkGiver_Haul.PotentialWorkThingsGlobal`.
**TKS Priority Treatment** patches `Pawn_JobTracker.TryFindAndStartJob`.
**Sense of Urgency** adds parallel "urgent" WorkGiverDefs - a likely
source of duplicate `*` options, and worth disabling when testing.
**Reclaim, Reuse, Recycle** adds the harvesting/refurbishment tables
(both `WorkGiver_DoBill`, so both are sweep-eligible).

## Two broken third-party mods - ignore their log noise

Both compiled against RimWorld 1.6, calling a 7-parameter overload where
1.5 has 6. Neither is our bug.

- **Sense of Urgency** (`ZombiePhil.Urgency`, Workshop 3001253573) -
  `Toils_General.WaitWith`. **Hunting is completely broken**: hunters
  loop "started 10 jobs in 10 ticks" forever. Recommend disabling.
- **Automatic Hunting** (`Arylice.Rimworld.AutomaticHunting`) -
  `TraverseParms.For`, throws every tick in `GameComponentTick`.

## Still-open bugs in our code (not yet fixed)

- `ScanCells` has no `IsForbidden` check (`ScanThings` has one).
  Harmless for sow only because `GrowerSow.JobOnCell` checks it
  downstream - latent for any future cell-based WorkGiver.
- The `CanReserve` pre-filter in `TaskScanner` omits
  `ignoreOtherReservations` while `JobOnCell` passes `forced` through,
  so the pre-filter is stricter than the job it gates. Now also applies
  to fires: vanilla only reservation-checks a fire past 15 tiles, we
  check every one.
- `IsPreparatoryJob` is not scoped to cell targets, so construction
  frames also get one extra re-queue. Plausibly an improvement, capped,
  but untested beyond sow.
- Perf watch: `CanReachTarget` now runs per cell in the radial scan
  (~800 at default radius 16, ~7,800 at the max of 50).

## Still unanswered from earlier sessions

1. **"Cannot force-haul stone blocks."** Best guess: a stockpile in
   reach doesn't have "Blocks" ticked. **The new disabled menu entries
   should now answer this directly** - see item 3 of what changed.
2. **"The `* forced delivery to (ITEM)` is gone."** Needs a repro, and
   re-checking with Sense of Urgency disabled.

## Planned but NOT implemented - do not build without a fresh go-ahead

- `DoNotBeLazy_Architecture.md` section 5.4 - idle-pawn nudge
  ("standing" rule).
- `DoNotFreakOut_Architecture.md` - a separate, Harmony-free mod. Not
  started, no folder.
- Drafted pawns in fire sweeps - see the open decision above.

## Method notes (these work well, keep using them)

- **Reflect on the real game DLL** (`lib/Assembly-CSharp.dll`) via
  PowerShell + `[System.Reflection.Assembly]::LoadFrom`. `GetType()`
  returns null for a wrong namespace rather than throwing - and
  `GetTypes()` throws `ReflectionTypeLoadException` on this assembly, so
  catch it and read `$_.Exception.Types` to enumerate. Caught this way:
  `IngestibleProperties`/`ThingDefGenerator_Corpses` are in `RimWorld`
  not `Verse`; `ReservationManager` is in `Verse.AI` not `RimWorld`.
- **The github decompile is an OLDER BUILD than our DLL - always
  cross-check signatures by reflection before compiling against them.**
  Cost a build error this session: the decompile has
  `pawn.story.WorkTagIsDisabled(...)`, but in 1.5 it's
  `pawn.WorkTagIsDisabled(...)` on `Pawn` itself. Same for
  `FirstRespectedReserver`, which is 3-arg here and 2-arg there.
- **Read vanilla's own solution before inventing one.** The menu
  feedback work was mostly reading `AddJobGiverWorkOrders` and copying
  its scoping rules; guessing would have produced a menu full of grey.
- **Grep the Workshop folder for label text** to identify which mod owns
  an unexpected menu entry - `<label>[^<]*harvest[^<]*</label>` across
  `E:\SteamLibrary\steamapps\workshop\content\294100` found the
  harvesting table in seconds.
- **Grep Core Defs XML** at
  `E:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\` - note
  `WorkGiverDefs\` is a single `WorkGivers.xml`. This is where
  `directOrderable`/`canBeDoneWhileDrafted` on `FightFires` came from.
- **Decompiled bodies** via
  `https://raw.githubusercontent.com/josh-m/RW-Decompile/master/RimWorld/<Class>.cs`
  (`Verse.AI` classes sit under a literal `Verse.AI/` folder - that's
  where `HaulAIUtility` lives, not `RimWorld/`).
- **Generalized lesson, now three times burned:** vanilla WorkGivers put
  real preconditions in `Potential*Global`/`ExtraRequirements`, not in
  `JobOn*`. This mod calls `JobOn*` directly and silently bypasses all
  of them. Firefighting is the third instance and the first where the
  bypass was what we *wanted* - but note it still had to be done by
  hand, in `FireCompat`, rather than falling out for free.

## Workflow notes

- User wants `DoNotBeLazy_Architecture.md` updated after essentially
  every turn - keep doing that. Per CLAUDE.md, doc edit precedes code.
- User tests by manually copying the built DLL into their RimWorld
  `Mods/DoNotBeLazy/` folder and fully restarting the game. **The
  2026-08-18 build has not been copied over yet.**
- `gh` CLI is not installed on this machine.
- Commit message style: detailed body explaining *why*. **Only commit
  when explicitly asked.**
- The model plan in architecture doc section 5 assigns Opus to
  `FloatMenuPatch`/`SweepManager` work and Sonnet to the test checklist.
  Reactive playtest bug-fixing was never given a model assignment -
  don't claim it was.
