<!-- Pickup context for a fresh session. Updated 2026-08-17. Read this first, then CLAUDE.md's referenced docs as usual. -->

# Pickup: Do Not Be Lazy

Resume **this** conversation (the consume/log-triage session) with:

```
claude --resume 88fc941c-80ed-4d29-b235-7b39abac91ce
```

Earlier sessions, for reference only:

```
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
bills) that send eligible pawns to do that task repeatedly across a
radius until done. Also has a `* Consume` option for eating/drugs
(separate system, not WorkGiver-based) and pause/resume on critical
needs (hunger/rest/joy/mood).

## State right now - READ THIS FIRST

- Builds clean: `cd DoNotBeLazy/Source/DoNotBeLazy && dotnet build`
  (0 errors, 0 warnings).
- **The sow work IS committed** as `cc502c9`, plus `7bd8048` for a
  commit-batch-file fix. The previous version of this file claimed
  nothing was committed and `HEAD` was `b2b5de9` - that was stale and
  cost time. Check `git log` rather than trusting this line.
- **The sow fixes are STILL not verified in game.** Phase 1 of the test
  plan below has not been run. Everything in `cc502c9` about
  `wantedPlantDef`, zone gates, reachability and blocker chaining is
  static analysis only.
- **What IS now verified in game (2026-08-17):** verbose logging works,
  and a `HaulMerge` sweep runs end to end (targets found, jobs handed
  out, `Succeeded`, pool counting down). That's the first confirmed
  working sweep from a real save.

## Test plan

A 26-test playtest plan is published here:

```
https://claude.ai/code/artifact/e60cfd11-1f82-46a8-9111-a25d9352a2dd
```

Phase 0 (prove the DLL is current, the logger is live, and the
`wantedPlantDef` reflection resolved) already passes as of 2026-08-17.
**Phase 1 - the four sow tests - is the next thing to run.**

One correction to that plan: T3.5 says food yields `* Eat meal`. Wrong.
Only drugs set `ingestCommandString` in Core, so food and corpses fall
through to our hardcoded `"Consume " + LabelShort` and read
`* Consume fine meal`. The architecture doc has the same wrong example.

## TOP BUG FOR NEXT SESSION: the need pause/resume loop

Found 2026-08-17 in a real log. **Not yet fixed.** This is the first
thing to work on.

Observed: a pawn was paused for a critical need after *every single*
task in a haul sweep - three tasks, three pauses - instead of pausing
once, going to eat, and coming back.

```
Anarch: HaulToCell on Thing_Chocolate13373128 (29 left)
Anarch paused from sweep: need at/below threshold...
Anarch: job ended InterruptForced (HaulMerge)
Anarch: HaulToCell on Thing_Chocolate13401960 (28 left)
Anarch paused from sweep: need at/below threshold...
Anarch: job ended Succeeded (HaulMerge)
Anarch: HaulToCell on Thing_Steel14203825 (27 left)
Anarch paused from sweep: need at/below threshold...
```

Root cause: `SweepManager.Notify_JobEnded` resumes on the **first** job
end after a pause (`if (pausedForNeed.Remove(pawn))`) without checking
whether the need was ever satisfied. `PauseForNeed` calls
`EndCurrentJob(InterruptForced)`, which defaults `startNewJob: true`, so
vanilla immediately starts *something* - not necessarily eating. When
that something ends, we resume. `NeedMonitor` then re-pauses 60 ticks
later because `IsPaused` is false again. Loop.

**Both docs currently assert this can't happen** - architecture doc
section 2 and the 2026-08-15 status entry both say "interrupt-loop risk
doesn't reappear because resumption is gated on a real job-end event,
not a repeated need check." That reasoning is wrong: the job-end event
can be the replacement job the interrupt itself triggered. Those claims
are now marked corrected in the architecture doc - don't re-derive.

Likely fix (not implemented, not agreed): re-check the need on resume
and stay paused while it's still under threshold. Needs an architecture
doc edit first, per CLAUDE.md.

## Second finding: the float-menu path emits no log lines at all

`AddConsumeOption`, `FindTargetWithJob`, `IsSweepEligible` and the whole
option-building path in `FloatMenuPatch` contain **zero**
`Logger.Message` calls - only `Error`/`Warning`. Only `SweepManager` and
`TaskScanner` are traced, and those only run *after* an option is
picked.

So any "wrong/missing/duplicate menu option" bug is invisible to the
trace no matter how many times it's reproduced. Diagnose those from the
defs instead (see the method notes at the bottom). Worth considering a
verbose line per offered option next time this class of bug comes up.

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
whether that code path logs at all (see the float-menu finding above).

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
`<pawn> paused from sweep: ...`. The `plant=` field is the decisive one
for sow.

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

- **The need pause/resume loop** - see above, top priority.
- `ScanCells` has no `IsForbidden` check (`ScanThings` has one).
  Harmless for sow only because `GrowerSow.JobOnCell` checks it
  downstream - latent for any future cell-based WorkGiver.
- The `CanReserve` pre-filter in `TaskScanner` omits
  `ignoreOtherReservations` while `JobOnCell` passes `forced` through,
  so the pre-filter is stricter than the job it gates.
- `IsPreparatoryJob` is not scoped to cell targets, so construction
  frames also get one extra re-queue. Plausibly an improvement, capped,
  but untested beyond sow.
- Perf watch: `CanReachTarget` now runs per cell in the radial scan
  (~800 at default radius 16, ~7,800 at the max of 50).

## Still unanswered from earlier sessions

1. **"Cannot force-haul stone blocks."** Best guess: a stockpile in
   reach doesn't have "Blocks" ticked. Needs the user's stockpile setup.
2. **"The `* forced delivery to (ITEM)` is gone."** Needs a repro, and
   re-checking with Sense of Urgency disabled.

## Planned but NOT implemented - do not build without a fresh go-ahead

- `DoNotBeLazy_Architecture.md` section 5.4 - idle-pawn nudge
  ("standing" rule).
- `DoNotFreakOut_Architecture.md` - a separate, Harmony-free mod. Not
  started, no folder.

## Method notes (these work well, keep using them)

- **Reflect on the real game DLL** (`lib/Assembly-CSharp.dll`) via
  PowerShell + `[System.Reflection.Assembly]::LoadFrom`. `GetType()`
  returns null for a wrong namespace rather than throwing - and
  `GetTypes()` throws `ReflectionTypeLoadException` on this assembly, so
  catch it and read `$_.Exception.Types` to enumerate. Caught this
  session: `IngestibleProperties` and `ThingDefGenerator_Corpses` are in
  `RimWorld`, not `Verse`.
- **Grep the Workshop folder for label text** to identify which mod owns
  an unexpected menu entry - `<label>[^<]*harvest[^<]*</label>` across
  `E:\SteamLibrary\steamapps\workshop\content\294100` found the
  harvesting table in seconds. Faster than guessing.
- **Grep Core Defs XML** at
  `E:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\` - note
  `WorkGiverDefs\` is a single `WorkGivers.xml`.
- **Decompiled bodies** via
  `https://raw.githubusercontent.com/josh-m/RW-Decompile/master/RimWorld/<Class>.cs`
  (`Verse.AI` classes sit under a literal `Verse.AI/` folder).
- **Generalized lesson, now twice-burned:** vanilla WorkGivers put real
  preconditions in `Potential*Global`/`ExtraRequirements`, not in
  `JobOn*`. This mod calls `JobOn*` directly and silently bypasses all
  of them. Before adding any WorkGiverDef to the eligible set, read the
  worker's decompiled source for (a) mutable statics, (b) an
  `ExtraRequirements` override, (c) reachability/fire/zone-toggle checks
  living in the scan.

## Workflow notes

- User wants `DoNotBeLazy_Architecture.md` updated after essentially
  every turn - keep doing that. Per CLAUDE.md, doc edit precedes code.
- User tests by manually copying the built DLL into their RimWorld
  `Mods/DoNotBeLazy/` folder and fully restarting the game.
- `gh` CLI is not installed on this machine.
- Commit message style: detailed body explaining *why*. **Only commit
  when explicitly asked.**
- The model plan in architecture doc section 5 assigns Opus to
  `FloatMenuPatch`/`SweepManager` work and Sonnet to the test checklist.
  Reactive playtest bug-fixing was never given a model assignment -
  don't claim it was.
