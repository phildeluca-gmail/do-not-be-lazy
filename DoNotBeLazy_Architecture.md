<!-- Updated: 2026-08-15 EDT - in-game testing underway, Consume added -->

# Do Not Be Lazy - RimWorld 1.5 Mod Architecture

## 0. Current Status (2026-08-15)

Phases 1-3 are implemented, an Opus compatibility/correctness pass has
been run over the Phase 3 files, and the project builds clean (`dotnet
build` in `DoNotBeLazy/Source/DoNotBeLazy`, 0 errors/0 warnings). The
mod is now being tested in an actual running game (not just statically
verified) - see below for what's come out of that so far.

**From in-game testing (2026-08-15):**
- Achtung is not part of this user's modlist - deprioritized as a
  compatibility target, see the section 4 bullet. User's actual modlist
  includes **Pick Up And Haul** (Workshop ID 1279012058) - relevant any
  time a Hauling-category oddity shows up, since it adds its own
  `HaulToInventory` WorkGiverDef (label "stuff things in inventory and
  haul") alongside vanilla `HaulGeneral`. Confirmed not a bug - it's a
  legitimate, useful hauling variant (grabs multiple items per trip) and
  is deliberately left un-excluded.
- **Fixed: `* Consume` never appeared for drugs (wake-up, smokeleaf,
  etc.), only unrelated Hauling options showed instead.** Root cause:
  `FoodUtility.WillEat`, used to gate `CanConsume`, is a food-appetite
  check (preferability/nutrition-based) and rejects pure drugs outright
  since they aren't food. `CanConsume` in `FloatMenuPatch` now branches:
  items with `ingestible.drugCategory != DrugCategory.None` skip
  `WillEat` entirely and use a much simpler check instead.
- **Teetotalers are now skipped when assigning `* Consume` for a drug**,
  per explicit user request. Uses `pawn.story.traits.HasTrait(TraitDefOf.DrugDesire,
  -1)` (confirmed against the actual trait XML: `DrugDesire` degree -1 =
  Teetotaler). Vanilla technically *allows* force-feeding a drug to a
  Teetotaler (there's a dedicated "forced to take drugs" mood thought for
  it), so this is a deliberate choice to skip rather than force, not a
  vanilla limitation being worked around.
- Reported "pawns not all going to a large stack, and the consume
  context menu seems disabled" turned out to be two separate things
  once clarified:
  - The "consume" menu (eating food, smoking, snorting drugs) was never
    covered by this mod at all - it's not WorkGiver-based, so it was
    architecturally out of reach of the whole sweep system. **New
    feature added**: `* <verb> <item>` (e.g. `* Eat meal`, `* Smoke
    smokeleaf joint`) now appears when an ingestible thing is clicked,
    and orders every eligible selected pawn to take one dose/meal each,
    fired off immediately (not tracked as an ongoing sweep - there's
    nothing to chain, the job either succeeds or it doesn't). See
    `FloatMenuPatch.AddConsumeOption` in section 3.2.
  - No specific bug found in the sweep mechanism itself from this report.
- Reported "wood can't be collected, space existed for it" - confirmed
  **not a mod bug**: right-clicking the wood directly showed neither the
  vanilla `Haul` option nor our `* Haul`, and `FloatMenuPatch` only ever
  offers a sweep where the normal action would already be valid. If
  vanilla itself has no haul job here, `HasJobOnThing` is correctly
  returning false - almost always means no stockpile zone in reach is
  actually accepting Wood (filter, fullness, or forbidden status), not
  something this mod controls.
- Mood now has its own separate interrupt threshold (`moodThreshold`,
  default 10%, its own slider) rather than sharing `needThreshold` - see
  section 3.4. Ending the forced job (already the existing behavior) is
  sufficient to make a pawn address hunger/rest/joy on their own via
  vanilla's think tree - no explicit "go do X" job-issuing was needed or
  added. Mood has no single fix-it job in vanilla; releasing the pawn to
  their normal AI is the only lever available there too.

Files that exist and their state:
- Phase 1: `DoNotBeLazyMod.cs`, `Logger.cs`, `About.xml`, `.csproj` - done.
- Phase 2: `DoNotBeLazySettings.cs`, `PawnValidator.cs`, `TaskScanner.cs`,
  `NeedMonitor.cs`, `JobTrackerPatch.cs` - done. No separate
  `JobDriver_AreaSweep.cs` was needed or written - see section 3.2.
- Phase 3: `SweepManager.cs`, `FloatMenuPatch.cs` - done, reviewed, and
  patched for a critical bug (below). See 3.2/3.3 for how these ended up
  differing from the original plan.

Several implementation decisions were made this session that revise the
original plan in this document. They're captured inline in the relevant
sections rather than listed separately, so section 3 below reflects
current reality, not the original design. The main ones, for a quick
diff against memory:

- **No `FloatMenuMakerMap.GetOptions` method exists** in the actual game
  DLL for this version (1.5.9214.33606) - confirmed by reflecting on
  `lib/Assembly-CSharp.dll` directly rather than trusting recollection.
  The real integration points are `ChoicesAtFor` (1 pawn) and
  `ChoicesAtForMultiSelect` (2+ pawns). See 3.2/3.3.
- FloatMenuPatch does **not** clone existing FloatMenuOptions. It
  independently determines eligible WorkGiverDefs and only offers a sweep
  when a normal action would already apply (mirrors vanilla's own
  `HasJobOnThing` check rather than introspecting the menu vanilla built).
- **Single-pawn selections are supported.** Both `ChoicesAtFor` and
  `ChoicesAtForMultiSelect` are patched, as two nested `[HarmonyPatch]`
  classes inside `FloatMenuPatch` sharing one builder. No known gap left
  on selection size.
- `JobDriver_AreaSweep.cs` was dropped. Workstation bill continuation
  ("stop after one bill") is handled by `JobTrackerPatch` re-asking the
  `WorkGiverDef`'s scanner for another job on the same bill giver each
  time a `DoBill` job ends - no custom JobDriver needed.
- `SweepManager`'s job-to-job chaining is event-driven off
  `JobTrackerPatch`'s postfix (`Notify_JobEnded`), not tick-polled as
  originally described. `MapComponentTick` only does the periodic
  death/downed/mental/drafted/off-map cleanup check.
- **Critical bug found and fixed in the Opus pass:** `TryTakeOrderedJob`
  re-enters `EndCurrentJob` (`TryTakeOrderedJob` -> `StartJob` ->
  `EndCurrentJob`, confirmed via IL), so every job handout the mod made
  was firing `JobTrackerPatch`'s own postfix, which read it as the pawn's
  job ending and cancelled the sweep it had just started. Every sweep
  would have died after its first task. Fixed via `SweepManager.GiveJob`
  + a reentrancy flag (`AssigningJob`) that `JobTrackerPatch` checks and
  ignores. See section 3.2 SweepManager for detail.
- The same pass fixed several other real bugs (NRE risk in
  `TaskScanner.FindTargets` when `PotentialWorkThingsGlobal` returns null
  for most WorkGivers, a `Dictionary`-mutated-during-iteration crash in
  `NeedMonitor`, a nesting-unsafe static dictionary in `JobTrackerPatch`,
  missing per-pawn area-restriction re-checks, a silent no-op when the
  best workstation pawn couldn't actually get a job, and no exception
  guard around the float-menu postfix body) and added defensive/perf
  fixes (cached eligible-WorkGiverDef list, map derived from the first
  *spawned* pawn since caravan/world pawns have null `Map`). Full detail
  is folded into the relevant component descriptions in 3.2.
- **Achtung is not in this user's modlist** - not a compatibility target
  for actual testing/use. A static check was run against the real
  Achtung 1.5 DLL anyway (see section 4) and turned up nothing blocking;
  left as reference only in case the mod is shared more broadly later.
- **Cell-scanned WorkGivers now supported (fixed 2026-08-15).** Reported
  as a bug: right-clicking an empty crop-zone cell produced no `* Sow`.
  Root cause: `GrowerSow` is `scanCells=true, scanThings=false` - the
  target IS the empty cell, and the mod's whole pipeline (`FloatMenuPatch`,
  `TaskScanner`, `SweepManager`) was built entirely around `Thing`
  targets. Fixed generically (not special-cased to sowing), so this
  should also cover any other `scanCells`-only WorkGiver (e.g. clear
  snow), not just `GrowerSow`. `Growing` was also added to
  `SupportedWorkTypeDefNames`, since sowing wasn't in that list at all
  before. See `TaskScanner.ScanCells` and `FloatMenuPatch.FindTargetWithJob`
  in section 3.2.
- **`CookFillHopper` excluded (fixed 2026-08-15).** Reported as a bug:
  right-clicking food or drugs produced a confusing `* fill food hoppers`
  option. Root cause: `CookFillHopper` is `workType Hauling`, so it was
  swept up by the broad Hauling inclusion, and its `HasJobOnThing` legitimately
  returns true for food items that could refuel a nearby hopper - not a
  crash or a logic error, just noise nobody wants when trying to eat or
  take a drug. Added a small `ExcludedDefNames` denylist in
  `FloatMenuPatch` rather than narrowing the whole Hauling category;
  revisit if more of these turn up.
- Known open items, not yet closed: `showSweepOverlay` setting has no
  code behind it (section 3.4); `JobTrackerPatch`'s bill-continuation
  branch doesn't verify the ended job's target is the sweep's own bill
  giver (low risk, documented assumption); multiple WorkGiverDefs
  covering one activity (e.g. construction's deliver-resources vs
  finish-frame givers) can produce more than one `*` entry for the same
  click - menu-noise question, not a bug.

## 1. Intent

A RimWorld 1.5 mod that adds area-sweep task commands to the right-click context menu. When pawns are selected and the player right-clicks a target, asterisked (*) versions of valid actions appear at the bottom of the float menu. Choosing one causes all selected pawns with the required permissions and skills to perform that task type within a 16-tile radius of the click target, continuing until all matching tasks are complete. Tasks are interrupted when any need (hunger, recreation, sleep) drops to 5%.

## 2. Core Behaviors

**Menu entries:** For each valid float menu action on the clicked target, a duplicate entry prefixed with `*` appears below all normal entries. Only actions that support area-sweep logic are duplicated (hauling, construction, workstation bills, cleaning, mining, etc.).

**Pawn filtering:** When a `*` action is chosen, only pawns in the current selection who meet ALL of these criteria receive orders:
- Work type is enabled in their Work tab
- Skill level is sufficient (no "incapable" block)
- Not downed, in a mental break, or otherwise unavailable

**Task scanning (16-tile radius):** From the clicked cell, find all incomplete tasks of the same WorkGiver category within 16 tiles. Pawns are assigned to the nearest unassigned task first.

**Reservation model:** Vanilla reservations stay intact. Sweep pawns search for unreserved matching tasks within radius and claim them through the normal reservation system. No reservation override.

**Completion rules by type:**
- Construction: works as base game (multiple pawns can already work one frame). Sweep finds unfinished blueprints/frames within radius.
- Workstation: single pawn only. Selection priority: (1) highest relevant skill level, (2) ties broken by work speed stat (`StatDefOf.WorkSpeedGlobal` or the relevant specific stat like `SmithingSpeed`), (3) further ties broken by `MoveSpeed`. Works until all queued bills are fulfilled or materials are exhausted. Other selected pawns are NOT assigned to the same station.
- Hauling: each pawn claims a separate haulable within radius via normal reservation. Group fans out across available items.
- Mining: one pawn per cell. Sweep assigns each pawn to a separate unreserved minable cell within radius.
- Cleaning/other: one pawn per cell. Same as mining - fan out to distinct unreserved cells.

**Need interrupts:** A tick-level check monitors hunger, recreation, and sleep. When any drops to 5% or below, the pawn's forced job is cleared and they path to satisfy that need. They do NOT return to the sweep automatically afterward (this prevents infinite loops and lets the player re-issue if desired).

## 3. Technical Architecture

### 3.1 Project Structure

```
DoNotBeLazy/
  About/
    About.xml
    Preview.png
  Assemblies/
    DoNotBeLazy.dll
  Source/
    DoNotBeLazy/
      DoNotBeLazy.csproj
      Core/
        DoNotBeLazyMod.cs          # Mod entry, Harmony bootstrap
        DoNotBeLazySettings.cs     # ModSettings (radius, threshold)
      Patches/
        FloatMenuPatch.cs          # Postfix on GetOptions
      Components/
        SweepManager.cs            # MapComponent - tracks active sweeps
        NeedMonitor.cs             # GameComponent - tick-level need checks
      Jobs/
        JobDriver_AreaSweep.cs     # Custom JobDriver
        JobDef_Registration.cs     # Programmatic JobDef
      Utility/
        TaskScanner.cs             # Radius search, task matching
        PawnValidator.cs           # Permission/capability checks
```

### 3.2 Component Descriptions

**FloatMenuPatch.cs** - Two Harmony postfixes, on `FloatMenuMakerMap.ChoicesAtFor(Vector3 clickPos, Pawn pawn, bool suppressAutoTakeableGoto)` and `FloatMenuMakerMap.ChoicesAtForMultiSelect(Vector3 clickPos, List<Pawn> pawns)`. There is no `GetOptions` method on this class in the real 1.5 DLL (verified by reflecting on `lib/Assembly-CSharp.dll`); those two are the real entry points, plus internal helpers `AddHumanlikeOrders`/`AddJobGiverWorkOrders` that build the base list. Both are patched (as nested classes `FloatMenuPatch.SingleSelect` / `FloatMenuPatch.MultiSelect`, both discovered by `PatchAll` since it enumerates nested types), so sweeps work with any selection size.

The postfix body is wrapped in a try/catch that logs and swallows: an exception escaping a float-menu postfix takes the whole right-click menu down for every other mod patching the same area (Achtung). The sweep-eligible `WorkGiverDef` list is computed once and cached rather than re-walking `DefDatabase` on every right-click.

Rather than cloning existing `FloatMenuOption` entries (they don't expose the `WorkGiverDef` that produced them, so there's nothing to key a sweep off), the postfix independently walks `DefDatabase<WorkGiverDef>.AllDefsListForReading`, filters to sweep-eligible defs (see below), converts `clickPos` to a cell, and for each eligible def checks whether any selected pawn has `HasJobOnThing` true against a thing at that cell - the same predicate vanilla itself uses to decide whether to show the normal option. If so, it appends one `* <label>` `FloatMenuOption` whose action calls `SweepManager.BeginSweep(eligiblePawns, target, workGiverDef)`.

Sweep-eligible WorkGiverDefs: any whose `Worker is WorkGiver_DoBill` (covers all workstation/bill types without hardcoding each one), plus any whose `workType.defName` is `Hauling`, `Construction`, `Cleaning`, `Mining`, or `Growing` - minus a small `ExcludedDefNames` denylist (currently just `CookFillHopper`, see the status-section bullet above) for specific defs that technically match but produce confusing options nobody wants. Target detection (`FindTargetWithJob`) branches on `def.scanCells` vs `def.scanThings`: cell-scanned defs (`GrowerSow`) check `HasJobOnCell` against the clicked cell directly, so they work even when nothing is on that cell - which is the normal case for an empty tile waiting to be sown.

**Consume (added 2026-08-15):** Separate from all of the above - eating and drug use aren't `WorkGiverDef`-based in RimWorld at all (`JobDefOf.Ingest` instead), so `AddConsumeOption` in the same file handles it independently of `eligibleDefs`/`SweepManager` entirely. If any `Thing` at the clicked cell has `def.ingestible != null && def.ingestible.showIngestFloatOption` (the same flag vanilla itself uses to decide whether to offer an eat/smoke/snort option), and at least one selected pawn is alive/not downed/not in a mental state and `FoodUtility.WillEat` says yes, a `* <ingestCommandString>` option appears (e.g. `* Eat meal`, `* Smoke smokeleaf joint` - `ingestCommandString` is the same per-ThingDef format vanilla uses, so wording matches). Choosing it fires one `JobDefOf.Ingest` job per eligible pawn immediately, sized via the vanilla `FoodUtility.WillIngestStackCountOf` helper (same one the base game's single-pawn "Eat X" order uses) - not tracked as a sweep, since there's nothing to interrupt or chain: it's a one-shot order per pawn, same as manually right-clicking for each of them individually.

Deliberately does **not** exclude drafted pawns (unlike the WorkGiver-based sweeps) - you can manually order a drafted pawn to eat or take a combat drug in vanilla, and dosing a raiding party before a fight is a real use case. Also does not gate on hunger level - a manual order works regardless of current need, matching vanilla's manual-order semantics. If the stack doesn't have enough for everyone, later pawns in the loop may fail to get their dose once the stack runs empty from under them - not handled specially, since vanilla's own job system already has to tolerate pawns racing for the same food and fails harmlessly rather than crashing.

`CanConsume` branches on `thing.def.ingestible.drugCategory` (fixed 2026-08-15): non-drug food goes through `FoodUtility.WillEat` as before, but anything with a real drug category skips `WillEat` entirely (it's a food-appetite check and rejected every drug outright, which was the original bug - no `* Consume` was ever appearing for drugs) and instead only excludes Teetotalers (`pawn.story.traits.HasTrait(TraitDefOf.DrugDesire, -1)`), per explicit user request.

**SweepManager.cs** - A `MapComponent` maintaining `Dictionary<Pawn, SweepOrder>`. `SweepOrder` holds a `WorkGiverDef` and a `SharedPool` (`List<LocalTargetInfo>`) - for area sweeps, every pawn assigned in the same `BeginSweep` call shares the same pool instance, so claiming a target for one pawn removes it for the rest of the group (implements "nearest unassigned task first" via a linear nearest-in-pool scan per assignment). Workstation orders carry an empty pool since bill continuation doesn't use it (see JobTrackerPatch below). `LocalTargetInfo` transparently covers both Thing and cell targets, so the pool/nearest-scan logic didn't need to change to support `GrowerSow` - only the two spots that branch on target type explicitly did: `AssignNextTask` calls `scanner.JobOnCell` instead of `JobOnThing` when `!target.HasThing`, and `TargetStillValid` checks cell bounds/area/reservation instead of Thing-specific checks (Destroyed, forbidden) for the same case.

Job-to-job chaining is **event-driven**, not tick-polled: `JobTrackerPatch`'s postfix on `Pawn_JobTracker.EndCurrentJob` calls `SweepManager.Notify_JobEnded(pawn, condition)` when a swept pawn's job ends, and that pulls the next target off the shared pool.

Because `TryTakeOrderedJob` interrupts the pawn's current job, it re-enters `EndCurrentJob` (verified in IL: `TryTakeOrderedJob` -> `StartJob` -> `EndCurrentJob`), so every job handout the mod makes fires `JobTrackerPatch`'s own postfix with `InterruptForced` - which used to cancel the sweep that was mid-handout. All handouts now go through `SweepManager.GiveJob`, which raises a static `SweepManager.AssigningJob` flag that `JobTrackerPatch` checks and ignores. `MapComponentTick()` runs every 60 ticks and only checks for state changes nothing else observes - dead/downed/mental-break/drafted/off-map pawns get pulled from `activeSweeps`.

Workstation pawn selection (`PickBestWorkstationPawn`) ranks by skill level in the WorkGiverDef's primary relevant skill, then `StatDefOf.WorkSpeedGlobal`, then `StatDefOf.MoveSpeed`. The doc's original idea of tiebreaking on the specific per-trade stat (`SmithingSpeed` etc.) was dropped for v1 - there's no generic way to resolve "the specific stat for this WorkTypeDef" from the def alone, so `WorkSpeedGlobal` stands in. Revisit if it causes visibly wrong pawn picks in testing.

No `ExposeData()` on `SweepManager` - sweeps are cleared on load, matching the "simpler, recommended for v1" option in section 4.

**NeedMonitor.cs** - A `GameComponent` that runs a tick check (every 60 ticks for performance) on all pawns with active sweeps. If hunger, recreation, or sleep is at or below 5% (`need.CurLevelPercentage <= 0.05f`), it clears the pawn's sweep from `SweepManager` and ends their current forced job so the AI takes over for need satisfaction.

**TaskScanner.cs** - Static utility. Given a cell, radius, map, `WorkGiverDef`, and a driving pawn, returns a `List<LocalTargetInfo>` of matching incomplete tasks, via two independent branches (a def can be either or both):

- `ScanThings` (for `scanThings` defs): `scanner.PotentialWorkThingsGlobal(forPawn)` filtered by squared-distance-from-center, forbidden, allowed area, `CanReserve`, and `HasJobOnThing`. `PotentialWorkThingsGlobal` returns **null** on `WorkGiver_Scanner` itself and most WorkGivers never override it (construction and `WorkGiver_DoBill` included), so this falls back to `map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest)`, guarding against an undefined `ThingRequest` (which `ThingsMatching` throws on) - mirrors what vanilla's `JobGiver_Work` does.
- `ScanCells` (for `scanCells` defs, added 2026-08-15 to fix the missing-Sow bug): `GenRadial.RadialCellsAround(center, radius, true)` - the `GenRadial` approach originally planned for everything, kept for just this branch since there's no thing-lister equivalent for "empty cells that could be sown." Filtered by bounds, allowed area, `CanReserve`, and `HasJobOnCell`.

Filters out tasks already claimed by another pawn via the normal reservation system (no sweep-specific claim tracking needed).

**PawnValidator.cs** - Static utility. Given a pawn and a `WorkGiverDef`, returns bool for whether the pawn can perform that work type. Checks: dead/downed/mental-state/drafted, work type enabled and active in the pawn's work settings, Manipulation capacity.

**JobDriver_AreaSweep.cs** - Not written; turned out to be unnecessary. Workstation "stop after one bill" is overridden without a custom JobDriver: `JobTrackerPatch`'s postfix on `EndCurrentJob` checks if the ending job was `JobDefOf.DoBill` on a sweep's bill giver, and if so directly asks the `WorkGiver_Scanner` for another job on that same giver before falling through to `SweepManager`. `SweepManager` otherwise issues the same vanilla `Job` the float menu would have created, chained sequentially via `Notify_JobEnded`.

### 3.3 Key Integration Points

| RimWorld Class | Method | Patch Type | Purpose |
|---|---|---|---|
| `FloatMenuMakerMap` | `ChoicesAtFor` | Postfix | Append `*` entries to menu (1 pawn selected) |
| `FloatMenuMakerMap` | `ChoicesAtForMultiSelect` | Postfix | Append `*` entries to menu (2+ pawns selected) |
| `Pawn_JobTracker` | `EndCurrentJob` | Postfix | Notify SweepManager to queue next task |
| `Need` | `CurLevelPercentage` | (read only) | Polled by NeedMonitor, no patch needed |

### 3.4 Settings (ModSettings)

- `sweepRadius` - int, default 16, configurable 1-50
- `needThreshold` - float, default 0.05 (5%), configurable 0.01-0.20 - covers hunger/recreation/rest
- `moodThreshold` - float, default 0.10 (10%), configurable 0.01-0.30 - separate slider, mood specifically (added 2026-08-15 per user request; mood dropping to 5% is already close to a mental break, so it gets its own, higher default)
- `showSweepOverlay` - bool, default true (highlight radius on hover) - **setting exists but nothing reads it yet**; no overlay-drawing code has been written. Not in the Phase 1-3 plan as a separate task, so it fell out of scope. Needs a task added (likely a `MapComponent.MapComponentOnGUI()` or `MapComponent.MapComponentUpdate()` override) before this setting does anything.

## 4. Edge Cases and Risks

- **Achtung! compatibility:** **Not part of this user's actual modlist** - deprioritized, not a blocker for testing or release to this save. Left in as a static check only, in case the mod is shared more broadly later. Checked against the actual installed Achtung 1.5 DLL (`Achtung.dll`, reflected directly rather than guessed) at `E:\SteamLibrary\steamapps\workshop\content\294100\730936602\1.5\Assemblies\`. Findings:
  - `ChoicesAtForMultiSelect` (our 2+ pawn path): Achtung does not touch this method at all. No overlap.
  - `ChoicesAtFor` (our single-pawn path): Achtung postfixes it too (`FloatMenuMakerMap_ChoicesAtFor_Postfix`, appends its own options to the same `__result` list). Two postfixes stacking on one method is standard, low-risk Harmony usage - should compose fine regardless of load order since neither replaces the list, only appends to it.
  - `Pawn_JobTracker.EndCurrentJob` (our `JobTrackerPatch.cs`): Achtung applies a **Prefix, Postfix, AND a Transpiler** here. The transpiler is the one real unknown - it rewrites the method's IL, which is a deeper interaction than pre/postfix stacking. Our prefix (captures `curJob` before the body clears it) should still fire before whatever transpiled body runs, so it's likely fine, but this can't be fully confirmed by static analysis alone - **needs an actual in-game test with Achtung loaded** before calling this solid. Everything else here is verified; this is the one open item.
  - Also worth noting for later: Achtung patches `FloatMenuMakerMap.ScannerShouldSkip`, which our code doesn't call at all (we go straight to `HasJobOnThing`). If Achtung's patch suppresses certain WorkGivers under conditions vanilla wouldn't, our sweep option could still appear in a case Achtung intentionally hides the normal one. Cosmetic/UX risk, not a crash risk.
- **Reservation compliance:** Vanilla reservation system is respected, not overridden. `TaskScanner` filters out reserved targets via `map.reservationManager.CanReserve()`. For workstations, only the highest-skilled pawn in the selection is assigned; others are skipped for that task type.
- **Workstation bill depletion:** Bills can require materials. If materials run out mid-sweep, the pawn should gracefully exit the sweep rather than idle at the station.
- **Save/Load:** `SweepManager` should implement `ExposeData()` to persist active sweeps across saves, or clear them on load (simpler, recommended for v1).
- **Drafted pawns in selection:** If any selected pawns are drafted, exclude them from sweep assignment. Do not undraft them automatically.
- **Pawn death/downed/mental break mid-sweep:** SweepManager must detect these state changes on tick and remove the pawn from active sweeps. Check `pawn.Dead`, `pawn.Downed`, `pawn.InMentalState`.
- **Forbidden targets:** TaskScanner must check `thing.IsForbidden(pawn)` before including a target. Forbidden items/buildings are skipped.
- **Area restrictions:** Pawns with allowed-area restrictions may not be permitted to path to some tasks within the 16-tile radius. TaskScanner must check `pawn.Map.areaManager` and the pawn's allowed area before assigning.
- **Multiple sweep commands:** If the player issues a new sweep to a pawn already in a sweep, the new sweep replaces the old one. No stacking.
- **Roof collapse during mining sweeps:** Mining in radius can cause roof collapse. This is acceptable vanilla behavior. The mod does not need to predict structural integrity, but pawns killed or downed by collapse are removed from the sweep per the death/downed rule.
- **Performance:** `GenRadial.RadialCellsAround()` with radius 16 scans ~800 cells. TaskScanner should cache results per sweep initiation rather than rescanning every tick. Rescan only when a pawn completes a task and needs the next assignment.
- **Mod compatibility beyond Achtung:** WorkTab, Colony Manager, and similar mods change work priorities or auto-assign jobs. Since this mod uses forced jobs (not the work priority system), conflicts should be minimal. The forced job takes precedence over auto-assignment.
- **Multiple workstations in radius:** If the clicked target is a workstation, only that specific station is assigned (not other stations of the same type in radius). Workstation sweeps are single-target, not area-sweep.

## 5. Claude Code Execution Plan

### 5.1 Model Switching in Claude Code

Switch models per phase using `/model` mid-session or `--model` at launch:

```bash
# Launch with a specific model
claude --model haiku       # Phase 1 scaffolding
claude --model sonnet      # Phase 2-3 implementation
claude --model opus        # Complex patches, architecture review

# Or switch mid-session
/model haiku
/model sonnet
/model opus
```

Check current model anytime with `/status`. The conversation context carries over across model switches within a session, so switching mid-session is preferred over launching separate terminals when tasks are sequential.

Recommended workflow: start the session with `claude --model sonnet` (the workhorse), drop to `/model haiku` for scaffolding, and escalate to `/model opus` for the float menu patch and sweep manager.

### 5.2 Task Plan

#### Phase 1 - Scaffolding (`/model haiku`)

| Task | Notes |
|---|---|
| Create folder structure | mkdir commands only |
| Generate `About.xml` | Boilerplate XML with mod metadata |
| Generate `.csproj` | Reference paths for game DLLs and Harmony (see Section 6) |
| `DoNotBeLazyMod.cs` (Harmony bootstrap) | Standard `[StaticConstructorOnStartup]` pattern, ~15 lines |
| Debug logging utility | Static `Log` wrapper with a settings-gated verbose mode |

#### Phase 2 - Core Logic (`/model sonnet`)

| Task | Notes |
|---|---|
| `DoNotBeLazySettings.cs` | `ModSettings` with `ExposeData`, `Listing_Standard` UI. Sonnet because RimWorld's settings API has quirks Haiku may miss. |
| `PawnValidator.cs` | Work type checks, skill checks, state checks against RimWorld API |
| `TaskScanner.cs` | `GenRadial` usage, reservation checks, forbidden/area filtering, result caching |
| `NeedMonitor.cs` | 60-tick polling, need threshold check, job clearing |
| ~~`JobDriver_AreaSweep.cs`~~ | **Done differently** - not written, turned out unnecessary. Bill re-queue logic lives in `JobTrackerPatch` itself instead. |
| `Pawn_JobTracker.EndCurrentJob` postfix | Done - `JobTrackerPatch.cs` |

**Done.** Note: this session ran on Sonnet throughout (not switched to Haiku/Opus per phase) - see status note at top of doc.

#### Phase 3 - Complex Integration (`/model opus`) - DONE (built on Sonnet, not Opus - see below)

| Task | Notes |
|---|---|
| `FloatMenuPatch.cs` | **Done, but not as originally planned.** No `GetOptions` method exists in this game version's DLL (confirmed via reflection on `lib/Assembly-CSharp.dll`) - patched `ChoicesAtFor` and `ChoicesAtForMultiSelect` instead (both selection sizes covered). Does not clone existing `FloatMenuOption`s (not feasible - they don't expose their originating `WorkGiverDef`); independently determines eligible actions instead. See section 3.2 for full detail. |
| `SweepManager.cs` | Done. Job chaining is event-driven via `Notify_JobEnded` (called from `JobTrackerPatch`), not tick-polled. `MapComponentTick` only runs the pawn-state validity check. Workstation best-pawn tiebreaker uses `WorkSpeedGlobal` instead of the per-trade stat named in the original plan. See section 3.2. |

This phase was implemented on Sonnet rather than Opus per the original
model plan (session was already running Sonnet when the work started;
not manually switched). Build is clean, but this is exactly the
highest-risk phase the plan called out for Opus - **an Opus compatibility
pass over `FloatMenuPatch.cs` and `SweepManager.cs` before relying on
this in a real save is still worth doing**, per Phase 4 below.

#### Phase 4 - Polish and QA (`/model sonnet` then `/model opus`)

| Task | Model | Notes |
|---|---|---|
| Integration test checklist | **Sonnet** | Written test scenarios for manual QA |
| Full compatibility review | **Opus** | Achtung patch ordering, defensive null checks, WorkTab interaction |
| Final code review | **Opus** | Review all files for API misuse, race conditions, null refs |

### 5.3 Model Summary

| Model | Task Count | Used For |
|---|---|---|
| **Haiku** | 5 | Scaffolding, XML, folder structure, bootstrap, logging |
| **Sonnet** | 7 | Settings, validators, scanners, job drivers, need monitor, tests |
| **Opus** | 4 | Float menu patch, sweep manager, compatibility audit, final review |

## 6. Dependencies and Development Setup

### 6.0 Before You Start (Manual Steps)

These must be done by you before launching Claude Code. None of this is auto-resolved.

**Step 1:** Create a `lib/` folder in your project root.

**Step 2:** Copy these four DLLs into `lib/`:

| DLL | Copy From |
|---|---|
| `Assembly-CSharp.dll` | `<RimWorld>/RimWorldWin64_Data/Managed/` |
| `UnityEngine.dll` | `<RimWorld>/RimWorldWin64_Data/Managed/` |
| `UnityEngine.CoreModule.dll` | `<RimWorld>/RimWorldWin64_Data/Managed/` |
| `0Harmony.dll` | `<Steam>/steamapps/workshop/content/294100/2009463077/v1.5/Assemblies/` |

Adjust paths for your OS (see 6.1 below). If you can't find the Harmony Workshop folder, subscribe to the Harmony mod on Steam, launch RimWorld once, then check the path.

**Step 3:** Verify your project root looks like this before launching Claude Code:

```
DoNotBeLazy/
  lib/
    Assembly-CSharp.dll
    UnityEngine.dll
    UnityEngine.CoreModule.dll
    0Harmony.dll
```

Claude Code will generate everything else. The `.csproj` it creates will reference these DLLs via relative paths to `lib/`.

### 6.1 Required Reference DLLs

These are referenced by the `.csproj` at compile time but NOT shipped with the mod. Players already have them via RimWorld and the Harmony Workshop mod.

| DLL | Source | Typical Path (Windows) |
|---|---|---|
| `Assembly-CSharp.dll` | RimWorld install | `<RimWorld>/RimWorldWin64_Data/Managed/` |
| `UnityEngine.dll` | RimWorld install | `<RimWorld>/RimWorldWin64_Data/Managed/` |
| `UnityEngine.CoreModule.dll` | RimWorld install | `<RimWorld>/RimWorldWin64_Data/Managed/` |
| `0Harmony.dll` | Harmony Workshop mod | See below |

Platform-specific managed folder paths:
- **Windows:** `<Steam>/steamapps/common/RimWorld/RimWorldWin64_Data/Managed/`
- **macOS:** `<Steam>/steamapps/common/RimWorld/RimWorldMac.app/Contents/Resources/Data/Managed/`
- **Linux:** `<Steam>/steamapps/common/RimWorld/RimWorld_Data/Managed/`

### 6.2 Harmony Setup

Harmony is loaded as a separate Steam Workshop mod (ID `2009463077`). For development, you need `0Harmony.dll` as a compile-time reference.

**Where to find it:**

```
<Steam>/steamapps/workshop/content/294100/2009463077/v1.5/Assemblies/0Harmony.dll
```

If the Workshop path is hard to locate, copy `0Harmony.dll` into a `lib/` folder in your project and reference it from there. The `.csproj` should reference it with `<Private>false</Private>` so it is not copied to the output (players get it from the Workshop mod).

**In `About.xml`**, declare Harmony as a dependency so RimWorld loads it first:

```xml
<modDependencies>
  <li>
    <packageId>brrainz.harmony</packageId>
    <displayName>Harmony</displayName>
    <steamWorkshopUrl>steam://url/CommunityFilePage/2009463077</steamWorkshopUrl>
  </li>
</modDependencies>
<loadAfter>
  <li>brrainz.harmony</li>
</loadAfter>
```

### 6.3 .csproj Reference Pattern

```xml
<Reference Include="Assembly-CSharp">
  <HintPath>$(RIMWORLD_DIR)/RimWorldWin64_Data/Managed/Assembly-CSharp.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="UnityEngine">
  <HintPath>$(RIMWORLD_DIR)/RimWorldWin64_Data/Managed/UnityEngine.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="UnityEngine.CoreModule">
  <HintPath>$(RIMWORLD_DIR)/RimWorldWin64_Data/Managed/UnityEngine.CoreModule.dll</HintPath>
  <Private>false</Private>
</Reference>
<Reference Include="0Harmony">
  <HintPath>../../../lib/0Harmony.dll</HintPath>
  <Private>false</Private>
</Reference>
```

Set `RIMWORLD_DIR` as an environment variable or replace with your absolute path. `<Private>false</Private>` on every reference prevents the DLLs from being copied to the output folder.

### 6.4 Build Output

The compiled `DoNotBeLazy.dll` goes into `DoNotBeLazy/Assemblies/`. Configure the `.csproj` output path:

```xml
<OutputPath>../../Assemblies/</OutputPath>
```

### 6.5 Testing in Game

Copy or symlink the entire `DoNotBeLazy/` folder (containing `About/` and `Assemblies/`) into:

```
<RimWorld>/Mods/DoNotBeLazy/
```

Enable it in the mod list. Load order: Harmony first, then Core, then Do Not Be Lazy.
