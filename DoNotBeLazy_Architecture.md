<!-- Updated: 2026-08-13 20:14 EDT -->

# Do Not Be Lazy - RimWorld 1.5 Mod Architecture

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

**FloatMenuPatch.cs** - Harmony postfix on `FloatMenuMakerMap.GetOptions`. In RimWorld 1.5, this method receives `List<Pawn> selectedPawns` and returns `List<FloatMenuOption>`. The postfix iterates the existing options, creates asterisked clones for each valid one, and appends them to the list. Each clone's action delegate calls into `SweepManager.BeginSweep()`.

**SweepManager.cs** - A `MapComponent` that maintains a dictionary of active sweep assignments (`Dictionary<Pawn, SweepOrder>`). `SweepOrder` stores: originating cell, radius, WorkGiverDef, and remaining task queue. On `MapComponentTick()`, it checks whether each pawn has finished their current task and assigns the next one in the queue via `pawn.jobs.StartJob()`.

**NeedMonitor.cs** - A `GameComponent` that runs a tick check (every 60 ticks for performance) on all pawns with active sweeps. If hunger, recreation, or sleep is at or below 5% (`need.CurLevelPercentage <= 0.05f`), it clears the pawn's sweep from `SweepManager` and ends their current forced job so the AI takes over for need satisfaction.

**TaskScanner.cs** - Static utility. Given a cell, radius, map, and `WorkGiverDef`, returns a `List<LocalTargetInfo>` of all matching incomplete tasks. Uses `GenRadial.RadialCellsAround()` for the 16-tile search. Filters out tasks already claimed by another pawn (reserved or in another sweep).

**PawnValidator.cs** - Static utility. Given a pawn and a `WorkGiverDef`, returns bool for whether the pawn can perform that work type. Checks: work type enabled, not incapable, not downed, not in mental state, skill minimums met.

**JobDriver_AreaSweep.cs** - Thin wrapper. For most task types, the mod does not need a custom JobDriver. Instead, `SweepManager` issues the same vanilla `Job` the float menu would have created, but chains them sequentially. The JobDriver is only needed for workstation tasks where we override the "stop after one bill" behavior by requeuing.

### 3.3 Key Integration Points

| RimWorld Class | Method | Patch Type | Purpose |
|---|---|---|---|
| `FloatMenuMakerMap` | `GetOptions` | Postfix | Append `*` entries to menu |
| `Pawn_JobTracker` | `EndCurrentJob` | Postfix | Notify SweepManager to queue next task |
| `Need` | `CurLevelPercentage` | (read only) | Polled by NeedMonitor, no patch needed |

### 3.4 Settings (ModSettings)

- `sweepRadius` - int, default 16, configurable 1-50
- `needThreshold` - float, default 0.05 (5%), configurable 0.01-0.20
- `showSweepOverlay` - bool, default true (highlight radius on hover)

## 4. Edge Cases and Risks

- **Achtung! compatibility:** Achtung also patches `AddJobGiverWorkOrders` and `GetOptions`. Use postfix-only patching with null checks. Do not use transpilers.
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
| `JobDriver_AreaSweep.cs` | Workstation bill re-queue logic, toil chaining |
| `Pawn_JobTracker.EndCurrentJob` postfix | Notify SweepManager on job completion |

#### Phase 3 - Complex Integration (`/model opus`)

| Task | Notes |
|---|---|
| `FloatMenuPatch.cs` | Postfix on `GetOptions`. Must clone `FloatMenuOption` actions correctly, handle 1.5 multi-pawn `selectedPawns`, append entries in order, wire delegates. Highest compatibility risk. |
| `SweepManager.cs` | Stateful `MapComponent`. Tick-level job chaining, pawn state monitoring (death/downed/mental), sweep replacement, workstation best-pawn selection with tiebreakers. |

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
