using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Tests.EditMode
{
    public class CombatGuardTests
    {
        const CommandButtons K = CommandButtons.Guard, U = CommandButtons.HandSeries;
        static Entity H(ReplayWorld w) => CombatStrikeEffectsTests.Actor(w, w.Session.HumanCombatant);
        static Entity A(ReplayWorld w) => CombatStrikeEffectsTests.Actor(w, w.Session.AiCombatant);
        static T Read<T>(ReplayWorld w, Entity e) where T : unmanaged, IComponentData => w.Manager.GetComponentData<T>(e);
        static CombatStrikeSchedule Table()
        {
            var t = CombatStrikeSchedule.Default;
            t.Body.Radius = .05f; t.Hand1 = CombatStrikeTiming.FromTicks(0, 4, 2, 0);
            return t;
        }
        static void Start(ReplayWorld w, CombatStrikeSchedule table, int allies = 0) =>
            CombatStrikeEffectsTests.Start(w, table, .8f, allies);
        static void Hold(ReplayWorld w, uint first, uint end, bool fresh = false)
        {
            for (var tick = first; tick <= end; tick++)
                w.Script.Ai(tick, tick, ReplayRole.Ai, held: K, pressed: fresh && tick == first ? K : 0);
        }

        [TestCase(false, 90d, 100)] [TestCase(true, 100d, 96)]
        public void FrontBlockAndRearBypassUseFrozenFacingAndOneLedger(bool rear, double stability, int hp)
        {
            using var w = new ReplayWorld("front rear guard"); Start(w, Table());
            var target = A(w);
            w.Manager.SetComponentData(target, new CombatAutoFocus { Disabled = true });
            w.Manager.SetComponentData(target, new CombatFacing { Value = rear ? FacingDirection.Right : FacingDirection.Left });
            Hold(w, 1, 5); w.Script.Player(1, 1, ReplayRole.Human, pressed: U); w.Frame(5);
            Assert.That(Read<CombatStability>(w, target).Current, Is.EqualTo(stability));
            Assert.That(Read<CombatHealth>(w, target).Current, Is.EqualTo(hp));
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(H(w)).Length, Is.EqualTo(1), "Long window cannot re-charge the same target.");
            Assert.That(Read<CombatDamageFeedback>(w, target).LastDamageTick, Is.EqualTo(rear ? 1u : 0u));
            var facts = CombatStrikeEffectsTests.Facts(w);
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Block), Is.EqualTo(rear ? 0 : 1));
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Impact), Is.EqualTo(rear ? 1 : 0));
        }

        [TestCase(1f, 0f, true)] [TestCase(-1f, 0f, false)] [TestCase(0f, 1f, false)] [TestCase(0f, 0f, false)]
        public void IndependentDirectionCases(float x, float z, bool expected)
        {
            Assert.That(CombatGuardResolution.Front(new float3(x, 0, z), 1, 80), Is.EqualTo(expected));
        }

        [TestCase(-1f)] [TestCase(1f)]
        public void ConeBoundaryIsInclusiveWithDocumentedTolerance(float facing)
        {
            // Independent double trigonometry; exact boundary and a clearly outside angle.
            float3 Direction(double degrees) => new float3((float)Math.Cos(degrees * Math.PI / 180) * facing, 0,
                (float)Math.Sin(degrees * Math.PI / 180));
            Assert.That(CombatGuardResolution.Front(Direction(80), facing, 80), Is.True);
            Assert.That(CombatGuardResolution.Front(Direction(80.001), facing, 80), Is.False);
        }

        [TestCase(10d)] [TestCase(9.999d)] [TestCase(.001d)]
        public void ExactZeroAndSmallRemainderBreakOnThatContact(double remaining)
        {
            var settings = CombatGuardParameters.Default;
            var g = new CombatGuardState { Mode = CombatGuardMode.Held, StartTick = 1, Settings = settings };
            var r = CombatStability.Spawn(100); r.Current = remaining;
            var result = CombatGuardResolution.Resolve(ref r, g, true, new float3(1,0,0), 1, 10, .5, 1);
            Assert.That(result, Is.EqualTo(CombatGuardOutcome.Break));
            Assert.That(r.Current, Is.Zero); Assert.That(r.BreakUntil, Is.EqualTo(24.5));
            Assert.That(g.Effective(r, .5, true), Is.False, "The stale Action flag cannot give another block.");
        }

        [TestCase(0d, CombatGuardOutcome.Timely)] [TestCase(5.999d, CombatGuardOutcome.Timely)]
        [TestCase(6d, CombatGuardOutcome.Block)]
        public void TimelyUsesHalfOpenNumericalTimeAndOnlyOneContact(double time, CombatGuardOutcome expected)
        {
            var g = new CombatGuardState { Mode = CombatGuardMode.Held, StartTick = 1, FreshEntry = true, Settings = CombatGuardParameters.Default };
            var r = CombatStability.Spawn(100);
            Assert.That(CombatGuardResolution.Resolve(ref r, g, true, new float3(1,0,0), 1, 10, time, 6), Is.EqualTo(expected));
            var first = expected == CombatGuardOutcome.Timely ? 2.5 : 10;
            Assert.That(r.Current, Is.EqualTo(100 - first));
            CombatGuardResolution.Resolve(ref r, g, true, new float3(1,0,0), 1, 10, time, 6);
            Assert.That(r.Current, Is.EqualTo(90 - first), "Subsequent simultaneous attacker is a normal block.");
        }

        [TestCase(false, 90d)] [TestCase(true, 93.75d)]
        public void MovingPointAndSweptSamplesAgreeWithTheirOwnTimelyBoundary(bool swept, double expected)
        {
            using var w = new ReplayWorld("moving contact boundary"); var table = Table();
            var t = CombatStrikeTiming.FromTicks(0, 1, 2, swept ? CombatAttackTuning.NoChain : (byte)0);
            t.PreciseActiveWindow = true; t.ActiveStartTicks = .75; t.RecoveryStartTicks = 1;
            var strike = swept ? table.PhysicalAbilityStrike : table.Hand1Strike;
            strike.GuardDamage = swept ? 25 : 10;
            strike.DepthHalfWidth = 1; strike.ReachMax = 2;
            if (swept) { strike.SelfImpulse = 1; strike.LaunchTick = 0; strike.ElevationDegrees = 0; }
            if (swept) { table.PhysicalAbility = t; table.PhysicalAbilityStrike = strike; }
            else { table.Hand1 = t; table.Hand1Strike = strike; }
            Start(w,table);
            for(uint tick=1;tick<=6;tick++) w.Script.Ai(tick,tick,ReplayRole.Ai,moveZ:1,held:K,pressed:tick==1?K:0);
            w.Script.Player(6,6,ReplayRole.Human,moveX:1,pressed:swept?CommandButtons.PhysicalAbility:U);
            w.Frame(6);
            var target=A(w); var resource=Read<CombatStability>(w,target);
            Assert.That(resource.Current,Is.EqualTo(expected).Within(1e-8));
            var fact=CombatStrikeEffectsTests.Facts(w).Single(f=>f.Kind==(swept?CombatEffectKind.Timely:CombatEffectKind.Block));
            Assert.That(fact.Time,Is.EqualTo(swept?5.75d:6d).Within(1e-5));
            Assert.That(Read<CombatPosition>(w,target).Value.z,Is.GreaterThan(0));
            Assert.That(Read<CombatPosition>(w,H(w)).Value.x,Is.GreaterThan(0));
        }

        [Test]
        public void SameBatchBreakBypassesLaterAttackersAndEveryStrikeKeepsLedger()
        {
            using var w = new ReplayWorld("same batch break"); var table = Table(); table.Guard.MaxStability = 15;
            // Start's ally argument is on AI; explicitly give the attacking human team two allies.
            CombatStrikeSchedule.Publish(w.Manager, table);
            var setup = BattleSetup.Default; setup.Human.Position = float3.zero; setup.Ai.Position = new float3(.8f,0,0); setup.HumanAllies = 2;
            w.Request(BattleRequest.Start(setup)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            using var actors = w.Manager.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam));
            using var entities = actors.ToEntityArray(Allocator.Temp);
            using var queue = w.Manager.CreateEntityQuery(typeof(CombatCommandQueue));
            var pending = w.Manager.GetBuffer<CombatCommandSubmission>(queue.GetSingletonEntity());
            var n = 0;
            foreach (var e in entities)
            {
                if (Read<CombatTeam>(w, e).Value != 1) continue;
                w.Manager.SetComponentData(e, new CombatPosition { Value = new float3(0,0,n++ * .1f) });
                pending.Add(new CombatCommandSubmission(new CombatCommand { Combatant = Read<CombatantId>(w,e),
                    Tick = 1, Pressed = U, Source = CommandSource.AI }));
            }
            Hold(w, 1, 4); w.Frame(4);
            Assert.That(Read<CombatStability>(w,A(w)).Current, Is.Zero);
            Assert.That(Read<CombatHealth>(w,A(w)).Current, Is.EqualTo(92));
            var facts = CombatStrikeEffectsTests.Facts(w);
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Block), Is.EqualTo(1));
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Break), Is.EqualTo(1));
            Assert.That(facts.Count(f => f.Kind == CombatEffectKind.Impact), Is.EqualTo(2));
            foreach (var e in entities.Where(e => Read<CombatTeam>(w,e).Value == 1))
                Assert.That(w.Manager.GetBuffer<CombatHitRecord>(e).Length, Is.EqualTo(1));
        }

        [Test]
        public void WallOcclusionPrecedesGuardAndNeverSpendsResource()
        {
            using var w = new ReplayWorld("guard wall"); var arena = w.World.GetExistingSystemManaged<CombatArenaSystem>();
            var lease = arena.Acquire();
            arena.Stage(lease,new[] {
                new CombatArenaShape { Kind=CombatArenaKind.Box, AxisX=new float2(1,0), Center=float2.zero,
                    HalfSize=new float2(10,10), Bottom=-1, Top=0 },
                new CombatArenaShape { Kind=CombatArenaKind.Box, AxisX=new float2(1,0), Center=new float2(.4f,0),
                    HalfSize=new float2(.05f,1), Bottom=0, Top=3 },
            });
            Start(w,Table()); Hold(w,1,4,true); w.Script.Player(1,1,ReplayRole.Human,pressed:U); w.Frame(4);
            Assert.That(Read<CombatStability>(w,A(w)).Current,Is.EqualTo(100));
            Assert.That(Read<CombatHealth>(w,A(w)).Current,Is.EqualTo(100));
            Assert.That(w.Manager.GetBuffer<CombatHitRecord>(H(w)).Length,Is.Zero);
        }

        [Test]
        public void BlockUsesExistingTimingOnceThenHoldsPoseAndBrokenOverridesStaleGuard()
        {
            var set=CombatAnimationFixture.Set();
            Assert.That(set.Block,Is.Not.Null);
            var g=new CombatGuardState { Mode=CombatGuardMode.Held,StartTick=1,Settings=CombatGuardParameters.Default };
            var r=CombatStability.Spawn(100);
            var motion=new CombatMotion { Grounded=true };
            Assert.That(set.FrameFor(1,0,motion,default,g,r),Is.EqualTo(CombatAnimationFixture.TechA));
            Assert.That(set.FrameFor(31,0,motion,default,g,r),Is.EqualTo(CombatAnimationFixture.TechB));
            Assert.That(set.FrameFor(500,0,motion,default,g,r),Is.EqualTo(CombatAnimationFixture.TechB),"No cyclic jitter.");
            r.BreakSequence=1; r.BreakUntil=10;
            Assert.That(set.FrameFor(2,0,motion,default,g,r),Is.EqualTo(CombatAnimationFixture.TechB));
            Assert.That(CombatSpriteExtraction.GuardFactor(2,new CombatStability {LastOutcomeTick=2,LastOutcome=CombatGuardOutcome.Break},9),Is.LessThan(0));
            Assert.That(CombatSpriteExtraction.InstanceStride,Is.EqualTo(32));
        }

        [TestCase(false)] [TestCase(true)]
        public void TimelyAdvantageNeedsManualNextTickAttackAndIsConsumedOnce(bool counter)
        {
            using var w = new ReplayWorld("manual timely counter"); Start(w, Table());
            Hold(w, 1, 10, true); w.Script.Player(1, 1, ReplayRole.Human, pressed: U); w.Step();
            var r = Read<CombatStability>(w,A(w));
            Assert.That(r.Current, Is.EqualTo(97.5)); Assert.That(r.AdvantageSequence, Is.EqualTo(1));
            Assert.That(Read<CombatAttackState>(w,A(w)).IsAttacking, Is.False, "No automatic response.");
            // Replace the script so a response can share Held K on tick 2 without duplicate submissions.
            w.Script = new ReplayScript();
            for (uint tick = 2; tick <= 10; tick++) w.Script.Ai(tick,tick,ReplayRole.Ai,held:K,pressed: counter && tick == 2 ? U : 0);
            w.Step();
            var attack = Read<CombatAttackState>(w,A(w));
            Assert.That(attack.IsAttacking, Is.EqualTo(counter));
            if (counter)
            {
                Assert.That(attack.StartTick, Is.EqualTo(2)); Assert.That(attack.Timing, Is.EqualTo(Table().Hand1));
                Assert.That(Read<CombatGuardState>(w,A(w)).ConsumedAdvantage, Is.EqualTo(r.AdvantageSequence));
            }
        }

        [TestCase(false, 1f, .5f)] [TestCase(true, 4f, 2f)]
        public void OnlyAuthoredOnHitImpulseIsScaledAndBreakingHitKeepsFullImpulse(bool breaking, float x, float y)
        {
            using var w=new ReplayWorld("blocked authored impulse"); var table=Table();
            if(breaking) table.Guard.MaxStability=10;
            table.Hand1Strike.OnHitImpulse=new float3(4,2,0); Start(w,table);
            Hold(w,1,1); w.Script.Player(1,1,ReplayRole.Human,pressed:U); w.Step();
            using var q=w.Manager.CreateEntityQuery(typeof(CombatImpulseQueue));
            var impulses=w.Manager.GetBuffer<CombatImpulse>(q.GetSingletonEntity());
            Assert.That(impulses.Length,Is.EqualTo(1));
            Assert.That(impulses[0].Impulse,Is.EqualTo(new float3(x,y,0)));
            Assert.That(Read<CombatHealth>(w,A(w)).Current,Is.EqualTo(breaking?96:100));
        }

        [Test]
        public void TimelyBreakWinsOverAdvantageAndUnusedAdvantageExpires()
        {
            var g=new CombatGuardState {Mode=CombatGuardMode.Held,StartTick=1,FreshEntry=true,Settings=CombatGuardParameters.Default};
            var r=CombatStability.Spawn(2.5f);
            Assert.That(CombatGuardResolution.Resolve(ref r,g,true,new float3(1,0,0),1,10,1,1),Is.EqualTo(CombatGuardOutcome.Break));
            Assert.That(r.AdvantageSequence,Is.Zero);
            r=CombatStability.Spawn(100);
            CombatGuardResolution.Resolve(ref r,g,true,new float3(1,0,0),1,10,1,1);
            Assert.That(r.Advantage(g,12.999),Is.True); Assert.That(r.Advantage(g,13),Is.False);
        }

        [TestCase(CommandSource.Player)] [TestCase(CommandSource.AI)]
        public void GuardUsesIdenticalSourceRulesAndInvalidCommandCannotArmIt(CommandSource source)
        {
            using var w=new ReplayWorld("guard command parity"); Start(w,Table());
            if(source==CommandSource.Player) w.Script.Player(1,1,ReplayRole.Ai,moveX:2,held:K,pressed:K);
            else w.Script.Ai(1,1,ReplayRole.Ai,moveX:2,held:K,pressed:K);
            w.Step(); Assert.That(Read<CombatGuardState>(w,A(w)).Mode,Is.EqualTo(CombatGuardMode.None));
            if(source==CommandSource.Player) w.Script.Player(2,2,ReplayRole.Ai,held:K,pressed:K);
            else w.Script.Ai(2,2,ReplayRole.Ai,held:K,pressed:K);
            w.Step();
            Assert.That(Read<CombatGuardState>(w,A(w)).Mode,Is.EqualTo(CombatGuardMode.Held));
            Assert.That(Read<CombatGuardState>(w,A(w)).StartTick,Is.EqualTo(2));
            Assert.That(Read<CombatGuardState>(w,A(w)).FreshEntry,Is.True);
        }

        [Test]
        public void RegenRunsWithoutAttackersAndOnlyAfterFreeDelay()
        {
            using var w = new ReplayWorld("stability recovery"); var table = Table(); table.Guard.RegenDelayTicks = 2; Start(w, table);
            Hold(w,1,1); w.Script.Player(1,1,ReplayRole.Human,pressed:U); w.Step();
            Assert.That(Read<CombatStability>(w,A(w)).Current, Is.EqualTo(90));
            w.Frame(7); // release tick 2 -> exit until numerical time 7; tick 8 is free
            Assert.That(Read<CombatStability>(w,A(w)).Current, Is.EqualTo(90 + 20d / 60).Within(1e-10));
            w.Frame(10);
            Assert.That(Read<CombatStability>(w,A(w)).Current, Is.EqualTo(90 + 11 * 20d / 60).Within(1e-10));
            w.Step(40);
            Assert.That(Read<CombatStability>(w,A(w)).Current, Is.EqualTo(100));
        }

        [Test]
        public void BreakRequiresObservedReleaseAndNewLegalPress()
        {
            using var w = new ReplayWorld("guard break latch"); var table = Table(); table.Guard.MaxStability = 10; table.Guard.BreakTicks = 2; Start(w,table);
            Hold(w,1,4); w.Script.Player(1,1,ReplayRole.Human,pressed:U); w.Frame(4);
            Assert.That(Read<CombatGuardState>(w,A(w)).Mode, Is.EqualTo(CombatGuardMode.None));
            w.Step(); // accepted release at tick 5
            w.Script.Ai(6,6,ReplayRole.Ai,held:K); w.Step();
            Assert.That(Read<CombatGuardState>(w,A(w)).Mode, Is.EqualTo(CombatGuardMode.None), "Held alone never rearms after break.");
            w.Step(); w.Script.Ai(8,8,ReplayRole.Ai,held:K,pressed:K); w.Step();
            Assert.That(Read<CombatGuardState>(w,A(w)).Mode, Is.EqualTo(CombatGuardMode.Held));
            Assert.That(Read<CombatGuardState>(w,A(w)).StartTick, Is.EqualTo(8));
        }

        [Test]
        public void GuardSettingsAndSpawnMaximumStayFrozenAcrossPublication()
        {
            using var w = new ReplayWorld("frozen guard settings"); var table = Table(); Start(w,table); Hold(w,1,4); w.Step();
            table.Guard.Strength = 10; table.Guard.MaxStability = 300; CombatStrikeSchedule.Publish(w.Manager,table);
            w.Script.Player(2,2,ReplayRole.Human,pressed:U); w.Step();
            var r = Read<CombatStability>(w,A(w));
            Assert.That(r.Current, Is.EqualTo(90)); Assert.That(r.Max, Is.EqualTo(100));
            Assert.That(Read<CombatGuardState>(w,A(w)).Settings.Strength, Is.EqualTo(1));
        }

        [TestCase(CommandButtons.Jump, false)] [TestCase(CommandButtons.HandSeries, true)]
        [TestCase(CommandButtons.FootSeries, true)] [TestCase(CommandButtons.PhysicalAbility, true)]
        public void FreeJumpThenGuardThenStrikesPriority(CommandButtons other, bool guarding)
        {
            using var w = new ReplayWorld("guard priority"); Start(w,Table());
            w.Script.Player(1,1,ReplayRole.Human,held:K,pressed:K|other); w.Step();
            Assert.That(Read<CombatGuardState>(w,H(w)).Mode == CombatGuardMode.Held, Is.EqualTo(guarding));
            Assert.That(Read<CombatAttackState>(w,H(w)).IsAttacking, Is.False);
            Assert.That(Read<CombatMotion>(w,H(w)).Grounded, Is.EqualTo(guarding));
        }

        [Test]
        public void GuardDoesNotCancelAttackAndHeldAfterItHasNoRetroactiveTimely()
        {
            using var w = new ReplayWorld("guard after attack"); Start(w,Table());
            w.Script.Player(1,1,ReplayRole.Human,pressed:U);
            for(uint tick=2;tick<=8;tick++) w.Script.Player(tick,tick,ReplayRole.Human,held:K,pressed:tick==2?K:0);
            w.Frame(6);
            Assert.That(Read<CombatAttackState>(w,H(w)).StartTick, Is.EqualTo(1));
            w.Step();
            var g = Read<CombatGuardState>(w,H(w));
            Assert.That(g.Mode, Is.EqualTo(CombatGuardMode.Held)); Assert.That(g.StartTick, Is.EqualTo(7)); Assert.That(g.FreshEntry, Is.False);
        }

        [Test]
        public void FocusToggleDuringGuardChangesDesiredModeButKeepsDirectionThroughExit()
        {
            using var w = new ReplayWorld("guard facing freeze"); Start(w,Table());
            w.Script.Player(1,1,ReplayRole.Human,held:K,pressed:K);
            w.Script.Player(2,2,ReplayRole.Human,moveX:-1,held:K,pressed:CommandButtons.FocusToggle);
            w.Script.Player(3,3,ReplayRole.Human,moveX:-1); w.Frame(3);
            Assert.That(Read<CombatAutoFocus>(w,H(w)).Disabled, Is.True);
            Assert.That(Read<CombatFacing>(w,H(w)).Value, Is.EqualTo(FacingDirection.Right));
            w.Script.Player(9,9,ReplayRole.Human,moveX:-1); w.Frame(6);
            Assert.That(Read<CombatFacing>(w,H(w)).Value, Is.EqualTo(FacingDirection.Left));
        }

        [Test]
        public void ControlledMovementIsScaledButExternalImpulseIsNot()
        {
            using var w = new ReplayWorld("guard movement impulse"); Start(w,Table());
            w.Script.Player(1,1,ReplayRole.Human,moveX:-1,held:K,pressed:K); w.Step();
            Assert.That(Read<CombatPosition>(w,H(w)).Value.x,
                Is.EqualTo(-CombatMovementTuning.MoveSpeed * SimulationTiming.TickDuration * .35f).Within(1e-6));
            using var q = w.Manager.CreateEntityQuery(typeof(CombatImpulseQueue));
            w.Manager.GetBuffer<CombatImpulse>(q.GetSingletonEntity()).Add(new CombatImpulse {
                Target = w.Session.HumanCombatant, BattleNumber = w.Session.BattleNumber, Impulse = new float3(-3,4,0) });
            w.Script.Player(2,2,ReplayRole.Human,held:K); w.Step();
            var motion = Read<CombatMotion>(w,H(w));
            Assert.That(motion.ExternalVelocity.x, Is.LessThan(-1), "External velocity is not multiplied by .35.");
            Assert.That(motion.Grounded, Is.False);
            w.Script.Player(3,3,ReplayRole.Human,held:K); w.Step();
            Assert.That(Read<CombatGuardState>(w,H(w)).Mode, Is.EqualTo(CombatGuardMode.None));
        }

        [TestCase(false)] [TestCase(true)]
        public void ZeroDamageAndZeroPressureHaveSeparateSemantics(bool zeroPressure)
        {
            using var w = new ReplayWorld("zero guard pressure"); var t=Table();
            t.Hand1Strike.Damage=0; t.Hand1Strike.GuardDamage=zeroPressure?0:10; Start(w,t);
            Hold(w,1,3,true); w.Script.Player(1,1,ReplayRole.Human,pressed:U); w.Frame(3);
            var r=Read<CombatStability>(w,A(w));
            Assert.That(r.Current,Is.EqualTo(zeroPressure?100:97.5));
            Assert.That(r.AdvantageSequence,Is.EqualTo(zeroPressure?0u:1u));
            Assert.That(Read<CombatDamageFeedback>(w,A(w)).LastDamageTick,Is.Zero);
            Assert.That(CombatStrikeEffectsTests.Facts(w).Any(f=>f.Kind==CombatEffectKind.Impact),Is.False);
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(10)]
        public void GuardReplayIsIdenticalAcrossCatchUpAndRendererOff(int pacing)
        {
            string[] Run(int chunk, bool render)
            {
                using var w=new ReplayWorld("guard replay"); var t=Table(); CombatStrikeEffectsTests.BindTechnical(w,ref t); Start(w,t);
                Hold(w,1,20,true);
                for(uint tick=1;tick<=30;tick+=7) w.Script.Player(tick,tick,ReplayRole.Human,pressed:U);
                using var presentation=new CombatEffectsPresentation();
                for(var done=0;done<30;done+=chunk) { w.Frame(Math.Min(chunk,30-done)); if(render) CombatStrikeEffectsTests.Refresh(presentation,w); }
                return w.Records.SelectMany(record=>record.Combatants.Select(actor=>
                    actor.Id+":"+actor.Guard.Mode+":"+actor.Guard.StartTick+":"+actor.Stability.Current.ToString("R",CultureInfo.InvariantCulture)+
                    ":"+actor.Stability.BreakUntil.ToString("R",CultureInfo.InvariantCulture)+":"+actor.Health.Current+":"+actor.Facing)).ToArray();
            }
            CollectionAssert.AreEqual(Run(1,false),Run(pacing,true));
        }

        [Test]
        public void RestartAndDisabledMembershipDropIntentAndAdvantage()
        {
            using var w=new ReplayWorld("guard lifecycle"); Start(w,Table()); Hold(w,1,3,true);
            w.Script.Player(1,1,ReplayRole.Human,pressed:U); w.Step(); var target=A(w);
            w.Manager.SetComponentEnabled<InCombat>(target,false); w.Step();
            Assert.That(Read<CombatGuardState>(w,target).Mode,Is.EqualTo(CombatGuardMode.None));
            Assert.That(Read<CombatGuardState>(w,target).ConsumedAdvantage,Is.EqualTo(Read<CombatStability>(w,target).AdvantageSequence));
            w.Request(BattleRequest.Restart(BattleSetup.Default)); w.World.GetExistingSystemManaged<InitializationSystemGroup>().Update();
            Assert.That(w.Manager.Exists(target),Is.False);
            var fresh=Read<CombatStability>(w,A(w));
            Assert.That(fresh.Current,Is.EqualTo(fresh.Max)); Assert.That(fresh.AdvantageSequence,Is.Zero); Assert.That(fresh.BreakSequence,Is.Zero);
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(0f)] [TestCase(-1f)]
        public void GuardAuthoringRejectsInvalidStrengthWithoutClamping(float strength)
        {
            var data=new CharacterCombatSettings(); data.Guard.Strength=strength; var errors=new List<string>();
            Assert.That(data.Validate("guard",errors),Is.False); Assert.That(errors.Any(e=>e.Contains(".Guard")),Is.True);
        }

        [Test]
        public void VersionOneMigrationOnlyAddsGuardFields()
        {
            var data=new CharacterCombatSettings(); data.BodyHeight=3.5f; data.Hand1.Damage=19; data.Hand1.SelfImpulse=4;
            var json=JsonUtility.ToJson(data).Replace("\"m_Version\":2","\"m_Version\":1");
            var migrated=JsonUtility.FromJson<CharacterCombatSettings>(json);
            Assert.That(migrated.BodyHeight,Is.EqualTo(3.5f)); Assert.That(migrated.Hand1.Damage,Is.EqualTo(19));
            Assert.That(migrated.Hand1.SelfImpulse,Is.EqualTo(4)); Assert.That(migrated.Guard.MaxStability,Is.EqualTo(100));
            Assert.That(migrated.Hand1.GuardDamage,Is.EqualTo(10)); Assert.That(migrated.Version,Is.EqualTo(2));
        }
    }
}
