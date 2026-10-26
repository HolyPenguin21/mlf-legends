# Combat camera (Stage 02.4, Issue #22)

One scene-level presentation controller for the existing camera of a technical scene. It reads the authoritative
position of the human's fighter after the frame's simulation and writes **only its own Transform and Camera**. It is
not a combat system, has no per-fighter objects and never decides what is simulated.

## Piece

| Type | Kind | Responsibility |
|---|---|---|
| `CombatCameraController` | `MonoBehaviour` on the camera (`[RequireComponent(Camera)]`) | Orthographic projection, fixed angle, follow `BattleSession.HumanCombatant` → `CombatPosition` in the default world, pan and zoom. |

`CombatSmoke.unity` → *Main Camera* carries it (the scene's existing camera; no new scene, no Cinemachine, no package).

## Behaviour

- **Projection/angle:** `orthographic = true`; rotation = `Euler(Pitch, Yaw, 0)` (defaults 40°, 0°), set every update
  from configuration only. Follow, pan and zoom never rotate the view; no orbit or mouse rotation.
- **Follow:** look-at point = focus + `FollowOffset` (0, 1, 0) + pan; the camera sits `Distance` (30, clipping only)
  back along the view direction. Focus = ECS `CombatPosition` X/Z, altitude Y weighted by `AltitudeFollow` (default
  0: jumps are visible on screen instead of being cancelled by the camera). The followed value is the position after
  the frame's last fixed tick, never a position guessed from input.
- **Pan:** offset on the ground plane relative to follow, rotated by `Yaw`; speed `PanSpeed` × orthographic size per
  second (screen speed independent of zoom); clamped to `MaxPan` (20). *Recenter* drops it.
- **Zoom:** orthographic size, continuous (`ZoomRate`, e-folds per second of full input) and per wheel notch
  (`ZoomStepFactor`); clamped to `[MinOrthographicSize, MaxOrthographicSize]` = [2, 20], default 6. Positive input zooms in.
- **Timing:** `LateUpdate` (PlayerLoop `PreLateUpdate`), after the `Update`-phase `SimulationSystemGroup`; not a render
  callback. Pan/zoom use `Time.unscaledDeltaTime`, the existing presentation delta (not a combat clock), so they work at
  `Time.timeScale = 0` while the battle stays frozen. No smoothing/interpolation snapshot (Stage 03 decides).
- **Binding:** binding to a new **valid** target (Start/Restart, other human, new World) drops the pan offset and the
  cached entity; zoom is kept. Losing the target (End/Idle) does not touch the pan: the first update after End leaves
  position, size and pan exactly as they were (review #22); explicit pan/zoom input still works without a target.
  The cached entity is revalidated every frame (exists, same `CombatantId`, `InCombat` enabled) before use; Entity
  handles are recycled, IDs are not. Without a target (idle, End, Despawn, defeat, no/disposed World) the camera keeps
  its last focus and position, without exceptions and without writing ECS. A new World always rebinds.
- **ECS access:** two read-only `EntityQuery`s per World (session; `CombatantId` + `CombatPosition` + `InCombat`),
  created once and disposed on disable/World change (a disposed World released them already). A lookup by ID scans the
  in-combat actors only when the cached entity is invalid.

## Bindings (Camera map, provisional, remappable)

| Action | Keyboard / mouse | Gamepad |
|---|---|---|
| Pan (Vector2) | arrow keys | right stick |
| Zoom (Axis) | `=` in, `-` out | d-pad up / down |
| ZoomStep (Axis) | mouse wheel | — |
| Recenter (Button) | Home | right stick press |

None of these overlap the combat bindings (WASD, J, K, L, Space, Enter, left mouse). The controller owns a clone of the
Camera map while enabled and disposes it on disable; the shared asset is never enabled or disabled.

## Sprite renderer after the camera (Stage 03, #38)

`CombatSpriteRenderer` sits on the same Main Camera with `[DefaultExecutionOrder(100)]`, so in the same `LateUpdate` it
runs after this controller (order 0) and builds its sprite cards from the camera rotation placed this frame. The camera
never reads sprite state; the renderer never moves the camera. See `Sprites/SPRITES.md`.

## Calibration

All numbers above are Inspector values for calibration, not approved art direction. Stage 03.3 (final character,
first visible scene) is where angle, size and offsets get tuned against real sprites; until then CombatSmoke shows the
generated technical atlas (not a character).

## Tests

`Tests/EditMode/Input/CombatCameraControllerTests` (real battle World: follow after movement and jump, altitude follow,
fixed angle, pan/zoom clamps and invalid input, yaw, Restart rebinding, End/Despawn/defeat/World disposal, 200 vs 200
read-only snapshot identical before/after camera-only frames) and `Tests/PlayMode/Input/PlayerInputPlayModeTests`
(real LateUpdate in CombatSmoke, pan/zoom on pause, rebinding on bootstrap re-enable).
