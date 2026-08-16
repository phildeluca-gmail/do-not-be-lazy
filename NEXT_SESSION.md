<!-- Pickup context for a fresh session. Written 2026-08-16. Read this first, then CLAUDE.md's referenced docs as usual. -->

# Pickup: Do Not Be Lazy

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

## State right now

- Builds clean: `cd DoNotBeLazy/Source/DoNotBeLazy && dotnet build`
- All work is committed and pushed to `origin/master` (GitHub repo
  `phildeluca-gmail/do-not-be-lazy`), through commit `9d98167`.
- **Being actively playtested by the user in a real save**, not just
  statically verified - most of this session was live bug reports from
  actual gameplay, not planning. Expect more of that.
- Two architecture docs exist for **unimplemented, planned** features -
  do not build from them without a fresh go-ahead:
  - `DoNotBeLazy_Architecture.md` section 5.4 - an idle-pawn nudge
    ("standing" rule), part of this same mod.
  - `DoNotFreakOut_Architecture.md` - a new, separate, Harmony-free mod
    for proactive colony-wide need management. Not started, no folder
    created.

## Two things asked and not yet answered (from the last exchange)

1. **"Cannot force-haul stone blocks."** Checked `HaulGeneral`'s actual
   decompiled logic - no code-level reason it should fail. Best guess:
   a stockpile zone in reach doesn't have "Blocks" checked in its
   allowed-items filter (matches an earlier, confirmed wood-hauling
   report that turned out to be exactly this). **Needs the user to
   confirm their stockpile setup** before assuming it's a code bug.
2. **"The `* forced delivery to (ITEM)` is gone."** Could not find a
   code path that would make `ConstructDeliverResourcesToFrames`/
   `Blueprints` disappear entirely - reverting the giverClass-dedup
   regression (see below) may have already fixed this as a side effect,
   but that's unconfirmed. **Needs a repro**: what was clicked, and did
   it show once before (from the double-showing bug) and now shows
   zero times, or did it never show at all.

Ask about both before doing anything else construction/hauling-related.

## Known, accepted non-fixes

- **"Pack vehicles" (caravan loading) is out of scope as currently
  architected.** `WorkGiver_HelpGatheringItemsForCaravan` doesn't use
  `HasJobOnThing`/`HasJobOnCell` at all - it's driven by an active
  `LordJob_FormAndSendCaravan` Lord state, not a clickable target. This
  mod's entire detection pipeline is Thing/cell-scan based. Would need
  a genuinely different code path, not a small fix, if ever prioritized.
- **Achtung! is not in the user's modlist** - deprioritized as a
  compatibility target (see doc section 4). User's real modlist is base
  game + Pick Up And Haul only (confirmed directly, see the `modlist.md`
  memory file too).

## The one big lesson from this session, worth internalizing before touching FloatMenuPatch again

**A `giverClass`-based dedup fix (commit `81d42b0`) silently broke all
~19 workstation bill types** by assuming `giverClass` was a safe
uniqueness key - it isn't; every `WorkGiver_DoBill`-based workstation
type shares that one class. Caught and reverted in `9d98167`. The
takeaway that generalizes: **any "dedupe/filter by some shared
property" idea in `EligibleDefs()`/`IsSweepEligible()` needs its
uniqueness assumption checked against the *whole* `WorkGiverDef` set in
the actual XML (`grep` the giverClass/label/workType across all of
`WorkGiverDefs/WorkGivers.xml`), not just the one or two defs that
motivated the idea.** This bit us once already.

## How to investigate RimWorld API questions (established this session, works well)

- **Reflect on the actual game DLL** (`lib/Assembly-CSharp.dll`) via
  PowerShell + `[System.Reflection.Assembly]::LoadFrom` rather than
  trusting memory of the API - caught several wrong assumptions this
  way (e.g. `FloatMenuMakerMap.GetOptions` doesn't exist in this game
  version at all; `directOrderable` defaults true, verified via
  `Activator.CreateInstance` not `GetUninitializedObject` - the latter
  skips field initializers and gives a false answer).
- **Grep the actual Core Defs XML** at
  `E:\SteamLibrary\steamapps\common\RimWorld\Data\Core\Defs\` for
  WorkGiverDef specifics (workType, scanCells/scanThings, giverClass,
  directOrderable, equivalenceGroup) rather than guessing.
- **For method *bodies*, not just signatures**: the public decompile
  repos work well via WebFetch, e.g.
  `https://raw.githubusercontent.com/josh-m/RW-Decompile/master/RimWorld/<ClassName>.cs`
  (note: `Verse.AI` classes are under a literal `Verse.AI/` folder in
  that repo, not `Verse/AI/`). This is how the `forced` parameter's
  real effect (bypasses `CanReserve`'s `ignoreOtherReservations`) and
  `GrowerSow`'s exact preconditions were confirmed rather than guessed.
- **Check the user's actual mod/Workshop folder**
  (`E:\SteamLibrary\steamapps\workshop\content\294100\`) before
  assuming an unexpected menu option is a bug - it's how "Stuff
  Inventory" turned out to be the Pick Up And Haul mod, not us.

## Workflow notes

- User wants the architecture doc updated after essentially every turn
  - keep doing that, it's been the working pattern all session.
- User tests by manually copying the built DLL into their RimWorld
  `Mods/DoNotBeLazy/` folder and fully restarting the game between
  tests - static caches (like `FloatMenuPatch.eligibleDefs`) reset
  cleanly each time, not a stale-cache concern in practice.
- `gh` CLI is not installed on this machine - remote setup used a
  manually-pasted GitHub URL instead.
- Commit message style: detailed body explaining *why*, not just what -
  see any existing commit for the expected level of detail. Only commit
  when explicitly asked.
