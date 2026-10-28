# PR #48 — Stage 05.1 strikes + state-driven combat animation: evidence

Tested commit: **`a7e09df`** (branch `feature/stage05-strikes-combat-animation`, base main `00310c3`), clean detached
worktree, Unity **6000.5.4f1**, Windows 11, AMD Ryzen 5 3600, NVIDIA GeForce RTX 2060 SUPER (Direct3D 12), Burst on.
Each series ran **twice in separate processes**; raw NUnit XML, full Editor logs and performance TSVs are in `raw/`.
The art page used by every run is the PNG whose SHA256 equals the manifest's `meta.page_png_sha256`
(`0dde20a9ced880592198c4883b320f3ac1b3433fce5a8f8ac3d1801022ea65fc`, 1728x1536 RGBA). PNGs and graphics readbacks are not
committed.

## Real Unity runs

| Series | Command (per run) | Run 1 | Run 2 | Status |
|---|---|---|---|---|
| EditMode (all, incl. Performance) | `-batchmode -nographics -runTests -testPlatform EditMode` | 402/402 passed | 402/402 passed | **PASS** |
| PlayMode headless | `-batchmode -nographics -runTests -testPlatform PlayMode` | 41 passed, 0 failed, 22 skipped | same | **PASS** for 41; the 22 skipped are GPU tests reported `NOT RUN: no graphics device` and are covered by the next row |
| PlayMode GPU | `-batchmode -force-d3d12 -runTests -testPlatform PlayMode -testCategory "Graphics;Performance"` | 22/22 passed | 22/22 passed | **PASS** |
| #41/#42 depth tests on clean main `00310c3` (before this change) | GPU command above, `-testCategory Graphics` | 15/15 passed | — (one run) | **PASS** — previously NOT RUN |

Files: `raw/a7e09df/{editmode,playmode,gpu}-run{1,2}.{xml,log}`, `raw/main-00310c3/main-00310c3-gpu-graphics.{xml,log}`.
All exit codes 0 except NUnit result `Skipped:Ignored` for headless PlayMode (only the documented GPU skips).

During development (not evidence for `a7e09df`): first EditMode run 392/402 → fixed expectations of changed behaviour
(State owners, archetype, J now strikes, Jump+strike on one tick) and one **pre-existing test-data defect**:
`SpriteAnimationTimingTests.UnusedEndpointTangentsCannotHideNegativeOvershoot` used tangents −10 whose curve never goes
negative (minimum +0.0377 at u = 0.211, checked numerically), so it could never pass; the code path is unchanged by this PR
and the test had never been run before (#43/#45 Unity NOT RUN). Data changed to −30 (minimum −1.887). One new graphics test
first used a non-repeating tick window that a slow first frame skipped; it now uses the periodic strike window.

## What the runs cover

- **Strikes (EditMode `CombatAttackTests`, real runtime chain):** all five strikes: windup/active/recovery per tick, damage
  only on the first Active tick, ledger, return to free state; box boundaries (reach inclusive / beyond, overlap, behind,
  depth ±0.4, height 0.9 / 0.95); facing-dependent box and first-tick turning; movement/jump locks; airborne press dropped;
  Jump+strike on one tick; mashing (punch L-R-L-R then fresh series, kick L-R-spin then fresh series); mixed series
  (J U U, U J J); windup press dropped; finisher and 4th strike never chain; late buffered press chains on its tick; held
  button = one strike; allies/self/out-of-box never hit, several enemies once each; player/AI identical; catch-up 2/3/10
  ticks per frame = single-tick RawLog (render-rate independence); paused frames freeze; restart drops strike/buffer/ledger;
  despawned target never hit, despawned attacker lands nothing; 2/100/400 rosters (ledger unique, enemies only, everyone hit).
- **Animation (EditMode `CombatAnimationSetTests` on the real manifest + project config):** all eight clips bound, owner
  idle 6/1 s and run 5/0.5 s unchanged, only the expected `jump_000` note; for every strike the `strike_pose` frames show
  exactly on Active ticks, frames never step back and every frame shows; a curve/T edit redistributes frames but never moves
  the strike pose, and the T mismatch is reported; jump frames follow real physics (rise/fall only in the air, landing only
  on the ground for 16 ticks, then idle frame 0); landing while moving runs from frame 0, run phase = distance, diagonal
  strides at 0.707; short stops keep the streak, longer ones restart it; kick recovery hands over to `idle_combat_000`;
  missing/invalid markers, counts, settings and clips are rejected with field paths.
- **Extraction/renderer (EditMode + PlayMode):** run/jump/strike/idle selection before page/trim/depth grouping, manual worlds,
  read-only (identical replay with/without extraction), 2/100/400, pause, offscreen/disable, restart/end, World replacement,
  config edits and error recovery (no fallback clip), character switch A→B→A, real-art smoke scene (all eight clips).
- **GPU readbacks:** animated idle/run/strike frames match independently selected manual frames at 1/2/3 px per source
  pixel across both pages and depth; all #38/#41/#42 trim/flip/depth/wall/400-layer/opaque-geometry tests.
- **Default world (PlayMode):** exactly strike → facing → movement → hit in State, no owner in Collection/Publication.

## Performance (raw TSVs in `raw/a7e09df/performance-run{1,2}/`)

Simulation, full fixed tick (clock, lifecycle, commands, strike, facing, movement, hit), 600 measured ticks, `stage05-strikes.tsv`.
"strikes" = every fighter presses Attack/Kick every 3 ticks in packed facing rows (2–3 enemies in every box: worst case for
the brute-force hit scan). Managed GC bytes of the measured loop: **0** in every row.

| Combatants | idle mean / p95 µs (run1; run2) | strikes mean / median / p95 µs (run1; run2) |
|---|---|---|
| 2 | 14.6 / 19.1; 12.3 / 12.7 | 19.4 / 14.4 / 17.6; 15.1 / 14.4 / 17.0 |
| 100 | 18.3 / 22.7; 17.3 / 17.7 | 45.2 / 33.6 / 73.4; 45.8 / 34.4 / 73.9 |
| 400 | 33.4 / 34.2; 37.1 / 40.8 | 220.5 / 111.6 / 535.2; 235.0 / 117.1 / 531.7 |

Renderer with real art and the combat animation load (`stage05-combat-anim-n*.tsv`, run 2; even IDs run, ID%4 = 1 strike,
others idle): Refresh median 41 / 148 / 604 µs at 2 / 100 / 400, of which extraction 21 / 128 / 584 µs; submission ≈ 17.5 µs;
one submission (one page), 8 draw calls, one instance buffer, no buffer re-creation. The technical non-animated series at
400 measures extraction 627 µs, so state-driven selection adds no measurable cost; extraction is dominated by the existing
#41 depth policy. GC.Alloc samples during Refresh: 0 with a valid positive control; byte counter `NOT MEASURED`
(its control is invalid on this platform, reported as such by the harness).

## Static checks (not Unity execution)

- `art-static-validation-tested-png.json` — tested PNG `0dde20a9…`: size matches manifest, binary alpha, grown rects inside
  the page and disjoint, trim inside the canvas, **no neighbour bleed** in the 2 px extrusion, all eight clips present,
  out-of-scope clips absent; per-frame body bbox and feet-above-pivot (grounded clips within ±2 px of the pivot line; run
  stride frames up to 17 px and `jump_002` 25 px above it are authored poses).
- `art-static-validation-working-png.json` — **diagnostic of the PNG in the owner's main checkout, which changed during
  this task** (12:39, 12:43, 12:46 local time; not touched by the agent): kick strike frames are being redrawn. At the last
  snapshot (`38d30263…`): `kick_right_002` reaches the right edge of its rect (canvas x 184) and 24 pixels lie in its
  extrusion (they would be cut off / differ from the edge), 18 pixels have non-binary alpha (kick_left_002, kick_right_002,
  kick_spin_004), and `meta.page_png_sha256` no longer matches. With that PNG the manifest rect of `kick_right_002` must
  be widened (and the hash updated) before use. Not used by any run above.
- Jump markers: the delivered jump clip had only `entry [0]` / `return [5]`. `rise [1,2]`, `fall [3]`, `land [4,5]` were
  added from visual inspection (flagged in the manifest's `meta.jump_phase_markers`); `jump_000` is unused.

## NOT RUN

- Manual Game view / Frame Debugger / player build / Domain Reload disabled / owner GUI play of J/U/K with a real keyboard
  (input is covered by the automated Input System tests only).
- Legacy-depth mutation negative control of Evidence/41.
- Burst-off performance series; any hardware other than the machine above.
