<!-- Pickup context for a fresh session. Updated 2026-08-20. Read this first, then CLAUDE.md's referenced docs as usual. -->

# Pickup: Do Not Be Lazy

Resume **this** conversation (vehicle-packing investigation, doc commit
`c19f816`) with:

```
claude --resume 4b475e1d-24b5-48e9-94e4-f6ce4865faa9
```

Earlier sessions, for reference only:

```
OLD: claude --resume 5e862c7c-fa2b-40a6-b221-e0174424c01f   (08-18 review: six findings, radius, standing-still)
OLD: claude --resume 9fe56f23-dbd5-4515-818c-6170fe4921d1   (fire sweeps / need-pause fix / menu feedback)
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
bills/fight fires) that send eligible pawns to do that task repeatedly
across a radius until done. Also has a `* Consume` option for
eating/drugs (separate system, not WorkGiver-based) and pause/resume on
critical needs (hunger/rest/joy/mood).

## State right now - READ THIS FIRST

- Builds clean: `cd DoNotBeLazy/Source/DoNotBeLazy && dotnet build`
  (0 errors, 0 warnings).
- Working tree clean as of the last commit, `c19f816` (doc-only). Check
  `git log` rather than trusting this line - this file has been stale
  about commit state twice now and it cost time both times.
- **Last code commit is still `9dc8717`.** Neither the 2026-08-18
  evening review session nor the 2026-08-19/20 vehicle investigation
  changed any code. `c19f816` was the docs for both.
- **Test state for the 2026-08-18 code is UNCONFIRMED.** The overnight
  session ended without the DLL being copied into the RimWorld `Mods/`
  folder, and nobody established whether the sessions played since were
  running the 08-18 build. **Ask before treating fire sweeps, the
  need-pause fix, or the menu feedback as verified.**
- Still untested from before: **the sow fixes** (`cc502c9`). Phase 1 of
  the playtest plan has still not been run.
- **What IS verified in game (2026-08-17):** verbose logging works, and
  a `HaulMerge` sweep runs end to end. Still the only confirmed working
  sweep from a real save.

## Open item 1: vehicle packing offers no `* pack until done`

Reported 2026-08-19. **Fully diagnosed, nothing implemented, both fixes
awaiting a go-ahead.** Full detail in architecture doc section 0; the
short version:

1. **Vehicle Framework eats the whole right-click when 2+ pawns are
   selected.** It prefixes `Selector.HandleMapClicks`
   (`Extra.MultiSelectFloatMenu`); on a multi-select right-click over a
   cell holding a `VehiclePawn` it opens its own one-entry
   ("Board \<vehicle\>") `FloatMenuMulti` and returns `false`, so vanilla
   `HandleMapClicks` never runs and `ChoicesAtForMultiSelect` is never
   called. **Our postfix never fires - so *every* `*` option vanishes on
   a group click on a vehicle, not just pack.** Single-select is not
   intercepted and should already work.
2. **Even with the menu fixed, the sweep would do one item with one
   pawn.** `PackVehicle.PotentialWorkThingsGlobal` returns the
   *vehicles* awaiting loading, not the items, so the pool holds one
   entry; `BeginAreaSweep` drops every pawn after the first, and one
   `LoadVehicle` job later `AssignNextTask` finds an empty pool and ends
   the sweep. General gap - the pool assumes one job per target, which
   is wrong for `WorkGiver_Refuel` / `HaulToContainer` /
   `FillFermentingBarrel` in vanilla too.

**Do this first, before writing any code: the single-pawn test.** Select
**one** colonist, right-click a vehicle that is being packed. The option
should appear. If it does, cause 1 is confirmed and the diagnosis is
complete. **If it does not appear for a single pawn either, cause 1 is
not the whole story - keep digging.**

Already established, don't re-derive: `PackVehicle` passes
`IsSweepEligible` cleanly (workType Hauling, `directOrderable` defaults
true, worker is a `WorkGiver_Scanner`); its `JobOnThing` targets the
`VehiclePawn` itself; the vehicle *is* in `cell.GetThingList` despite
the multi-cell footprint; there is no `HasJobOnThing` override so our
probe is faithful; and `PotentialWorkThingRequest` is a plain property
that cannot throw.

## Open item 2: colonists standing still

Reported from play, 2026-08-18: **"workers are back to standing still
when hunt is not assigned."** Not diagnosed - no repro, no log pulled,
no code touched. **"Back to"** is the important word: a returning
symptom, not a first sighting.

Check in this order:

1. **Disable Sense of Urgency and retest.** `ZombiePhil.Urgency`
   (Workshop 3001253573) is built against 1.6 and throws on
   `Toils_General.WaitWith` in 1.5, and hunting is the specific thing it
   breaks - already documented below as hunters looping "started 10 jobs
   in 10 ticks" forever. A WorkGiver throwing inside
   `TryFindAndStartJob` can leave a pawn with no job at all, which looks
   exactly like standing still.
2. **Disable Do Not Be Lazy entirely and retest.** That single test
   separates our bug from the modlist's, and nothing else does.
3. **TKS Priority Treatment** patches `Pawn_JobTracker.TryFindAndStartJob`
   directly - the same method this whole symptom class runs through.
4. **Our one plausible contribution:** `JobTrackerPatch.Postfix` runs on
   every `EndCurrentJob` for every pawn. It early-returns unless the
   pawn is in an active sweep, so the blast radius is small - but the
   `scanner.JobOnThing(pawn, billGiver, true)` bill-continuation call is
   **not** wrapped in try/catch, so a throwing modded WorkGiver would
   propagate out of `EndCurrentJob` and could leave that pawn jobless.
   Only affects pawns already in a sweep.

**Do not build the section 5.4 idle-pawn nudge as the fix.** It is the
obvious-looking countermeasure and it would mask the cause: nudging a
pawn whose think tree is throwing just re-throws every two seconds.

## Review findings from 2026-08-18 evening - open, unfixed

All six are in the overnight session's own new code. Full detail in
architecture doc section 0.

1. **Menu feedback misses pawn-side refusals** -
   `FloatMenuPatch.cs:204`. `EligiblePawns` empty -> `continue` before
   any feedback is built, so "Hauling is priority 0", "pawn is drafted",
   "no Manipulation" still produce silent nothing. Plausibly the real
   answer to the stone-blocks report, alongside `NoEmptyPlaceLower`.
2. **A burning cell can produce a completely empty menu** -
   `FloatMenuPatch.cs:198`. Fire suppresses every other def and
   `* Consume`, and `FireCompat.HasFireJob` never sets `JobFailReason`,
   so a refusal there leaves zero entries and zero explanation.
3. **Duplicate fire entries under Sense of Urgency** -
   `IsFirefighting` keys on `workType.defName`, so that mod's parallel
   urgent def matches too. **Same class as the vehicle duplicate:**
   `PackVehicleTurret` also carries the label "pack vehicle" and also
   targets the `VehiclePawn`, so a vehicle needing cargo *and* turret
   ammo would show `* Pack vehicle until done` twice. Fix both here.
4. **Paused sweeps now survive interrupts that used to end them** -
   `SweepManager.cs:365`. A manual player order during a pause no longer
   ends the sweep; the pawn is pulled back when it finishes. Looks
   intentional, but it's wider than the reported bug.
5. **`MaxPauseTicks` only evaluated on a job end** -
   `SweepManager.cs:381`. Also the log line prints the constant, not the
   elapsed ticks, so it can claim "after 30000 ticks" when it was far
   longer. One-line fix.
6. **Minor fire-rescan effects** - a rescan can re-admit a fire another
   pawn is walking to; `BeginAreaSweep` (`SweepManager.cs:344`) breaks
   out of the pawn loop on an empty pool, so extra pawns never join a
   rescannable sweep. **That same `break` is half of vehicle-packing
   cause 2** - fixing one should fix the other.

1 and 2 are the two worth fixing before the playtest - both sit inside
the change that was specifically about not leaving the player guessing.

## Verified in earlier sessions (don't re-derive)

- **`FireCompat.HasFireJob` is a faithful port** of
  `WorkGiver_FightFires.HasJobOnThing` minus the intended home-area
  gate. Diffed against the decompile by hand. In particular: vanilla's
  faction test is *part of* the home-area gate
  (`(sameFaction || hostFaction) && !Home && manhattan > 15`), **not** a
  separate "don't help hostiles" rule - vanilla does let colonists beat
  fires on enemies. Dropping it with the home gate is correct. It reads
  like an omission on a cold read; don't "fix" it.
- `HandledDistSquared 25` == `InHorDistOf(..., 5f)`; `225` matches;
  `JobOnThing` is a bare `new Job(BeatFire, t)`, so bypassing
  `HasJobOnThing` is a complete override.
- `JobDriver_BeatFire.TryMakePreToilReservations` returns true without
  reserving - the reservation happens opportunistically in the approach
  toil. So `FireIsBeingHandled` does have reservations to read (fan-out
  works), and vanilla tolerates two pawns per fire where we don't.
- `FightFires` sets no `scanThings` in XML, so it takes the default
  `true` - the `scanThings` branch does run for it.
- **Radius behaviour, answering "is it really finding work of that type
  within 16 tiles":** yes, structurally. Both scan branches enforce the
  radius from the clicked cell, and the same `WorkGiverDef` builds the
  pool and issues every job. Two caveats: the pool is a **snapshot**
  taken at click time (only fire sweeps rescan, so work appearing later
  inside the radius is never picked up), and the entire scan runs
  against `eligiblePawns[0]` as driver - allowed-area, `CanReach` and
  `CanReserve` are per-pawn, so a restricted or walled-off driver
  shrinks the pool for the whole group. The comment at
  `SweepManager.cs:313-316` claims those filters don't vary by pawn;
  it's wrong, fix it when next in there.
- `showSweepOverlay` still does nothing - confirmed by grep, and
  already recorded in architecture doc 3.4. Relevant to the radius
  question: the player has no way to see what 16 tiles covers, and the
  checkbox implies they should.

## Test plan

A 26-test playtest plan is published here:

```
https://claude.ai/code/artifact/e60cfd11-1f82-46a8-9111-a25d9352a2dd
```

Phase 0 (prove the DLL is current, the logger is live, and the
`wantedPlantDef` reflection resolved) passes as of 2026-08-17. **Phase 1
- the four sow tests - has still not been run.** The plan predates the
fire-sweep and menu-feedback work, so it has no tests for either, and
nothing for vehicles.

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
the trace - the vehicle report is the second one in a row that a single
verbose line per offered option would have answered in minutes.

Partly mitigated by the disabled feedback entries, which surface refusal
reasons in the menu itself - but only for target-side refusals (see
finding 1 above).

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
`<pawn> paused from sweep: ...` /
`<pawn>: needs satisfied, resuming sweep (<def>)` /
`<pawn>: still under threshold after N ticks paused, ending sweep.`

The last two are the ones to grep when checking the pause/resume fix -
the old bug showed as a pause line after *every* task with no resume
line between them. For "did the radius scan find anything", the `scan
... : N targets` and `BeginSweep ... : N targets, M pawns` pair is the
direct evidence.

## Modlist

**~60 mods**, RimWorld 1.5.4409, no DLC. Authoritative list comes from
the `Loading game from file ... with mods:` block in any log.

On our code paths:

- **Performance Fish** patches `WorkGiverDef::get_Worker()` (which
  `EligibleDefs()` calls on every def), `ListerThings.ThingsMatching`,
  `WorkGiver_Haul.PotentialWorkThingsGlobal`.
- **TKS Priority Treatment** patches `Pawn_JobTracker.TryFindAndStartJob`.
- **Sense of Urgency** adds parallel "urgent" WorkGiverDefs - a likely
  source of duplicate `*` options, and worth disabling when testing.
- **Reclaim, Reuse, Recycle** adds the harvesting/refurbishment tables
  (both `WorkGiver_DoBill`, so both are sweep-eligible).
- **Vehicle Framework** (Workshop `3014915404`) prefixes
  `Selector.HandleMapClicks` and **suppresses vanilla's entire
  multi-select float menu over a vehicle** - see open item 1. It also
  adds nine WorkGiverDefs, of which the Hauling ones (`PackVehicle`,
  `PackVehicleTurret`, `RefuelVehicle`, `LoadUpgradeMaterials`) all
  target the `VehiclePawn` itself and all want repeat-on-the-same-target
  semantics we don't have. **Vanilla Vehicles Expanded** (`3014906877`)
  sits on top of it and adds a garage bench (`WorkGiver_DoBill`, so
  sweep-eligible) plus `VVE_RestoreWreck`.

## Two broken third-party mods - ignore their log noise

Both compiled against RimWorld 1.6, calling a 7-parameter overload where
1.5 has 6. Neither is our bug.

- **Sense of Urgency** (`ZombiePhil.Urgency`, Workshop 3001253573) -
  `Toils_General.WaitWith`. **Hunting is completely broken**: hunters
  loop "started 10 jobs in 10 ticks" forever. Recommend disabling. Prime
  suspect for the standing-still report above.
- **Automatic Hunting** (`Arylice.Rimworld.AutomaticHunting`) -
  `TraverseParms.For`, throws every tick in `GameComponentTick`.

## Still-open bugs in our code (not yet fixed)

- **The shared pool assumes one job per target.** Correct for
  haul/mine/cut/sow, wrong for every "keep bringing things to this one
  thing" WorkGiver - vehicle packing, and `WorkGiver_Refuel` /
  `HaulToContainer` / `FillFermentingBarrel` in vanilla. The
  `WorkstationTarget` path already does the right thing but is gated on
  `scanner is WorkGiver_DoBill` and is single-pawn by design.
- `ScanCells` has no `IsForbidden` check (`ScanThings` has one).
  Harmless for sow only because `GrowerSow.JobOnCell` checks it
  downstream - latent for any future cell-based WorkGiver.
- The `CanReserve` pre-filter in `TaskScanner` omits
  `ignoreOtherReservations` while `JobOnCell` passes `forced` through,
  so the pre-filter is stricter than the job it gates. Most likely
  reason for a pool smaller than the visible work. Also applies to
  fires: vanilla only reservation-checks a fire past 15 tiles, we check
  every one.
- `IsPreparatoryJob` is not scoped to cell targets, so construction
  frames also get one extra re-queue. Plausibly an improvement, capped,
  but untested beyond sow.
- The pool is scanned against one driver pawn; per-pawn filters
  (allowed area, reachability, reservation) therefore apply the driver's
  answer to the whole group. See the radius note above.
- Perf watch: `CanReachTarget` now runs per cell in the radial scan
  (~800 at default radius 16, ~7,800 at the max of 50).

## Still unanswered from earlier sessions

1. **"Cannot force-haul stone blocks."** Two candidate answers now: a
   stockpile in reach that doesn't accept Blocks (`NoEmptyPlaceLower`,
   which the new disabled entries will surface), or Hauling sitting at
   priority 0 for the selected pawns (which they will **not** surface -
   review finding 1).
2. **"The `* forced delivery to (ITEM)` is gone."** Needs a repro, and
   re-checking with Sense of Urgency disabled.

## Planned but NOT implemented - do not build without a fresh go-ahead

- **Vehicle fix 1** - Harmony prefix on the `Vehicles.FloatMenuMulti`
  constructor `(List<FloatMenuOption>, List<Pawn>, Pawn, string,
  Vector3)`, injecting our options into VF's group menu. The prefix runs
  before the base `Verse.FloatMenu` constructor caches option sizes, so
  mutating the list in place is safe. Patched by reflection
  (`AccessTools.TypeByName`) so VF stays a soft dependency. Would be the
  first patch in this mod aimed at another mod's type.
- **Vehicle fix 2** - generalise `WorkstationTarget` to "persistent
  target" (identified via a new `VehicleCompat` matching
  `Vehicles.WorkGiver_CarryToVehicle` subclasses by reflected type), and
  allow multiple pawns on one such target. VF reserves per *item* inside
  `FindThingToPack`, so parallel haulers are safe.
- `DoNotBeLazy_Architecture.md` section 5.4 - idle-pawn nudge
  ("standing" rule). See open item 2 for why this is not the fix for the
  standing-still report.
- The sweep radius overlay - `showSweepOverlay` exists as a setting with
  no code behind it.
- `DoNotFreakOut_Architecture.md` - a separate, Harmony-free mod. Not
  started, no folder.
- Drafted pawns in fire sweeps - vanilla allows drafted firefighting
  (`canBeDoneWhileDrafted`, `autoTakeablePriorityDrafted: 20`) and we
  don't. Needs a per-WorkGiver exception in both `PawnValidator.CanSweep`
  and `SweepManager.MapComponentTick`. **For now: undraft before
  ordering a fire sweep.** Ask before building it.

## Method notes (these work well, keep using them)

- **Reflect on the real game DLL** (`lib/Assembly-CSharp.dll`) via
  PowerShell + `[System.Reflection.Assembly]::LoadFrom`. `GetType()`
  returns null for a wrong namespace rather than throwing - and
  `GetTypes()` throws `ReflectionTypeLoadException` on this assembly, so
  catch it and read the Types list. Caught this way:
  `IngestibleProperties` / `ThingDefGenerator_Corpses` are in `RimWorld`
  not `Verse`; `ReservationManager` is in `Verse.AI` not `RimWorld`.
- **Reflecting on a *mod* DLL needs two extra tricks**, learned on
  `Vehicles.dll`:
  - Preload every referenced assembly first
    (`$asm.GetReferencedAssemblies()` lists them) from
    `RimWorldWin64_Data\Managed\` and the mod's own `Assemblies\`
    folder. An `AssemblyResolve` handler that calls `LoadFrom` inside
    itself recurses into a **StackOverflowException** that kills the
    PowerShell process outright - preload instead, or have the handler
    return only already-loaded assemblies.
  - PowerShell wraps the failure in a `MethodInvocationException`, so
    `$_.Exception.Types` is **null** - walk `.InnerException` down to
    the real `ReflectionTypeLoadException` first. Types whose base class
    failed to load are simply absent from the list, which looks exactly
    like "that type doesn't exist" - check `LoaderExceptions` before
    concluding anything.
- **Mod source beats decompiling the mod.** Vehicle Framework is on
  GitHub with per-version branches - `release/1.5` matches the shipped
  1.5 DLL, while `develop` is 1.6 and has already refactored classes we
  care about (`WorkGiver_CarryToVehicle` became generic there). Use
  `https://api.github.com/repos/<owner>/<repo>/git/trees/<branch>?recursive=1`
  to find file paths, then `raw.githubusercontent.com` for the source.
  Same rule as the RimWorld decompile: **check the branch matches the
  DLL you are actually running.**
- **The github decompile is an OLDER BUILD than our DLL - always
  cross-check signatures by reflection before compiling against them.**
  The decompile has `pawn.story.WorkTagIsDisabled(...)`, but in 1.5 it's
  `pawn.WorkTagIsDisabled(...)` on `Pawn` itself. Same for
  `FirstRespectedReserver`, 3-arg here and 2-arg there. **Bodies and
  control flow are still trustworthy** - that's what confirmed
  `FireCompat`.
- `curl -s https://raw.githubusercontent.com/josh-m/RW-Decompile/master/RimWorld/<Class>.cs`
  returns the full verbatim source and is better than a summarizing
  fetch when you need to diff logic line by line. `Verse.AI` classes sit
  under a literal `Verse.AI/` folder - that's where `HaulAIUtility`
  lives, not `RimWorld/`.
- **Read vanilla's own solution before inventing one.** The menu
  feedback work was mostly reading `AddJobGiverWorkOrders` and copying
  its scoping rules; guessing would have produced a menu full of grey.
- **Grep the Workshop folder for label text** to identify which mod owns
  an unexpected menu entry - `<label>[^<]*harvest[^<]*</label>` across
  `E:\SteamLibrary\steamapps\workshop\content\294100` found the
  harvesting table in seconds. To map folder ids to mod names, read each
  `<id>/About/About.xml` and pull its `<name>` tag.
- **Grep Core Defs XML** at
  `E:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\` - note
  `WorkGiverDefs\` is a single `WorkGivers.xml`. This is where
  `directOrderable` / `canBeDoneWhileDrafted` on `FightFires` came from,
  and where the absent `scanThings` (hence default true) was confirmed.
- **Generalized lesson, now three times burned:** vanilla WorkGivers put
  real preconditions in `Potential*Global` / `ExtraRequirements`, not in
  `JobOn*`. This mod calls `JobOn*` directly and silently bypasses all
  of them. Firefighting is the third instance and the first where the
  bypass was what we *wanted* - but it still had to be done by hand, in
  `FireCompat`, rather than falling out for free.
- **New generalized lesson from the vehicle report: when an option is
  missing, check whether our patch even ran before theorising about the
  WorkGiver.** A day's worth of plausible WorkGiver explanations was
  available and every one of them was wrong - another mod had suppressed
  the method we postfix. `Selector.HandleMapClicks` and
  `FloatMenuMakerMap.TryMakeFloatMenu` both sit upstream of our two
  entry points, and either can be prefixed away by anything in the
  modlist.

## Workflow notes

- User wants `DoNotBeLazy_Architecture.md` updated after essentially
  every turn - keep doing that. Per CLAUDE.md, doc edit precedes code.
- User tests by manually copying the built DLL into their RimWorld
  `Mods/DoNotBeLazy/` folder and fully restarting the game. **Whether
  the 2026-08-18 build has been copied over is still unconfirmed - ask.**
- `gh` CLI is not installed on this machine.
- Commit message style: detailed body explaining *why*. **Only commit
  when explicitly asked.**
- The model plan in architecture doc section 5 assigns Opus to
  `FloatMenuPatch` / `SweepManager` work and Sonnet to the test
  checklist. Reactive playtest bug-fixing was never given a model
  assignment - don't claim it was.
