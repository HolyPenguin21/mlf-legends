# MLF Legends — Stage 01 ECS performance baseline (Issue #7)

**What this is:** the measured overhead of the Stage 01 ECS foundation — the fixed-step clock, the command
processor and the battle lifecycle — with 2, 100 and 400 light combatants that exist at the same time.
**What this is not:** a combat performance figure. Stage 01 has no movement, collisions, attacks, AI, physics or
character rendering; nothing here predicts the cost of a real 200 vs 200 battle or a frame rate.

## Stage 02.1 measurement boundary (#31, 2026-10-26)

All measured figures below are **historical Stage 01**, before the motion system (and before PR #28/#29).
Stage 02.1 movement costs at **2 / 100 / 400: NOT RUN**; no new CPU, allocation or Burst execution claim.
The existing harness now includes the same `CombatMovementSystem` as the default world/replay, and asserts
`CombatMotion` and every other required component are present in the common unmanaged actor archetype.
Both timing and memory harnesses verify the State motion owner outside measured regions. Current cmd loads exercise horizontal
movement; they do not benchmark jumping. New report headers include runtime scope and motion component
size; outputs use `stage02-31-...` so runs cannot overwrite or be mistaken for Stage 01 results.

Stage 02.2 (#33) adds `CombatFacingSystem` (one linear pass reading `AppliedCombatCommand`, writing
`CombatFacing`) to the same runtime list. Both harnesses now verify, outside measured regions, exactly one
facing and one movement owner in State with facing first. Report scope says Stage 02.2 and outputs use
`stage02-33-...`. Stage 02.2 costs at **2 / 100 / 400: NOT RUN**.

## Stage 02.2 + #35 measurement (2026-10-26, measured)

Commit `f912f37` (branch `fix/35-lifecycle-proxy`: Stage 02.1 movement, Stage 02.2 facing, #35 Initialization proxy),
same machine as the historical baseline (Ryzen 5 3600, 32 GB, Windows 11 10.0.26100), Unity 6000.5.4f1 Editor
batchmode `-nographics`, EditMode `-testCategory Performance` alone (6/6 passed per mode), Entities/Collections 6.5.0,
Burst 1.8.29, safety checks + collections checks + job debugger on. Burst on: probe job executed Burst code; off:
`--burst-disable-compilation`. The owner's Unity Editor was open on another project copy during the run (background
load not controlled). The benchmark does not update Initialization, so the proxy is not in these numbers; requests are
applied by updating the lifecycle handle directly, outside the measured ticks.

Mean µs per tick (per request for start/end/restart), repetitions 1 / 2 / 3; last column N = 400 highest p95 per sample.
GC.Alloc was **0** in every series, Burst on and off.

| Burst on | N = 2 | N = 100 | N = 400 | N = 400 p95 | Stage 01 N = 400 |
|---|---|---|---|---|---|
| start | 21.6 / 21.4 / 21.8 | 70.7 / 72.2 / 70.6 | 221.5 / 221.7 / 218.9 | 251.7 | 197.8–205.8 |
| first tick | 26.5 / 26.5 / 26.7 | 30.2 / 36.6 / 29.8 | 40.4 / 47.7 / 40.4 | 95.4 | 29.4–40.9 |
| end | 18.0 / 18.0 / 17.9 | 19.7 / 22.0 / 20.0 | 22.6 / 24.1 / 22.9 | 37.0 | 21.2–30.0 |
| restart | 20.6 / 21.0 / 20.8 | 67.8 / 68.9 / 71.6 | 214.9 / 215.7 / 219.9 | 257.8 | 190.9–191.6 |
| idle, 1 tick/frame | 14.5 / 15.3 / 14.3 | 16.1 / 16.0 / 16.0 | 21.4 / 21.8 / 21.3 | 23.2 | 16.5–16.7 |
| idle, 10 ticks/frame | 8.9 / 8.9 / 8.9 | 10.6 / 11.4 / 10.6 | 15.9 / 15.9 / 15.9 | 163.8 (frame) | 11.6 |
| cmd, 1 tick/frame — runtime | 15.0 / 14.9 / 14.9 | 24.1 / 24.1 / 24.8 | 51.1 / 51.5 / 51.1 | 53.9 | 45.8–46.3 |
| cmd, 4 ticks/frame — runtime | 10.4 / 10.5 / 10.5 | 19.8 / 19.5 / 19.6 | 47.7 / 46.8 / 47.0 | 212.1 (frame) | 41.7–41.9 |
| cmd, 10 ticks/frame — runtime | 9.4 / 9.4 / 9.5 | 18.9 / 18.5 / 18.5 | 45.5 / 45.6 / 45.7 | 478.1 (frame) | 41.0–41.1 |
| cmd-lead32, 1 tick/frame — runtime | 17.2 / 17.1 / 17.3 | 176.0 / 170.8 / 176.4 | 727.7 / 720.7 / 729.7 | 761.6 | 725.1–726.1 |
| cmd-lead32, 10 ticks/frame — runtime | 11.5 / 11.6 / 11.5 | 171.8 / 172.0 / 164.4 | 720.1 / 735.6 / 715.1 | 7653.0 (frame) | 717.8–746.8 |

| Burst off | N = 2 | N = 100 | N = 400 | Stage 01 N = 400 |
|---|---|---|---|---|
| start | 48.2 / 47.8 / 48.4 | 241.3 / 239.6 / 228.9 | 776.9 / 782.7 / 779.6 | 681.1–682.5 |
| restart | 48.7 / 49.3 / 49.7 | 230.9 / 227.9 / 228.9 | 771.5 / 774.9 / 778.4 | 671.5–677.3 |
| idle, 1 tick/frame | 23.0 / 22.9 / 23.4 | 38.2 / 38.8 / 38.5 | 84.6 / 84.6 / 84.8 | 44.8–45.1 |
| cmd, 1 tick/frame — runtime | 22.2 / 22.3 / 22.3 | 68.0 / 67.9 / 68.1 | 223.0 / 223.1 / 224.0 | 183.7–184.7 |
| cmd-lead32, 1 tick/frame — runtime | 28.9 / 28.7 / 29.3 | 488.3 / 488.0 / 496.6 | 2130.8 / 2117.5 / 2130.6 | 2108.5–2119.0 |

Memory: combatant chunks 1 / 1 / 4 (16 / 16 / 64 KiB), queue buffer high-water 312 KB at N = 400, unchanged.
Native growth per frame (cmd k1/k10, lead32 k1/**k18**, 100 frames × 3 reps): median and p95 0 B, with isolated 64 KiB steps.
Manager inspection of the raw TSVs corrects the previous claim of zero drift in every series: dedicated N = 400 memory-series
nativeDeltaBytes are -576 B (Burst on, cmd k10, rep 2) and -27,546 B (Burst off, lead32 k18, rep 3); other dedicated series
end at 0 B. Timing reports also contain small nonzero process-memory deltas and a +135,152 B Burst-off cmd k1 rep 3 sample.
These counter changes are not an allocation ownership trace or proof of a persistent leak. GC.Alloc samples are 0 in all reports.
Separately, both cold-import EditMode Editor logs report four Persistent allocations leaked on shutdown; source UNKNOWN,
tracked in #30. Zero benchmark growth must not be used to dismiss that shutdown warning. Initialization and the full Unity
frame are outside this harness and remain unmeasured.

Reading (measured, not optimised): with Burst the added State pass (facing + movement over N actors, plus two system
dispatches) costs ≈ +2 µs/tick at N = 2 and ≈ +5 µs/tick at N = 400 (idle and cmd alike); spawn/restart ≈ +23 µs at
N = 400 (`CombatMotion` initialisation). The full pending store still dominates (≈ 728 µs at N = 400, #25). Without
Burst the managed movement/facing loops add ≈ 40 µs/tick at N = 400. No optimisation is proposed from these numbers.

## How to run

The tests are in `Assets/Tests/EditMode/Performance/` (category `Performance`) and also run as part of the normal
EditMode suite (`Tools/Run-UnityTests.ps1`, about +60 s). For a baseline, run them alone with the Editor closed:

```powershell
$u = 'C:\Path\To\6000.5.4f1\Editor\Unity.exe'
# Burst on (Editor default)
& $u -batchmode -nographics -projectPath <repo> -runTests -testPlatform EditMode -testCategory Performance `
     -testResults <repo>\TestResults\perf-on.xml -logFile <repo>\TestResults\perf-on.log
# Burst off
& $u -batchmode -nographics -projectPath <repo> --burst-disable-compilation -runTests -testPlatform EditMode `
     -testCategory Performance -testResults <repo>\TestResults\perf-off.xml -logFile <repo>\TestResults\perf-off.log
```

Current tests write `TestResults/performance/stage02-33-<burst-on|burst-off>[-memory]-n<N>.tsv` (Stage 02.1 used `stage02-31-...`, historical Stage 01 `stage01-7-...`) (ignored by Git): an
environment header (CPU, RAM, OS, Unity/Entities/Collections/Burst versions, Burst state proven by a probe job,
safety checks, job debugger, managed code optimization) and one row per scenario and repetition with sample count,
mean / median / p95 / min / max (µs, or bytes for memory rows), mean per tick, GC.Alloc count and native growth.
The tests assert correctness only, never timings, so they do not fail on a slower machine. Use a project path
without non-ASCII characters (Issue #17).

## How combatants are created

Only through the real `BattleLifecycleSystem` (Issue #7 roster contract, `Bootstrap/BOOTSTRAP.md`, "Rosters"):
`BattleSetup.HumanAllies = AiAllies = N/2 - 1`, so N = 2 is the unchanged default 1v1 duel and N = 400 is 200 vs 200.
One `CombatantIdAllocator.TryReserve(N)`, one private `Spawn`, one archetype (`CombatantId`, `CombatTeam`,
`CombatPosition`, `CombatFacing`, `InCombat`, then `AppliedCombatCommand` from the processor). No benchmark
factory, no IDs written by the test, no GameObject, MonoBehaviour, Rigidbody, Animator or NavMeshAgent per
combatant (asserted).

## Method

- **World:** one isolated `World` per roster size, built from `ReplayWorld.RuntimeSystems` with
  `DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups`: the default world's
  `FixedStepSimulationSystemGroup` (begin/end ECB systems, lifecycle, clock, collection/state/publication phases,
  command processor) ordered by the systems' own attributes. One clock, one queue, one session (asserted).
- **Test systems:** a producer in the command-collection phase (managed `SystemBase`) that appends the load to the
  single `CombatCommandSubmission` buffer and **times itself**; a publication-phase memory probe, enabled only in
  the memory test.
- **Timed region:** `Stopwatch` around exactly one runtime call — `FixedStepSimulationSystemGroup.Update()` for a
  render frame, or the lifecycle's own update for a battle request. `World.SetTime`, `BattleRequest.Submit`,
  correctness checks, statistics and reports are outside it. No strings, LINQ, logs or replay recording inside.
- **Frames:** every simulated render frame (1, 4 or 10 fixed ticks, half a tick past the last one, as in the clock
  tests) is followed by a real editor frame (`yield return null`, untimed), so main-thread Temp memory is released
  at frame ends as in the game.
- **Repetitions:** per roster size one warm-up pass (JIT, Burst, chunk and buffer growth; not recorded), then 3
  measured passes in the same World. Per pass: 10× Start → first tick → End, 10× Restart of a running battle, then
  1000 measured ticks for each load and pacing, each in a fresh battle after 40 warm-in ticks. `GC.Collect()` before
  each series (untimed). Identical deterministic inputs in every pass; Stage 01 has no RNG or seed consumer.
- **Loads:** `idle` = no command; `cmd` = one command per combatant per tick for the current tick; `cmd-lead32` =
  the same plus an equal command 32 ticks ahead (pending store full: N × 32, 12 800 at N = 400; each due pending
  command meets its current twin as `Duplicate`). Asserted after every series: exactly 1000 ticks ran, every
  combatant got its command of the last tick, N accepted, no stale/unknown/rejected outcome, pending = N × 32 or 0.
- **Rows:** a load's full frame, `:runtime` (frame minus the producer's self-time) and `:producer` (the test's
  submission cost). "Per tick" for 4/10-tick frames is the frame time divided by the ticks.
- **GC:** count of `GC.Alloc` samples on the main thread (the method of Unity's `Is.Not.AllocatingGCMemory`).
  Bytes are **not** measured: `GC.GetAllocatedBytesForCurrentThread()` returns 0 in the Editor's Mono (probed).
- **Native:** `Profiler.GetTotalAllocatedMemoryLong()` (process-wide) per frame — at the peak after any tick of the
  frame (probe) and at its end — plus chunk count × 16 KiB and the queue buffers' capacity.

## Environment

| | |
|---|---|
| Date | 2026-10-25, UTC 15:59–16:00 (Burst on) and 16:00–16:01 (Burst off) |
| CPU | AMD Ryzen 5 3600, 6 cores / 12 threads, 3.6 GHz base; Windows power plan "AMD Ryzen High Performance" |
| RAM | 32 GB (2 × 16 GB DDR4-2666) |
| GPU | NVIDIA GeForce RTX 2060 SUPER (driver 32.0.16.1664) — unused: `-nographics`, nothing is rendered |
| OS | Windows 11 Pro 10.0.26100 |
| Unity | 6000.5.4f1, **Editor**, batchmode, EditMode test runner (no player build measured) |
| Packages | Entities 6.5.0, Collections 6.5.0, Burst 1.8.29 |
| Burst | on: compilation enabled, probe job ran Burst code; off: `--burst-disable-compilation`, probe job ran managed code |
| Checks | Burst safety checks on, `ENABLE_UNITY_COLLECTIONS_CHECKS` on, job debugger on (Editor defaults) |
| Managed code | Release optimization; `Stopwatch` 10 MHz, high resolution |
| Commit | `c172450` on branch `feature/stage01-7` (runtime and harness as in the PR) |

## Results — Burst on

Mean µs **per tick** (per request for start/end/restart), repetitions 1 / 2 / 3. The last column is N = 400 per
sample (per frame for 4/10-tick frames): median of the 3 repetition medians / highest p95. GC.Alloc was **0** in
every series (all N, all repetitions).

| Scenario | N = 2 | N = 100 | N = 400 | N = 400 per sample, median / p95 |
|---|---|---|---|---|
| start (queue reset + spawn N) | 21.9 / 22.2 / 22.0 | 67.4 / 65.4 / 65.5 | 197.8 / 198.4 / 205.8 | 195.0 / 240.2 |
| first tick (adds `AppliedCombatCommand` to N) | 21.8 / 21.9 / 22.4 | 23.7 / 24.1 / 24.0 | 29.7 / 29.4 / 40.9 | 27.3 / 65.9 |
| end (destroy N + queue reset) | 17.9 / 18.1 / 17.9 | 19.2 / 19.1 / 19.4 | 21.2 / 21.4 / 30.0 | 21.0 / 47.1 |
| restart (destroy N + reset + spawn N) | 20.4 / 20.7 / 20.5 | 62.2 / 62.2 / 62.2 | 190.9 / 191.2 / 191.6 | 190.6 / 195.6 |
| idle, 1 tick/frame | 12.7 / 12.6 / 12.6 | 13.6 / 13.6 / 13.6 | 16.5 / 16.5 / 16.7 | 16.3 / 17.8 |
| idle, 4 ticks/frame | 8.7 / 8.7 / 8.6 | 9.6 / 9.6 / 9.6 | 12.4 / 12.4 / 12.5 | 49.4 / 52.9 |
| idle, 10 ticks/frame | 7.8 / 7.8 / 7.8 | 8.9 / 8.8 / 8.8 | 11.6 / 11.6 / 11.6 | 114.8 / 119.8 |
| cmd, 1 tick/frame — runtime | 13.0 / 13.0 / 13.1 | 21.4 / 21.5 / 22.0 | 45.8 / 46.0 / 46.3 | 45.6 / 49.2 |
| cmd, 4 ticks/frame — runtime | 9.1 / 9.1 / 9.1 | 17.3 / 17.3 / 17.4 | 41.9 / 41.8 / 41.7 | 166.1 / 171.2 |
| cmd, 10 ticks/frame — runtime | 8.2 / 8.2 / 8.2 | 16.5 / 16.4 / 16.5 | 41.0 / 41.1 / 41.0 | 407.7 / 431.7 |
| cmd-lead32, 1 tick/frame — runtime | 15.4 / 15.3 / 15.5 | 169.6 / 168.5 / 173.1 | 725.5 / 725.1 / 726.1 | 736.1 / 756.0 |
| cmd-lead32, 10 ticks/frame — runtime | 10.2 / 10.3 / 10.1 | 166.3 / 169.9 / 161.7 | 720.6 / 746.8 / 717.8 | 7355.3 / 7784.6 |
| *test producer, cmd, 1 tick/frame (harness, not runtime)* | 3.8 / 3.7 / 3.8 | 9.7 / 9.6 / 9.8 | 27.6 / 27.6 / 27.8 | 27.5 / 28.5 |
| *test producer, cmd-lead32 (harness, not runtime)* | 3.9 / 3.9 / 4.0 | 15.5 / 15.5 / 15.6 | 51.3 / 51.5 / 51.3 | 51.0 / 53.7 |

Full frames (runtime + producer) are in the TSV files, e.g. cmd at 400, 1 tick/frame: 73.4 / 73.6 / 74.1 µs.

Derived (Burst on, from the means above; not separately measured):
- Command processing at 1 tick/frame: (cmd runtime − idle) / N ≈ **80 ns** (N = 100) and **74 ns** (N = 400) per command.
- Spawn: (start at 400 − start at 2) / 398 ≈ **0.45 µs per combatant**, including the queue reset.
- Fixed overhead of an empty tick (N = 2, mostly group/system dispatch and Editor safety): ≈ 12.6 µs at 1 tick per
  frame, ≈ 7.8 µs per tick at 10 ticks per frame.
- Full pending store: at N = 400 a tick costs ≈ **726 µs** instead of ≈ 46 µs with the same accepted commands, i.e.
  ≈ 53 ns per kept pending command per tick; a 10-tick catch-up frame then takes ≈ 7.4 ms (p95 7.8 ms).

## Results — Burst off (managed code)

Mean µs per tick (per request), repetitions 1 / 2 / 3; GC.Alloc **0** everywhere.

| Scenario | N = 2 | N = 100 | N = 400 |
|---|---|---|---|
| start (queue reset + spawn N) | 47.7 / 47.4 / 48.1 | 202.5 / 202.6 / 203.7 | 682.1 / 682.5 / 681.1 |
| first tick (adds `AppliedCombatCommand` to N) | 32.7 / 33.4 / 32.7 | 41.5 / 41.7 / 41.5 | 77.4 / 75.8 / 75.8 |
| end (destroy N + queue reset) | 41.3 / 41.5 / 42.9 | 43.2 / 43.0 / 42.7 | 49.5 / 48.6 / 49.3 |
| restart (destroy N + reset + spawn N) | 48.3 / 48.7 / 49.1 | 201.0 / 201.5 / 202.5 | 671.8 / 677.3 / 671.5 |
| idle, 1 tick/frame | 18.7 / 18.5 / 18.7 | 24.8 / 24.8 / 24.8 | 44.8 / 45.1 / 44.8 |
| idle, 4 ticks/frame | 13.2 / 13.1 / 13.2 | 19.3 / 19.3 / 19.4 | 39.2 / 39.5 / 39.6 |
| idle, 10 ticks/frame | 11.9 / 11.9 / 12.0 | 18.2 / 18.2 / 18.1 | 38.4 / 38.2 / 38.5 |
| cmd, 1 tick/frame — runtime | 18.2 / 18.2 / 18.4 | 55.0 / 54.8 / 54.8 | 184.7 / 183.7 / 184.4 |
| cmd, 4 ticks/frame — runtime | 13.5 / 13.5 / 13.6 | 50.3 / 50.1 / 50.4 | 178.9 / 179.6 / 179.2 |
| cmd, 10 ticks/frame — runtime | 12.5 / 12.5 / 12.5 | 48.9 / 48.9 / 49.2 | 178.0 / 178.4 / 177.7 |
| cmd-lead32, 1 tick/frame — runtime | 24.8 / 24.7 / 24.7 | 481.2 / 481.1 / 488.8 | 2108.5 / 2119.0 / 2112.8 |
| cmd-lead32, 10 ticks/frame — runtime | 18.6 / 18.7 / 19.5 | 476.9 / 483.2 / 470.4 | 2094.9 / 2095.3 / 2085.9 |
| *test producer, cmd, 1 tick/frame (harness, not runtime)* | 3.8 / 3.9 / 3.9 | 9.7 / 9.7 / 9.7 | 27.8 / 27.8 / 27.9 |
| *test producer, cmd-lead32 (harness, not runtime)* | 4.0 / 4.0 / 4.0 | 15.5 / 15.5 / 15.6 | 51.5 / 52.0 / 51.5 |

Burst makes the 400-combatant command tick ≈ 4.0× (184 → 46 µs), the full-pending tick ≈ 2.9× and the spawn ≈ 3.4× cheaper in this Editor configuration.

## Memory

| Measurement | N = 2 | N = 100 | N = 400 |
|---|---|---|---|
| GC.Alloc count, every timed series (start/end/restart/ticks, all loads and pacings, Burst on and off) | 0 | 0 | 0 |
| Combatant chunks (16 KiB each) | 1 (16 KiB) | 1 (16 KiB) | 4 (64 KiB) |
| Command queue buffers, high-water capacity (submissions + pending + results, after the lead32 warm-up) | 1.3 KB | 78 KB | 312 KB |
| Native growth per frame, peak after any tick and at frame end — cmd 1 and 10 ticks/frame, lead32 1 and **18** ticks/frame, 100 frames × 3 repetitions, Burst on and off | 0 B median and p95 in every series; max a single 64 KiB step in one frame of a series; drift over a 100-frame series ≤ 64 KiB | same | same |

The native counter is process-wide and grows only when Unity's allocators take new memory; reuse of
already-reserved Temp or chunk memory is invisible to it. "0 B" therefore means *no new native memory per frame and
no accumulation across frames*, not "no Temp scratch". The processor's per-tick Temp scratch (estimated in
`Commands/COMMANDS.md` at ≈ 0.5 MB at the full pending store) is **not observable** with public APIs and remains an
estimate. During development an earlier, synchronous version of this harness (all series inside one editor frame)
saw the process-wide counter grow by up to ~0.7 GB across 1000 lead32 ticks; the final harness, with editor frames,
does not reproduce it (also not over 100 lead32 ticks without a frame end), and the cause was not isolated.

## Findings from the measurement

1. **Fixed:** entity names filled Entities' process-wide name store. `Spawn` named every combatant `Combatant <id>`;
   the store keeps 16 384 unique names until a domain reload and IDs are never reused, so ~40 restarts of a 200 vs 200
   battle produced one Editor error (`Max unique Entity Name capacity exceeded`, a 0.25 s stall in the first run) and
   then no names at all. The initial fix named only the two leaders (`BOOTSTRAP.md`); historical regression test
   `RepeatedLargeRostersKeepNamingTheLeaders` (replaced by the bounded-name regression in #19; see follow-up below).
2. **Open, for the command processor owner (#4):** the cost of a full pending store dominates everything else
   measured: ≈ 726 µs per tick at 400 combatants × 32 kept commands, ≈ 7.4 ms per 10-tick catch-up frame (Burst on,
   Editor). The processor walks the whole pending buffer and rebuilds its key map every tick. Producers that submit
   up to 32 ticks ahead for every combatant would make this the worst case; one command per tick costs ≈ 46 µs.

## Limits of these numbers

- **Editor, not a build:** safety checks, the job debugger and Editor-only bookkeeping are on; a player build is
  expected to be cheaper but was **NOT RUN**. Batchmode with `-nographics`: no rendering, no GPU work.
- **One machine, one run per Burst mode.** Repetitions agree within a few percent; run-to-run variation on other
  days or machines is unmeasured. No CPU frequency lock; other processes were running (Unity Hub, an Editor with
  another project copy).
- **Light entities only:** no movement, collisions, spatial queries, attacks, AI, physics, animation, rendering or
  presentation sync. The figures bound the overhead of the Stage 01 foundation, not a battle.
- **Main thread only:** nothing in Stage 01 schedules jobs; worker-thread scaling is untested.
- **Producer:** a managed test system; its self-time is reported separately and subtracted for "runtime". Real
  input/AI producers (Stage 02+, Issue #22) will have their own cost.
- **GC bytes** are not measured (counter unavailable); only the count of GC.Alloc samples.
- Spawn includes `EntityManager.SetName` for the two leaders (Editor only).

## Recording a new baseline

Copy this block, fill it from the TSV headers and rows, and keep the old one for comparison.

```text
Date / commit:
Machine: CPU | RAM | GPU | OS | power plan
Unity / Entities / Collections / Burst:
Mode: Editor batchmode EditMode | Player build (type, scripting backend)
Burst: on/off (probe) | safety checks | collections checks | job debugger | managed optimization
Command: (exact command line)
Per N in 2 / 100 / 400, Burst on and off, repetitions 1 / 2 / 3 (mean µs per tick or request):
  start | first tick | end | restart
  idle k1 / k4 / k10
  cmd runtime k1 / k4 / k10 | producer k1
  cmd-lead32 runtime k1 / k10
  GC.Alloc count (all series) | chunks | queue buffer high-water | per-frame native peak/end
Notes / deviations:
```

### Naming follow-up (#19, formerly #26, 2026-10-26)

The historical benchmark's two ID-derived leader names still grew the global store by two strings per
battle. Bootstrap now uses the constant role names `Combatant Human` / `Combatant AI`; all allies stay
unnamed and identity stays exclusively in `CombatantId`. This bounds the bootstrap actor-name contribution
to two strings for the entire process/domain, including repeated new Worlds without Domain Reload.
The 45-session mixed-roster naming regression is prepared but NOT RUN without Unity. No performance
measurements were rerun or changed by this follow-up; the historical timing values above remain historical.
