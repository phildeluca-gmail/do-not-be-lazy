<!-- Pickup context for a fresh session. Updated 2026-08-16. Read this first, then CLAUDE.md's referenced docs as usual. -->

# Pickup: Do Not Be Lazy

Resume **this** conversation (the sow-fix session) with:

```
claude --resume cc6c6703-86ad-4821-85ea-64813ca0b8ec
```

Earlier sessions, for reference only:

```
OLD: claude --resume 18d354df-c62b-4ef8-805c-7cbd58244e51
OLD: claude --resume bd100a68-9153-4629-abf2-f0045dc3b922
```

Read `CLAUDE.md` first (project instructions), then this file, then
`DoNotBeLazy_Architecture.md` section 0 for the detailed current-state
log. This file is the fast-orientation summary; the architecture doc's
status section has the full reasoning behind everything below.

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
- **NOTHING FROM THIS SESSION IS COMMITTED.** `HEAD` is still `b2b5de9`.
  Seven behavioural changes plus a new file sit in the working tree.
  Do not assume the repo reflects the code.
- **NOTHING FROM THIS SESSION IS VERIFIED IN GAME.** Every fix below is
  static analysis + clean build only. The user had not run the new DLL
  when the session ended.

Uncommitted:

```
 M DoNotBeLazy/Source/DoNotBeLazy/Components/SweepManager.cs
 M DoNotBeLazy/Source/DoNotBeLazy/Core/DoNotBeLazySettings.cs
 M DoNotBeLazy/Source/DoNotBeLazy/Core/Logger.cs
 M DoNotBeLazy/Source/DoNotBeLazy/Patches/FloatMenuPatch.cs
 M DoNotBeLazy/Source/DoNotBeLazy/Utility/TaskScanner.cs
 M DoNotBeLazy_Architecture.md
 M NEXT_SESSION.md
?? DoNotBeLazy/Source/DoNotBeLazy/Utility/GrowerCompat.cs
```

## What was fixed this session (all unverified)

Reported: "`* sow crops` appears but doesn't sow crops, the pawns seem
to get new jobs" and "sow assigns sowing to unzoned terrain."
**Both are one root cause.**

`WorkGiver_Grower.wantedPlantDef` is a **mutable `protected static`**
(verified by reflection on `lib/Assembly-CSharp.dll`). Vanilla only
initializes it inside `PotentialWorkCellsGlobal`; this mod never calls
that, so it holds whatever the last caller left. Two failures: the stale
def is baked into the job as `plantDefToSow` and `JobDriver_PlantSow`'s
goto toil `FailOn`s it (job dies on the walk, sweep ends, pawn wanders
off); and the *only* zone-membership gate is `CalculateWantedPlantDef`
returning null, which sits inside the `if (wantedPlantDef == null)`
branch that a stale value skips - so unzoned dirt falls through to a
sow job.

1. **Static reset** - new `Utility/GrowerCompat.cs`, called before every
   `HasJobOnCell`/`JobOnCell` on a Grower scanner, at all three sites
   (`TaskScanner.ScanCells`, `FloatMenuPatch.FindTargetWithJob`,
   `SweepManager.AssignNextTask`). Fixes both reported symptoms.
2. **Sow gates** - `CanAcceptSowNow()` + `Zone_Growing.allowSow`, which
   live only in `ExtraRequirements` and which fix 1 does NOT restore.
3. **Reachability** - `WorkGiver_Grower.AllowUnreachable` is true, so
   vanilla does its own `CanReach`; we did neither.
4. **Blocker-job chaining** - `GrowerSow.JobOnCell` can return
   `CutPlant`/`HaulAside` instead of `Sow`; the cleared cell was being
   dropped. Re-queued once, guarded by `SweepOrder.Requeued`.
5. **A failed target is no longer a failed sweep** - `Notify_JobEnded`
   used to end the sweep on *any* non-`Succeeded` condition. Now splits
   target-scoped failures (`Incompletable`, `QueuedNoLongerValid`,
   `ErroredPather` - continue) from pawn-scoped interrupts
   (`InterruptForced/Optional`, `Errored` - stop), bounded by
   `MaxConsecutiveFailures` (8, per pawn).
6. **Fire filtered per target**, not just the clicked cell -
   `TaskScanner.TargetIsBurning`, also re-checked in `TargetStillValid`.
   Deliberately diverges from vanilla's whole-zone skip.
7. **Verbose logging actually works now** - see below.

## The logging trap - do not repeat this mistake

`Logger.VerboseLogging` was hardcoded `false` with a Phase-1 comment
saying it would be wired to settings "once Phase 2 exists." It never
was. **Every `Logger.Message` call in the mod was a no-op from Phase 1
until 2026-08-16.** Multiple live playtest logs showed zero
`[DoNotBeLazy]` lines and were read as "nothing fired" when the truth
was "nothing could be printed." Now driven by a settings checkbox
(set in both `DoWindowContents` and `ExposeData`).

**Before concluding anything from an absence of log lines, confirm the
logger is live.**

## Workflow: stop pasting whole logs

The user pasted several ~8,000-line logs; each burned enormous context
to reach a two-line answer, and the user called this out directly. The
fix is in place - use it.

1. Options > Mod Settings > Do Not Be Lazy > tick "Verbose logging".
2. Reproduce.
3. Ask the user to run this (the `!` prefix runs it in-session so the
   output lands in the conversation):

```
! Select-String -Path "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log" -Pattern '\[DoNotBeLazy\]' | ForEach-Object { $_.Line }
```

RimWorld truncates `Player.log` on launch - extract before restarting.
Zero lines is itself diagnostic (checkbox off, or stale DLL loaded).

Trace lines emitted: `BeginSweep <def>: N targets, M pawns` /
`scan <def> r=N at <cell> for <pawn>: N targets` /
`<pawn>: <JobDef> on <target> plant=<def> (N left)` /
`<pawn>: no job for <target>` / `<pawn>: job ended <condition>`.
The `plant=` field is the decisive one for sow.

## Modlist - the old note was wrong

Previous versions of this file and the `modlist` memory said "base game
+ Pick Up And Haul only." **Wrong.** The playtest save loads **~60
mods** (RimWorld 1.5.4409, still no DLC). Get the authoritative list
from the `Loading game from file ... with mods:` block in any log.

On our code paths: **Performance Fish** patches
`WorkGiverDef::get_Worker()` (which `EligibleDefs()` calls on every def),
`ListerThings.ThingsMatching`, `WorkGiver_Haul.PotentialWorkThingsGlobal`.
**TKS Priority Treatment** patches `Pawn_JobTracker.TryFindAndStartJob`.
**Sense of Urgency** adds parallel "urgent" WorkGiverDefs - a likely
source of duplicate `*` options, and worth disabling when testing.

## Two broken third-party mods - ignore their log noise

Both fail the same way: compiled against RimWorld 1.6, calling a
7-parameter overload where 1.5 has 6. Neither is our bug.

- **Sense of Urgency** (`ZombiePhil.Urgency`, Workshop 3001253573) -
  `Toils_General.WaitWith` 7 params vs 6. **Hunting is completely
  broken**: every hunter loops "started 10 jobs in 10 ticks" forever.
  Its About.xml claims 1.5 support and it ships
  `ZombiePhil.Urgency.v15.dll`, so it *looks* compatible. Recommend
  disabling. (It also owns the `HunterHuntByPriority` WorkGiverDef -
  I initially misattributed this to Automatic Hunting; a grep of the
  Workshop folder settled it.)
- **Automatic Hunting** (`Arylice.Rimworld.AutomaticHunting`) -
  `TraverseParms.For` 7 params vs 6, throws every tick in
  `GameComponentTick`.

## Still-open bugs in our code (not yet fixed)

- `ScanCells` has no `IsForbidden` check (`ScanThings` has one).
  Harmless for sow only because `GrowerSow.JobOnCell` checks it
  downstream - latent for any future cell-based WorkGiver.
- The `CanReserve` pre-filter in `TaskScanner` omits
  `ignoreOtherReservations` while `JobOnCell` passes `forced` through,
  so the pre-filter is stricter than the job it gates.
- Fix 4 (`IsPreparatoryJob`) is not scoped to cell targets, so
  construction frames also get one extra re-queue. Plausibly an
  improvement, capped, but untested beyond sow.
- Perf watch: `CanReachTarget` now runs per cell in the radial scan
  (~800 at default radius 16, ~7,800 at the max of 50). If right-click
  hitches at large radius, move the check after `HasJobOnCell`.

## Still unanswered from earlier sessions

1. **"Cannot force-haul stone blocks."** Best guess: a stockpile in
   reach doesn't have "Blocks" ticked. Needs the user's stockpile setup.
2. **"The `* forced delivery to (ITEM)` is gone."** Needs a repro. Now
   also worth re-checking against Urgency/Performance Fish rather than
   assuming a clean modlist.

## Planned but NOT implemented - do not build without a fresh go-ahead

- `DoNotBeLazy_Architecture.md` section 5.4 - idle-pawn nudge
  ("standing" rule).
- `DoNotFreakOut_Architecture.md` - a separate, Harmony-free mod. Not
  started, no folder.

## Method notes (these work well, keep using them)

- **Reflect on the real game DLL** (`lib/Assembly-CSharp.dll`) via
  PowerShell + `[System.Reflection.Assembly]::LoadFrom` rather than
  trusting memory. This session it caught: `wantedPlantDef` really is
  static; `FireUtility` is in `RimWorld`, not `Verse`; the exact
  `JobCondition` values; `LocalTargetInfo` has real `==`/`GetHashCode`;
  and both third-party version mismatches. `GetType()` returns null for
  a wrong namespace rather than throwing - check for null, or the script
  fails confusingly two lines later.
- **Decompiled bodies** via
  `https://raw.githubusercontent.com/josh-m/RW-Decompile/master/RimWorld/<Class>.cs`
  (`Verse.AI` classes sit under a literal `Verse.AI/` folder).
- **Grep Core Defs XML** at
  `E:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\` for
  WorkGiverDef specifics. Grepping the whole Workshop folder is slow -
  run it in the background and wait for it rather than guessing.
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
  `FloatMenuPatch`/`SweepManager` work. Reactive playtest bug-fixing was
  never given a model assignment by that plan - don't claim it was.
