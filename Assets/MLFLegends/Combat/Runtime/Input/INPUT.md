# Player input (Stage 02.3, Issue #22)

Real Unity Input System input for the one fighter the human controls, through the existing single command queue.
This folder adds no queue, clock, accumulator or combat state: its only output is at most one `CombatCommand` per real
fixed tick, appended to `CombatCommandSubmission` before the processor. Facing, movement, jump and every later rule
stay with their owners in `Simulation/`; player and AI commands are validated and applied identically.

## Pieces

| Type | Kind | Responsibility | Owner / lifetime |
|---|---|---|---|
| `PlayerInputCapture` | plain class, `IDisposable` | Raw device state of the Player actions + presses not yet consumed (`CommandButtons` bits). Not a command queue: no tick, no combatant, no `CombatCommand`. | Created/disposed by the adapter (tests create their own). Owns a **clone** of the asset's Player map, its `performed` callbacks and one `InputSystem.onDeviceChange` / `onAfterUpdate` subscription. |
| `PlayerInputCommandSystem` | `SystemBase`, `CombatCommandCollectionSystemGroup` | The single tick-aligned Player producer: per real fixed tick, one command for `BattleSession.HumanCombatant`, for the tick being simulated, before the processor (`OrderLast`). | One per World; auto-created in the default world, absent from replay/benchmark/manual worlds unless a test adds it. Holds a reference to the attached capture and the last binding (battle number, human ID); no static state. |
| `PlayerInputAdapter` | `MonoBehaviour` (scene) | Scene glue: while enabled, owns one capture and keeps it attached to the **current** default World's producer (checked on enable and once per frame: attaches when a World appears later, moves A → B without disable/enable); forwards application focus. | `CombatSmoke.unity` → *Player Input*. Disable/destroy detaches + disposes. A static enable counter only orders adapters (like the bootstrap owner token). |

The actions asset is `Assets/InputSystem_Actions.inputactions` (also the project-wide actions). Nobody here enables or
disables the shared asset or its maps: each capture enables and disposes only its own clone.

## Bindings — current owner layout (#52)

| Player action | Physical key | Logical flag |
|---|---|---|
| PunchLeft | Y | PunchLeft |
| PunchRight | U | PunchRight |
| Attack | I | Attack (general hand series) |
| KickLeft | H | KickLeft |
| KickRight | J | KickRight |
| KickSpin | K | KickSpin |
| Kick | L | Kick (general foot series) |
| Jump | Space | Jump |

No Shift is required. These are the only combat action bindings; no mouse/Enter/gamepad/other-device combat aliases.
Move/WASD and its existing stick bindings are preserved. Camera/UI maps are unchanged. Player template Look, Interact,
Crouch, Previous, Next, Sprint and Guard are removed. Crouch is excluded; Guard has no key/implementation; Dash/Cast deferred.

The capture uses one cloned map and shared arrays of the eight button names/flags. Arrays/callback delegates are created
once per capture; sampling/disposal iterate those same arrays. Capture ORs all pressed bits; it does NOT choose strikes.
CombatAttackTuning.StrikeButton applies priority in simulation: exact hand L/R, exact foot L/R/spin, general hand, general
foot. Jump wins from free grounded stance. Exact/general flags remain distinct through the existing one-slot combo buffer.
See [current settings and validation](../../../../../Docs/STAGE03_CONTROLS.md).

## Sampling policy (what one command contains)

| Field | Source | Catch-up ticks of one frame |
|---|---|---|
| `MoveX`, `MoveZ` | `Move.ReadValue<Vector2>()` → `PlayerInputCapture.Quantize` | same live value on every tick |
| `Held` | device state of each action's bound buttons (`InputControl.IsPressed`) | same live value on every tick |
| `Pressed` | `performed` callbacks since the last consumption, OR-ed bits | first tick that produces a command only |
| `Tick` | `SimulationClock.Tick` (the tick being simulated) | — |
| `Source` | `Player` | — |

- **Quantization** (one explicit policy for analog bindings): idle when the stick magnitude is below 0.25
  (`MoveDeadZone`); otherwise an axis gives its sign when it is at least sin 22.5° of the magnitude, i.e. 8 equal 45°
  sectors. Keyboard composites (±1 and normalized diagonals) map exactly. `CombatMovementSystem` alone normalizes the
  diagonal speed. No float axes in commands.
- **Taps:** press and release between two ticks give `Pressed` with `Held = None`. Several presses of a button before a
  tick are one bit (current contract; no repeat counting). A 0-tick render frame keeps its presses for the next tick.
- **Held never creates Pressed.** A button already down when a capture is enabled is Held at once but never Pressed.
- **Attach boundary:** `Attach` drops presses recorded before it and binds to the session as it is at that moment, so a
  fresh tap made right after attaching to a running battle reaches its first tick (review #22); a later Start/Restart
  is still a new binding that drops older presses.
- **Not** a frame-rate `Tick + 1` producer (the pacing-dependent scheme of PR #20): commands are produced inside the
  fixed step, so 30/60/120 FPS and uneven pacing give one command per real tick.

## Frame order (verified in the real PlayerLoop, PlayMode tests)

```text
PreUpdate.NewInputUpdate        Input System (default ProcessEventsInDynamicUpdate): events → device state → callbacks (presses)
Update
├─ MonoBehaviour.Update / coroutines
└─ SimulationSystemGroup        default world, appended at the end of Update by Entities
   └─ FixedStepSimulationSystemGroup × N (0..N ticks)
      BeginFixedStep ECB → BattleLifecycleSystem → CombatClockSystem →
      CombatCommandCollectionSystemGroup [PlayerInputCommandSystem … CombatCommandProcessorSystem (OrderLast)] →
      CombatStateUpdateSystemGroup [CombatActionSystem → CombatFacingSystem → CombatMovementSystem → CombatHitSystem] → Publication → EndFixedStep ECB
PreLateUpdate.ScriptRunBehaviourLateUpdate   CombatCameraController (reads ECS, writes the camera only)
```

The project has no InputSettings asset, so the update mode is the default dynamic update: no custom accumulator, no
manual `InputSystem.Update()`, no use of `ProcessEventsInFixedUpdate` (that is MonoBehaviour `FixedUpdate`, unrelated
to the ECS combat tick).

## Binding, lifecycle and cleanup

The producer submits only for the **current, active, in-combat** `HumanCombatant` (found by `CombatantId` among enabled
`InCombat` actors each tick; no Entity handle is kept). Otherwise it submits nothing and drops pending presses:

| Situation | Effect |
|---|---|
| No capture attached / capture disposed | nothing (replay, benchmark, manual worlds; scenes without adapter) |
| Battle idle, human despawned or `InCombat` disabled | no command (no `UnknownCombatant` noise), presses dropped |
| New battle number or human ID (Start/Restart/End, also between catch-up ticks) | presses dropped before the first command of the new binding; Held/Move are the live state |
| `Time.timeScale == 0` (pause) | presses ignored and dropped after every input update; on resume Held/Move are live, no synthetic Pressed |
| Application focus lost / regained (`OnApplicationFocus`) | presses dropped; buttons held so far blocked (below); while unfocused Move/Held sample neutral, so the fighter stops on the next tick |
| Device disabled / enabled / soft or hard reset (the Input System's own focus cycle) | presses dropped; buttons held before it blocked (below) |
| Device removed / disconnected | presses dropped; the actions cancel the lost controls (neutral) |
| Second adapter enabled (duplicate, additive scene switch) | the producer keeps the adapter with the highest enable order, whichever attaches first; a replaced adapter neither retakes control nor detaches its successor; still one producer |
| Default World appears after the adapter was enabled, or is replaced A → B | the adapter detaches from A (if alive) and attaches to the new World's producer (stale presses dropped); each World is tried once, so an outranked adapter does not retry every frame |
| Adapter disabled/destroyed, Play Mode exit | detach (if still attached) + dispose of the clone and every subscription; a disposed World is skipped |

Pause cannot carry a press across it: a tap of the last unpaused 0-tick frame is dropped too.

**Held across a focus change or device reset (blocking).** With its default background behaviour the Input System
soft-resets/disables devices on focus loss and re-enables + syncs (or resets) them on regain; a key that is still
physically down then comes back through a sync or the next device report, which the action sees as a new press. The
capture therefore blocks every button that was held (device state at the end of the previous Input System update) when
focus changes or a device is disabled/enabled/reset: its presses are ignored until the device reports it released
(a held → released transition in a later update; the synthetic release of the reset itself does not count). Result:
a restored hold is Held without Pressed (no unwanted Jump/Attack); a button that was not held presses normally;
release → press works. Conservative edge: a button held at the interruption, released while unfocused and never
re-reported, loses its first press after regain (the next release unblocks it).

## Manual check (owner GUI, record in Docs/QA.md; NOT RUN until performed)

Use the external art PNG and `Assets/Scenes/CombatSmoke.unity`. Inspect AppliedCombatCommand/CombatAttackState and the
bootstrap's published timing revision alongside the Game view.
1. Y/U/H/J/K always choose their named strike; K starts spin from free grounded stance. I alternates hands, L follows
   the existing foot series. Holding never repeats; simultaneous keys follow the documented priority, Space wins when free.
2. Hold direction through jump/landing: frames 4 then 5 play on the ground before run/idle, no sliding. Strike/Space taps
   during recovery do not execute later. Startup log shows the configured landing duration (currently 12 ticks after 68c43c).
3. Verify pause, focus/device reset, disable/re-enable, restart/despawn and World replacement drop stale presses; live
   holds resume without a synthetic press. Repeat with Domain/Scene reload disabled.
4. Camera controls still follow/pan/zoom/recenter; UI bindings work. Enter/mouse/gamepad buttons do not start combat actions.
5. Record the tested SHA and actual Unity results; source/model checks alone do not close Stage 03.

## Tests

- `Tests/EditMode/Input/PlayerInputProducerTests` (InputTestFixture virtual keyboard/gamepad + the real asset + the real
  producer in a replay World): 30/60/120 FPS and variable pacing, 0/1/2/3/10 ticks per frame, taps, holds, diagonals,
  stick quantization, Restart between catch-up ticks, End/idle/despawn/defeat, pause, focus and device loss, attach /
  replace / detach / dispose, replay worlds without adapter, player-vs-AI parity, 200 vs 200; review regressions: a fresh
  tap right after attaching to a running battle, enable-order priority, keys held across a reset or disable/enable cycle.
- `Tests/EditMode/Input/PlayerInputQuantizeTests`: the quantization policy.
- `Tests/PlayMode/Input/PlayerInputPlayModeTests`: real PlayerLoop order, real-loop taps/holds at 30/60/120 FPS
  (`Time.captureFramerate`), pause, focus, additive scene switch/re-enable, camera follow/rebind and the exact view on End
  (CombatSmoke); the Input System's real focus cycle (test runtime focus, runInBackground on/off) with the device
  re-reporting held Space/I/L; an adapter following a default World that appears late or is replaced A → B → A.
- `Tests/EditMode/Performance/PlayerInputBenchmarkTests`: producer cost, see `Docs/PERFORMANCE_BASELINE.md`.

## Compatibility and scope (#52)

CommandButtons is now ushort. Old numeric values Attack=1, Jump=2, Guard=4, Kick=8 remain stable; the five exact bits are
16/32/64/128/256. Guard remains a reserved logical flag for compatibility, without an action or device binding.
Typed command/buffer/replay fields carry all bits; no byte truncation. Rebuild consumers of the unmanaged command layout;
no binary command-file format is promised. Undefined bits 512 and above remain invalid at the common processor.
Existing lifecycle, input pacing and generic strike rules are retained; historical Unity evidence uses its original SHA.
