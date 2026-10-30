using System;
using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    public class CombatStrikeEffectsTests
    {
        internal static Entity Actor(ReplayWorld w, CombatantId id)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatantId)); using var e = q.ToEntityArray(Allocator.Temp);
            return e.ToArray().Single(x => w.Manager.GetComponentData<CombatantId>(x).Value == id.Value);
        }
        internal static Entity StreamEntity(ReplayWorld w)
        { using var q = w.Manager.CreateEntityQuery(typeof(CombatEffectStream)); return q.GetSingletonEntity(); }
        internal static CombatEffectFact[] Facts(ReplayWorld w)
        {
            var e = StreamEntity(w); var stream = w.Manager.GetComponentData<CombatEffectStream>(e);
            var b = w.Manager.GetBuffer<CombatEffectFact>(e, true); var facts = new CombatEffectFact[stream.Count];
            for (var i = 0; i < facts.Length; i++) facts[i] = b[(stream.First + i) % CombatEffectStream.Capacity];
            return facts;
        }
        internal static void BindTechnical(ReplayWorld w, ref CombatStrikeSchedule table)
        {
            var combat = new CharacterCombatSettings();
            for (var k = CombatAttackKind.Hand1; k <= CombatAttackKind.PhysicalAbility; k++)
            {
                combat.Strike(k).Swing = new CharacterEffectSettings { Source = CombatEffectSource.Technical, ClipId = "swing", Offset = new Vector3(.4f, 1, 0) };
                combat.Strike(k).Impact = new CharacterEffectSettings { Source = CombatEffectSource.Technical, ClipId = "impact", DurationSeconds = .2f };
            }
            w.Manager.GetComponentObject<CombatEffectResources>(StreamEntity(w)).Prepare(ref table, combat, CombatAnimationFixture.Set(), null, Vector2.zero);
        }
        internal static void Start(ReplayWorld w, CombatStrikeSchedule table, float distance = .8f, int allies = 0)
        {
            CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = float3.zero; setup.Ai.Position = new float3(distance, 0, 0);
            setup.AiAllies = (ushort)allies;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
        }
        static SpriteCardBasis Basis => SpriteCardBasis.FromCamera(new float3(0, -1, 1), new float3(0, 1, 1));
        internal static void Refresh(CombatEffectsPresentation p, ReplayWorld w) =>
            p.Refresh(w.World, null, Basis, 2, .02f, null, null, null, 0, false);

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void SwingAnchorAndLifetimeMatchActualBodyIncludingLastActive(bool left, bool subTick)
        {
            using var w = new ReplayWorld("body and Swing integration");
            var definition = CombatAnimationFixture.Definition(); var errors = new List<string>();
            var set = CombatAnimationSet.Create(definition, id => {
                var settings = CombatAnimationFixture.Settings(definition, id);
                settings.SpeedCurve = new AnimationCurve(new Keyframe(0, .2f), new Keyframe(.5f, 2), new Keyframe(1, .7f));
                return settings;
            }, "nonuniform", errors);
            Assert.That(set, Is.Not.Null, string.Join(";", errors));
            var table = set.StrikeSchedule;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 4, 2, 0);
            if (subTick) { table.Hand1.PreciseActiveWindow = true; table.Hand1.ActiveStartTicks = .1; table.Hand1.RecoveryStartTicks = 3.2; }
            BindTechnical(w, ref table);
            var binding = w.Manager.GetComponentObject<CombatEffectResources>(StreamEntity(w)).Get(table.Hand1Strike.SwingEffect);
            // Unique calibrated anchors on the three distinct body poses; no timing oracle shared with production.
            binding.BodyTiming = set.Attack(CombatAttackKind.Hand1);
            binding.Settings.Anchor = CombatEffectAnchor.HandLeft; binding.Settings.Offset = Vector3.zero;
            binding.BodyNominalPixels = 2;
            binding.AnchorPixels = new[] { new float2(1, 0), new float2(2, 0), new float2(3, 0) };
            binding.AnchorVisible = new[] { true, true, true };
            binding.AnchorDirections = new[] { new float2(1,0), new float2(1,0), new float2(1,0) };
            binding.AnchorLayers = new SpriteAnchorLayer[3];
            Start(w, table, left ? -20 : 20);
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries);
            using var p = new CombatEffectsPresentation();
            for (uint tick = 1; tick <= 5; tick++)
            {
                w.Step(); Refresh(p, w);
                var e = Actor(w, w.Session.HumanCombatant);
                var attack = w.Manager.GetComponentData<CombatAttackState>(e);
                var motion = w.Manager.GetComponentData<CombatMotion>(e);
                var body = set.FrameFor(tick, w.Session.BattleStartTick, motion, attack);
                if (tick <= 4)
                {
                    Assert.That(body, Is.EqualTo(CombatAnimationFixture.TechC), "The phase clamp keeps the actual body Active.");
                    Assert.That(p.Stats.Alive, Is.EqualTo(1), "Swing must survive the final displayed Active tick.");
                    var support = w.Manager.GetComponentData<CombatPosition>(e).Value;
                    var expected = SpriteCardMath.CardToWorld(Basis, support, new float2(left ? -2 : 2, 0), 1);
                    Assert.That(math.distance(p.GetInstance(0).Position, expected), Is.LessThan(1e-6f));
                    Refresh(p, w); Assert.That(p.Stats.Alive, Is.EqualTo(1), "Repeated/paused presentation is read-only.");
                }
                else
                {
                    Assert.That(body, Is.EqualTo(CombatAnimationFixture.TechB));
                    Assert.That(p.Stats.Alive, Is.Zero, "First Recovery has no Swing.");
                }
            }
        }

        [Test]
        public void MovingOrdinaryContactUsesFinalTimeAndFinalWaistDespitePartialWindow()
        {
            using var w = new ReplayWorld("ordinary sample time"); var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 1, 2, 0);
            table.Hand1.PreciseActiveWindow = true; table.Hand1.ActiveStartTicks = .1; table.Hand1.RecoveryStartTicks = .2;
            Start(w, table);
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries);
            w.Script.Ai(1, 1, ReplayRole.Ai, moveX: 1);
            w.Step();
            var impact = Facts(w).Single(f => f.Kind == CombatEffectKind.Impact);
            var target = Actor(w, w.Session.AiCombatant);
            var position = w.Manager.GetComponentData<CombatPosition>(target).Value;
            Assert.That(impact.Time, Is.EqualTo(1d));
            Assert.That(impact.Position, Is.EqualTo(position + new float3(0, table.Body.Height / 2, 0)));
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(Actor(w, w.Session.HumanCombatant)).Length, Is.EqualTo(1));
        }

        [TestCase(.25, .75)] [TestCase(1, 2)] [TestCase(1.9, 2.01)]
        public void DamageIntervalIsHalfOpenAndIndependentOfVisualPhases(double first, double end)
        {
            var t = CombatStrikeTiming.FromTicks(1, 1, 5, 1);
            t.PreciseActiveWindow = true; t.ActiveStartTicks = first; t.RecoveryStartTicks = 2;
            t.OverrideDamageDuration = true; t.DamageEndTicks = end;
            for (uint tick = 0; tick < 9; tick++)
                Assert.That(t.DealsDamageOn(tick), Is.EqualTo(tick < 7 && tick < end && tick + 1 > first));
            Assert.That(t.SwingEnd, Is.EqualTo(2)); Assert.That(t.PhaseAt(3), Is.EqualTo(CombatAttackPhase.Recovery));
            t.DamageWindow((uint)Math.Floor(first), out var lo, out var hi);
            Assert.That(lo, Is.EqualTo(first - Math.Floor(first)).Within(1e-6));
            Assert.That(hi, Is.EqualTo(Math.Min(1, end - Math.Floor(first))).Within(1e-6));
        }

        [TestCase(0f)] [TestCase(-.1f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(100f)]
        public void InvalidOverrideIsRejectedWithoutChangingPhases(float seconds)
        {
            var settings = new CharacterCombatSettings(); var set = CombatAnimationFixture.Set(); var t = set.StrikeSchedule;
            var before = t.PhysicalAbility;
            settings.PhysicalAbility.OverrideDamageDuration = true; settings.PhysicalAbility.DamageDurationSeconds = seconds;
            Assert.Throws<ArgumentException>(() => settings.ApplyTo(ref t, set));
            Assert.That(t.PhysicalAbility.ActiveStart, Is.EqualTo(before.ActiveStart));
            Assert.That(t.PhysicalAbility.RecoveryStart, Is.EqualTo(before.RecoveryStart));
        }

        [TestCase(0)] [TestCase(4)]
        public void HpZeroStillHasOneLedgerPerTargetButNoDamagingFeedback(int damage)
        {
            using var w = new ReplayWorld("effects confirmation"); var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 4, 2, 0); table.Hand1Strike.Damage = damage;
            table.Body.Radius = .05f; BindTechnical(w, ref table); Start(w, table, .8f, 1);
            using var actors = w.Manager.CreateEntityQuery(typeof(CombatTeam), typeof(CombatantId));
            using var entities = actors.ToEntityArray(Allocator.Temp); var n = 0;
            foreach (var e in entities) if (w.Manager.GetComponentData<CombatTeam>(e).Value == 2)
            {
                w.Manager.SetComponentData(e, new CombatPosition { Value = new float3(.8f, 0, n++ * .15f) });
                w.Manager.SetComponentData(e, new CombatHealth { Current = 0, Max = 100 });
            }
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries);
            w.Frame(4);
            var facts = Facts(w);
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Swing), Is.EqualTo(1));
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Impact), Is.Zero);
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(Actor(w, w.Session.HumanCombatant)).Length, Is.EqualTo(2));
            foreach (var e in entities) if (w.Manager.GetComponentData<CombatTeam>(e).Value == 2)
                Assert.That(w.Manager.GetComponentData<CombatDamageFeedback>(e).LastDamageTick, Is.Zero);
        }

        [Test]
        public void MissHeldAndJumpPriorityDoNotManufactureImpactsOrExtraSwings()
        {
            using var w = new ReplayWorld("effects miss"); var table = CombatStrikeSchedule.Default; BindTechnical(w, ref table); Start(w, table, 20);
            w.Script.Player(1, 1, ReplayRole.Human, held: CommandButtons.HandSeries, pressed: CommandButtons.HandSeries);
            for (uint tick = 2; tick <= 60; tick++) w.Script.Player(tick, tick, ReplayRole.Human, held: CommandButtons.HandSeries);
            w.Frame(10); w.Frame(10); w.Frame(10); w.Frame(10); w.Frame(10); w.Frame(10);
            Assert.That(Facts(w).Length, Is.EqualTo(1)); Assert.That(Facts(w)[0].Kind, Is.EqualTo(CombatEffectKind.Swing));
            w.Script.Player(61, 61, ReplayRole.Human, pressed: CommandButtons.Jump | CommandButtons.PhysicalAbility); w.Step();
            Assert.That(Facts(w).Length, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void LongerOCanHitLateRecoveryTargetExactlyOnceWithoutExtendingSwing(bool extended)
        {
            using var w = new ReplayWorld("O damage duration"); var table = CombatStrikeSchedule.Default;
            var t = CombatStrikeTiming.FromTicks(1, 1, 8, CombatAttackTuning.NoChain);
            t.OverrideDamageDuration = extended; t.DamageEndTicks = 9; table.PhysicalAbility = t;
            table.Body.Radius = .05f;
            var strike = table.PhysicalAbilityStrike; strike.ReachMax = .5f; strike.SelfImpulse = 12;
            strike.LaunchTick = 1; strike.ElevationDegrees = 0; strike.OnHitImpulse = new float3(0, 1, 0);
            table.PhysicalAbilityStrike = strike; BindTechnical(w, ref table); Start(w, table, 1.5f);
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.PhysicalAbility);
            w.Frame(10);
            var impacts = Facts(w).Where(f => f.Kind == CombatEffectKind.Impact).ToArray();
            Assert.That(impacts.Length, Is.EqualTo(extended ? 1 : 0));
            if (extended) Assert.That(impacts[0].Time, Is.GreaterThan(2), "Contact occurs after original Active, during graphic Recovery.");
            var swing = Facts(w).Single(f => f.Kind == CombatEffectKind.Swing);
            Assert.That(swing.End, Is.EqualTo(3));
            Assert.That(w.Records.Sum(r => r.Impulses.Length), Is.EqualTo(extended ? 1 : 0), "One authored on-hit impulse, no ledger reset during long window.");
            using var presentation = new CombatEffectsPresentation(); Refresh(presentation, w);
            Assert.That(presentation.Stats.Alive, Is.EqualTo(extended ? 1 : 0), "Swing expired; only live short Impact remains.");
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void CatchUpAndRepeatedRefreshPreserveEventsAndReadOnlyReplay(int pacing)
        {
            string[] Run(bool vfx, out string[] events)
            {
                using var w = new ReplayWorld("read-only effect replay"); var table = CombatStrikeSchedule.Default;
                table.Hand1 = CombatStrikeTiming.FromTicks(0, 1, 1, 0); BindTechnical(w, ref table); Start(w, table);
                for (uint tick = 1; tick <= 30; tick++) w.Script.Player(tick, tick, ReplayRole.Human, pressed: CommandButtons.HandSeries | (tick == 4 ? CommandButtons.FocusToggle : 0));
                using var p = new CombatEffectsPresentation();
                for (var frame = 0; frame < 30; frame += pacing)
                {
                    w.Frame(Math.Min(pacing, 30 - frame));
                    if (vfx) { Refresh(p, w); var count = p.Stats.Alive; Refresh(p, w); Assert.That(p.Stats.Alive, Is.EqualTo(count)); }
                }
                events = Facts(w).Select(f => $"{f.Sequence}:{f.Kind}:{f.StartTick}:{f.Time:R}:{f.Position}").ToArray();
                return w.Records.SelectMany(r => r.Normalized).ToArray();
            }
            var a = Run(false, out var fa); var b = Run(true, out var fb);
            CollectionAssert.AreEqual(a, b); CollectionAssert.AreEqual(fa, fb);
        }

        [Test]
        public void PauseRestartDespawnAndWorldChangeDoNotReplayExpiredHistory()
        {
            using var w = new ReplayWorld("effect lifecycle"); var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 2, 1, 0); BindTechnical(w, ref table); Start(w, table);
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries); w.Step();
            using var p = new CombatEffectsPresentation(); Refresh(p, w); Assert.That(p.Stats.Alive, Is.EqualTo(2));
            var saved = p.GetInstance(0);
            var pausedTick = w.Tick;
            w.World.SetTime(new Unity.Core.TimeData(w.World.Time.ElapsedTime, 0)); w.FixedStep.Update();
            Assert.That(w.Tick, Is.EqualTo(pausedTick));
            Refresh(p, w); Assert.That(p.GetInstance(0).Frame, Is.EqualTo(saved.Frame));
            w.Request(BattleRequest.Despawn(w.Session.HumanCombatant)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Refresh(p, w); Assert.That(p.Stats.Alive, Is.EqualTo(1), "World contact flash survives despawn; attached Swing does not.");
            w.Request(BattleRequest.End()); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Refresh(p, w); Assert.That(p.Stats.Alive, Is.Zero); Assert.That(Facts(w), Is.Empty);
            w.Request(BattleRequest.Start(BattleSetup.Default)); w.Step(); Refresh(p, w); Assert.That(p.Stats.Alive, Is.Zero);
            using var other = new ReplayWorld("different effect World"); Start(other, CombatStrikeSchedule.Default);
            Refresh(p, other); Assert.That(p.Stats.Alive, Is.Zero);
        }

        [TestCase(CombatEffectMissingAnchor.FallbackOffset, 1)] [TestCase(CombatEffectMissingAnchor.Skip, 0)]
        public void MissingAnchorHasExplicitFallbackAndMirrorsOffsetExactlyOnce(CombatEffectMissingAnchor policy, int alive)
        {
            using var w = new ReplayWorld("VFX anchor fallback"); var pages = TechnicalAtlasFixture.CreatePages();
            var json = new TextAsset(CombatAnimationFixture.Manifest()); var catalog = ScriptableObject.CreateInstance<SpriteAtlasCatalog>();
            try
            {
                catalog.Set(json, TechnicalAtlasFixture.Bind(pages));
                var combat = new CharacterCombatSettings();
                combat.Hand1.Swing = new CharacterEffectSettings { Source = CombatEffectSource.Technical, ClipId = "swing",
                    Anchor = CombatEffectAnchor.CastOrigin, MissingAnchor = policy, Offset = new Vector3(.4f, 1, 0) };
                var set = CombatAnimationFixture.Set(); var table = set.StrikeSchedule;
                w.Manager.GetComponentObject<CombatEffectResources>(StreamEntity(w)).Prepare(ref table, combat, set, catalog, Vector2.zero);
                Start(w, table, -20);
                w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries); w.Step();
                using var p = new CombatEffectsPresentation(); Refresh(p, w);
                Assert.That(p.Stats.MissingAnchors, Is.EqualTo(1)); Assert.That(p.Stats.Alive, Is.EqualTo(alive));
                if (alive != 0)
                {
                    var support = w.Manager.GetComponentData<CombatPosition>(Actor(w, w.Session.HumanCombatant)).Value;
                    Assert.That(p.GetInstance(0).Position, Is.EqualTo(support + new float3(-.4f, 1, 0)));
                    Assert.That(p.GetInstance(0).Facing, Is.EqualTo(-1));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(json);
                foreach (var page in pages) UnityEngine.Object.DestroyImmediate(page);
            }
        }

        [Test]
        public void PresentationOverflowIsVisibleAndReadOnly()
        {
            using var w = new ReplayWorld("alive VFX capacity"); var table = CombatStrikeSchedule.Default;
            BindTechnical(w, ref table); Start(w, table, 20); w.Step();
            var e = StreamEntity(w); var stream = w.Manager.GetComponentData<CombatEffectStream>(e);
            var facts = w.Manager.GetBuffer<CombatEffectFact>(e);
            for (var i = 0; i < CombatEffectsPresentation.MaxAlive + 9; i++)
                stream.Append(facts, new CombatEffectFact { Kind = CombatEffectKind.Impact, Binding = table.Hand1Strike.ImpactEffect,
                    BattleNumber = w.Session.BattleNumber, Time = 0, End = 10, Facing = 1 });
            w.Manager.SetComponentData(e, stream);
            using var p = new CombatEffectsPresentation(); Refresh(p, w);
            Assert.That(p.Stats.Alive, Is.EqualTo(CombatEffectsPresentation.MaxAlive)); Assert.That(p.Stats.Dropped, Is.EqualTo(9));
            var before = w.Manager.GetComponentData<CombatEffectStream>(e);
            Refresh(p, w); Assert.That(w.Manager.GetComponentData<CombatEffectStream>(e).Sequence, Is.EqualTo(before.Sequence));
            Assert.That(p.Stats.Dropped, Is.EqualTo(9));
        }

        [Test]
        public void SubTickExecutionBetweenRefreshesStillLeavesItsAcceptedImpact()
        {
            using var w = new ReplayWorld("sub-tick effect history"); var table = CombatStrikeSchedule.Default;
            Assert.That(CombatStrikeTiming.TryFromSeconds(.001, .002, .003, 0, out var timing, out _), Is.True);
            table.Hand1 = timing; BindTechnical(w, ref table); Start(w, table);
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries); w.Frame(3);
            Assert.That(Facts(w).Count(f => f.Kind == CombatEffectKind.Swing), Is.EqualTo(1));
            Assert.That(Facts(w).Count(f => f.Kind == CombatEffectKind.Impact), Is.EqualTo(1));
            using var p = new CombatEffectsPresentation(); Refresh(p, w);
            Assert.That(p.Stats.Alive, Is.EqualTo(1), "Accepted impact lives on even though the entire action already ended; expired Swing is not replayed.");
        }

        [Test]
        public void InspectorDamageReadoutInvalidatesOnCombatOnlyEdit()
        {
            var definition = CombatAnimationFixture.Definition(); var catalog = ScriptableObject.CreateInstance<SpriteAtlasCatalog>();
            var json = new TextAsset(CombatAnimationFixture.Manifest()); var config = ScriptableObject.CreateInstance<CharacterAnimationConfig>();
            UnityEditor.Editor editor = null;
            try
            {
                catalog.Set(json, Array.Empty<SpriteAtlasPageBinding>());
                var entry = new CharacterAnimationEntry { CharacterId = definition.CharacterId, AtlasCatalog = catalog,
                    Animations = CombatAnimationSet.ClipIds.Select(id => CombatAnimationFixture.Settings(definition, id)).ToArray() };
                config.Set(entry); editor = UnityEditor.Editor.CreateEditor(config);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var ensure = editor.GetType().GetMethod("EnsureCache", flags); Assert.That(ensure, Is.Not.Null);
                var cache = editor.GetType().GetField("m_Characters", flags);
                string Read()
                {
                    ensure.Invoke(editor, new object[] { config });
                    var list = (System.Collections.IList)cache.GetValue(editor); var item = list[0];
                    return (string)item.GetType().GetField("Strikes").GetValue(item);
                }
                var before = Read();
                entry.Combat.Hand1.OverrideDamageDuration = true; entry.Combat.Hand1.DamageDurationSeconds = .1f;
                config.Set(entry);
                Assert.That(Read(), Is.Not.EqualTo(before), "Combat-only edit must refresh the exact read-only interval without touching animation settings.");
            }
            finally
            {
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                UnityEngine.Object.DestroyImmediate(config); UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(json);
            }
        }

        [Test]
        public void ResourceCapacityDisablesOnlyNewPresentationAndKeepsGameplayPublication()
        {
            using var w = new ReplayWorld("VFX version capacity"); var table = CombatStrikeSchedule.Default;
            for (var i = 0; i <= CombatEffectResources.MaxBindings / 10; i++) BindTechnical(w, ref table);
            var resources = w.Manager.GetComponentObject<CombatEffectResources>(StreamEntity(w));
            Assert.That(resources.Count, Is.EqualTo(CombatEffectResources.MaxBindings));
            Assert.That(resources.CapacityDropped, Is.EqualTo(10)); Assert.That(resources.Get(1), Is.Not.Null);
            Assert.That(table.Hand1Strike.SwingEffect, Is.Zero); Assert.That(table.Hand1Strike.ImpactEffect, Is.Zero);
            table.Hand1Strike.Damage = 9;
            Assert.DoesNotThrow(() => CombatStrikeSchedule.Publish(w.Manager, table));
            using var query = w.Manager.CreateEntityQuery(typeof(CombatStrikeSchedule));
            Assert.That(query.GetSingleton<CombatStrikeSchedule>().Hand1Strike.Damage, Is.EqualTo(9));
        }

        [Test]
        public void UnknownEffectHandleIsRejectedAndGpuLayoutStays32Bytes()
        {
            using var w = new ReplayWorld("invalid VFX handle"); var table = CombatStrikeSchedule.Default;
            table.Hand1Strike.SwingEffect = 123456;
            Assert.Throws<ArgumentException>(() => CombatStrikeSchedule.Publish(w.Manager, table));
            Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<CombatEffectsPresentation.Instance>(), Is.EqualTo(32));
        }

        [Test]
        public void RingOverflowHasStableOldestDropAndNeverMutatesCombat()
        {
            using var w = new ReplayWorld("effect overflow"); var e = StreamEntity(w);
            var stream = w.Manager.GetComponentData<CombatEffectStream>(e); var facts = w.Manager.GetBuffer<CombatEffectFact>(e);
            for (var i = 0; i < CombatEffectStream.Capacity + 7; i++) stream.Append(facts, new CombatEffectFact { End = 10 });
            Assert.That(stream.Count, Is.EqualTo(CombatEffectStream.Capacity)); Assert.That(stream.Dropped, Is.EqualTo(7));
            Assert.That(facts[stream.First].Sequence, Is.EqualTo(8));
            stream.Trim(facts, 130); Assert.That(stream.Count, Is.Zero); Assert.That(stream.Expired, Is.EqualTo(CombatEffectStream.Capacity));
        }

        [Test]
        public void ConfigEditFreezesCurrentBindingAndNextExecutionUsesNewVersion()
        {
            using var w = new ReplayWorld("frozen VFX binding"); var table = CombatStrikeSchedule.Default;
            table.Hand1 = CombatStrikeTiming.FromTicks(0, 4, 1, 0); BindTechnical(w, ref table); Start(w, table, 20);
            w.Script.Player(1, 1, ReplayRole.Human, pressed: CommandButtons.HandSeries).Player(6, 6, ReplayRole.Human, pressed: CommandButtons.HandSeries);
            w.Step(); var first = Facts(w).Single();
            var old = table.Hand1Strike.SwingEffect;
            table.Hand1.OverrideDamageDuration = true; table.Hand1.DamageEndTicks = 1;
            BindTechnical(w, ref table); CombatStrikeSchedule.Publish(w.Manager, table);
            Assert.That(table.Hand1Strike.SwingEffect, Is.Not.EqualTo(old));
            var attack = w.Manager.GetComponentData<CombatAttackState>(Actor(w, w.Session.HumanCombatant));
            Assert.That(attack.Strike.SwingEffect, Is.EqualTo(old));
            Assert.That(attack.Timing.DamageEnd, Is.EqualTo(4), "Current execution retains its damage interval.");
            w.Frame(5); var next = Facts(w).Last();
            Assert.That(first.Binding, Is.EqualTo(old)); Assert.That(next.Binding, Is.EqualTo(table.Hand1Strike.SwingEffect));
            Assert.That(w.Manager.GetComponentData<CombatAttackState>(Actor(w, w.Session.HumanCombatant)).Timing.DamageEnd, Is.EqualTo(1));
        }
    }
}

