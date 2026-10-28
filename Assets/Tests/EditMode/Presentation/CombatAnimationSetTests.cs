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

        static CombatAttackState Striking(CombatAttackKind kind) => new CombatAttackState { Kind = kind, StartTick = Start, SeriesLength = 1 };

        [Test]
        public void ProjectConfigBindsAllEightClipsAndKeepsTheOwnersIdleAndRunSettings()
        {
            Assert.That(Project("idle_combat").FrameCount, Is.EqualTo(6));
            Assert.That(Project("idle_combat").TotalDurationSeconds, Is.EqualTo(1.0));
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
            }), "Config T equals every authoritative strike; only the explicit unused-anticipation note remains.");
            Assert.That(set.LandingTicks, Is.EqualTo(16u), "land = 2 of 6 equal shares of 0.8 s.");
        }

        [TestCase(CombatAttackKind.PunchLeft)]
        [TestCase(CombatAttackKind.PunchRight)]
        [TestCase(CombatAttackKind.KickLeft)]
        [TestCase(CombatAttackKind.KickRight)]
        [TestCase(CombatAttackKind.KickSpin)]
        public void StrikePoseFramesCoverExactlyTheActiveTicksAndEveryFrameShowsInOrder(CombatAttackKind kind)
        {
            var set = Real(out var definition);
            definition.TryGetClipIndex(CombatAnimationSet.AttackClip(kind), out var clipIndex);
            var clip = definition.Clips[clipIndex];
            var pose = clip.Markers["strike_pose"];
            var strike = CombatAttackTuning.Get(kind);
            var locals = new List<int>();
            for (uint e = 0; e < strike.TotalTicks; e++)
            {
                var frame = set.FrameFor(Start + e, 0, Grounded(), Striking(kind));
                var local = clip.Frames.ToList().IndexOf((int)frame);
                locals.Add(local);
                var active = CombatAttackTuning.PhaseAt(strike, e) == CombatAttackPhase.Active;
                Assert.That(pose.Contains(local), Is.EqualTo(active), $"tick {e}: {Id(definition, frame)}");
            }

            Assert.That(locals, Is.Ordered, "Never steps back a frame.");
            Assert.That(locals.Distinct(), Is.EqualTo(Enumerable.Range(0, clip.Frames.Count)), "Every authored frame shows at least one tick.");
        }

        [Test]
        public void CurveAndDurationChangeFrameSharesButNeverTheActiveWindow()
        {
            var definition = Parse(Json);
            var flat = Real(out _);
            var steep = new CharacterAnimationSettings
            {
                ClipId = "punch_left", FrameCount = 6, TotalDurationSeconds = 1.0, SpeedCurve = AnimationCurve.Linear(0, 0.2f, 1, 3f),
            };
            var edited = Build(definition, c => c == "punch_left" ? steep : Project(c), out var errors, out var warnings);
            Assert.That(edited, Is.Not.Null, string.Join("\n", errors));
            Assert.That(warnings.Any(w => w.Contains("punch_left.TotalDurationSeconds") && w.Contains("29 ticks")), Is.True,
                "A T that differs from the authoritative strike is reported, not silently ignored.");
            var strike = CombatAttackTuning.Get(CombatAttackKind.PunchLeft);
            var differs = false;
            for (uint e = 0; e < strike.TotalTicks; e++)
            {
                var a = Id(definition, flat.FrameFor(Start + e, 0, Grounded(), Striking(CombatAttackKind.PunchLeft)));
                var b = Id(definition, edited.FrameFor(Start + e, 0, Grounded(), Striking(CombatAttackKind.PunchLeft)));
                Assert.That(b == "punch_left_003", Is.EqualTo(a == "punch_left_003"), $"tick {e}: the strike pose stays on the Active ticks");
                differs |= a != b;
            }

            Assert.That(differs, Is.True, "The curve redistributes windup/recovery frames.");
        }

        [Test]
        public void JumpFramesFollowTheMotionOwnersPhysicsAndLandingPlaysOnlyOnTheGround()
        {
            var set = Real(out var definition);
            using var w = new ReplayWorld("jump frames");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: CommandButtons.Jump);
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step(90);
            var records = w.BattleRecords(1);
            var landing = System.Array.FindIndex(records, r => r.Combatant(r.HumanId).Motion.Grounded);
            Assert.That(landing, Is.GreaterThan(30));
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
                    Assert.That(id, Is.EqualTo(i - landing < 8 ? "jump_004" : "jump_005"), $"r{i + 1}: landing on the ground");
                else
                    Assert.That(id, Does.StartWith("idle_combat_"), $"r{i + 1}");
            }

            var afterLanding = records[landing + (int)set.LandingTicks];
            Assert.That(Id(definition, set.FrameFor(afterLanding.Tick, afterLanding.BattleStartTick,
                afterLanding.Combatant(afterLanding.HumanId).Motion, afterLanding.Combatant(afterLanding.HumanId).Attack)), Is.EqualTo("idle_combat_000"),
                "Idle restarts at its first frame when entered.");
        }

        [Test]
        public void LandingWhileMovingRunsAtOnceAndRunPhaseFollowsDistanceNotTime()
        {
            var set = Real(out var definition);
            using var w = new ReplayWorld("run frames");
            var script = new ReplayScript().Player(1, 1, ReplayRole.Human, moveX: 1, pressed: CommandButtons.Jump);
            for (uint r = 2; r <= 120; r++)
                script.Player(r, r, ReplayRole.Human, moveX: 1, moveZ: (sbyte)(r > 80 ? 1 : 0));
            w.Script = script;
            w.Request(BattleRequest.Start(BattleSetup.Default));
            w.Step(120);
            var records = w.BattleRecords(1);
            var landing = System.Array.FindIndex(records, r => r.Combatant(r.HumanId).Motion.Grounded);
            var land = records[landing].Combatant(records[landing].HumanId);
            Assert.That(land.Motion.MoveStreakStartTick, Is.EqualTo(records[landing].Tick), "Landing restarts the movement streak.");
            Assert.That(Id(definition, set.FrameFor(records[landing].Tick, 0, land.Motion, land.Attack)), Is.EqualTo("run_000"),
                "Landing with movement: run from its first frame, no landing pose.");
            for (var i = landing; i < records.Length; i++)
            {
                var c = records[i].Combatant(records[i].HumanId);
                var frame = set.FrameFor(records[i].Tick, 0, c.Motion, c.Attack);
                Assert.That(frame, Is.EqualTo(set.Run.FrameAt(c.Motion.MoveStreakDistance / CombatMovementTuning.MoveSpeed)), $"r{i + 1}");
            }

            var straight = records[79].Combatant(records[79].HumanId).Motion.MoveStreakDistance - records[78].Combatant(records[78].HumanId).Motion.MoveStreakDistance;
            var diagonal = records[99].Combatant(records[99].HumanId).Motion.MoveStreakDistance - records[98].Combatant(records[98].HumanId).Motion.MoveStreakDistance;
            Assert.That(diagonal / straight, Is.EqualTo(math.SQRT2 / 2).Within(1e-4f), "Diagonal motion strides proportionally slower.");
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
        public void KickRecoveryHandsOverToIdleOnItsDeclaredCompatibleFrame()
        {
            var set = Real(out var definition);
            foreach (var kind in new[] { CombatAttackKind.KickLeft, CombatAttackKind.KickRight, CombatAttackKind.KickSpin })
            {
                var strike = CombatAttackTuning.Get(kind);
                var last = Start + (uint)strike.TotalTicks - 1;
                Assert.That(Id(definition, set.FrameFor(last, 0, Grounded(), Striking(kind))), Does.EndWith(kind == CombatAttackKind.KickSpin ? "_008" : "_005"));
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
            // The follow-up cuts the strike at Windup + Active + ChainDelay: the art's chain_exit frame must already show.
            var set = Real(out var definition);
            definition.TryGetClipIndex(CombatAnimationSet.AttackClip(kind), out var clipIndex);
            var clip = definition.Clips[clipIndex];
            var strike = CombatAttackTuning.Get(kind);
            var chainTick = Start + (uint)(strike.WindupTicks + strike.ActiveTicks + strike.ChainDelayTicks);
            var local = clip.Frames.ToList().IndexOf((int)set.FrameFor(chainTick - 1, 0, Grounded(), Striking(kind)));
            Assert.That(local, Is.GreaterThanOrEqualTo(clip.Markers["chain_exit"][0]),
                "The last frame before the follow-up is at or after chain_exit (no visual skip of the recovery start).");
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

            Rejected(Without(Json, "kick_left", "strike_pose", "\"strike_posed\": [2]"), Project, "mage_female_01.kick_left.markers.strike_pose: missing");
            Rejected(Without(Json, "jump", "land", "\"landing\": [4, 5]"), Project, "mage_female_01.jump.markers.land: missing");
            Rejected(Without(Json, "punch_right", "strike_pose", "\"strike_pose\": [1, 3]"), Project, "punch_right.markers.strike_pose: frames must be consecutive");
            Rejected(Without(Json, "jump", "fall", "\"fall\": [1]"), Project, "jump.markers: rise, fall and land must be disjoint and in this order");
            var truncated = new CharacterAnimationSettings { ClipId = "punch_left", FrameCount = 4, TotalDurationSeconds = 29 / 60.0 };
            Rejected(Json, c => c == "punch_left" ? truncated : Project(c), "punch_left: strike_pose [3..3] needs at least one windup frame before it and one recovery frame");
            Rejected(Json, c => c == "kick_spin" ? null : Project(c), "mage_female_01.kick_spin: animation settings missing");
            var looping = new CharacterAnimationSettings { ClipId = "idle_combat", FrameCount = 6, TotalDurationSeconds = 1 };
            var renamed = Json.Replace("\"idle_combat\": {", "\"idle_renamed\": {").Replace("\"to_clip\": \"idle_combat\"", "\"to_clip\": \"idle_renamed\"");
            Rejected(renamed, c => c == "idle_combat" ? looping : Project(c), "mage_female_01.idle_combat: clip missing from manifest");
        }
    }
}
