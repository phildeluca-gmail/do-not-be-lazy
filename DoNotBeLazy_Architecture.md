<!-- Updated: 2026-08-20 EDT - vehicle-packing investigation, no code changes; VF hijacks the multi-select right-click, and the shared pool assumes one job per target -->

# Do Not Be Lazy - RimWorld 1.5 Mod Architecture

## 0. Current Status (2026-08-20)

Phases 1-3 are implemented, an Opus compatibility/correctness pass has
been run over the Phase 3 files, and the project builds clean (`dotnet
build` in `DoNotBeLazy/Source/DoNotBeLazy`, 0 errors/0 warnings). The
mod is now being tested in an actual running game (not just statically
verified) - see below for what's come out of that so far.

**INVESTIGATION SESSION (2026-08-19/20) - no code changed.** Report from
play: *"clicking on a vehicle being packed does not [offer] `* pack until
done`."* Diagnosed against Vehicle Framework's own source and the shipped
1.5 assembly. Nothing in this entry is a code change; two fixes are
proposed at the end and **neither is approved yet**.

**Root cause 1 - with 2+ pawns selected our float menu postfix never runs
at all on a vehicle.** Not a WorkGiver problem; the menu is never built.
Vehicle Framework prefixes `Selector.HandleMapClicks`
(`Harmony/PatchCategories/Extra.cs:73`, `Extra.MultiSelectFloatMenu`).
On a right-click with 2+ things selected it calls
`SelectionHelper.MultiSelectClicker`, which - when every selected object
is a player-faction non-vehicle `Pawn` **and** the cell under the mouse
holds a `VehiclePawn` - calls `vehicle.MultiplePawnFloatMenuOptions()`
and returns `true`. The prefix then returns `false`, so **vanilla
`HandleMapClicks` never runs**, `FloatMenuMakerMap.ChoicesAtForMultiSelect`
is never called, and neither of our postfixes fires. VF shows its own
`FloatMenuMulti` holding exactly one entry, "Board \<vehicle\>"
(`VehiclePawn_Rendering.cs:1166`).

Consequence is wider than the report: on a group right-click on any
vehicle, **every** `*` option disappears - pack, refuel, repair, haul,
`* Consume`, all of it. With a **single** pawn selected VF does not
intercept (`___selected.Count > 1` gates it), so `ChoicesAtFor` runs
normally and the option should already appear. That asymmetry is the
decisive test and it has not been run yet: select one colonist,
right-click the vehicle. If the option appears there and not with a
group, cause 1 is confirmed and complete. **If it does not appear for a
single pawn either, cause 1 is not the whole story - keep digging before
writing any code.**

Everything on our side is already correct for this def, verified rather
than assumed:

- `PackVehicle` is `workType Hauling` (in `SupportedWorkTypeDefNames`),
  sets no `directOrderable` so it defaults true, and is not in
  `ExcludedDefNames`.
- `Vehicles.WorkGiver_PackVehicle : WorkGiver_CarryToVehicle :
  WorkGiver_Scanner` - confirmed by reflection over the shipped
  `Vehicles.dll`, so `IsSweepEligible` accepts it.
- Its `JobOnThing` takes the **`VehiclePawn` itself** as `t`, and the
  vehicle is present in `cell.GetThingList(map)` - VF's own
  `MultiSelectClicker` reads `thingGrid.ThingsAt(mousePos)` the same
  way, so the multi-cell footprint is not a problem.
- No `HasJobOnThing` override, so the base `JobOnThing(...) != null`
  applies and our probe is faithful.
- Its `PotentialWorkThingRequest` is `ThingRequest.ForGroup(Pawn)` - a
  plain property, so the `req` read in `FindTargetWithJob` cannot throw.

`HelpPackVehicleCaravan` shares the label "pack vehicle" but its worker
is a bare `WorkGiver`, not a `WorkGiver_Scanner`, so we already reject
it correctly.

**Root cause 2 - even with the menu reachable, "until done" would haul
one item with one pawn and stop.** `PackVehicle.PotentialWorkThingsGlobal`
returns `VehicleReservationManager.VehicleListers(LoadVehicle)` - the
**vehicles awaiting loading**, not the items to load. So the pool comes
back holding a single entry, the vehicle itself. Then:

- `BeginAreaSweep` (`SweepManager.cs:344`) breaks out of the pawn loop
  the moment the pool empties, so only the *first* colonist is ever
  assigned and the rest of the selection is silently dropped.
- Each `JobOnThing` yields one `LoadVehicle` job carrying one item. When
  it ends, `AssignNextTask` finds an empty, non-rescannable pool and
  calls `RemoveSweep`. One pawn, one item, sweep over - strictly worse
  than leaving vanilla hauling to it, and nothing like the label.

**This is a general gap, not a vehicle one.** The shared pool assumes
one job per target. Every "repeatedly bring things to this one thing"
WorkGiver has the same shape, vanilla included - `WorkGiver_Refuel`,
`WorkGiver_HaulToContainer`, `FillFermentingBarrel`. The mod already has
the right machinery for it in `SweepOrder.WorkstationTarget` (re-ask the
same scanner for the same target until it answers null, see
`AssignNextTask`), but that path is gated on `scanner is WorkGiver_DoBill`
and is deliberately single-pawn per section 2.

**Root cause 3 (minor) - duplicate entries.** `PackVehicleTurret`
(`Vehicles.WorkGiver_RefuelVehicleTurret`, `workType Hauling`) is a
`WorkGiver_Scanner` that also targets the `VehiclePawn` and also carries
the label "pack vehicle". A vehicle needing both cargo and turret ammo
would offer `* Pack vehicle until done` twice. Same class as the Sense
of Urgency duplicate already open; fold it into that fix rather than
solving it twice.

**Proposed fix 1 - menu reachability. NOT APPROVED, NOT BUILT.** Harmony
**prefix** on the `Vehicles.FloatMenuMulti` constructor, signature
confirmed by reflection as
`(List<FloatMenuOption>, List<Pawn>, Pawn, string, Vector3)`. A prefix
runs before the derived constructor's body, hence before the base
`Verse.FloatMenu` constructor caches option sizes, so mutating the
incoming list in place is safe and the added entries lay out normally.
The constructor hands us the selected pawns and the click position -
exactly the arguments `AddSweepOptions` already takes. Patched by
reflection (`AccessTools.TypeByName("Vehicles.FloatMenuMulti")`, applied
only when the type resolves), so Vehicle Framework stays a soft
dependency and nothing is added to `lib/`. This would be the first
Harmony patch in the mod aimed at another mod's type.

**Proposed fix 2 - repeat semantics. NOT APPROVED, NOT BUILT.**
Generalise the `WorkstationTarget` path from "the worker is a
`WorkGiver_DoBill`" to "this WorkGiver works a persistent target",
identified by a new `VehicleCompat` helper matching
`Vehicles.WorkGiver_CarryToVehicle` subclasses by reflected type - which
covers `PackVehicle` and `LoadUpgradeMaterials` and any future subclass
without a defName list. Also allow **multiple** pawns on one persistent
target for this case: VF reserves per *item* inside `FindThingToPack`
(`pawn.CanReserve(thing)`), so parallel haulers are safe, unlike the
single-pawn bill case.

Both fixes touch things this document governs - a new Harmony patch, a
new compat file, and a change to sweep semantics - so per `CLAUDE.md`
this entry lands first and the code waits on a fresh go-ahead.

**REVIEW SESSION (2026-08-18, evening) - no code changed.** A read-only
pass over everything the overnight session landed, plus one new in-game
report. `git status` clean, `dotnet build` still 0 errors / 0 warnings.
Nothing in this entry is a code change; it is what the review
established and what it turned up.

**Verified against the real decompile, not just re-read: `FireCompat.HasFireJob`
is a faithful port of `WorkGiver_FightFires.HasJobOnThing` minus the
intended gate.** Fetched `RimWorld/WorkGiver_FightFires.cs` and
`RimWorld/JobDriver_BeatFire.cs` from the decompile and diffed by hand:

- The faction test in vanilla's pawn-attached branch is
  `(pawn2.Faction == pawn.Faction || pawn2.HostFaction == pawn.Faction
  || pawn2.HostFaction == pawn.HostFaction) && !Home[fire.Position] &&
  ManhattanDistanceFlat(...) > 15`. The faction clause exists **only as
  part of the home-area gate** - it is not a separate "don't help
  hostiles" rule, and vanilla does let colonists beat out fires on
  enemies. So dropping the whole clause along with the home gate is
  correct, not an accidental omission. Recorded because it reads like an
  omission on a cold read and would otherwise get "fixed" by someone
  later.
- `HandledDistSquared = 25f` with `<=` is exactly vanilla's
  `InHorDistOf(f.Position, 5f)`. `ReserveCheckDistSquared = 225f` with
  `>` matches. `JobOnThing` really is a bare
  `return new Job(JobDefOf.BeatFire, t)` - confirmed, so bypassing
  `HasJobOnThing` is a complete override with nothing left behind it.
- `JobDriver_BeatFire.TryMakePreToilReservations` returns `true` without
  reserving anything; the reservation happens opportunistically in the
  approach toil (`if (CanReserve(...)) pawn.Reserve(...)`) and the job
  proceeds either way. Two consequences: `FireIsBeingHandled` does have
  real reservations to read, so the fan-out mechanism works as intended;
  and vanilla tolerates two pawns on one fire where our pool-based
  hand-out does not. Ours is the stricter, wanted behaviour.
- `FightFires` sets no `scanThings` in `WorkGivers.xml`, so it takes the
  `WorkGiverDef` default of `true` - the `scanThings` branch does run
  for it (`scanCells` defaults false).

**Question answered: does a sweep actually find work of the chosen type
within the radius of the click?** Yes, structurally - and the two ways
it can quietly find less are both already-known issues, now with a
concrete consequence attached:

- Radius is honoured in both branches. `BeginAreaSweep` reads
  `Settings.sweepRadius` (default 16) and passes the clicked cell as
  centre; `ScanCells` walks `GenRadial.RadialCellsAround` and re-checks
  `LengthHorizontalSquared > radiusSquared`; `ScanThings` pulls the
  map-wide lister and drops anything past the same distance. Type is
  honoured because the same `WorkGiverDef` builds the pool and issues
  every job drawn from it.
- **It is a snapshot, not a standing order.** The pool is built once at
  `BeginSweep`, and only fire sweeps rescan. Work that becomes available
  inside the radius *after* the click - a plant matures, haulables get
  dropped, a frame finally has its resources - is never picked up.
  "Until done" means "until the list from the moment you clicked is
  done". By design, but stated nowhere a player would see it.
- **The whole scan runs against one pawn.** `BeginAreaSweep` takes
  `eligiblePawns[0]` as the driver, and the comment there asserts the
  filters it applies "don't vary by which pawn asked". That is wrong for
  three of them: allowed-area, `CanReach`, and `CanReserve` are all
  per-pawn. A driver who is area-restricted or walled off from part of
  the radius shrinks the pool for the entire group. Correct the comment
  when this area is next touched.
- The scan-time `CanReserve` omits `ignoreOtherReservations` while the
  `HasJobOn*` probe immediately after passes `forced: true`. So a target
  another colonist merely has reserved is dropped at scan time even
  though the forced job would have taken it. Already on the open list;
  this is the most likely reason for a pool that comes back smaller than
  the player can see work for.

**Confirmed by grep, not newly discovered: `showSweepOverlay` still does
nothing.** Section 3.4 already records that the setting exists and
nothing reads it; a full-tree grep tonight confirms that is still true -
the field is declared, scribed, and given a checkbox reading "Show sweep
radius overlay on hover", and no code anywhere reads it. Worth
re-flagging here rather than only in 3.4 because it interacts with the
radius question above: a player asking "is it really sweeping 16 tiles"
has no way to see the answer, and the checkbox actively implies they
should. Either build the overlay or drop the checkbox; a control that
does nothing is worse than neither. Untouched tonight because no code
was changed.

**Review findings, open and unfixed.** Ordered by how much they matter,
all of them in the overnight session's own new code:

1. **The new menu feedback doesn't cover pawn-side refusals**
   (`FloatMenuPatch.cs:204`). When `EligiblePawns` comes back empty the
   def is skipped with `continue` **before** any feedback is built - so
   "Hauling is priority 0 in the work tab", "the pawn is drafted", "no
   Manipulation" all still produce the old silent nothing. Given
   `PawnValidator.CanSweep` requires `WorkIsActive`, this is at least as
   likely an answer to the standing *cannot force-haul stone blocks*
   report as `NoEmptyPlaceLower` is. Fix is a disabled entry for this
   case, scoped by `req.Accepts(thing)` the same way so it cannot spam
   every def in the game.
2. **A burning cell can now produce a completely empty menu**
   (`FloatMenuPatch.cs:198`). Fire suppresses every other def and
   `* Consume`, but `FireCompat.HasFireJob` never writes to
   `JobFailReason`, so if it refuses (unreachable, already handled) or
   no selected pawn is fire-eligible, the player gets zero entries and
   zero explanation. Needs a hardcoded reason string on the firefighting
   path - there is no vanilla one to inherit, since vanilla never offers
   this option at all.
3. **Duplicate fire entries under Sense of Urgency.**
   `FireCompat.IsFirefighting` keys on `workType.defName`, so that mod's
   parallel urgent firefighting def matches too and a burning cell would
   offer `* fight fires until done` twice. Same duplicate class already
   suspected elsewhere in the menu; fire inherits it.
4. **Paused sweeps now survive interrupts that used to end them**
   (`SweepManager.cs:365`). The paused branch returns before the
   `TargetFailureIsRecoverable` check, so while paused, a manual player
   work order is no longer "something took this pawn" - when that job
   ends and needs are satisfied, the sweep yanks them back. Drafting is
   still caught by `MapComponentTick`. This looks intentional given the
   "resume last-ordered work" requirement, but it is a behaviour change
   beyond the reported bug and will show up in testing.
5. **`MaxPauseTicks` is only evaluated on a job end**
   (`SweepManager.cs:381`). A pawn who sleeps 20,000 ticks in one job
   holds the pool until they wake. Harmless, but the log line prints the
   constant rather than the elapsed time, so it claims "after 30000
   ticks paused" when it may have been far longer. One-line fix.
6. **Two minor fire-rescan effects.** A rescan re-admits a fire another
   pawn is walking to (more than 5 tiles out, not yet reserved), so two
   pawns can converge - self-correcting, not worth code.
   `BeginAreaSweep` also breaks out of the pawn-assignment loop the
   moment the pool is empty (`SweepManager.cs:344`), so extra pawns
   never join a rescannable sweep that a rescan would have found work
   for.

None of the six is a crash or compile risk. 1 and 2 are the two worth
fixing before the playtest, since both sit inside the change that was
specifically about not leaving the player guessing.

**NEW REPORT (2026-08-18, from play): colonists stand still when hunting
is not assigned.** Reported verbatim as "workers are back to standing
still when hunt is not assigned", and **"back to"** is the important
word - a returning symptom, not a first sighting. Not diagnosed: no
repro, no log pulled, no code changed. Deferred to next session as the
first item. What is already known that bears on it, in the order worth
checking:

- **Sense of Urgency** (`ZombiePhil.Urgency`, Workshop 3001253573) is
  compiled against 1.6 and throws on `Toils_General.WaitWith` in 1.5.
  Hunting is the specific thing it breaks - already documented here as
  hunters looping "started 10 jobs in 10 ticks" forever. A WorkGiver
  that throws during `TryFindAndStartJob` can plausibly leave a pawn
  with no job at all, which looks exactly like standing still. **Test
  with that mod disabled before anything else.**
- **TKS Priority Treatment** patches `Pawn_JobTracker.TryFindAndStartJob`
  directly - the same method any "pawn will not pick up work" symptom
  runs through.
- **Ours, and cheap to rule in or out:** `JobTrackerPatch.Postfix` runs
  on every `EndCurrentJob` for every pawn. It early-returns unless the
  pawn is in an active sweep, so the blast radius is small - but the
  `scanner.JobOnThing(pawn, billGiver, true)` bill-continuation call is
  **not** wrapped in a try/catch, so a throwing modded WorkGiver would
  propagate out of `EndCurrentJob` and could leave that pawn jobless.
  Affects only pawns in a sweep. A run with Do Not Be Lazy disabled
  entirely is the one test that separates our bug from the modlist's.
- **Section 5.4's idle-pawn nudge is precisely the countermeasure for
  this symptom and is still unbuilt.** Do not build it as a fix before
  the cause is known: nudging an idle pawn whose think tree is throwing
  just re-throws on a two-second timer.

**Test state is UNCONFIRMED for everything from 2026-08-18.** The
overnight session ended with the DLL not yet copied into the RimWorld
`Mods/` folder. The user played tonight and reported no real bugs, but
whether that session was running the 2026-08-18 build was never
established. **Ask before treating any 2026-08-18 change as verified in
game.** The only sweep confirmed working from a real save remains the
`HaulMerge` run from 2026-08-17.

**NEW (2026-08-18): `* Fight fires until done` - group firefighting, with
vanilla's home-area restriction deliberately overridden.** Reported as
"cannot group-select to put out fires." Three separate gates were
stopping it, all of them deliberate at the time:

- `FightFires` is `directOrderable: false` in vanilla (confirmed in
  `Data/Core/Defs/WorkGiverDefs/WorkGivers.xml`), and `IsSweepEligible`
  rejects on exactly that flag - a rule added earlier precisely so we
  wouldn't have to denylist non-orderable defs one at a time.
- `FloatMenuPatch.Build` bailed out of the *entire* right-click when a
  `Fire` was on the clicked cell, on the reasoning that nothing we offer
  is sensible to send pawns into on a burning tile. True for everything
  we offered at the time; it also made the one sensible option
  impossible.
- `TaskScanner.TargetIsBurning` filters every burning target out of the
  pool, and a `Fire` *is* the burning thing, so even a firefighting
  sweep would have scanned up an empty pool.

All three now have a firefighting exemption, keyed on
`workType.defName == "Firefighter"` rather than on the `FightFires`
defName, so a modded firefighting WorkGiver gets the same treatment.
`Build` no longer bails on fire: when the clicked cell holds a `Fire` it
offers the fire sweep **and nothing else** - the original "don't send
pawns into a burning tile" reasoning still holds for every other def.

**Deliberate divergence from vanilla, per explicit user decision:** the
home-area restriction is overridden. `WorkGiver_FightFires.HasJobOnThing`
refuses any fire whose position isn't in `pawn.Map.areaManager.Home`
(for fires not attached to a pawn) - that gate is why auto-firefighting
ignores wildfires. Since the player is explicitly clicking the fire, we
bypass it. `JobOnThing` has no gate of its own (its whole body is
`return new Job(JobDefOf.BeatFire, t)`), so the override is entirely a
matter of not calling `HasJobOnThing` - hence `FireCompat.HasFireJob`,
which re-implements the rest of vanilla's checks and drops only the home
-area one. What it keeps, verified against the decompiled source:

- fire attached to a pawn: never the pawn's own fire, and the attached
  pawn must be reachable
- `WorkTags.Firefighting` disabled on the pawn
- the reservation check vanilla only applies past 15 tiles
- `FireIsBeingHandled` - re-implemented rather than called, because
  `WorkGiver_FightFires` is `internal` and the helper is unreachable
  from our assembly even though it's `public static` on it. This is what
  makes a group fan out instead of piling on one fire: a fire with a
  respected reserver standing within 5 tiles counts as handled.
- reachability to the fire itself (the framework's usual filter, which
  we skip by calling `HasJob*` directly - same bypass class as the whole
  `Potential*Global` lesson below)

**Open, and deliberately not changed while the decision was
unavailable: drafted pawns are still excluded from fire sweeps.**
`FightFires` is `canBeDoneWhileDrafted: true` with
`autoTakeablePriorityDrafted: 20`, so vanilla explicitly does let
drafted pawns fight fires, and "fire breaks out mid-raid" is a real use
case. Including them would need a per-WorkGiver exception in *two*
places - `PawnValidator.CanSweep` (rejects `pawn.Drafted`) and
`SweepManager.MapComponentTick` (drops a pawn from the sweep the moment
they're drafted) - which is a structural exception to a rule the whole
codebase currently holds uniformly, and section 4's drafted-pawn edge
case states outright. Left consistent for now. **Undraft before ordering
a fire sweep.** Revisit on request.

Fires spread, so a fire sweep is the first sweep type where a pool built
once at `BeginSweep` is actively wrong. `SweepOrder` now carries the
scan origin and radius, and `AssignNextTask` re-scans **once** when the
pool empties on a rescannable order (firefighting only). One rescan per
call, guarded by a local flag - if the rescan also comes up empty the
sweep ends normally, so there's no tight loop.

**FIXED (2026-08-18): the need pause/resume loop.** The bug documented
below on 2026-08-17 - a pawn paused for a critical need after every
single task instead of pausing once, eating, and coming back. Two
distinct symptoms, one root cause, and the second symptom is worse than
what the log first showed:

1. `Notify_JobEnded` consumed `pausedForNeed` on the **first** job end
   after a pause, whatever that job was. `PauseForNeed` calls
   `EndCurrentJob(InterruptForced)`, which defaults `startNewJob: true`,
   so vanilla's think tree immediately starts *something*; when that
   ended we resumed and yanked the pawn off a meal they'd only just
   started walking to.
2. Once the flag was spent, the pawn was no longer "paused" - so the
   *next* interrupt, which is usually the genuine one (vanilla forcing
   them to go eat), arrived at `Notify_JobEnded` unpaused, hit
   `TargetFailureIsRecoverable(InterruptForced)` = false, and
   `RemoveSweep`d the order outright. That is the "they take a break and
   never come back" report: the sweep isn't paused at that point, it's
   gone.

Fix: resumption is now gated on the need actually being satisfied, not
on a job ending. `Notify_JobEnded` re-checks the pawn's needs while
paused and **stays** paused if any is still under threshold, without
touching the failure counter or ending the sweep. `NeedMonitor`'s
existing `IsPaused` guard then does the rest - a paused pawn is never
re-interrupted, so the 60-tick re-pause loop can't form.

Resumption uses **hysteresis**: `NeedMonitor.ResumeMargin` (0.05, i.e.
5 percentage points of `CurLevelPercentage`) above the pause threshold.
Without it a need hovering exactly at the threshold thrashes - resume,
drop a hair, re-pause, one job interrupt per cycle. Mood is the case
that actually needs this; food and rest jump well clear on their own.

Bounded by `MaxPauseTicks` (30,000 ticks, half an in-game day). A need
that never recovers - a mood that stays under threshold - would
otherwise hold a paused sweep and its share of the pool forever. On
expiry the sweep ends with a log line rather than hanging.

**NEW (2026-08-18): the menu now says *why* a sweep isn't on offer.**
Requested after several "I told them to haul and nothing happened"
reports (stone blocks, wood) that each cost a session to chase and each
turned out to be a stockpile filter, not a mod bug. Previously a def
that found no target was silently skipped, so "no option" and "option
that does nothing" looked identical from the player's side.

Modelled directly on vanilla's own `FloatMenuMakerMap.AddJobGiverWorkOrders`,
which is where RimWorld already solves this problem - read from the
decompiled source rather than invented:

- `Verse.AI.JobFailReason` is a static the WorkGivers write into while
  answering `HasJobOn*`. `JobFailReason.Clear()` before the probe,
  `JobFailReason.HaveReason` / `.Reason` after. `HaulAIUtility` sets it
  for every haul refusal path there is - forbidden, forbidden outside
  allowed area, reserved for prisoners, burning, and
  `NoEmptyPlaceLower` ("no empty place to put it"), which is the exact
  answer to the stone-blocks and wood reports.
- The failing option is added **disabled** (null action, `Disabled` set
  explicitly), labelled with vanilla's own `CannotGenericWork` key plus
  the reason in parentheses, so wording and translation match the base
  game.
- Scoped the way vanilla scopes it, which is what keeps the menu from
  filling with thirty greyed-out entries: only defs whose own
  `PotentialWorkThingRequest.Accepts(thing)` is true for something on
  the clicked cell are candidates, and of those, only ones that actually
  produced a reason get an entry. No reason means no entry.
- Capped at `MaxFeedbackOptions` (3) regardless, and only shown when no
  live `*` option was produced for that def.

Second surface, for the case where the option *was* offered and the
sweep still started empty (the clicked target had a job but the radius
scan came back with nothing): `BeginAreaSweep` now raises a
`Messages.Message(..., MessageTypeDefOf.RejectInput)`. The float menu is
already gone by then, so a message is the only channel left. Between the
two, a sweep that does nothing should no longer be silent.

**First verified-working sweep (2026-08-17).** A `HaulMerge` sweep ran
end to end in a real save - 30 targets found, jobs handed out, pool
counting down, `Succeeded` conditions. Verbose logging is confirmed live
in game. Everything in the 2026-08-16 sow entry below remains **static
analysis only** - none of the `GrowerSow` work has been exercised yet.
A 26-test playtest plan covering it is published at
`https://claude.ai/code/artifact/e60cfd11-1f82-46a8-9111-a25d9352a2dd`.

**BUG, FOUND 2026-08-17 - FIXED 2026-08-18 (see the entry above for the
fix; this entry is kept for the diagnosis): the need pause/resume loop.**
The
same log showed a pawn being paused for a critical need after *every
single task* of a haul sweep - three tasks, three pauses - rather than
pausing once, eating, and resuming.

`SweepManager.Notify_JobEnded` resumes on the **first** job end after a
pause (`if (pausedForNeed.Remove(pawn))`) and never checks whether the
need was actually satisfied. `PauseForNeed` calls
`EndCurrentJob(InterruptForced)`, which defaults `startNewJob: true`, so
vanilla's think tree immediately starts *something* - not necessarily an
eat/sleep/joy job. Whatever it is, when it ends we treat that as "the
need was addressed" and resume. `NeedMonitor` then re-pauses 60 ticks
later, because clearing `pausedForNeed` made `IsPaused` false again.

**This corrects a claim made twice in this document** - in section 2 and
in the 2026-08-15 "sweeps now resume after a need interrupt" entry
below, both of which asserted that interrupt-loop risk doesn't reappear
"because resumption is gated on a real job-end event, not a repeated
need check." That reasoning is wrong: the job-end event can be the
replacement job that the interrupt itself triggered. Both passages are
annotated in place; do not re-derive this.

Likely fix, **not implemented and not yet agreed**: re-check the need on
resume and stay paused while it remains under threshold. Per CLAUDE.md
that needs a decision and a doc edit before any code. **Implemented
2026-08-18, essentially as described, plus hysteresis and a give-up
bound - see the 2026-08-18 entry.**

**Diagnostic gap found the same day: the float-menu path is entirely
untraced.** `AddConsumeOption`, `FindTargetWithJob`, `IsSweepEligible`
and the option-building path in `FloatMenuPatch` contain zero
`Logger.Message` calls - only `Error`/`Warning`. Only `SweepManager` and
`TaskScanner` emit trace lines, and only *after* an option is chosen. So
"wrong / missing / duplicate menu option" reports cannot be diagnosed
from the log at all, however many times they're reproduced; they have to
be worked out from the defs. Worth adding a verbose line per offered
option the next time this class of bug comes up.

**Investigated, deliberately not changed (2026-08-17): `* Consume` on a
corpse.** Reported as "harvesting a corpse at a harvesting table should
be called harvest, not consume." User's decision is to leave the code
as-is. Findings, so this isn't re-derived:

- The harvesting table belongs to **Reclaim, Reuse, Recycle (Continued)**
  (`Mlie.ReclaimReuseRecycle`, Workshop `2567364887`).
- Its `R3_DoWorkHarvestCorpse` is `giverClass WorkGiver_DoBill`,
  `workType Doctor`, label "harvest corpse", with
  `fixedBillGiverDefs: R3_TableHarvesting`. We already offer it
  correctly as `* Harvest corpse until done`, since `IsSweepEligible`
  accepts any `WorkGiver_DoBill` regardless of work type.
- But `fixedBillGiverDefs` means `HasJobOnThing` is true only for the
  **table**, and `FindTargetWithJob` only inspects things on the clicked
  cell. So clicking the *corpse* can only ever yield `* Consume ...`;
  the harvest option requires clicking the *table*. Not a bug so much as
  a consequence of workstation sweeps being single-target by design
  (section 4, "Multiple workstations in radius").
- `RimWorld.IngestibleProperties` (namespace is `RimWorld`, not `Verse`
  - verified by reflection) defaults `showIngestFloatOption` to **true**
  and `ingestCommandString` to **empty**. Only drugs set that string in
  Core, so corpses and plain food fall through to `AddConsumeOption`'s
  hardcoded `"Consume " + LabelShort`. The resulting label matches
  vanilla's own wording exactly, so this is a design question rather
  than a divergence.

**Fixed: severe regression introduced by the previous session's own
giverClass-dedup fix.** That fix (for the deliver-resources-shown-twice
bug) deduped `eligibleDefs` by `def.giverClass`. Turns out `giverClass`
is not a safe uniqueness key across the whole WorkGiverDef set: **all
~19 workstation bill types** (cooking, smithing, tailoring, art,
stonecutting, smelting, drug production, everything routed through
`WorkGiver_DoBill`) **share that exact one giverClass**, so the dedup
was silently collapsing every workstation type in the game down to just
one surviving `*` option. Reverted to the original approach: a scoped,
explicit `ExcludedDefNames` denylist naming the two Hauling-tagged
duplicate defs specifically (`DeliverResourcesToFrames`,
`DeliverResourcesToBlueprints`), leaving their Construction-tagged
identical-behavior counterparts as the sole survivors. Caught via a
user bug report before it shipped to a broader install base - worth
remembering: **any future "dedupe by some shared property" idea needs
its uniqueness assumption checked against the *whole* def set, not just
the two defs that originally motivated it.**
- **Not fixable as a simple bug: "pack vehicles" (caravan loading).**
  `WorkGiver_HelpGatheringItemsForCaravan` doesn't implement
  `HasJobOnThing`/`HasJobOnCell` at all - it overrides `NonScanJob()`,
  a third WorkGiver mechanism this mod's detection pipeline (built
  entirely around Thing/cell scanning) never calls. It's also gated on
  an active, in-progress `LordJob_FormAndSendCaravan` (Lord/game-state
  driven, not tied to a clickable target) - confirmed via its decompiled
  source. This isn't a target-scoped "sweep an area" task the way
  everything else in this mod is; supporting it would need a
  genuinely different code path, not a quick fix. Out of scope unless
  explicitly prioritized.
- **Unconfirmed: "cannot force-haul stone blocks."** `HaulGeneral`'s
  actual logic (confirmed via decompiled `WorkGiver_Haul`/`HaulAIUtility`
  source) only fails `PawnCanAutomaticallyHaulFast` on unreachability,
  reservation conflict (which `forced: true` already bypasses), being
  reserved food for prisoners, or being on fire - then calls
  `HaulToStorageJob`, which returns null if there's no valid stockpile
  destination. No code-level reason stone blocks specifically would
  differ from any other haulable already confirmed working. Best guess,
  matching the earlier wood-hauling report exactly: check whether a
  stockpile zone in reach actually has "Blocks" checked in its allowed
  items filter - "Blocks" is often a separate category from other raw
  resources in stockpile presets. Needs confirmation, not yet verified
  as an actual code bug.
- **Unconfirmed: "the * forced delivery to (ITEM) is gone."** Could not
  find a code path that would make `ConstructDeliverResourcesToFrames`/
  `Blueprints` disappear entirely (as opposed to the double-showing bug
  already fixed) - their giverClass is unique (not part of the
  `WorkGiver_DoBill` collision above), so the now-reverted dedup logic
  wouldn't have removed them outright either. Needs a repro to
  investigate further: what exactly was clicked, and did it show once
  during the double-showing bug and now shows zero times, or never
  showed at all this session.

**Phase 5 (idle pawn nudge, section 5.4) is architected but NOT
implemented** - planning only, per explicit request. A companion new
mod, Do Not Freak Out, is also architected but not started - see
`DoNotFreakOut_Architecture.md`.

**From in-game testing (2026-08-16) - the WorkGiver static-state class of
bug.** Reported: "`* sow crops` appears but doesn't sow crops, the pawns
seem to get new jobs", plus "sow assigns sowing to unzoned terrain and
should only sow in zoned terrain." Both are the **same root cause**, and
it generalizes past `GrowerSow`.

`WorkGiver_Grower.wantedPlantDef` is a **mutable `protected static`
field** (verified by reflection against the real
`lib/Assembly-CSharp.dll`, not assumed). Vanilla only ever initializes it
inside `PotentialWorkCellsGlobal`: `WorkGiver_GrowerSow.ExtraRequirements`
sets it per zone/building, and the enumerator nulls it after each one.
This mod never calls `PotentialWorkCellsGlobal` - the whole detection
pipeline calls `HasJobOnCell`/`JobOnCell` directly - so that setup never
runs and the field holds whatever the last caller left in it.
`JobOnCell` only recomputes it lazily, `if (wantedPlantDef == null)`.
Two distinct failures fall out:

- **Wrong crop / job dies on arrival.** A stale non-null value is baked
  into `new Job(JobDefOf.Sow, c) { plantDefToSow = wantedPlantDef }`.
  `JobDriver_PlantSow`'s *goto* toil then hard-`FailOn`s
  `!job.plantDefToSow.CanEverPlantAt(TargetLocA, Map)` (and on
  `AdjacentSowBlocker`), so the job dies during the walk, before any
  work. Non-`Succeeded` -> `Notify_JobEnded` -> `RemoveSweep` -> vanilla's
  think tree immediately hands the pawn something unrelated. That is
  exactly the reported "pawns seem to get new jobs", and the user's log
  shows the matching chain (`EndCurrentJob` -> `TryFindAndStartJob` ->
  `DetermineNextJob` -> `JobGiver_Work`) with **no** DoNotBeLazy error
  lines - nothing threw, the mod issued a job that vanilla correctly
  rejected.
- **Sowing unzoned terrain.** Zone membership is never checked
  explicitly anywhere in `JobOnCell`. The *only* gate is
  `CalculateWantedPlantDef` returning null (via
  `GridsUtility.GetPlantToGrowSettable`, which returns null when a cell
  has neither a plant-grower edifice nor a growing zone) - and that call
  lives inside the `if (wantedPlantDef == null)` branch. A stale non-null
  value skips the branch, so the gate never runs and bare dirt falls
  through to a sow job.

`WorkGiver_GrowerHarvest` is unaffected: no `ExtraRequirements` override,
and it recomputes per cell via `CalculateWantedPlantDef`.

**Generalized lesson, and the second one of this shape** (after the
`giverClass` dedup regression): vanilla WorkGivers put real preconditions
in `Potential*Global`/`ExtraRequirements`, not in `JobOn*`. Calling
`JobOn*` directly - this mod's entire architecture - silently bypasses
all of them. Before adding any WorkGiverDef to the eligible set, read the
worker's decompiled source and check for (a) mutable static fields, (b)
an `ExtraRequirements` override, (c) reachability/fire/zone-toggle checks
that live in the scan rather than the job.

Four fixes, all in this pass (new `Utility/GrowerCompat.cs` holds the
shared logic; see 3.2):

1. **Reset the static before every Grower `HasJobOnCell`/`JobOnCell`
   call** (`FloatMenuPatch.FindTargetWithJob`, `TaskScanner.ScanCells`,
   `SweepManager.AssignNextTask`). Nulling it restores correct per-cell
   semantics through vanilla's own lazy branch, and with it the zone
   gate. Fixes both reported symptoms.
2. **Apply the `ExtraRequirements` gates that fix 1 does *not* restore** -
   `IPlantToGrowSettable.CanAcceptSowNow()` and `Zone_Growing.allowSow`.
   These live only in `ExtraRequirements`, which is never called at all,
   so without this a zone with "allow sow" toggled off is still swept.
3. **Reachability.** `WorkGiver_Grower.AllowUnreachable` is `true`, which
   is precisely why vanilla does its own `pawn.CanReach` per zone inside
   `PotentialWorkCellsGlobal`. Neither `TaskScanner` nor `PawnValidator`
   checked reachability at all, so one unpathable cell produced a job
   that failed and killed the whole sweep through the same
   `RemoveSweep` path.
4. **Blocker-job chaining.** `GrowerSow.JobOnCell` legitimately returns a
   `CutPlant` or `HaulAside` job *instead of* `Sow` when something blocks
   the cell. On success `Notify_JobEnded` advanced to the next pool
   entry, so the cell just cleared never got sown. Preparatory jobs are
   now detected (returned job's `targetA` differs from the target asked
   about) and the cell is re-queued **once** - the once-only guard is
   what keeps an unsatisfiable target from looping. Note this check is
   not scoped to cell targets: construction deliver-resource jobs also
   point `targetA` at the resource rather than the frame, so frames get
   one extra re-queue too. Plausibly an improvement (frames usually need
   several deliveries) and it's capped, but it is untested beyond sow.

**A failed target is no longer a failed sweep (2026-08-16).** Separate
from the four fixes above, and the reason the `plantDefToSow` bug
presented as "sowing does nothing" rather than "one cell got skipped":
`Notify_JobEnded` ended the entire sweep on *any* non-`Succeeded`
`JobCondition`, then vanilla's think tree handed the pawn something
unrelated. For an "until done" command that's the wrong default - a raid
interrupting a pawn, another colonist claiming a target first, or one
unreachable cell all silently cancelled the whole order.

`JobCondition` is now split (`TargetFailureIsRecoverable`):

- `Incompletable`, `QueuedNoLongerValid`, `ErroredPather` - the *target*
  didn't work out. Drop it, hand out the next one, sweep continues.
- `InterruptForced`, `InterruptOptional` - something deliberately took
  this pawn (manual order, drafting, mental break, another mod).
  Retrying would tug-of-war with whoever interrupted, so the sweep ends.
- `Errored` - a real exception in the job system; retrying risks a loop
  rather than a recovery. Ends the sweep.

Bounded by `MaxConsecutiveFailures` (8), tracked **per pawn** in
`SweepManager.consecutiveFailures` and reset on any successful task
(per-pawn rather than per-`SweepOrder` because an order is shared across
a group sweep, and one pawn's bad luck shouldn't count against the
others). The bound is not just a heuristic: each retry re-enters
`AssignNextTask` from inside `EndCurrentJob`, so an unbounded retry is
also unbounded recursion if every target fails on the tick it's issued.
Eight failures with no success in between ends the sweep with a log line.

**Fire is now filtered per target, not just on the clicked cell
(2026-08-16).** The earlier fire fix bailed out of the whole right-click
if the clicked cell held a `Fire` - but that only covers the one tile the
player aimed at, and a sweep radius routinely spans tiles they never
looked at. Nothing filtered fire during the scan at all: `GrowerSow` and
`GrowerHarvest` don't check it (a scorched-but-mature plant, or a cell
burnt back to bare ground, still passes `HasJobOnCell`), and vanilla's
own guards - `Zone_Growing.ContainsStaticFire`, `Building.IsBurning()` -
live in `PotentialWorkCellsGlobal`, which this mod never calls. Same
`Potential*Global`-bypass class as everything else in this entry.

`TaskScanner.TargetIsBurning` (via `RimWorld.FireUtility`) is applied in
both scan branches and again in `SweepManager.TargetStillValid` - the
re-check matters because a sweep can run for a long time and a fire that
starts after the pool was built is precisely what the scan-time filter
cannot catch.

**Deliberate divergence from vanilla:** vanilla skips an *entire* grow
zone when it contains static fire. This filters per target instead, so
the unburnt part of a field stays workable - better behaviour for an
explicit player order, and it avoids re-walking every zone cell once per
scanned cell (which the zone-wide check would cost inside a radial scan).

**From in-game testing (2026-08-15):**
- **Fixed: right-clicking a construction target showed two identical
  `* deliver resources...` options.** `ConstructDeliverResourcesToFrames`
  (workType Construction) and `DeliverResourcesToFrames` (workType
  Hauling) - and the Blueprints equivalents - are literally the same
  `giverClass` registered twice under different WorkTypeDefs, so whichever
  work priority the player has higher governs it in vanilla. Since this
  mod includes both Construction and Hauling categories, both got offered
  as separate, functionally-identical `*` options. `EligibleDefs` now
  de-dupes by `giverClass` (not `equivalenceGroup` - that field also
  covers `ConstructFinishFrames`, which is a genuinely different action
  with its own giverClass and would have been wrongly hidden by grouping
  on that instead).
- **`* construct placed frames` (finish frame) not appearing alongside
  the deliver-resources options is expected, not a bug**, if the frame
  isn't fully resourced yet - `WorkGiver_ConstructFinishFrames` only has
  a job once all materials are delivered, same as vanilla. It should show
  up once resourcing is complete (from a `* deliver resources` sweep or
  otherwise) on a later right-click at that same spot.
- **Fixed: Sow/Harvest still didn't work on ordinary crop cells** even
  after the "Allow Sow" explanation - turned out to actually be a real
  bug, not user setup. Every `HasJobOnThing`/`JobOnThing`/`HasJobOnCell`/
  `JobOnCell` call in this mod (`FloatMenuPatch`, `TaskScanner`,
  `SweepManager`, `JobTrackerPatch`) was using the default `forced:
  false`. Vanilla's own float-menu building passes `forced: true` for
  manually-issued player orders, and the decompiled
  `WorkGiver_GrowerSow.JobOnCell` threads `forced` straight into its
  reservation check (`pawn.CanReserve(..., forced)` → maps to
  `ignoreOtherReservations`) - with `forced: false` a reservation
  conflict that a real manual order would bypass could silently fail us.
  All call sites now pass `forced: true`.
- **Added: `* <label> until done`** on every WorkGiverDef-based sweep
  option label, per request - not applied to `* Consume`, since that's a
  one-shot action per pawn, not a loop.
- Confirmed already correct, no change needed: ineligible pawns (wrong/
  disabled work type) are already excluded per-task via
  `PawnValidator.CanSweep` for every WorkGiverDef-based sweep, Growing
  and PlantCutting included - same mechanism as everything else.
- **Behavior change, by explicit request: sweeps now resume after a need
  interrupt instead of ending.** Original design (see section 2 history)
  deliberately did NOT auto-resume, to avoid interrupt loops. Reversed:
  `NeedMonitor` now calls `SweepManager.PauseForNeed` instead of
  `RemoveSweep` - the sweep order stays alive, the pawn's forced job ends
  so vanilla AI sends them to eat/sleep/recreate, and once *that* job
  ends on its own `Notify_JobEnded` sees the pawn was paused and resumes
  them (next pool target, or the same workstation for a `WorkGiver_DoBill`
  order - `SweepOrder` now carries a `WorkstationTarget` Thing so a
  paused cooking/crafting pawn can be sent back to the same station, not
  just area sweeps). No repeated-pause spam: `NeedMonitor` skips pawns
  already marked paused rather than re-triggering every 60 ticks while
  they sleep it off. Interrupt-loop risk doesn't reappear here because
  resumption is gated on a real job-end event, not a repeated need check.
  **WRONG - corrected 2026-08-17, see the top of this section.** The
  job-end event can be the replacement job the interrupt itself started,
  so the pawn resumes without ever addressing the need and gets re-paused
  60 ticks later. Observed in a real log. Not yet fixed.
- **Fixed: right-clicking fire offered `* Sow crops` / `* Harvest crops`
  instead of putting the fire out.** Two things going on:
  - `GrowerHarvest` turned out to be `scanCells` too (not `scanThings` as
    assumed) - same shape as `GrowerSow`, and neither checks for fire on
    the cell, so a scorched-but-still-mature plant (or a cell already
    burnt bare) still passes `HasJobOnCell`.
  - `FightFires` is `directOrderable: false` in vanilla - firefighting is
    emergency/auto-taken by any available pawn regardless of orders,
    which is *why* there's no vanilla "put out fire" float menu option
    to begin with. So the fix isn't "add a fire-fighting sweep option" -
    it's bailing out of the whole click when there's an active `Fire`
    thing on the cell (`Build()` in `FloatMenuPatch`, checked right after
    `thingsHere` is built), since nothing this mod offers makes sense to
    send pawns into a burning tile for. Also added a general
    `directOrderable` check to `IsSweepEligible` - respecting that flag
    is more robust than denylisting every non-orderable def we happen to
    trip over (see the `CookFillHopper` bullet below for the ad-hoc
    version of that problem).
- **Fixed: right-clicking a plant already marked for harvest/cut/chop did
  nothing.** `PlantsCut` (cutting/chopping) is `workType PlantCutting` -
  a completely separate `WorkTypeDef` from `Growing`, and was missing
  from `SupportedWorkTypeDefNames` entirely. Added.
- **Empty farm cell doing nothing - revised.** Initially diagnosed as
  vanilla behaving correctly (zone `allowSow`, plant type, season - all
  real preconditions `WorkGiver_GrowerSow.JobOnCell` checks, confirmed
  against its decompiled source). That diagnosis was real but incomplete
  - it missed the `forced` bug above, which was *also* failing sow/harvest
  independently of zone setup, since `JobOnCell`'s reservation check
  depends on it. Both real issues, now both accounted for.
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
    feature added**: `* <verb> <item>` (e.g. `* Consume fine meal`,
    `* Smoke smokeleaf joint`) now appears when an ingestible thing is
    clicked,
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
  giver (low risk, documented assumption). The identical-giverClass
  duplicate-option case (deliver-resources under both Construction and
  Hauling) is fixed - see the dated bullet above.

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

**Need interrupts:** A tick-level check monitors hunger, recreation, sleep (`needThreshold`, default 5%) and mood (`moodThreshold`, its own separate default 10%). When any drops to threshold or below, the pawn's forced job is cleared and they path to satisfy that need. **Updated 2026-08-15, reversing the original design below:** they now DO return to the last-ordered work automatically once that need-driven job finishes on its own (`SweepManager.PauseForNeed` / section 3.2) - a cooking pawn who gets tired pauses, sleeps, and resumes cooking. Interrupt-loop risk (the original reason for not auto-resuming) doesn't reappear because resumption only fires on a genuine job-end event, not a repeated need poll - a pawn can't get stuck bouncing between "resume" and "immediately re-interrupt" every tick. **This last sentence is WRONG - corrected 2026-08-17, see section 0.** The bouncing does happen: a genuine job-end event is not the same as the need being satisfied, and a real log shows a pawn paused after every task of a sweep. **Fixed 2026-08-18:** a job end is now only the *trigger* to re-check; the pawn stays paused until the need is genuinely back above threshold plus a 5-point margin, and a sweep that stays paused past `MaxPauseTicks` (half an in-game day) ends rather than hanging. See section 0.

**Firefighting (added 2026-08-18):** right-clicking a `Fire` offers `* Fight fires until done` and nothing else - every other sweep type is suppressed on a burning tile, which is the pre-existing rule, but firefighting itself is now offered rather than the whole click being dropped. Vanilla's home-area restriction on firefighting is deliberately overridden for this explicit player order; drafted pawns are still excluded, as they are from every other sweep. Fire sweeps re-scan once when their pool empties, since fires spread. Details in section 0 and `FireCompat` in 3.2.

**Feedback when nothing is available (added 2026-08-18):** if a sweep-eligible WorkGiver plausibly applies to the clicked thing but has no job for it, a **disabled** `* ...` entry is added stating the reason vanilla itself gives (`JobFailReason`), e.g. hauling with no stockpile that accepts the item. Capped at three such entries, and only when a reason actually exists. If a sweep starts but the radius scan finds nothing, a rejected-input message says so.

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
        GrowerCompat.cs            # WorkGiver_Grower static-state + sow gates
        FireCompat.cs              # FightFires detection, home-area override
```

### 3.2 Component Descriptions

**FloatMenuPatch.cs** - Two Harmony postfixes, on `FloatMenuMakerMap.ChoicesAtFor(Vector3 clickPos, Pawn pawn, bool suppressAutoTakeableGoto)` and `FloatMenuMakerMap.ChoicesAtForMultiSelect(Vector3 clickPos, List<Pawn> pawns)`. There is no `GetOptions` method on this class in the real 1.5 DLL (verified by reflecting on `lib/Assembly-CSharp.dll`); those two are the real entry points, plus internal helpers `AddHumanlikeOrders`/`AddJobGiverWorkOrders` that build the base list. Both are patched (as nested classes `FloatMenuPatch.SingleSelect` / `FloatMenuPatch.MultiSelect`, both discovered by `PatchAll` since it enumerates nested types), so sweeps work with any selection size.

The postfix body is wrapped in a try/catch that logs and swallows: an exception escaping a float-menu postfix takes the whole right-click menu down for every other mod patching the same area (Achtung). The sweep-eligible `WorkGiverDef` list is computed once and cached rather than re-walking `DefDatabase` on every right-click.

Rather than cloning existing `FloatMenuOption` entries (they don't expose the `WorkGiverDef` that produced them, so there's nothing to key a sweep off), the postfix independently walks `DefDatabase<WorkGiverDef>.AllDefsListForReading`, filters to sweep-eligible defs (see below), converts `clickPos` to a cell, and for each eligible def checks whether any selected pawn has `HasJobOnThing` true against a thing at that cell - the same predicate vanilla itself uses to decide whether to show the normal option. If so, it appends one `* <label>` `FloatMenuOption` whose action calls `SweepManager.BeginSweep(eligiblePawns, target, workGiverDef)`.

Sweep-eligible WorkGiverDefs: any whose `Worker is WorkGiver_DoBill` (covers all workstation/bill types without hardcoding each one), plus any whose `workType.defName` is `Hauling`, `Construction`, `Cleaning`, `Mining`, or `Growing` - minus a small `ExcludedDefNames` denylist (currently just `CookFillHopper`, see the status-section bullet above) for specific defs that technically match but produce confusing options nobody wants. Target detection (`FindTargetWithJob`) branches on `def.scanCells` vs `def.scanThings`: cell-scanned defs (`GrowerSow`) check `HasJobOnCell` against the clicked cell directly, so they work even when nothing is on that cell - which is the normal case for an empty tile waiting to be sown.

**Consume (added 2026-08-15):** Separate from all of the above - eating and drug use aren't `WorkGiverDef`-based in RimWorld at all (`JobDefOf.Ingest` instead), so `AddConsumeOption` in the same file handles it independently of `eligibleDefs`/`SweepManager` entirely. If any `Thing` at the clicked cell has `def.ingestible != null && def.ingestible.showIngestFloatOption` (the same flag vanilla itself uses to decide whether to offer an eat/smoke/snort option), and at least one selected pawn is alive/not downed/not in a mental state and `FoodUtility.WillEat` says yes, a `* <ingestCommandString>` option appears (e.g. `* Smoke smokeleaf joint`, `* Snort yayo` - `ingestCommandString` is the same per-ThingDef format vanilla uses, so wording matches). **Corrected 2026-08-17:** only *drugs* set `ingestCommandString` in Core - it defaults to empty, verified by reflection on `RimWorld.IngestibleProperties`. Plain food and corpses therefore fall through to the hardcoded `"Consume " + LabelShort` fallback and read `* Consume fine meal`, not `* Eat meal`. That still matches vanilla's own English wording, which uses the `ConsumeThing` key for the same fallback. Choosing it fires one `JobDefOf.Ingest` job per eligible pawn immediately, sized via the vanilla `FoodUtility.WillIngestStackCountOf` helper (same one the base game's single-pawn "Eat X" order uses) - not tracked as a sweep, since there's nothing to interrupt or chain: it's a one-shot order per pawn, same as manually right-clicking for each of them individually.

Deliberately does **not** exclude drafted pawns (unlike the WorkGiver-based sweeps) - you can manually order a drafted pawn to eat or take a combat drug in vanilla, and dosing a raiding party before a fight is a real use case. Also does not gate on hunger level - a manual order works regardless of current need, matching vanilla's manual-order semantics. If the stack doesn't have enough for everyone, later pawns in the loop may fail to get their dose once the stack runs empty from under them - not handled specially, since vanilla's own job system already has to tolerate pawns racing for the same food and fails harmlessly rather than crashing.

`CanConsume` branches on `thing.def.ingestible.drugCategory` (fixed 2026-08-15): non-drug food goes through `FoodUtility.WillEat` as before, but anything with a real drug category skips `WillEat` entirely (it's a food-appetite check and rejected every drug outright, which was the original bug - no `* Consume` was ever appearing for drugs) and instead only excludes Teetotalers (`pawn.story.traits.HasTrait(TraitDefOf.DrugDesire, -1)`), per explicit user request.

**SweepManager.cs** - A `MapComponent` maintaining `Dictionary<Pawn, SweepOrder>`. `SweepOrder` holds a `WorkGiverDef` and a `SharedPool` (`List<LocalTargetInfo>`) - for area sweeps, every pawn assigned in the same `BeginSweep` call shares the same pool instance, so claiming a target for one pawn removes it for the rest of the group (implements "nearest unassigned task first" via a linear nearest-in-pool scan per assignment). Workstation orders carry an empty pool since bill continuation doesn't use it (see JobTrackerPatch below). `LocalTargetInfo` transparently covers both Thing and cell targets, so the pool/nearest-scan logic didn't need to change to support `GrowerSow` - only the two spots that branch on target type explicitly did: `AssignNextTask` calls `scanner.JobOnCell` instead of `JobOnThing` when `!target.HasThing`, and `TargetStillValid` checks cell bounds/area/reservation instead of Thing-specific checks (Destroyed, forbidden) for the same case.

Job-to-job chaining is **event-driven**, not tick-polled: `JobTrackerPatch`'s postfix on `Pawn_JobTracker.EndCurrentJob` calls `SweepManager.Notify_JobEnded(pawn, condition)` when a swept pawn's job ends, and that pulls the next target off the shared pool. A non-`Succeeded` condition does not necessarily end the sweep - see `TargetFailureIsRecoverable` and the 2026-08-16 status entry for how target-scoped failures are separated from pawn-scoped interrupts, and for the per-pawn `MaxConsecutiveFailures` bound that keeps the retry path from recursing.

Because `TryTakeOrderedJob` interrupts the pawn's current job, it re-enters `EndCurrentJob` (verified in IL: `TryTakeOrderedJob` -> `StartJob` -> `EndCurrentJob`), so every job handout the mod makes fires `JobTrackerPatch`'s own postfix with `InterruptForced` - which used to cancel the sweep that was mid-handout. All handouts now go through `SweepManager.GiveJob`, which raises a static `SweepManager.AssigningJob` flag that `JobTrackerPatch` checks and ignores. `MapComponentTick()` runs every 60 ticks and only checks for state changes nothing else observes - dead/downed/mental-break/drafted/off-map pawns get pulled from `activeSweeps`.

Workstation pawn selection (`PickBestWorkstationPawn`) ranks by skill level in the WorkGiverDef's primary relevant skill, then `StatDefOf.WorkSpeedGlobal`, then `StatDefOf.MoveSpeed`. The doc's original idea of tiebreaking on the specific per-trade stat (`SmithingSpeed` etc.) was dropped for v1 - there's no generic way to resolve "the specific stat for this WorkTypeDef" from the def alone, so `WorkSpeedGlobal` stands in. Revisit if it causes visibly wrong pawn picks in testing.

No `ExposeData()` on `SweepManager` - sweeps are cleared on load, matching the "simpler, recommended for v1" option in section 4.

**NeedMonitor.cs** - A `GameComponent` that runs a tick check (every 60 ticks for performance) on all pawns with active sweeps. If hunger, recreation, or sleep is at or below 5% (`need.CurLevelPercentage <= 0.05f`), it clears the pawn's sweep from `SweepManager` and ends their current forced job so the AI takes over for need satisfaction.

**TaskScanner.cs** - Static utility. Given a cell, radius, map, `WorkGiverDef`, and a driving pawn, returns a `List<LocalTargetInfo>` of matching incomplete tasks, via two independent branches (a def can be either or both):

- `ScanThings` (for `scanThings` defs): `scanner.PotentialWorkThingsGlobal(forPawn)` filtered by squared-distance-from-center, forbidden, allowed area, `CanReserve`, and `HasJobOnThing`. `PotentialWorkThingsGlobal` returns **null** on `WorkGiver_Scanner` itself and most WorkGivers never override it (construction and `WorkGiver_DoBill` included), so this falls back to `map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest)`, guarding against an undefined `ThingRequest` (which `ThingsMatching` throws on) - mirrors what vanilla's `JobGiver_Work` does.
- `ScanCells` (for `scanCells` defs, added 2026-08-15 to fix the missing-Sow bug): `GenRadial.RadialCellsAround(center, radius, true)` - the `GenRadial` approach originally planned for everything, kept for just this branch since there's no thing-lister equivalent for "empty cells that could be sown." Filtered by bounds, allowed area, `CanReserve`, `GrowerCompat` sow gates, reachability, and `HasJobOnCell`.

Both branches check reachability (`pawn.CanReach` with the scanner's own `PathEndMode`, at `Danger.Deadly` since these are manually-issued player orders). This is not redundant with vanilla: `WorkGiver_Grower.AllowUnreachable` is `true`, meaning the framework deliberately skips its usual reachability filter and expects the WorkGiver's own `Potential*Global` to do it - which this mod never calls. Without the check, an unreachable target yields a job that fails on pathing, and that non-`Succeeded` end kills the entire sweep via `RemoveSweep`.

Filters out tasks already claimed by another pawn via the normal reservation system (no sweep-specific claim tracking needed).

**GrowerCompat.cs** (added 2026-08-16) - Static utility isolating everything this mod has to do by hand because it calls `JobOn*` directly instead of going through `PotentialWorkCellsGlobal`. See the 2026-08-16 status entry for the full reasoning. Three responsibilities:

- `ResetWantedPlantDef(scanner)` - nulls `WorkGiver_Grower.wantedPlantDef` (a shared mutable static, reached via a cached Harmony `StaticFieldRefAccess` delegate rather than per-call `FieldInfo.SetValue`, since this runs once per cell in a radial scan). No-op for non-Grower scanners. **Must be called immediately before every `HasJobOnCell`/`JobOnCell` on a Grower scanner** - it is what makes vanilla's lazy `if (wantedPlantDef == null)` recompute per cell, which in turn is what restores both the correct `plantDefToSow` and the zone-membership gate.
- `SowSettingsAllow(cell, map)` - the `ExtraRequirements` gates that resetting the static does *not* restore: `IPlantToGrowSettable.CanAcceptSowNow()` and `Zone_Growing.allowSow`. Applied to `WorkGiver_GrowerSow` only; `GrowerHarvest` has no `ExtraRequirements` override to mirror.
- `IsPreparatoryJob(job, target)` - true when a WorkGiver returned a blocker-clearing job (`CutPlant`, `HaulAside`) rather than the work asked for, detected by the job's `targetA` differing from the target queried. Used by `SweepManager` to re-queue that target once instead of dropping it.

**FireCompat.cs** (added 2026-08-18) - Static utility, same shape and
purpose as `GrowerCompat`: the things we have to do by hand because we
call `HasJobOnThing` directly, plus the one vanilla check we deliberately
drop. Three responsibilities:

- `IsFirefighting(def)` - `workType.defName == "Firefighter"`, not a
  `FightFires` defName match, so modded firefighting WorkGivers get the
  same handling. This is the flag the exemptions in `FloatMenuPatch`
  (`directOrderable`, the burning-cell bail-out) and `TaskScanner` /
  `SweepManager` (`TargetIsBurning`) all key off.
- `HasFireJob(pawn, thing, forced)` - stands in for
  `WorkGiver_FightFires.HasJobOnThing`, keeping its pawn-attached-fire
  rules, `WorkTags.Firefighting`, the past-15-tiles reservation check and
  reachability, and **dropping the home-area gate** per the user's
  explicit decision (section 0). Note the worker class is `internal`, so
  neither the type nor its `public static FireIsBeingHandled` can be
  referenced from this assembly - the latter is re-implemented here
  (`FirstRespectedReserver` within 5 tiles), which is also what stops a
  whole group piling onto one fire.
- `JobOnThing` is **not** wrapped: its entire vanilla body is
  `return new Job(JobDefOf.BeatFire, t)`, and we hold the scanner as a
  `WorkGiver_Scanner`, so the normal virtual call already does the right
  thing.

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
- **Drafted pawns in selection:** If any selected pawns are drafted, exclude them from sweep assignment. Do not undraft them automatically. **Open question as of 2026-08-18:** firefighting is the one case where vanilla disagrees - `FightFires` is `canBeDoneWhileDrafted: true` with `autoTakeablePriorityDrafted: 20`, and a fire during a raid is exactly when the player has everyone drafted. Currently still excluded, for consistency; changing it means a per-WorkGiver exception in both `PawnValidator.CanSweep` and `SweepManager.MapComponentTick`. Awaiting a decision.
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

### 5.4 Phase 5 - Idle Pawn Nudge ("Standing" Rule) - PLANNED, NOT IMPLEMENTED

Architected 2026-08-15 per explicit request. **Not built yet** - this
section is planning only, no code exists for it.

**Relevant to the 2026-08-18 standing-still report (see section 0):**
this feature is the direct countermeasure for "colonists stand around
doing nothing", so it is going to look like the obvious fix. It is not,
until the cause is known. If pawns are idle because a modded WorkGiver
throws inside `TryFindAndStartJob`, ending their wait job just re-runs
the same throwing think tree every two seconds - the nudge would hide a
diagnosable bug behind a busy-looking colony. Diagnose first, then
decide whether this is still worth building.

**Intent:** a periodic, low-frequency check that catches colonists who
are standing idle (no job, or parked in a wander/wait job) despite
available work existing, and nudges them to reconsider - without
picking or assigning a specific job ourselves. This is the counterpart,
for un-swept colonists going idle, to what `SweepManager`/`TaskScanner`
already do for pawns actively working a sweep.

**Shares its scan mechanism (same shape, independent implementation) with
Do Not Freak Out** - a new, separate mod also being architected in this
pass, see `DoNotFreakOut_Architecture.md`. The two are fully decoupled -
no code or project dependency between them, each mod runs its own
scanner.

**Scan mechanism** (a new `GameComponent`, tentatively `IdleScanner.cs`
under `Components/`):
- Interval: every 120 ticks (2 real-world seconds at normal 1x game
  speed). Deliberately tick-based rather than real-time-based, matching
  every other periodic check already in this mod (`NeedMonitor`,
  `SweepManager.MapComponentTick`) - at higher game speeds this scans
  proportionally more often in real time, same as vanilla's own
  tick-scaled behaviors. Flagged as a choice, not a certainty - revisit
  if faster-than-real-time scanning at 3x speed turns out to feel too
  aggressive.
- Scope: one pawn total per interval, globally across all player-owned
  spawned maps (not one pawn per map) - a single rotating index over an
  alphabetically-sorted (`LabelShort`) list of all free colonists across
  `Find.Maps`, rebuilt fresh each interval (list membership changes as
  pawns die/join/despawn; rebuilding avoids stale-index bugs from a
  cached list). Index wraps around via modulo once it reaches the end.
- Camera: never touched. No `CameraJumper`, no `Find.Selector.Select`,
  no `Find.CameraDriver` calls anywhere in this component - the "do not
  move the view" requirement is enforced by simply never calling
  anything that could move it, not by suppressing some other API's side
  effect.
- Skip condition: `pawn.jobs?.curJob?.playerForced == true` - a generic,
  vanilla-level flag (confirmed present on `Verse.AI.Job` via reflection
  on the actual game DLL) meaning "this pawn has an active player-forced
  job," true for `SweepManager`-issued jobs and any other forced order
  regardless of source. Chosen specifically so this doesn't need to
  know about `SweepManager`'s internal state, or Do Not Freak Out's -
  fully decoupled.

**Rule-specific check and action** (this part is unique to Do Not Be
Lazy; Do Not Freak Out's is different - see its own doc):
- "Standing" detection: `pawn.jobs?.curJob == null`, or `curJob.def` is
  one of the idle-family JobDefs (`JobDefOf.Wait`, `Wait_Wander`,
  `GotoWander`, `Wait_MaintainPosture` - all confirmed present via
  reflection). Exact JobDef set to finalize during implementation; these
  four are the reflection-confirmed starting candidates.
- Action, deliberately minimal: `pawn.jobs.EndCurrentJob(JobCondition.InterruptForced)`
  (same mechanism `NeedMonitor` already uses). This does **not** hand-pick
  a job - it just ends whatever idle/wait state the pawn is in, which
  (per `EndCurrentJob`'s `startNewJob: true` default) triggers the
  pawn's own think tree to re-evaluate immediately. If real, available
  work exists that the AI simply hadn't picked up yet, this is enough to
  surface it. If no work is actually available, the pawn ends up back in
  an idle/wait job anyway - harmless, not a loop risk, since we only
  re-nudge on the next scheduled 2-second turn in the rotation, not
  every tick.

**Settings (new, additive to `DoNotBeLazySettings`):**
- `idleNudgeEnabled` - bool, default true - master on/off toggle,
  separate from the existing sweep settings since this is a distinct
  feature a player might not want.

**Open questions to resolve before implementation, not before:**
- Should drafted pawns be included in the alphabetical rotation? Current
  lean: no, exclude them (mirrors `PawnValidator.CanSweep`'s existing
  drafted-pawn exclusion) - a standing drafted pawn is very likely
  intentional (holding a position), not neglect.
- Exact idle-JobDef set may need adjusting once tested in-game -
  vanilla's idle/wander job family isn't perfectly documented and the
  four listed are a reasonable starting guess, not a verified-complete
  list.

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
