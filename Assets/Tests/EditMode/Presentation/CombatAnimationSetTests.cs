using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    // Stage 05.1: frame selection of the real mage manifest + project config against authoritative state. Only the tracked
    // JSON/config are read (no PNG needed); frames are compared by their manifest IDs.
    public class CombatAnimationSetTests
    {
        const string ManifestPath = "Assets/MLFLegends/Art/Characters/MageFemale01/Metadata/mage_female_01.atlas.json";
        const string ConfigPath = "Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset";
        const string Character = "mage_female_01";
        const uint Start = 100;

        static string Json => AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath).text;

        static CharacterAnimationConfig Config => AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>(ConfigPath);

        static SpriteAtlasDefinition Parse(string json)
        {
            var errors = new List<string>();
            var definition = SpriteAtlasManifest.Parse(json, errors);
            Assert.That(definition, Is.Not.Null, string.Join("\n", errors));
            return definition;
        }

        static CharacterAnimationSettings Project(string clip) =>
            Config.TryResolve(Character, clip, out _, out var settings, out _) ? settings : null;

        static CombatAnimationSet Build(SpriteAtlasDefinition definition, System.Func<string, CharacterAnimationSettings> settings,
            out List<string> errors, out List<string> warnings)
        {
            errors = new List<string>();
            warnings = new List<string>();
            return CombatAnimationSet.Create(definition, settings, Character, errors, warnings);
        }

        static CombatAnimationSet Real(out SpriteAtlasDefinition definition)
        {
            definition = Parse(Json);
            var set = Build(definition, Project, out var errors, out _);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));
            return set;
        }

        static string Id(SpriteAtlasDefinition d, uint frame) => d.Frames[(int)frame].Id;

        static CombatMotion Grounded() => new CombatMotion { Grounded = true };

        /// <summary>A strike started on <see cref="Start"/> with the windows <paramref name="set"/> derives (what the scene publishes).</summary>
        static CombatAttackState Striking(CombatAnimationSet set, CombatAttackKind kind) =>
            new CombatAttackState { Kind = kind, StartTick = Start, SeriesLength = 1, Timing = set.StrikeSchedule.Get(kind) };

        /// <summary>Phase a config local frame belongs to.</summary>
        static CombatAttackPhase PhaseOf(CharacterAnimationSettings settings, int local) =>
            local <= settings.PreparationEndFrame ? CombatAttackPhase.Windup
            : local <= settings.ActiveEndFrame ? CombatAttackPhase.Active : CombatAttackPhase.Recovery;

        static CharacterAnimationSettings Copy(CharacterAnimationSettings s) => new CharacterAnimationSettings
        {
            ClipId = s.ClipId, EndFrame = s.EndFrame, TotalDurationSeconds = s.TotalDurationSeconds, SpeedCurve = new AnimationCurve(s.SpeedCurve.keys),
            PreparationEndFrame = s.PreparationEndFrame, ActiveEndFrame = s.ActiveEndFrame, LandingStartFrame = s.LandingStartFrame,
        };

        // Artificial schedules declare every timing input; project edits must not silently shorten these fixtures.
        static CharacterAnimationSettings JumpFixture() => new CharacterAnimationSettings
        {
            ClipId = "jump", EndFrame = 5, TotalDurationSeconds = 0.8,
            SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1),
            PreparationEndFrame = 0, ActiveEndFrame = 0, LandingStartFrame = 4,
        };

        static CharacterAnimationSettings PunchFixture() => new CharacterAnimationSettings
        {
            ClipId = "punch_left", EndFrame = 5, TotalDurationSeconds = 29 / 60.0,
            SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1),
            PreparationEndFrame = 2, ActiveEndFrame = 3, LandingStartFrame = -1,
        };

        [Test]
        public void ProjectConfigBindsAllEightClipsAndKeepsTheOwnersIdleAndRunSettings()
        {
            Assert.That(Project("idle_combat").StartFrame, Is.Zero);
            Assert.That(Project("idle_combat").EndFrame, Is.EqualTo(5));
            Assert.That(Project("idle_combat").FrameCount, Is.EqualTo(6));
            Assert.That(Project("idle_combat").TotalDurationSeconds, Is.EqualTo(0.75));
            Assert.That(Project("run").FrameCount, Is.EqualTo(5));
            Assert.That(Project("run").TotalDurationSeconds, Is.EqualTo(0.5));
            var definition = Parse(Json);
            Assert.That(CombatAnimationSet.ClipIds.All(c => definition.TryGetClipIndex(c, out _)), Is.True, "All eight clips are in the manifest.");
            foreach (var absent in new[] { "idle_relaxed", "walk", "dash_start", "cast_release" })
                Assert.That(definition.TryGetClipIndex(absent, out _), Is.False, "Out-of-scope clips are absent and not required.");
            var set = Build(definition, Project, out var errors, out var warnings);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));
            Assert.That(warnings, Is.EqualTo(new[]
            {
                "mage_female_01.jump: local frame 0 (jump_000) is in no rise/fall/land marker and is never shown (ECS takeoff is immediate)",
                "mage_female_01.punch_right: Active frames 3..3 differ from the manifest's strike_pose 2..3 (the config decides)",
            }), "Current owner config: the unused anticipation and explicitly retuned right punch are reported.");
            Assert.That(Project("jump").EndFrame, Is.EqualTo(4));
            Assert.That(Project("jump").TotalDurationSeconds, Is.EqualTo(1.0));
            Assert.That(Project("jump").LandingStartFrame, Is.EqualTo(4));
            Assert.That((set.LandingStartFrame, set.LandingEndFrame), Is.EqualTo((4, 4)));
            Assert.That(set.LandingTicks, Is.EqualTo(12u), "Flat T=1 over five frames: one landing frame holds 1/5 s = 12 ticks.");
            for (uint e = 0; e < 12; e++)
                Assert.That(Id(definition, set.FrameFor(Start + e, 0,
                    new CombatMotion { Grounded = true, LandTick = Start, LandingRecoveryTicks = 12 }, default)),
                    Is.EqualTo("jump_004"), $"Project landing tick {e}: only configured frame 4.");
        }

        [TestCase(CombatAttackKind.PunchLeft, 14, 6, 9, 24)]
        [TestCase(CombatAttackKind.PunchRight, 14, 6, 9, 24)]
        [TestCase(CombatAttackKind.KickLeft, 12, 6, 12, 24)]
        [TestCase(CombatAttackKind.KickRight, 12, 6, 12, 24)]
        [TestCase(CombatAttackKind.KickSpin, 20, 5, 5, 65535)] // finisher: CombatStrikeTiming.NoChain
        public void ConfigPhasesAreTheSimulatedWindowsAndEveryTickDrawsAFrameOfItsPhase(CombatAttackKind kind, int windup, int active,
            int recovery, int chainPoint)
        {
            // 68c43c project config, flat curves: punches 29 ticks / 6 frames (phases 0..2 / 3 / 4..5),
            // kicks 30 / 5 (0..1 / 2 / 3..4), spin 30 / 6 (0..3 / 4 / 5).
            // Independently: Active starts at floor(T*60*(PreparationEnd+1)/N), Recovery at ceil(T*60*(ActiveEnd+1)/N).
            var set = Real(out var definition);
            var settings = Project(CombatAnimationSet.AttackClip(kind));
            var timing = set.StrikeSchedule.Get(kind);
            Assert.That((timing.WindupTicks, timing.ActiveTicks, timing.RecoveryTicks, (int)timing.ChainPoint),
                Is.EqualTo((windup, active, recovery, chainPoint)));
            definition.TryGetClipIndex(settings.ClipId, out var clipIndex);
            var clip = definition.Clips[clipIndex];
            var locals = new List<int>();
            for (uint e = 0; e < timing.End; e++)
            {
                var frame = set.FrameFor(Start + e, 0, Grounded(), Striking(set, kind));
                var local = clip.Frames.ToList().IndexOf((int)frame);
                locals.Add(local);
                Assert.That(PhaseOf(settings, local), Is.EqualTo(timing.PhaseAt(e)), $"tick {e}: {Id(definition, frame)}");
            }

            Assert.That(locals, Is.Ordered, "Never steps back a frame.");
            Assert.That(locals.Distinct(), Is.EqualTo(Enumerable.Range(settings.StartFrame, settings.FrameCount)), "Every configured frame shows at least one tick; excluded manifest frames never show.");
        }

        [Test]
        public void DurationCurveAndRangeMoveTheSimulatedWindowsAndTheDrawingFollowsThemAtAnyRenderRate()
        {
            var definition = Parse(Json);
            var fixture = PunchFixture();
            var flat = Build(definition, c => c == "punch_left" ? fixture : Project(c), out var baselineErrors, out _);
            Assert.That(flat, Is.Not.Null, string.Join("\n", baselineErrors));
            var steep = Copy(fixture);
            steep.TotalDurationSeconds = 1.0;
            steep.SpeedCurve = AnimationCurve.Linear(0, 0.2f, 1, 3f);
            var edited = Build(definition, c => c == "punch_left" ? steep : Project(c), out var errors, out _);
            Assert.That(edited, Is.Not.Null, string.Join("\n", errors));
            var a = flat.StrikeSchedule.PunchLeft;
            var b = edited.StrikeSchedule.PunchLeft;
            Assert.That(b.End, Is.EqualTo(60), "T = 1 s is 60 ticks: the strike itself is longer.");
            var timing = edited.Attack(CombatAttackKind.PunchLeft);
            Assert.That(b.ActiveStart, Is.EqualTo((int)System.Math.Floor(timing.ActiveStartSeconds * 60 + 1e-6)));
            Assert.That(b.RecoveryStart, Is.EqualTo((int)System.Math.Ceiling(timing.RecoveryStartSeconds * 60 - 1e-6)));
            Assert.That(timing.ActiveStartSeconds, Is.GreaterThan(0.5), "Slow start: the first half of the frames takes more than half of T.");
            Assert.That(b.ActiveStart, Is.Not.EqualTo(a.ActiveStart));
            // Rendering samples the authoritative tick only: repeated samples (any render rate) of one tick are identical,
            // and the drawn phase equals the simulated one on every tick.
            for (uint e = 0; e < b.End; e++)
            {
                var state = Striking(edited, CombatAttackKind.PunchLeft);
                var frame = edited.FrameFor(Start + e, 0, Grounded(), state);
                Assert.That(edited.FrameFor(Start + e, 0, Grounded(), state), Is.EqualTo(frame));
                var local = definition.Clips[definition.Clips.ToList().FindIndex(c => c.Id == "punch_left")].Frames.ToList().IndexOf((int)frame);
                Assert.That(PhaseOf(steep, local), Is.EqualTo(b.PhaseAt(e)), $"tick {e}");
            }

            // A shorter range (EndFrame 4: frame 5 excluded) plays only frames 0..4 over the same T and curve.
            var shortened = Copy(fixture);
            shortened.EndFrame = 4;
            var cut = Build(definition, c => c == "punch_left" ? shortened : Project(c), out errors, out _);
            Assert.That(cut, Is.Not.Null, string.Join("\n", errors));
            Assert.That(cut.Attack(CombatAttackKind.PunchLeft).FrameCount, Is.EqualTo(5));
            Assert.That(cut.Attack(CombatAttackKind.PunchLeft).DurationSeconds, Is.EqualTo(fixture.TotalDurationSeconds));
            for (uint e = 0; e < cut.StrikeSchedule.PunchLeft.End; e++)
                Assert.That(Id(definition, cut.FrameFor(Start + e, 0, Grounded(), Striking(cut, CombatAttackKind.PunchLeft))), Is.Not.EqualTo("punch_left_005"));
        }

        [Test]
        public void ARunningStrikeKeepsItsTimingWhenTheConfigChangesAndStillDrawsItsOwnPhase()
        {
            var definition = Parse(Json);
            var fixture = PunchFixture();
            var flat = Build(definition, c => c == "punch_left" ? fixture : Project(c), out var baselineErrors, out _);
            Assert.That(flat, Is.Not.Null, string.Join("\n", baselineErrors));
            var started = Striking(flat, CombatAttackKind.PunchLeft); // windows fixed when the strike started
            var longer = Copy(fixture);
            longer.TotalDurationSeconds = 2.0;
            var edited = Build(definition, c => c == "punch_left" ? longer : Project(c), out var errors, out _);
            Assert.That(edited, Is.Not.Null, string.Join("\n", errors));
            Assert.That(edited.StrikeSchedule.PunchLeft.End, Is.EqualTo(120));
            for (uint e = 0; e < started.Timing.End; e++)
            {
                var local = definition.Clips[definition.Clips.ToList().FindIndex(c => c.Id == "punch_left")].Frames.ToList()
                    .IndexOf((int)edited.FrameFor(Start + e, 0, Grounded(), started));
                Assert.That(PhaseOf(longer, local), Is.EqualTo(started.Timing.PhaseAt(e)), $"tick {e}: drawn phase = the strike's own phase");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void JumpFramesFollowTheMotionOwnersPhysicsAndLandingPlaysOnlyOnTheGround(bool projectLanding)
        {
            var definition = Parse(Json);
            var jump = projectLanding ? Project("jump") : JumpFixture();
            var set = Build(definition, clip => clip == "jump" ? jump : Project(clip), out var errors, out _);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));
            Assert.That(set.LandingTicks, Is.EqualTo(projectLanding ? 12u : 16u),
                "Project: one of five flat frames at T=1 (12 ticks); fixture: two of six at T=0.8 (16 ticks).");
            using var w = new ReplayWorld("jump frames");
            CombatStrikeSchedule.Publish(w.Manager, set.StrikeSchedule);
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Jump);
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step(90);
            var records = w.BattleRecords(1);
            var landing = System.Array.FindIndex(records, r => r.Combatant(r.HumanId).Motion.Grounded);
            Assert.That(landing, Is.GreaterThan(30));
            var contact = records[landing].Combatant(records[landing].HumanId).Motion;
            Assert.That(contact.LandingRecoveryTicks, Is.EqualTo(projectLanding ? 12u : 16u), "Movement copies the published duration at contact.");
            for (var i = 0; i < records.Length; i++)
            {
                var r = records[i];
                var c = r.Combatant(r.HumanId);
                var id = Id(definition, set.FrameFor(r.Tick, r.BattleStartTick, c.Motion, c.Attack));
                if (!c.Motion.Grounded)
                {
                    var expected = c.Motion.VerticalVelocity > 0f ? new[] { "jump_001", "jump_002" } : new[] { "jump_003" };
                    Assert.That(expected, Does.Contain(id), $"r{i + 1} airborne vy {c.Motion.VerticalVelocity}: rise/fall only, never landing");
                    Assert.That(c.Position.y, Is.GreaterThan(0f), "World height comes from CombatPosition.");
                }
                else if (i - landing < set.LandingTicks)
                    Assert.That(id, Is.EqualTo(projectLanding || i - landing < 8 ? "jump_004" : "jump_005"), $"r{i + 1}: landing on the ground");
                else
                    Assert.That(id, Does.StartWith("idle_combat_"), $"r{i + 1}");
            }

            var afterLanding = records[landing + (int)set.LandingTicks];
            Assert.That(Id(definition, set.FrameFor(afterLanding.Tick, afterLanding.BattleStartTick,
                afterLanding.Combatant(afterLanding.HumanId).Motion, afterLanding.Combatant(afterLanding.HumanId).Attack)), Is.EqualTo("idle_combat_000"),
                "Idle restarts at its first frame when entered.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LandingWhileMovingRecoversBeforeRunAndRunPhaseFollowsDistanceNotTime(bool projectLanding)
        {
            var definition = Parse(Json);
            var jump = projectLanding ? Project("jump") : JumpFixture();
            var set = Build(definition, clip => clip == "jump" ? jump : Project(clip), out var errors, out _);
            Assert.That(set, Is.Not.Null, string.Join("\n", errors));
            Assert.That(set.LandingTicks, Is.EqualTo(projectLanding ? 12u : 16u));
            using var w = new ReplayWorld("run frames");
            var script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            for (uint r = 2; r <= 120; r++)
                script.Player(r, r, ReplayRole.Human, moveX: 1, moveZ: (sbyte)(r > 80 ? 1 : 0));
            CombatStrikeSchedule.Publish(w.Manager, set.StrikeSchedule);
            w.Script = script;
            var setup = BattleSetup.Default;
            setup.Ai.Position = new float3(20, 0, 20); // Unobstructed stride/landing fixture, not enemy pressure.
            w.Request(BattleRequest.Start(setup));
            w.Step(120);
            var records = w.BattleRecords(1);
            var landing = System.Array.FindIndex(records, r => r.Combatant(r.HumanId).Motion.Grounded);
            var land = records[landing].Combatant(records[landing].HumanId);
            Assert.That(land.Motion.LandingRecoveryTicks, Is.EqualTo(projectLanding ? 12u : 16u));
            Assert.That(land.Motion.MoveStreakStartTick, Is.EqualTo(SimulationClock.NoTick), "Contact clears the run streak.");
            Assert.That(Id(definition, set.FrameFor(records[landing].Tick, 0, land.Motion, land.Attack)), Is.EqualTo("jump_004"));
            for (var i = landing; i < landing + set.LandingTicks; i++)
            {
                var c = records[i].Combatant(records[i].HumanId);
                Assert.That(c.Position.x, Is.EqualTo(land.Position.x), "Held movement cannot slide during landing recovery.");
                Assert.That(Id(definition, set.FrameFor(records[i].Tick, 0, c.Motion, c.Attack)), Does.StartWith("jump_"));
            }
            var firstRun = landing + (int)set.LandingTicks;
            var resumed = records[firstRun].Combatant(records[firstRun].HumanId);
            Assert.That(resumed.Motion.MoveStreakStartTick, Is.EqualTo(records[firstRun].Tick));
            Assert.That(Id(definition, set.FrameFor(records[firstRun].Tick, 0, resumed.Motion, resumed.Attack)), Is.EqualTo("run_000"));
            for (var i = firstRun; i < records.Length; i++)
            {
                var c = records[i].Combatant(records[i].HumanId);
                var frame = set.FrameFor(records[i].Tick, 0, c.Motion, c.Attack);
                Assert.That(frame, Is.EqualTo(set.Run.FrameAt(c.Motion.MoveStreakDistance / CombatMovementTuning.MoveSpeed)), $"r{i + 1}");
            }

            var straight = records[79].Combatant(records[79].HumanId).Motion.MoveStreakDistance - records[78].Combatant(records[78].HumanId).Motion.MoveStreakDistance;
            var diagonal = records[99].Combatant(records[99].HumanId).Motion.MoveStreakDistance - records[98].Combatant(records[98].HumanId).Motion.MoveStreakDistance;
            Assert.That(diagonal / straight, Is.EqualTo(1f).Within(1e-4f), "Normalized diagonals cover the same actual planar distance as straight motion.");
        }

        [Test]
        public void ShortStopsKeepTheRunStreakAndLongerStopsRestartIt()
        {
            using var w = new ReplayWorld("run streak");
            var script = new ReplayScript();
            for (uint r = 1; r <= 10; r++) script.Player(r, r, ReplayRole.Human, moveX: 1);
            for (uint r = 17; r <= 20; r++) script.Player(r, r, ReplayRole.Human, moveX: -1); // 6-tick stop, then reverse
            for (uint r = 28; r <= 30; r++) script.Player(r, r, ReplayRole.Human, moveX: 1); // 7-tick stop
            w.Script = script;
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step(30);
            var records = w.BattleRecords(1);
            uint Streak(int r) => records[r - 1].Combatant(records[r - 1].HumanId).Motion.MoveStreakStartTick - records[r - 1].BattleStartTick;
            Assert.That(Streak(10), Is.EqualTo(1u));
            Assert.That(Streak(17), Is.EqualTo(1u), "A stop of MoveStreakGraceTicks and a direction change keep the stride.");
            Assert.That(Streak(28), Is.EqualTo(28u), "A longer stop starts a new run from frame 0.");
        }

        [Test]
        public void ConfiguredKickRecoveryHandsOverToIdleAfterItsLastConfiguredFrame()
        {
            var set = Real(out var definition);
            foreach (var kind in new[] { CombatAttackKind.KickLeft, CombatAttackKind.KickRight, CombatAttackKind.KickSpin })
            {
                var last = Start + (uint)set.StrikeSchedule.Get(kind).End - 1;
                Assert.That(Id(definition, set.FrameFor(last, 0, Grounded(), Striking(set, kind))), Does.EndWith(kind == CombatAttackKind.KickSpin ? "_005" : "_004"));
                var free = new CombatAttackState { EndTick = last + 1 };
                Assert.That(Id(definition, set.FrameFor(last + 1, 0, Grounded(), free)), Is.EqualTo("idle_combat_000"));
            }
        }

        [TestCase(CombatAttackKind.PunchLeft)]
        [TestCase(CombatAttackKind.PunchRight)]
        [TestCase(CombatAttackKind.KickLeft)]
        [TestCase(CombatAttackKind.KickRight)]
        public void TheChainPointShowsAFrameTheArtMarksAsChainable(CombatAttackKind kind)
        {
            // The follow-up cuts the strike at its chain point (Recovery start + ChainDelay): the art's chain_exit frame must already show.
            var set = Real(out var definition);
            definition.TryGetClipIndex(CombatAnimationSet.AttackClip(kind), out var clipIndex);
            var clip = definition.Clips[clipIndex];
            var chainTick = Start + (uint)set.StrikeSchedule.Get(kind).ChainPoint;
            var local = clip.Frames.ToList().IndexOf((int)set.FrameFor(chainTick - 1, 0, Grounded(), Striking(set, kind)));
            Assert.That(local, Is.GreaterThanOrEqualTo(clip.Markers["chain_exit"][0]),
                "The last frame before the follow-up is at or after chain_exit (no visual skip of the recovery start).");
        }

        [Test]
        public void TheStrikeScheduleNeedsOnlyTheStrikeClipsAndReportsShortPhasesAndMarkerDifferences()
        {
            var definition = Parse(Json);
            var errors = new List<string>();
            var warnings = new List<string>();
            // Jump settings missing: the visual set is rejected, the simulation schedule is not affected.
            Assert.That(CombatAnimationSet.TryCreateStrikeSchedule(definition, c => c == "jump" ? null : Project(c), Character, errors, warnings,
                out var schedule), Is.True, string.Join("\n", errors));
            Assert.That(schedule.IsValid, Is.True);
            Assert.That(schedule.PunchLeft.End, Is.EqualTo(29));
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("punch_right: Active frames 3..3 differ"));

            // T = 1/60 s over 6 frames: Preparation [0, 0.5) tick, Active [0.5, 0.667), Recovery [0.667, 1): one Active tick,
            // no Preparation and no Recovery tick (warned), never a skipped Active phase.
            var tiny = PunchFixture();
            tiny.TotalDurationSeconds = 1 / 60.0;
            errors.Clear();
            Assert.That(CombatAnimationSet.TryCreateStrikeSchedule(definition, c => c == "punch_left" ? tiny : Project(c), Character, errors, warnings,
                out schedule), Is.True, string.Join("\n", errors));
            Assert.That(((int)schedule.PunchLeft.ActiveStart, (int)schedule.PunchLeft.RecoveryStart, (int)schedule.PunchLeft.End), Is.EqualTo((0, 1, 1)));
            Assert.That(warnings.Any(w => w.Contains("punch_left: Preparation lasts")), Is.True, string.Join("\n", warnings));
            Assert.That(warnings.Any(w => w.Contains("punch_left: Recovery lasts")), Is.True, string.Join("\n", warnings));

            var moved = PunchFixture();
            moved.ClipId = "punch_right";
            moved.PreparationEndFrame = 2;
            warnings.Clear();
            Assert.That(CombatAnimationSet.TryCreateStrikeSchedule(definition, c => c == "punch_right" ? moved : Project(c), Character, errors, warnings,
                out _), Is.True);
            Assert.That(warnings, Has.Some.Contains("punch_right: Active frames 3..3 differ from the manifest's strike_pose 2..3 (the config decides)"));
        }

        static string Without(string json, string clip, string marker, string replacement)
        {
            var at = json.IndexOf($"\"{clip}\": {{");
            var key = json.IndexOf($"\"{marker}\": [", at);
            var end = json.IndexOf(']', key) + 1;
            return json.Substring(0, key) + replacement + json.Substring(end);
        }

        [Test]
        public void MissingOrInvalidDataIsRejectedWithFieldDiagnostics()
        {
            void Rejected(string json, System.Func<string, CharacterAnimationSettings> settings, string expected)
            {
                var set = Build(Parse(json), settings, out var errors, out _);
                Assert.That(set, Is.Null);
                Assert.That(errors.Any(e => e.Contains(expected)), Is.True, $"Expected '{expected}' in:\n{string.Join("\n", errors)}");
            }

            var legacyJump = JumpFixture();
            legacyJump.LandingStartFrame = -1; // Explicit config ranges intentionally do not require the old marker.
            Rejected(Without(Json, "jump", "land", "\"landing\": [4, 5]"),
                c => c == "jump" ? legacyJump : Project(c), "mage_female_01.jump.markers.land: missing");
            Rejected(Without(Json, "jump", "fall", "\"fall\": [1]"), Project, "jump.markers: rise, fall and land must be disjoint and in this order");
            Rejected(Json, c => c == "kick_spin" ? null : Project(c), "mage_female_01.kick_spin: animation settings missing");
            CharacterAnimationSettings Punch(int end, int preparationEnd, int activeEnd)
            {
                var punch = PunchFixture();
                punch.EndFrame = end;
                punch.PreparationEndFrame = preparationEnd;
                punch.ActiveEndFrame = activeEnd;
                return punch;
            }

            // EndFrame shortened below the phases: Recovery would be empty. Reported, never silently moved.
            Rejected(Json, c => c == "punch_left" ? Punch(3, 2, 3) : Project(c), "punch_left.ActiveEndFrame: 3; Recovery 4..3 must hold at least one frame (EndFrame 3)");
            Rejected(Json, c => c == "punch_left" ? Punch(5, -1, 3) : Project(c), "punch_left.PreparationEndFrame: -1; Preparation 0..-1 must hold at least one frame");
            Rejected(Json, c => c == "punch_left" ? Punch(5, 5, 5) : Project(c), "punch_left.PreparationEndFrame: 5");
            Rejected(Json, c => c == "punch_left" ? Punch(5, 2, 2) : Project(c), "punch_left.ActiveEndFrame: 2; Active 3..2 must hold at least one frame");
            Rejected(Json, c => c == "punch_left" ? Punch(1, 0, 1) : Project(c), "punch_left: a strike needs at least 3 frames (Preparation, Active, Recovery), StartFrame 0..EndFrame 1 has 2");
            Rejected(Json, c => c == "punch_left" ? Punch(0, 0, 0) : Project(c), "a strike needs at least 3 frames");
            // Legacy (unmarked) phases are rejected rather than guessed.
            var legacy = new CharacterAnimationSettings { ClipId = "kick_left", EndFrame = 5, TotalDurationSeconds = 0.55 };
            Rejected(Json, c => c == "kick_left" ? legacy : Project(c), "kick_left.ActiveEndFrame: 0; Active 1..0 must hold at least one frame");
            // After an atlas update that shortens a clip, a stale EndFrame names the field and the clip's current range.
            var spin = Copy(Project("kick_spin"));
            spin.EndFrame = 12;
            Rejected(Json, c => c == "kick_spin" ? spin : Project(c), "kick_spin.EndFrame: 12 is outside the clip's local frames 0..8 (0-based clip indices; was the atlas clip shortened?)");
            var looping = new CharacterAnimationSettings { ClipId = "idle_combat", EndFrame = 5, TotalDurationSeconds = 1 };
            var renamed = Json.Replace("\"idle_combat\": {", "\"idle_renamed\": {").Replace("\"to_clip\": \"idle_combat\"", "\"to_clip\": \"idle_renamed\"");
            Rejected(renamed, c => c == "idle_combat" ? looping : Project(c), "mage_female_01.idle_combat: clip missing from manifest");
        }
    }
}
