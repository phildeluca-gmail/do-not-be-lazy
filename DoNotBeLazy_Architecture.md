<!-- Updated: 2026-08-15 EDT - Phase 3 complete, build green -->

# Do Not Be Lazy - RimWorld 1.5 Mod Architecture

## 0. Current Status (2026-08-15)

Phases 1-3 are implemented, an Opus compatibility/correctness pass has
been run over the Phase 3 files, and the project builds clean (`dotnet
build` in `DoNotBeLazy/Source/DoNotBeLazy`, 0 errors/0 warnings). Not yet
tested in a running game - see Phase 4 in section 5.2.

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
- **Static Achtung compatibility check run against the actual installed
  Achtung 1.5 DLL** (not guessed) - see the Achtung bullet in section 4.
  One item remains open: Achtung's transpiler on `EndCurrentJob` hasn't
  been tested in a running game together with our patch on the same
  method.
- Known open items, not yet closed: `showSweepOverlay` setting has no
  code behind it (section 3.4); `JobTrackerPatch`'s bill-continuation
  branch doesn't verify the ended job's target is the sweep's own bill
  giver (low risk, documented assumption); WorkGivers that only
  `scanCells` (e.g. clear-snow) never produce a sweep option; multiple
  WorkGiverDefs covering one activity (e.g. construction's
  deliver-resources vs finish-frame givers) can produce more than one
  `*` entry for the same click - menu-noise question, not a bug.

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

Sweep-eligible WorkGiverDefs: any whose `Worker is WorkGiver_DoBill` (covers all workstation/bill types without hardcoding each one), plus any whose `workType.defName` is `Hauling`, `Construction`, `Cleaning`, or `Mining`.

**SweepManager.cs** - A `MapComponent` maintaining `Dictionary<Pawn, SweepOrder>`. `SweepOrder` holds a `WorkGiverDef` and a `SharedPool` (`List<LocalTargetInfo>`) - for area sweeps, every pawn assigned in the same `BeginSweep` call shares the same pool instance, so claiming a target for one pawn removes it for the rest of the group (implements "nearest unassigned task first" via a linear nearest-in-pool scan per assignment). Workstation orders carry an empty pool since bill continuation doesn't use it (see JobTrackerPatch below).

Job-to-job chaining is **event-driven**, not tick-polled: `JobTrackerPatch`'s postfix on `Pawn_JobTracker.EndCurrentJob` calls `SweepManager.Notify_JobEnded(pawn, condition)` when a swept pawn's job ends, and that pulls the next target off the shared pool.

Because `TryTakeOrderedJob` interrupts the pawn's current job, it re-enters `EndCurrentJob` (verified in IL: `TryTakeOrderedJob` -> `StartJob` -> `EndCurrentJob`), so every job handout the mod makes fires `JobTrackerPatch`'s own postfix with `InterruptForced` - which used to cancel the sweep that was mid-handout. All handouts now go through `SweepManager.GiveJob`, which raises a static `SweepManager.AssigningJob` flag that `JobTrackerPatch` checks and ignores. `MapComponentTick()` runs every 60 ticks and only checks for state changes nothing else observes - dead/downed/mental-break/drafted/off-map pawns get pulled from `activeSweeps`.

Workstation pawn selection (`PickBestWorkstationPawn`) ranks by skill level in the WorkGiverDef's primary relevant skill, then `StatDefOf.WorkSpeedGlobal`, then `StatDefOf.MoveSpeed`. The doc's original idea of tiebreaking on the specific per-trade stat (`SmithingSpeed` etc.) was dropped for v1 - there's no generic way to resolve "the specific stat for this WorkTypeDef" from the def alone, so `WorkSpeedGlobal` stands in. Revisit if it causes visibly wrong pawn picks in testing.

No `ExposeData()` on `SweepManager` - sweeps are cleared on load, matching the "simpler, recommended for v1" option in section 4.

**NeedMonitor.cs** - A `GameComponent` that runs a tick check (every 60 ticks for performance) on all pawns with active sweeps. If hunger, recreation, or sleep is at or below 5% (`need.CurLevelPercentage <= 0.05f`), it clears the pawn's sweep from `SweepManager` and ends their current forced job so the AI takes over for need satisfaction.

**TaskScanner.cs** - Static utility. Given a cell, radius, map, `WorkGiverDef`, and a driving pawn, returns a `List<LocalTargetInfo>` of matching incomplete tasks. Implemented as `scanner.PotentialWorkThingsGlobal(forPawn)` filtered by squared-distance-from-center, forbidden, allowed area, `CanReserve`, and `HasJobOnThing` - not `GenRadial.RadialCellsAround()` as originally planned, since the WorkGiver API is already thing-scoped and iterating things directly avoids an 800-cell scan.

`PotentialWorkThingsGlobal` returns **null** on `WorkGiver_Scanner` itself and most WorkGivers never override it (construction and `WorkGiver_DoBill` included), so the scanner falls back to `map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest)` for `scanThings` givers, guarding against an undefined `ThingRequest` (which `ThingsMatching` throws on). This mirrors what vanilla's `JobGiver_Work` does. Cell-scanned (`scanCells`) givers such as clear-snow find nothing and simply offer no sweep. Filters out tasks already claimed by another pawn via the normal reservation system (no sweep-specific claim tracking needed).

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
- `needThreshold` - float, default 0.05 (5%), configurable 0.01-0.20
- `showSweepOverlay` - bool, default true (highlight radius on hover) - **setting exists but nothing reads it yet**; no overlay-drawing code has been written. Not in the Phase 1-3 plan as a separate task, so it fell out of scope. Needs a task added (likely a `MapComponent.MapComponentOnGUI()` or `MapComponent.MapComponentUpdate()` override) before this setting does anything.

## 4. Edge Cases and Risks

- **Achtung! compatibility:** Checked against the actual installed Achtung 1.5 DLL (`Achtung.dll`, reflected directly rather than guessed) at `E:\SteamLibrary\steamapps\workshop\content\294100\730936602\1.5\Assemblies\`. Findings:
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
