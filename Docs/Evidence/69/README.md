# #69 evidence — Unity runs, 2026-10-29

Machine: AMD Ryzen 5 3600 (12 logical), 32 GB, RTX 2060 SUPER (D3D12 for the PlayMode runs below unless noted),
Windows 11 26100. Toolchain: Unity 6000.5.4f1, Entities/Collections 6.5.0, Burst 1.8.29, safety checks on.
All runs: a detached worktree (the owner's Editor holds the main project) on a **clean checkout of the named commit**
(`git status` clean except Unity's `ProjectSettings/TimeManager.asset` rewrite, never committed). Results only from actual
runs: **PASS / FAIL / NOT RUN**. Raw NUnit XML and Editor logs: [`raw/<sha>/`](raw) (only the licensing "Machine Id" value is redacted).

Commits of the branch (base main `86012a006b51f6295a855dbaa9b6bbf1d3d23133`):
`78f1176` fixes 1.1/1.2/1.3 → `0ede130` faults found by the full ordered run → `f945b09` per-character data →
`3b9c436` U/I/O series → `12d8b69` checks/benchmark columns → `32c1ce1`, `00abb4c` benchmark-only test fixes → docs.
**Runtime/test source tested by the suites = `12d8b69`**; `32c1ce1`/`00abb4c` change only
`Assets/Tests/EditMode/Performance/CombatContactBenchmarkTests.cs` (Performance category, not part of the suites), and the
final commit changes only Markdown/evidence.

| Check | Commit | Result |
|---|---|---|
| PlayMode, full ordered run, `-nographics` (reproduce the reported FAIL) | main `86012a0` | **FAIL**: 34 passed, 10 failed (all `PlayerInputPlayModeTests` except the player-loop one, "CombatSmoke requires CombatArenaSystem"), 22 skipped (graphics without a device). [raw](raw/main-86012a0) |
| PlayMode, full ordered run, D3D12 | `78f1176` (cascade fix) | **FAIL 60/66**: cascade gone; 6 hidden faults surfaced — arena authoring reports another lease's frozen diagnostics; 5 stale input tests (landing lock, scene Cube). [raw](raw/78f1176) |
| EditMode, all non-Performance | `12d8b69` | **PASS 583/583** [raw](raw/12d8b69) |
| PlayMode, full ordered run, D3D12 | `12d8b69` | **PASS 66/66** (incl. GPU graphics/performance tests and all 11 input tests) [raw](raw/12d8b69) |
| Benchmark `BodyContactScenarios2_100_400…`, Burst on | `12d8b69` | **FAIL** (test bug: NullReferenceException disposing an EntityQuery after its World — the new explicit dispose order; fixed in `32c1ce1`) [raw](raw/12d8b69/bench-nre.xml) |
| Benchmark, Burst on | `32c1ce1` | **PASS** (first complete run; superseded by the next row, same numbers within noise) [raw](raw/32c1ce1) |
| Benchmark, Burst on, with `velocity_residual_max` | `00abb4c` | **PASS** — [TSV](stage05-contact-velocity-burst-on.tsv), [raw](raw/00abb4c) |
| Burst-off benchmark | — | **Excluded by owner decision** (#66): production runs with Burst |
| Other Performance suites (baseline, arena, strikes, input) | — | **NOT RUN** on this branch |
| Owner manual checklist (STAGE05_1_CHARACTER_SERIES §5) | — | **NOT RUN** (owner's interactive Editor) |

Source/model statements in the contract are separate from these executions; nothing above was inferred.

## Benchmark — 2/100/400, Burst on (`00abb4c`, 120 warm + 600 measured ticks)

Managed-kernel flags (contacts and Hit) were **False** in every row: Burst executed. `gc_samples` = 0 in every owner loop.
Indexed and complete-scan Hit searches matched in every row (`search_match`). No whole-island rollback anywhere.

| bodies / scenario | Movement mean / p95 µs | full tick mean / p95 µs | holds (body-ticks) | discarded | velocity passes max / residual |
|---|---:|---:|---:|---:|---|
| 2 any | 29–34 / 43–50 | 75–115 / 103–165 | 0 | 0 | ≤1 / 0 |
| 100 opponents | 533 / 592 | 743 / 1045 | 0 | 0 | 1 / 0 |
| 100 corridor | 1164 / 1294 | 1362 / 1524 | 1200 | 0 | 1 / 0 |
| 100 impulse_single | 963 / 2114 | 465 / 614 | 97 | 0 | 1 / 0 |
| 100 impulse_mass | 500 / 1143 | 569 / 698 | 8 | 0 | **48 (cap) / 0.0021 u/s** |
| 400 opponents | 2139 / 2342 | 2896 / 4173 | 0 | 0 | 1 / 0 |
| 400 strikes | 1983 / 2103 | 2859 / 4189 | 0 | 0 | 1 / 0 |
| 400 corridor | 13479 / 13976 | 14278 / 14918 | 37800 (63/tick) | 0 | 1 / 0 |
| 400 impulse_single | 10491 / 26465 | 1793 / 2471 | 3091 | 0 | 1 / 0 |
| 400 impulse_mass | 2824 / 2467 | 3568 / 4263 | 8000 (all 400 on each of 20 impulse ticks) | 8000 | 1 / 0 |

Hit search, 400 dense opponents: complete scan ≈ 813 µs vs index batch ≈ 1356 µs (index slower in dense packs, as in
#66); 400 sparse: 803 vs 326 µs (index faster). Old #66 numbers are not carried over; these come from the new solver.

**Known limits (unchanged by #69, measured again, not hidden):** a mass-impulse tick into a 400 jam still holds every
body and discards those impulses (counted); a 400 single-file corridor still holds 63 bodies per tick at ≈13.5 ms
Movement; dense indexed search is slower than a scan. Free small/medium chains do work: 6 and 20 touching bodies transfer
an impulse end to end with **no** hold/discard/rollback (`FreeSmallAndMediumChainsTransferWithoutHoldsOrDiscards`; the
20-chain publishes only the documented slop, max 0.0009 of the allowed 0.015). The new final-velocity pass converges in
one pass almost everywhere; in 100 impulse_mass one tick reached the 48-pass cap with a residual of 0.0021 u/s (below the
0.05 u/s stop speed; bounded and diagnosed, `VelocityResidual`). Crowd pass-through for a future "power attack" is 05.2
and needs its own acceptance; collision was not disabled and slop was not raised.

## Native memory (`process_native_delta` of #66)

`Profiler.GetTotalAllocatedMemoryLong` is process-wide. The 471 220 416 B at 400/wall_pressure in #66 covered the owner
loop **plus** the test-only search comparison with the Profiler enabled. Split now (TSV columns):

- `native_owner_profiler_on` / `native_compare_pass` / `native_owner_profiler_off`: mostly 0 or ≈1.46 MB. Large jumps of
  fixed sizes (e.g. 335 544 320, 402 653 184, 471 220 560, 538 329 376 B) appear in **one** of these windows of some
  scenarios and land at the **same** byte values in the same scenarios across two runs — including a **2-body** scenario's
  test-only comparison pass (202 785 296 B) and a Profiler-off owner pass (135 679 632 B). They are therefore not
  proportional to the owner's work or roster: they are process allocator/Profiler region growth at fixed points of the
  process timeline.
- The owner's own native capacity is constant: warm = final payload+scratch capacity is asserted in every scenario.
- `native_after_dispose` (after World disposal, minus before World creation; ~100 KB of test scratch still alive):
  1.8–5.3 MB in scenarios without a jump, independent of 2/100/400, so no per-roster growth survives disposal; in
  scenarios with a jump the jump stays in the process total (the allocator keeps the region).

Conclusion: **a leak is neither proven nor excluded** by this process-wide metric; it does not support a "zero native
memory" claim either (`gc_samples = 0` is a managed-allocation statement only). Attributing the region growth needs a
Memory Profiler capture — NOT RUN.
