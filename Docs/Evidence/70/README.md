# #70 evidence — Unity runs and art checks, 2026-10-29

Machine: AMD Ryzen 5 3600 (12 logical), 32 GB, RTX 2060 SUPER (D3D12 for PlayMode), Windows 11 26100. Unity 6000.5.4f1,
Entities/Collections 6.5.0, Burst 1.8.29, safety checks on. Runs in a detached worktree (the owner's Editor holds the main
project) on a **clean checkout of the named commit** (`git status` clean except Unity's `ProjectSettings/TimeManager.asset`
rewrite, never committed), with the external atlas PNG copied in (SHA256 `8aada5ec…a443f7`, outside Git).
Results only from actual runs: **PASS / FAIL / NOT RUN**. Raw NUnit XML and logs: [`raw/`](raw) (licensing Machine Id redacted).

Base main `47013974c7773d2cdd565e6eabc3441003aeee0e`. Commits: `c636073` physics corrections (0.1/0.2) → `2defedd` owner cube
as arena source (0.3) → `1b93b33` v2 atlas → docs/evidence. **Source tested by the suites = `1b93b33`**; the following
commit changes only Markdown/evidence.

| Check | Commit | Result |
|---|---|---|
| EditMode, all non-Performance | `1b93b33` | **PASS 603/603** [raw](raw/1b93b33) |
| PlayMode, full ordered run, D3D12 (GPU graphics tests, real-art smoke of CombatSmoke) | `1b93b33` | **PASS 59/59** [raw](raw/1b93b33) |
| EditMode non-Performance with the 0.1/0.2 fix (patched worktree at main + fix files) | = `c636073` sources | PASS 597/597 (pre-commit run; superseded by the row above) |
| Mutation: old two-pass `ClipWalls` restored | worktree only | **FAILS as intended**: `AcuteCornerLeavesNoVelocityIntoEitherWall`, `RotatedCorners…(acute)` (hidden velocity (0,−1.92) into the 45° wall) [raw](raw/mutations/w70-mut-clip.xml) |
| Mutation: final touching-wall query removed | worktree only | corner tests still pass (B's depenetration re-touches A); the query is covered directly by `TouchingWallsAreTheSidesWithinGapOfTheFinalPosition` [raw](raw/mutations) |
| Package/page check (`Tools/Install-AtlasPackage.py --check`) | installed page | **PASS**: 12 clips / 76 frames, 192×160, 2000×2016 RGBA, extrusion/separation/markers valid, no opaque pixel outside frames; 17 semi-transparent owner pixels (warning) — [atlas-check.json](atlas-check.json) |
| Imported Sprite rects/pivots vs manifest (test `ImportedPivotsKeepTheFeetLine…`) | `1b93b33` | **PASS**: 76 bindings, every rect on its cell, pivot Y 136, X = 96 + owner offset |
| Real atlas in CombatSmoke, scene camera | `1b93b33` | rendered — [smoke-real-atlas.png](smoke-real-atlas.png) (evidence image, not an automated visual check) |
| Contact benchmark 2/100/400, Burst on | `1b93b33` | **PASS 1/1** — [TSV](stage05-contact-velocity-burst-on.tsv), [raw](raw/1b93b33) |
| Burst-off benchmark | — | **Excluded by owner decision** (#66) |
| Other Performance suites (sprites, baseline, arena, strikes, input) | — | **NOT RUN** on this branch |
| Owner visual acceptance (poses, series 1-1-2, landing frames, flip, anchors on the new art in the Game view) | — | **NOT RUN** (owner) |

Migration report of the pre-#70 owner config against the v2 atlas (test output): [migration-report-pre70-config.txt](migration-report-pre70-config.txt).
The owner's Sprite Editor slices that were converted into pivots: [owner-slices-1706.png.meta.txt](owner-slices-1706.png.meta.txt),
offsets [owner-horizontal-offsets.json](owner-horizontal-offsets.json).

## Package and files

The artist package `mage_female_01_v2_128` (README, previews, frames, strips, validation.json) was in the owner's Downloads;
it has since been moved by the owner. SHA256: package JSON `8515f849728375290b808c542d640400c0f17af663cdebfe4df5f6da55498b6c`
(also `package_json_sha256` in `atlas-check.json`); package PNG `b82abb7ecc9c4b923401d82d73c582596e90614ea1bbcf3d01e842d4380e5ef4`;
installed owner-edited page `8aada5ec29dfb46ad594a0d2315d2c81201aca65c93aed6cf2fba52a85a443f7`.

## Benchmark — 2/100/400, Burst on (`1b93b33`) vs #69 (`00abb4c`)

Managed-kernel flags False in every row (Burst executed). Movement / full-tick means are within run-to-run noise of #69
(e.g. 400 opponents 2139 → 2078 µs, 400 corridor 13479 → 13182 µs, 100 opponents 533 → 530 µs): the exact cone projection and
the final touching-wall query add no measurable cost at these shape counts. Holds/discards are unchanged (400 corridor
37800 hold body-ticks, 400 impulse_mass 8000 discarded velocities — the known #66 jam), no work limit, so the new
velocity-pass budget guard was not reached in the benchmark; its policy is covered by the guard test only.
