<!-- Created: 2026-08-15 EDT - architecture only, no code written yet -->

# Do Not Freak Out - RimWorld 1.5 Mod Architecture

## 0. Current Status (2026-08-15)

**Architected only. No code exists for this mod yet.** This document is
planning per explicit request ("ARCHITECT these and do not do them
yet"). Do not scaffold, write source, or create the project folder from
this document without a separate go-ahead.

This is a new, standalone mod - not a feature of Do Not Be Lazy. The two
share a scan-mechanism *shape* (see section 3.2) but are fully
decoupled: no shared code, no project dependency in either direction,
each usable without the other installed. See `DoNotBeLazy_Architecture.md`
section 5.4 for the sibling feature (idle pawn nudge) that was
architected in the same pass and does belong to Do Not Be Lazy.

## 1. Intent

A RimWorld 1.5 mod that quietly watches the colony's critical needs and
proactively interrupts a pawn before they cross into a mental break,
rather than only reacting after the fact. Runs continuously in the
background with no player interaction required beyond initial settings.

## 2. Core Behaviors

**Scan cadence:** one colonist, chosen in strict alphabetical order by
name, is checked every 2 real-world seconds (120 ticks at normal 1x
speed - see section 3.2 for why ticks were chosen over wall-clock time).
Not all colonists at once, and not per-map - one pawn, globally, per
interval, cycling through an alphabetically-sorted list of every
player-owned spawned colonist across all maps, wrapping back to the
start once it reaches the end.

**No camera movement:** nothing in this mod ever selects a pawn or moves
the camera. The scan and any resulting action happen entirely offscreen
unless the player happens to already be looking at that pawn.

**Skip rule:** if the pawn currently has a player-forced job
(`Job.playerForced == true`), do nothing this cycle - leave them alone
entirely, don't even evaluate their needs. This is deliberately generic
(a vanilla-level flag, not anything specific to Do Not Be Lazy) so pawns
mid-sweep in Do Not Be Lazy, or under any other mod's forced order, are
left to whatever system is already managing them. If Do Not Be Lazy is
also installed, its own `NeedMonitor` already covers swept pawns (with
pause/resume - see that mod's architecture doc section 3.2); this mod's
job is the *rest* of the colony, not swept pawns.

**Need check (four stats):** if any of food, rest, joy (recreation), or
mood is at or below a configurable threshold (default 20%), the pawn's
current job is ended so their own AI addresses it - same "let vanilla
figure out what to do" philosophy as Do Not Be Lazy's `NeedMonitor` for
hunger/rest/joy, and same caveat for mood specifically (no single
vanilla job directly "fixes" mood; releasing the pawn to their normal AI
is the only lever available, same as documented in Do Not Be Lazy).
**The exact four stats are an assumption, not something the user
explicitly enumerated** - inferred from "the four important stats" by
matching Do Not Be Lazy's own already-established need set
(`needThreshold` covers food/rest/joy, `moodThreshold` covers mood
separately). Confirm before implementing; if wrong, everything below
still applies structurally, just swap the `Need` lookups.

## 3. Technical Architecture

### 3.1 Project Structure (proposed, not created)

```
DoNotFreakOut/
  About/
    About.xml
  Assemblies/
    DoNotFreakOut.dll
  Source/
    DoNotFreakOut/
      DoNotFreakOut.csproj
      Core/
        DoNotFreakOutMod.cs        # Mod entry point
        DoNotFreakOutSettings.cs   # ModSettings (per-stat thresholds, enable toggle)
        Logger.cs                  # Same pattern as Do Not Be Lazy's
      Components/
        NeedScanner.cs             # GameComponent - the whole mod, functionally
```

Deliberately thin. Unlike Do Not Be Lazy, **this mod needs no Harmony
patches at all** - nothing here intercepts a vanilla method; it's a
self-contained periodic check calling only public APIs
(`EndCurrentJob`, need lookups) from its own `GameComponent`. That means
no Harmony dependency, no `About.xml` `modDependencies`/`loadAfter`
entries, and no `lib/0Harmony.dll` reference needed - genuinely simpler
than Do Not Be Lazy, not just smaller.

### 3.2 Component Descriptions

**NeedScanner.cs** - `GameComponent`, ticks every 120 ticks (2 seconds
at 1x speed). Tick-based rather than wall-clock-based: matches how every
periodic check in Do Not Be Lazy already works (`NeedMonitor`,
`SweepManager.MapComponentTick`), and means the scan rate scales with
game speed the same way vanilla's own tick-scaled behaviors do - a
deliberate choice, flagged as revisitable if scanning noticeably faster
at 3x speed turns out to feel wrong.

Maintains a single rotating integer index (not per-map) over an
alphabetically-sorted list of `Find.Maps`-spanning free colonists,
rebuilt fresh each tick interval - rebuilding avoids stale-index bugs
as colonists die, join, or despawn between scans; the list is short
enough (normal colony sizes) that resorting every 2 seconds is not a
performance concern.

Per interval:
1. Advance the index (mod by current list length), pick that pawn.
2. If `pawn.jobs?.curJob?.playerForced == true`, stop - do nothing this
   cycle.
3. Check food/rest/joy/mood against their thresholds (see 3.4). If any
   is at or below threshold, call `pawn.jobs.EndCurrentJob(JobCondition.InterruptForced)`
   and let the pawn's own AI pick up eating/sleeping/recreating. No
   job is hand-assigned - same minimal-intervention approach as Do Not
   Be Lazy's `NeedMonitor`.

No `Notify_JobEnded`/patch machinery needed here at all - there's no
"resume a sweep" concept in this mod, so once the interrupt fires,
`NeedScanner`'s job for that pawn is done until their name comes back
around in rotation.

### 3.3 Key Integration Points

| RimWorld Class | Member | Type | Purpose |
|---|---|---|---|
| `Verse.AI.Job` | `playerForced` | Field (read only) | Skip check - confirmed present via reflection on the actual game DLL, no patch needed |
| `Pawn_JobTracker` | `EndCurrentJob` | Called directly (not patched) | Release a pawn to address a critical need |
| `Need` | `CurLevelPercentage` | (read only) | Polled per pawn, no patch needed |

No Harmony patches anywhere in this mod - see 3.1.

### 3.4 Settings (ModSettings, proposed)

- `enabled` - bool, default true - master on/off toggle
- `needThreshold` - float, default 0.20 (20%) - shared threshold for
  food/rest/joy, mirroring Do Not Be Lazy's naming
- `moodThreshold` - float, default 0.20 (20%) - separate slider for
  mood, mirroring Do Not Be Lazy's precedent of treating mood
  independently from the other three (see that mod's `DoNotBeLazySettings`)

Two thresholds rather than four (one shared for food/rest/joy, one for
mood) to match Do Not Be Lazy's existing UX rather than introduce a
denser settings screen than that mod has - revisit to fully independent
sliders per stat if it turns out players want that granularity.

## 4. Edge Cases and Risks

- **Interaction with Do Not Be Lazy, if both are installed:** no
  conflict expected. The `playerForced` skip rule means this mod never
  touches a pawn Do Not Be Lazy has actively swept (that mod's own
  `NeedMonitor` already handles pause/resume for those). Neither mod
  patches the other or shares state - if only one is installed, nothing
  changes about how the other behaves.
- **Drafted pawns:** current lean is to exclude them from the rotation
  entirely (same reasoning as Do Not Be Lazy's `PawnValidator` - a
  drafted pawn with a critical need is very likely a deliberate combat
  tradeoff the player is already aware of, not neglect to correct).
  Open question to confirm before implementing.
- **Mental-break-in-progress pawns:** should probably be skipped
  outright (nothing meaningful to interrupt into) rather than have needs
  checked - `pawn.InMentalState` as the guard, mirroring
  `PawnValidator.CanSweep`'s existing exclusion in Do Not Be Lazy.
- **Downed/dead pawns:** same - skip via `pawn.Dead || pawn.Downed`
  before even checking needs.
- **Performance:** rebuilding and sorting the colonist list every 120
  ticks is trivial at normal colony sizes (tens of pawns); not
  considered a risk worth optimizing away preemptively.
- **Multiplayer mod compatibility:** not investigated. Flagging as
  unexamined rather than silently assuming it's fine, consistent with
  how Do Not Be Lazy treats unverified compatibility claims.

## 5. Execution Plan (not started)

Would follow the same phase-and-model pattern as Do Not Be Lazy (see
that doc's section 5) once implementation is greenlit:

| Phase | Rough scope | Model |
|---|---|---|
| 1 - Scaffolding | Folder structure, `About.xml`, `.csproj` (no Harmony reference needed), mod entry point, logger | Haiku |
| 2 - Core logic | `DoNotFreakOutSettings.cs`, `NeedScanner.cs` | Sonnet |
| 3 - Verification | Reflect on the actual game DLL to confirm the four-stats assumption, idle-detection specifics if any, and `playerForced` behavior under real load; build and manual in-game test | Sonnet, escalate to Opus if issues found |

Much shorter than Do Not Be Lazy's plan - no Harmony patch phase, no
float-menu integration risk, no sweep-manager complexity. The bulk of
Do Not Be Lazy's execution-plan risk (Phase 3, float menu patch +
sweep manager) simply doesn't exist here.

## 6. Dependencies and Development Setup (proposed)

Same `lib/` DLL pattern as Do Not Be Lazy for the two required game
references, minus Harmony entirely:

| DLL | Source | Needed? |
|---|---|---|
| `Assembly-CSharp.dll` | RimWorld install | Yes |
| `UnityEngine.dll` | RimWorld install | Yes |
| `UnityEngine.CoreModule.dll` | RimWorld install | Yes |
| `0Harmony.dll` | Harmony Workshop mod | **No** - this mod patches nothing |

`About.xml` needs no `modDependencies`/`loadAfter` entries for Harmony,
unlike Do Not Be Lazy's.
