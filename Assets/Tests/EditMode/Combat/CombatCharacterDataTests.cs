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
    // #69 per-character body and strike data: one publication table (CombatStrikeSchedule) with timing, per-slot damage/hit
    // box and the body; values frozen per strike execution; body applied at spawn; validation, migration and slot clips.
    public class CombatCharacterDataTests
    {
        const CommandButtons Hand = CommandButtons.HandSeries;

        static BattleSetup Duel(float aiX) => new BattleSetup
        {
            Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = FacingDirection.Right },
            Ai = new CombatantSetup { Team = 2, Position = new float3(aiX, 0f, 0f), Facing = FacingDirection.Left },
        };

        static CombatantRecord H(TickRecord r) => r.Combatant(r.HumanId);
        static CombatantRecord Ai(TickRecord r) => r.Combatant(r.AiId);

        static CombatStrikeSchedule Table(ReplayWorld w)
        {
            using var query = w.Manager.CreateEntityQuery(typeof(CombatStrikeSchedule));
            return query.GetSingleton<CombatStrikeSchedule>();
        }

        static CombatStrikeSchedule WithHand1(int damage, float reachMax)
        {
            var table = CombatStrikeSchedule.Default;
            var strike = table.Hand1Strike; strike.Damage = damage; strike.ReachMax = reachMax;
            table.Hand1Strike = strike;
            return table;
        }

        [Test]
        public void DefaultDataKeepsThePreviousValuesBodyAndTable()
        {
            var table = CombatStrikeSchedule.Default;
            for (var kind = CombatAttackKind.Hand1; kind <= CombatAttackKind.PhysicalAbility; kind++)
                Assert.That(table.GetStrike(kind), Is.EqualTo(CombatAttackTuning.Get(kind).Parameters), kind.ToString());
            Assert.That(table.Body, Is.EqualTo(CombatBodyTuning.Default));
            Assert.That(table.IsValid, Is.True);

            // The default authoring data publishes exactly the default table (prior HP/Inside results until the owner edits).
            var settings = new CharacterCombatSettings();
            Assert.That(settings.Version, Is.EqualTo(CharacterCombatSettings.CurrentVersion));
            Assert.That(settings.Validate("defaults", new List<string>()), Is.True);
            var published = CombatStrikeSchedule.Default;
            settings.ApplyTo(ref published);
            Assert.That(published, Is.EqualTo(CombatStrikeSchedule.Default));
            Assert.That(new[] { settings.ClipFor(CombatAttackKind.Hand1), settings.ClipFor(CombatAttackKind.Hand2), settings.ClipFor(CombatAttackKind.Foot1),
                settings.ClipFor(CombatAttackKind.Foot2), settings.ClipFor(CombatAttackKind.PhysicalAbility) },
                Is.EqualTo(new[] { "punch_left", "punch_right", "kick_left", "kick_right", "physical_ability" }), "Default slot clips of the v2 art (#70).");
        }

        [Test]
        public void StrikeValuesAreFrozenAtStartAndAnEditActsFromTheNextStrike()
        {
            using var w = new ReplayWorld("frozen strike values");
            w.Script = new ReplayScript().Player(1, 1, ReplayRole.Human, pressed: Hand).Player(40, 40, ReplayRole.Human, pressed: Hand)
                .Player(80, 80, ReplayRole.Human, pressed: Hand);
            w.Request(BattleRequest.Start(Duel(.5f)));
            w.Step(5); // first strike in Preparation (Hand1 windup 13)
            CombatStrikeSchedule.Publish(w.Manager, WithHand1(9, 1f));
            w.Step(45); // second strike starts at 40 and is in Recovery/over by 50
            CombatStrikeSchedule.Publish(w.Manager, WithHand1(9, .3f)); // target at .5 is now out of reach
            w.Step(50); // third strike Active at 93..97
            var records = w.BattleRecords(1);
            var old = CombatAttackTuning.Get(CombatAttackKind.Hand1).Parameters;
            Assert.That(H(records[0]).Attack.Strike, Is.EqualTo(old));
            Assert.That(H(records[20]).Attack.Strike, Is.EqualTo(old), "A table published mid-strike does not change the running strike.");
            Assert.That(Ai(records[35]).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - old.Damage), "Old damage, old reach.");
            Assert.That(H(records[39]).Attack.Strike.Damage, Is.EqualTo(9), "The next strike copies the new table.");
            Assert.That(Ai(records[75]).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - old.Damage - 9));
            Assert.That(H(records[79]).Attack.Strike.ReachMax, Is.EqualTo(.3f));
            Assert.That(Ai(records.Last()).Health.Current, Is.EqualTo(CombatAttackTuning.MaxHealth - old.Damage - 9),
                "The third strike uses the shortened reach: the target is out of its box.");
        }

        [Test]
        public void BodyComesFromTheTableAtSpawnOnly()
        {
            using var w = new ReplayWorld("body at spawn");
            var table = CombatStrikeSchedule.Default;
            table.Body = new CombatBody { Radius = .5f, Height = 1.5f, Mass = 3f };
            CombatStrikeSchedule.Publish(w.Manager, table);
            w.Request(BattleRequest.Start(Duel(5f)));
            w.Step();
            var bodies = Bodies(w);
            Assert.That(bodies.All(b => b.Equals(table.Body)), Is.True, "Every fighter of the character gets its body, any role/team.");

            CombatStrikeSchedule.Publish(w.Manager, CombatStrikeSchedule.Default);
            w.Step();
            Assert.That(Bodies(w).All(b => b.Equals(table.Body)), Is.True, "A running battle keeps the spawned bodies.");
            w.Request(BattleRequest.Restart(Duel(5f)));
            w.Step();
            Assert.That(Bodies(w).All(b => b.Equals(CombatBodyTuning.Default)), Is.True, "The next restart uses the new body.");
        }

        static CombatBody[] Bodies(ReplayWorld w)
        {
            using var q = w.Manager.CreateEntityQuery(typeof(CombatBody), typeof(CombatantId));
            return q.ToComponentDataArray<CombatBody>(Allocator.Temp).ToArray();
        }

        [Test]
        public void OutOfRangeValuesAreRejectedNotClamped()
        {
            using var w = new ReplayWorld("character limits");
            foreach (var body in new[] { new CombatBody { Radius = .01f, Height = 2, Mass = 1 }, new CombatBody { Radius = .3f, Height = 9, Mass = 1 },
                new CombatBody { Radius = .3f, Height = 2, Mass = 0 }, new CombatBody { Radius = float.NaN, Height = 2, Mass = 1 } })
            {
                var table = CombatStrikeSchedule.Default; table.Body = body;
                Assert.Throws<ArgumentException>(() => CombatStrikeSchedule.Publish(w.Manager, table), body.ToString());
            }
            foreach (var strike in new[]
            {
                new CombatStrikeParameters { Damage = -1, ReachMax = 1, DepthHalfWidth = .4f, HeightTolerance = .9f },
                new CombatStrikeParameters { Damage = 4, ReachMin = 1.2f, ReachMax = 1, DepthHalfWidth = .4f, HeightTolerance = .9f },
                new CombatStrikeParameters { Damage = 4, ReachMax = 9, DepthHalfWidth = .4f, HeightTolerance = .9f },
                new CombatStrikeParameters { Damage = 4, ReachMax = 1, DepthHalfWidth = -.1f, HeightTolerance = .9f },
            })
            {
                var table = CombatStrikeSchedule.Default; table.Foot2Strike = strike;
                Assert.Throws<ArgumentException>(() => CombatStrikeSchedule.Publish(w.Manager, table));
            }
            Assert.That(Table(w).Revision, Is.Zero, "Nothing rejected was published.");

            var settings = new CharacterCombatSettings { BodyMass = 500 };
            settings.Hand2.ReachMin = 2; settings.Hand2.ReachMax = 1;
            settings.Foot1.ClipId = "run";
            var errors = new List<string>();
            Assert.That(settings.Validate("characters.x.Combat", errors), Is.False);
            Assert.That(errors.Count, Is.EqualTo(3), string.Join("\n", errors));
            Assert.That(settings.BodyMass, Is.EqualTo(500), "Reported, never clamped.");
            Assert.That(errors.Any(e => e.Contains("characters.x.Combat.Hand2")) && errors.Any(e => e.Contains("Foot1.ClipId")), Is.True);
        }

        [Test]
        public void MigrationFillsThePreviousValuesOnceAndLeavesAnimationTimingUntouched()
        {
            // An entry serialized before #69 (no Combat block): animation data must survive byte-for-byte.
            const string legacy = "{\"CharacterId\":\"mage\",\"Animations\":[{\"ClipId\":\"punch_left\",\"EndFrame\":5,\"TotalDurationSeconds\":0.75," +
                "\"PreparationEndFrame\":2,\"ActiveEndFrame\":3,\"LandingStartFrame\":-1}]}";
            var entry = JsonUtility.FromJson<CharacterAnimationEntry>(legacy);
            Assert.That(entry.Combat, Is.Not.Null);
            Assert.That(entry.Combat.Version, Is.EqualTo(CharacterCombatSettings.CurrentVersion));
            Assert.That(entry.Combat.Body, Is.EqualTo(CombatBodyTuning.Default));
            Assert.That(entry.Combat.Hand1.Parameters, Is.EqualTo(CombatAttackTuning.Get(CombatAttackKind.Hand1).Parameters));
            var punch = entry.Animations.Single();
            Assert.That((punch.EndFrame, punch.TotalDurationSeconds, punch.PreparationEndFrame, punch.ActiveEndFrame), Is.EqualTo((5, .75, 2, 3)));

            // A zero-initialized block (version 0) is migrated once.
            var zero = JsonUtility.FromJson<CharacterAnimationEntry>("{\"CharacterId\":\"mage\",\"Combat\":{\"BodyRadius\":0,\"m_Version\":0}}");
            Assert.That(zero.Combat.Body, Is.EqualTo(CombatBodyTuning.Default));
            Assert.That(zero.Combat.Foot2.ClipId, Is.EqualTo("kick_right"));
            Assert.That(zero.Combat.PhysicalAbility.ClipId, Is.EqualTo("kick_spin"), "Pre-#69 data keeps the v1 clip it was authored for; the v2 rename is the explicit atlas migration.");

            // Owner values at version 1 are never rewritten: idempotent across repeated load/save.
            entry.Combat.Hand1.Damage = 9; entry.Combat.BodyMass = 2.5f; entry.Combat.PhysicalAbility.ClipId = "kick_spin";
            var once = JsonUtility.ToJson(entry);
            var again = JsonUtility.ToJson(JsonUtility.FromJson<CharacterAnimationEntry>(once));
            Assert.That(again, Is.EqualTo(once));
            Assert.That(JsonUtility.FromJson<CharacterAnimationEntry>(again).Combat.Hand1.Damage, Is.EqualTo(9));
        }

        [Test]
        public void EachCharacterKeepsItsOwnDataAndSlotClips()
        {
            var config = ScriptableObject.CreateInstance<CharacterAnimationConfig>();
            try
            {
                var a = new CharacterAnimationEntry { CharacterId = "a" };
                var b = new CharacterAnimationEntry { CharacterId = "b" };
                b.Combat.Hand1.Damage = 11; b.Combat.BodyRadius = .45f; b.Combat.Hand2.ClipId = "punch_left";
                config.Set(a, b);
                Assert.That(config.TryResolveCombat("a", out var ca, out var error), Is.True, error);
                Assert.That(config.TryResolveCombat("b", out var cb, out error), Is.True, error);
                var ta = CombatStrikeSchedule.Default; ca.ApplyTo(ref ta);
                Assert.That(ta, Is.EqualTo(CombatStrikeSchedule.Default), "Editing B never changes A.");
                var tb = CombatStrikeSchedule.Default; cb.ApplyTo(ref tb);
                Assert.That((tb.Hand1Strike.Damage, tb.Body.Radius), Is.EqualTo((11, .45f)));
                Assert.That(config.StrikeClip("b", CombatAttackKind.Hand2), Is.EqualTo("punch_left"));
                Assert.That(config.StrikeClip("a", CombatAttackKind.Hand2), Is.EqualTo("punch_right"));
                b.Combat.Hand1.Damage = 99; // Set() stored a defensive copy
                Assert.That(config.TryResolveCombat("b", out cb, out _) && cb.Hand1.Damage == 11, Is.True);
                Assert.That(config.TryResolveCombat("missing", out _, out error), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }
    }
}
