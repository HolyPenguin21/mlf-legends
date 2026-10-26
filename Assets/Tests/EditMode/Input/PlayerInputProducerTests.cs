using System;
using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace MLFLegends.Tests.EditMode
{
    // Stage 02.3 (#22): real Input System events (InputTestFixture virtual keyboard/gamepad, the project's action asset,
    // real action callbacks) through the real PlayerInputCommandSystem into the single queue of an isolated replay World
    // (real clock, lifecycle, processor, facing, movement; ReplayRecorder captures every completed tick).
    // A test "frame" = the queued device events processed in ONE Input System update (as PreUpdate does in the player
    // loop; PlayMode tests check that real order), then 0..18 fixed ticks. Assertions read the published
    // AppliedCombatCommand, CombatCommandResult and positions, never the capture's internals alone.
    public class PlayerInputProducerTests : InputTestFixture
    {
        public const string ActionsPath = "Assets/InputSystem_Actions.inputactions";

        Keyboard m_Keyboard;
        Gamepad m_Gamepad;
        InputActionAsset m_Asset;
        ReplayWorld m_Replay;
        PlayerInputCommandSystem m_Producer;
        PlayerInputCapture m_Capture;
        float m_TimeScale;

        // Frame index of every recorded tick (parallel to m_Replay.Records).
        readonly List<int> m_TickFrames = new List<int>();
        int m_Frame;

        public override void Setup()
        {
            base.Setup();
            m_TimeScale = Time.timeScale;
            m_Keyboard = InputSystem.AddDevice<Keyboard>();
            m_Gamepad = InputSystem.AddDevice<Gamepad>();
            m_Keys.Clear();
            m_Pad = default;
            m_Asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(ActionsPath);
            Assert.That(m_Asset, Is.Not.Null, ActionsPath);
            m_TickFrames.Clear();
            m_Frame = 0;
        }

        public override void TearDown()
        {
            Time.timeScale = m_TimeScale;
            m_Capture?.Dispose();
            m_Capture = null;
            m_Replay?.Dispose();
            m_Replay = null;
            m_Producer = null;
            base.TearDown();
        }

        // ---- rig ----

        /// <summary>Replay World + the real input producer (explicitly added: replay worlds have none) + a battle.</summary>
        void StartRig(BattleSetup? setup = null, bool attach = true)
        {
            m_Replay = new ReplayWorld("Player input");
            m_Producer = AddProducer(m_Replay);
            m_Capture = new PlayerInputCapture(m_Asset);
            if (attach)
                m_Producer.Attach(m_Capture);
            m_Replay.Request(BattleRequest.Start(setup ?? BattleSetup.Default));
        }

        static PlayerInputCommandSystem AddProducer(ReplayWorld replay)
        {
            var producer = replay.World.CreateSystemManaged<PlayerInputCommandSystem>();
            var collection = replay.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
            collection.AddSystemToUpdateList(producer);
            collection.SortSystems();
            return producer;
        }

        /// <summary>One render frame: <paramref name="input"/> queues device events, one Input System update processes them, then the ticks run.</summary>
        void Frame(int ticks, Action input = null)
        {
            input?.Invoke();
            InputSystem.Update();
            if (ticks > 0)
            {
                var before = m_Replay.Records.Count;
                m_Replay.Frame(ticks);
                Assert.That(m_Replay.Records.Count - before, Is.EqualTo(ticks), "Every fixed tick of the frame ran once.");
                for (var i = 0; i < ticks; i++)
                    m_TickFrames.Add(m_Frame);
            }

            m_Frame++;
        }

        // Full-state events with the explicit set of held keys/buttons. (InputTestFixture.Press queues delta events that copy
        // the device's other bits at queue time, so several queued events of one device before one update would undo each
        // other; a full state per event is what a real device report carries.)
        readonly HashSet<Key> m_Keys = new HashSet<Key>();
        GamepadState m_Pad;

        void Down(Key key)
        {
            m_Keys.Add(key);
            InputSystem.QueueStateEvent(m_Keyboard, new KeyboardState(m_Keys.ToArray()));
        }

        void Up(Key key)
        {
            m_Keys.Remove(key);
            InputSystem.QueueStateEvent(m_Keyboard, new KeyboardState(m_Keys.ToArray()));
        }

        /// <summary>Press and release inside one Input System update: a tap shorter than any tick.</summary>
        void Tap(Key key)
        {
            Down(key);
            Up(key);
        }

        void Pad(GamepadButton button, bool down)
        {
            m_Pad = m_Pad.WithButton(button, down);
            InputSystem.QueueStateEvent(m_Gamepad, m_Pad);
        }

        void Stick(Vector2 value)
        {
            m_Pad.leftStick = value;
            InputSystem.QueueStateEvent(m_Gamepad, m_Pad);
        }

        TickRecord Last => m_Replay.Records[m_Replay.Records.Count - 1];

        static CombatantRecord Human(TickRecord record) => record.Combatant(record.HumanId);

        static CombatCommand HumanCommand(TickRecord record) => Human(record).Applied.Command;

        TickRecord[] BattleTicks(uint battle = 1) => m_Replay.BattleRecords(battle);

        static float Step => CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration;

        // ---- frame pacing ----

        static readonly int[] s_VariableFrameMs = { 7, 33, 16, 50, 8, 8, 167, 16, 4, 25, 70, 12 };

        /// <summary>
        /// Render frames covering <paramref name="seconds"/>: the start time of each frame (its input) and the fixed ticks it
        /// runs (the catch-up up to the frame's end). <paramref name="fps"/> 0 = uneven frame times from 4 to 167 ms
        /// (0-tick and 10-tick frames). Integer microseconds: no float drift in the expected pacing.
        /// </summary>
        static (double[] start, int[] ticks) Pacing(int fps, double seconds)
        {
            var starts = new List<double>();
            var ticks = new List<int>();
            long elapsedUs = 0;
            for (var i = 0; elapsedUs < seconds * 1_000_000; i++)
            {
                starts.Add(elapsedUs / 1_000_000.0);
                var before = elapsedUs * SimulationTiming.TickRateHz / 1_000_000;
                elapsedUs = fps > 0 ? (i + 1L) * 1_000_000 / fps : elapsedUs + s_VariableFrameMs[i % s_VariableFrameMs.Length] * 1000L;
                // fps divides 1 000 000 for 30/60/120: exact frame boundaries.
                ticks.Add((int)(elapsedUs * SimulationTiming.TickRateHz / 1_000_000 - before));
            }

            return (starts.ToArray(), ticks.ToArray());
        }

        // ---- frame pacing: 30/60/120 FPS and variable pacing ----

        /// <summary>
        /// The same timed input at different frame pacings: D held 0.0–0.5 s, a K tap at 0.1 s, K held 1.2–2.2 s (after the
        /// first landing). Every tick's accepted command equals the device state of its frame; each press reaches exactly
        /// the first tick at or after its frame; two jumps happen, holding K never re-jumps.
        /// </summary>
        [TestCase(30)]
        [TestCase(60)]
        [TestCase(120)]
        [TestCase(0, Description = "variable pacing, 0..10 ticks per frame")]
        public void HeldAndPressedFollowTheDeviceAtEveryFramePacing(int fps)
        {
            StartRig();
            var (frameTimes, pacing) = Pacing(fps, 3.0);
            if (fps > 0)
                Assert.That(pacing.Sum(), Is.EqualTo(180));
            else
                Assert.That(pacing, Has.Member(0).And.Member(10), "Variable pacing has 0-tick and 10-tick frames.");

            var dDown = false;
            var kDown = false;
            var tapPending = true;
            var tapFrame = -1;
            var holdFrame = -1;
            var moveFrames = new bool[pacing.Length];
            var jumpFrames = new bool[pacing.Length];
            for (var f = 0; f < pacing.Length; f++)
            {
                var t = frameTimes[f];
                var frame = f;
                Frame(pacing[f], () =>
                {
                    if (!dDown && t < 0.5)
                    {
                        Down(Key.D);
                        dDown = true;
                    }
                    else if (dDown && t >= 0.5)
                    {
                        Up(Key.D);
                        dDown = false;
                    }

                    if (tapPending && t >= 0.1)
                    {
                        Tap(Key.K);
                        tapPending = false;
                        tapFrame = frame;
                    }

                    if (!kDown && t >= 1.2 && t < 2.2)
                    {
                        Down(Key.K);
                        kDown = true;
                        holdFrame = frame;
                    }
                    else if (kDown && t >= 2.2)
                    {
                        Up(Key.K);
                        kDown = false;
                    }
                });
                moveFrames[f] = dDown;
                jumpFrames[f] = kDown;
            }

            var ticks = BattleTicks();
            Assert.That(ticks.Length, Is.EqualTo(pacing.Sum()), "Every tick of the battle recorded (battle started before frame 0).");
            int FirstTickAtOrAfter(int frame) => Enumerable.Range(0, ticks.Length).First(i => m_TickFrames[i] >= frame);
            var expectedPressTicks = new[] { FirstTickAtOrAfter(tapFrame), FirstTickAtOrAfter(holdFrame) };

            var pressTicks = new List<int>();
            for (var i = 0; i < ticks.Length; i++)
            {
                var record = ticks[i];
                var command = HumanCommand(record);
                Assert.That(Human(record).Applied.WasSubmitted, Is.True, $"tick {i}: the producer submits every tick");
                Assert.That(command.Source, Is.EqualTo(CommandSource.Player));
                Assert.That(command.Tick, Is.EqualTo(record.Tick), "Commands are for the tick being simulated.");
                Assert.That(record.Results.Count(r => r.Label == "H"), Is.EqualTo(1), $"tick {i}: one Player command per tick");
                Assert.That(record.Results.Single(r => r.Label == "H").Outcome, Is.EqualTo(CommandOutcome.Accepted));
                var frame = m_TickFrames[i];
                Assert.That(command.MoveX, Is.EqualTo(moveFrames[frame] ? 1 : 0), $"tick {i} frame {frame}: Move is the live hold");
                Assert.That(command.MoveZ, Is.Zero);
                Assert.That(command.Held, Is.EqualTo(jumpFrames[frame] ? CommandButtons.Jump : CommandButtons.None), $"tick {i}: Held");
                Assert.That(record.Combatant(record.AiId).Applied.WasSubmitted, Is.False, "Nothing is produced for the AI.");
                if (command.Pressed != CommandButtons.None)
                {
                    Assert.That(command.Pressed, Is.EqualTo(CommandButtons.Jump));
                    pressTicks.Add(i);
                }
            }

            Assert.That(pressTicks, Is.EqualTo(expectedPressTicks), "Each press reaches the first tick of its frame (or the next tick), once.");
            Assert.That(HumanCommand(ticks[expectedPressTicks[0]]).Held, Is.EqualTo(CommandButtons.None), "A tap between ticks: Pressed without Held.");

            var takeoffs = Enumerable.Range(1, ticks.Length - 1).Count(i => Human(ticks[i - 1]).Motion.Grounded && !Human(ticks[i]).Motion.Grounded);
            takeoffs += Human(ticks[0]).Motion.Grounded ? 0 : 1;
            Assert.That(takeoffs, Is.EqualTo(2), "The tap jumps, the hold jumps once, holding through landing never re-jumps.");

            var moved = ticks.Count(r => HumanCommand(r).MoveX == 1);
            Assert.That(moved, Is.InRange(29, 31), "≈0.5 s of D at 60 Hz, whatever the frame rate.");
            Assert.That(Human(Last).Position.x, Is.EqualTo(BattleSetup.Default.Human.Position.x + moved * Step).Within(1e-4f));
            Assert.That(Human(Last).Facing, Is.EqualTo(FacingDirection.Right));
        }

        /// <summary>A frame with 0, 1, 2, 3 or 10 ticks: Move/Held on every catch-up tick, Pressed only on the first one.</summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(10)]
        public void CatchUpTicksRepeatHeldButNeverPressed(int ticks)
        {
            StartRig();
            Frame(1);
            Frame(ticks, () =>
            {
                Down(Key.D);
                Tap(Key.K);
                Down(Key.L);
            });
            Frame(3);

            var battle = BattleTicks().Skip(1).ToArray();
            Assert.That(battle.Length, Is.EqualTo(ticks + 3));
            for (var i = 0; i < battle.Length; i++)
            {
                var command = HumanCommand(battle[i]);
                Assert.That(command.MoveX, Is.EqualTo(1), $"tick {i}");
                Assert.That(command.Held, Is.EqualTo(CommandButtons.Guard), $"tick {i}: L held, K already released");
                Assert.That(command.Pressed, Is.EqualTo(i == 0 ? CommandButtons.Jump | CommandButtons.Guard : CommandButtons.None),
                    $"tick {i}: presses only on the first tick after the frame (a 0-tick frame keeps them)");
            }

            Assert.That(Human(battle[0]).Motion.Grounded, Is.False, "The tap jumped.");
            var x = Human(Last).Position.x;
            Frame(2, () => Up(Key.D));
            Assert.That(HumanCommand(Last).MoveX, Is.Zero);
            Assert.That(Human(Last).Position.x, Is.EqualTo(x), "Releasing stops on the next tick (no inertia).");
        }

        [Test]
        public void SeveralPressesBeforeOneTickAreOneBitAndAJumpHeldForSecondsJumpsOnce()
        {
            StartRig();
            Frame(1);
            Frame(0, () => Tap(Key.K));
            Frame(0, () => Tap(Key.K));
            Frame(0, () =>
            {
                Tap(Key.K);
                Tap(Key.J);
            });
            Frame(1);
            Assert.That(HumanCommand(Last).Pressed, Is.EqualTo(CommandButtons.Jump | CommandButtons.Attack));
            Frame(1);
            Assert.That(HumanCommand(Last).Pressed, Is.EqualTo(CommandButtons.None), "Consumed by one tick.");

            // Land, then hold K for 2 s.
            for (var i = 0; i < 6; i++)
                Frame(10);
            Assert.That(Human(Last).Motion.Grounded, Is.True);
            var before = BattleTicks().Length;
            Frame(1, () => Down(Key.K));
            for (var i = 0; i < 120; i++)
                Frame(1);
            var held = BattleTicks().Skip(before).ToArray();
            Assert.That(held.Count(r => HumanCommand(r).Pressed != CommandButtons.None), Is.EqualTo(1));
            Assert.That(held.All(r => HumanCommand(r).Held == CommandButtons.Jump), Is.True);
            Assert.That(Enumerable.Range(1, held.Length - 1).Count(i => Human(held[i - 1]).Motion.Grounded && !Human(held[i]).Motion.Grounded)
                + (Human(held[0]).Motion.Grounded ? 0 : 1), Is.EqualTo(1), "Holding K jumps once, never again after landing.");
            Assert.That(Human(Last).Motion.Grounded, Is.True);
        }

        [Test]
        public void DiagonalsAndSticksUseTheDigitalContractAndMovementNormalizesTheDiagonal()
        {
            StartRig();
            Frame(1);
            var start = Human(Last).Position;
            Frame(1, () =>
            {
                Down(Key.W);
                Down(Key.A);
            });
            Assert.That((HumanCommand(Last).MoveX, HumanCommand(Last).MoveZ), Is.EqualTo(((sbyte)-1, (sbyte)1)), "W = +Z (world depth), A = −X.");
            var diagonal = Step * 0.70710677f;
            Assert.That(Human(Last).Position.x, Is.EqualTo(start.x - diagonal).Within(1e-5f), "1/√2 per axis, from CombatMovementSystem.");
            Assert.That(Human(Last).Position.z, Is.EqualTo(start.z + diagonal).Within(1e-5f));
            Assert.That(Human(Last).Facing, Is.EqualTo(FacingDirection.Left));
            Frame(1, () =>
            {
                Up(Key.W);
                Up(Key.A);
            });
            Assert.That(HumanCommand(Last).IsNone, Is.True);

            (Vector2 stick, sbyte x, sbyte z)[] cases =
            {
                (new Vector2(0.1f, 0.15f), 0, 0), (new Vector2(0.9f, 0.3f), 1, 0), (new Vector2(0.7f, 0.7f), 1, 1),
                (new Vector2(-0.2f, -0.9f), 0, -1), (new Vector2(-0.6f, 0.4f), -1, 1), (Vector2.zero, 0, 0),
            };
            foreach (var (stick, x, z) in cases)
            {
                Frame(1, () => Stick(stick));
                Assert.That((HumanCommand(Last).MoveX, HumanCommand(Last).MoveZ), Is.EqualTo((x, z)), $"stick {stick}");
            }
        }

        [Test]
        public void GamepadButtonsMapToTheSameIntentions()
        {
            StartRig();
            Frame(1); // the battle's first tick drops presses made before it started
            Frame(1, () =>
            {
                Pad(GamepadButton.South, true);
                Pad(GamepadButton.RightShoulder, true);
                Pad(GamepadButton.West, true);
                Pad(GamepadButton.West, false);
            });
            var command = HumanCommand(Last);
            Assert.That(command.Held, Is.EqualTo(CommandButtons.Jump | CommandButtons.Guard));
            Assert.That(command.Pressed, Is.EqualTo(CommandButtons.Jump | CommandButtons.Guard | CommandButtons.Attack));
        }

        // ---- lifecycle ----

        [Test]
        public void RestartBetweenCatchUpTicksDropsTheOldBattlesPressesAndRebindsToTheNewHuman()
        {
            StartRig();
            Frame(1);
            m_Replay.RequestInStep(typeof(CombatStateUpdateSystemGroup), battle: 1, relativeTick: 2,
                BattleRequest.Restart(BattleSetup.Default));
            Frame(0, () => Tap(Key.K));
            Frame(4, () =>
            {
                Down(Key.D);
                Tap(Key.J); // pressed in battle 1's frame, consumed by its tick 2
            });
            var first = BattleTicks(1);
            Assert.That(first.Length, Is.EqualTo(2), "Battle 1 ends after its tick 2; the Restart applies before the next catch-up tick.");
            Assert.That(HumanCommand(first[1]).Pressed, Is.EqualTo(CommandButtons.Jump | CommandButtons.Attack));
            var second = BattleTicks(2);
            Assert.That(second.Length, Is.EqualTo(3), "The remaining three catch-up ticks of the frame belong to battle 2.");
            Assert.That(second[0].HumanId, Is.Not.EqualTo(first[0].HumanId));
            foreach (var record in second)
            {
                Assert.That(HumanCommand(record).Combatant.Value, Is.EqualTo(record.HumanId), "Bound to the new human.");
                Assert.That(HumanCommand(record).Pressed, Is.EqualTo(CommandButtons.None), "No press crosses into the new battle.");
                Assert.That(HumanCommand(record).MoveX, Is.EqualTo(1), "The live hold is the actual device state.");
                Assert.That(record.Results.Any(r => r.Command.Combatant.Value == first[0].HumanId), Is.False, "Nothing for the old human.");
            }

            // A press made while battle 2 runs, then a Restart between frames: the press belongs to battle 2 and is dropped.
            Frame(0, () => Tap(Key.K));
            m_Replay.Request(BattleRequest.Restart(BattleSetup.Default));
            Frame(3);
            Assert.That(BattleTicks(3).Select(r => HumanCommand(r).Pressed), Is.All.EqualTo(CommandButtons.None));
            Assert.That(BattleTicks(3).Select(r => HumanCommand(r).MoveX), Is.All.EqualTo(1));
        }

        [Test]
        public void IdleDespawnedOrDefeatedHumanGetsNoCommandAndStalePressesAreDropped()
        {
            StartRig();
            Frame(1);
            Frame(0, () => Tap(Key.K));
            m_Replay.Request(BattleRequest.End());
            Frame(3, () => Down(Key.D));
            Assert.That(m_Replay.Records.Skip(1).All(r => !r.Active && r.Results.Length == 0), Is.True, "Idle: nothing submitted.");
            Assert.That(m_Capture.PendingPressed, Is.EqualTo(CommandButtons.None), "Idle ticks drop presses.");

            Frame(0, () => Tap(Key.J));
            m_Replay.Request(BattleRequest.Start(BattleSetup.Default));
            Frame(2);
            Assert.That(BattleTicks(2).Select(r => HumanCommand(r).Pressed), Is.All.EqualTo(CommandButtons.None),
                "Presses from the idle period never reach the next battle.");
            Assert.That(HumanCommand(Last).MoveX, Is.EqualTo(1));

            // Defeat stand-in: a test disables InCombat (the future defeat owner's only write).
            var human = HumanEntity();
            m_Replay.Manager.SetComponentEnabled<InCombat>(human, false);
            Frame(2, () => Tap(Key.K));
            Assert.That(Last.Results, Is.Empty, "No command (and no UnknownCombatant) for a defeated human.");
            Assert.That(m_Capture.PendingPressed, Is.EqualTo(CommandButtons.None));
            m_Replay.Manager.SetComponentEnabled<InCombat>(human, true); // restore for the despawn case

            m_Replay.Request(BattleRequest.Despawn(m_Replay.Session.HumanCombatant));
            Frame(2, () => Tap(Key.K));
            Assert.That(Last.Results, Is.Empty, "No command for a despawned human.");
            Assert.That(Last.Combatant(Last.AiId).Applied.Command.Tick, Is.EqualTo(Last.Tick), "The AI keeps being simulated.");
        }

        Entity HumanEntity()
        {
            using var query = m_Replay.Manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(CombatantId) },
                Options = EntityQueryOptions.IgnoreComponentEnabledState,
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            var human = m_Replay.Session.HumanCombatant.Value;
            return entities.ToArray().Single(e => m_Replay.Manager.GetComponentData<CombatantId>(e).Value == human);
        }

        [Test]
        public void PauseCollectsNoPressesAndResumeUsesTheLiveHoldWithoutASyntheticPress()
        {
            StartRig();
            Frame(1);
            Frame(0, () => Tap(Key.J)); // a tap of the last unpaused frame, not consumed by any tick
            Time.timeScale = 0f;
            Frame(0, () => Tap(Key.K));
            Frame(0, () =>
            {
                Down(Key.K);
                Down(Key.D);
            });
            Frame(0);
            Assert.That(m_Capture.PendingPressed, Is.EqualTo(CommandButtons.None));
            Time.timeScale = 1f;
            Frame(2);
            foreach (var record in BattleTicks().Skip(1))
            {
                Assert.That(HumanCommand(record).Pressed, Is.EqualTo(CommandButtons.None), "No press from before or during the pause.");
                Assert.That(HumanCommand(record).Held, Is.EqualTo(CommandButtons.Jump), "Held is the actual state after the pause.");
                Assert.That(HumanCommand(record).MoveX, Is.EqualTo(1));
                Assert.That(Human(record).Motion.Grounded, Is.True, "Holding K through the pause does not jump on resume.");
            }

            Frame(1, () =>
            {
                Up(Key.K);
                Down(Key.K);
            });
            Assert.That(HumanCommand(Last).Pressed, Is.EqualTo(CommandButtons.Jump), "A fresh press after resume works.");
        }

        [Test]
        public void FocusLossAndDeviceLossNeutraliseInputAndDropPresses()
        {
            StartRig();
            Frame(1, () => Down(Key.D));
            Assert.That(HumanCommand(Last).MoveX, Is.EqualTo(1));
            Frame(0, () => Tap(Key.K));
            m_Capture.SetFocused(false);
            Frame(1, () => Tap(Key.J));
            Assert.That(HumanCommand(Last).IsNone, Is.True, "Unfocused: neutral, the fighter stops on the next tick.");
            var x = Human(Last).Position.x;
            m_Capture.SetFocused(true);
            Frame(1);
            Assert.That(HumanCommand(Last).MoveX, Is.EqualTo(1), "Focus back: the live hold.");
            Assert.That(HumanCommand(Last).Pressed, Is.EqualTo(CommandButtons.None), "Presses around the focus change are dropped.");
            Assert.That(Human(Last).Position.x, Is.GreaterThan(x));

            Frame(0, () => Tap(Key.K));
            InputSystem.RemoveDevice(m_Keyboard);
            Frame(1);
            Assert.That(HumanCommand(Last).IsNone, Is.True, "Removed keyboard: no move, no press.");
            m_Keyboard = InputSystem.AddDevice<Keyboard>();
            m_Keys.Clear();
            Frame(1);
            Assert.That(HumanCommand(Last).IsNone, Is.True, "A newly connected keyboard reports no stale state.");
            Frame(1, () => Down(Key.D));
            Assert.That(HumanCommand(Last).MoveX, Is.EqualTo(1), "…and drives the fighter.");
        }

        // ---- ownership ----

        [Test]
        public void TheLatestCaptureIsTheOnlyPlayerProducerAndDetachedOrDisposedCapturesProduceNothing()
        {
            StartRig();
            var second = new PlayerInputCapture(m_Asset);
            try
            {
                Frame(0, () => Tap(Key.K));
                m_Producer.Attach(second);
                Frame(2, () => Down(Key.D));
                Assert.That(m_Producer.Source, Is.SameAs(second));
                foreach (var record in BattleTicks())
                {
                    Assert.That(record.Results.Where(r => r.Label == "H").Select(r => r.Outcome), Is.EqualTo(new[] { CommandOutcome.Accepted }),
                        "One Player command per tick: no Duplicate/Conflict from a second source.");
                    Assert.That(HumanCommand(record).Pressed, Is.EqualTo(CommandButtons.None), "Attach drops pending presses.");
                }

                Assert.That(m_Producer.Detach(m_Capture), Is.False, "A replaced capture cannot detach its successor.");
                Assert.That(m_Producer.Source, Is.SameAs(second));
                Assert.That(m_Producer.Detach(second), Is.True);
                Frame(2, () => Tap(Key.K));
                Assert.That(Last.Results, Is.Empty);
                Assert.That(Human(Last).Applied.WasSubmitted, Is.False);

                m_Producer.Attach(second);
                second.Dispose();
                Frame(1, () => Tap(Key.K));
                Assert.That(Last.Results, Is.Empty, "A disposed capture is ignored, without exceptions.");
                Assert.That(second.TakePressed(), Is.EqualTo(CommandButtons.None));
            }
            finally
            {
                second.Dispose();
            }

            Assert.That(() => m_Producer.Attach(null), Throws.ArgumentNullException);
        }

        [Test]
        public void ReplayWorldsWithoutAnAttachedCaptureNeverReceiveHardwareCommands()
        {
            m_Replay = new ReplayWorld("No adapter");
            m_Replay.Request(BattleRequest.Start(BattleSetup.Default));
            Frame(3, () =>
            {
                Down(Key.D);
                Tap(Key.K);
            });
            Assert.That(BattleTicks().All(r => r.Results.Length == 0 && !Human(r).Applied.WasSubmitted), Is.True, "Replay World: no producer.");
            m_Replay.Dispose();

            StartRig(attach: false);
            m_TickFrames.Clear();
            Frame(3, () => Tap(Key.K));
            Assert.That(BattleTicks().All(r => r.Results.Length == 0), Is.True, "Producer without capture: inert.");
            Assert.That(m_Producer.Source, Is.Null);
        }

        [Test]
        public void TheProducerRunsInCollectionBeforeTheProcessorAndTheCaptureOwnsItsActions()
        {
            StartRig();
            var collection = m_Replay.World.GetExistingSystemManaged<CombatCommandCollectionSystemGroup>();
            using (var members = collection.GetAllSystems(Allocator.Temp))
            {
                var types = members.AsArray().ToArray().Select(m_Replay.World.Unmanaged.GetSystemTypeIndex).ToArray();
                var producer = TypeManager.GetSystemTypeIndex<PlayerInputCommandSystem>();
                Assert.That(types.Count(t => t == producer), Is.EqualTo(1));
                Assert.That(Array.IndexOf(types, producer), Is.LessThan(Array.IndexOf(types, TypeManager.GetSystemTypeIndex<CombatCommandProcessorSystem>())));
            }

            ReplayWorld.AssertStateOwners(m_Replay.World);
            var shared = m_Asset.FindActionMap(PlayerInputCapture.MapName);
            var wasEnabled = shared.enabled;
            m_Capture.Dispose();
            Assert.That(shared.enabled, Is.EqualTo(wasEnabled), "The shared asset is never enabled or disabled by a capture.");
            Assert.That(m_Capture.IsDisposed, Is.True);
            m_Capture.Dispose(); // idempotent
            m_Capture.Sample(out var x, out var z, out var held);
            Assert.That((x, z, held), Is.EqualTo(((sbyte)0, (sbyte)0, CommandButtons.None)));
            Assert.That(() => new PlayerInputCapture(null), Throws.ArgumentNullException);
        }

        // ---- shared command path ----

        /// <summary>
        /// The canonical commands accepted from the keyboard, replayed as AI-sourced commands for the same fighter in a fresh
        /// World, give bit-identical positions, motion and facing: one movement/facing rule for both sources.
        /// </summary>
        [Test]
        public void SameAcceptedIntentionsFromPlayerAndAiGiveTheSameMovementAndFacing()
        {
            StartRig();
            Frame(4, () => Down(Key.D));
            Frame(0, () => Tap(Key.K));
            Frame(5, () => Down(Key.W));
            Frame(3, () =>
            {
                Up(Key.D);
                Down(Key.A);
            });
            Frame(6, () => Up(Key.W));
            Frame(2, () => Up(Key.A));
            var player = BattleTicks();

            using var ai = new ReplayWorld("AI replay");
            ai.Request(BattleRequest.Start(BattleSetup.Default));
            foreach (var record in player)
            {
                var c = HumanCommand(record);
                ai.Script.Ai(record.RelativeTick, record.RelativeTick, ReplayRole.Human, c.MoveX, c.MoveZ, c.Held, c.Pressed);
            }

            ai.Step(player.Length);
            var replayed = ai.BattleRecords(1);
            Assert.That(replayed.Length, Is.EqualTo(player.Length));
            for (var i = 0; i < player.Length; i++)
            {
                var p = Human(player[i]);
                var a = Human(replayed[i]);
                Assert.That(a.Applied.Command.Source, Is.EqualTo(CommandSource.AI));
                Assert.That((a.Position, a.Motion, a.Facing), Is.EqualTo((p.Position, p.Motion, p.Facing)), $"tick r{i + 1}");
            }

            Assert.That(player.Any(r => !Human(r).Motion.Grounded), Is.True, "The sequence includes a jump.");
            Assert.That(player.Select(r => Human(r).Facing).Distinct().Count(), Is.EqualTo(2), "…and both facings.");
        }

        /// <summary>200 vs 200: one producer, one command for the human; every other fighter still gets every tick.</summary>
        [Test]
        public void LargeRosterKeepsSimulatingEveryFighterWhileOnlyTheHumanIsDriven()
        {
            var setup = BattleSetup.Default;
            setup.HumanAllies = setup.AiAllies = 199;
            StartRig(setup);
            Frame(10, () => Down(Key.D));
            Frame(10, () => Tap(Key.K));
            foreach (var record in BattleTicks())
            {
                Assert.That(record.Combatants.Length, Is.EqualTo(400));
                Assert.That(record.Combatants.All(c => c.HasApplied && c.Applied.Command.Tick == record.Tick), Is.True,
                    "All 400 combatants are simulated every tick, on screen or not.");
                Assert.That(record.Combatants.Count(c => c.Applied.WasSubmitted), Is.EqualTo(1));
                Assert.That(record.Results.Length, Is.EqualTo(1));
            }

            Assert.That(Human(Last).Position.x, Is.EqualTo(BattleSetup.Default.Human.Position.x + 20 * Step).Within(1e-4f));
            Assert.That(BattleTicks().Count(r => HumanCommand(r).Pressed == CommandButtons.Jump), Is.EqualTo(1));
        }
    }
}
