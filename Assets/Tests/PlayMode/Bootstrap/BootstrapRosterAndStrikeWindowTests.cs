using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using MLFLegends.Combat;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MLFLegends.Tests.PlayMode
{
    // Stage 03 tuning: CombatSmokeBootstrap team sizes (start/restart through the one lifecycle path) and the strike windows
    // it publishes from the project animation config, in the auto-created default world. Every test ends its battle.
    public class BootstrapRosterAndStrikeWindowTests
    {
        const string ConfigPath = "Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset";

        static EntityManager Manager => World.DefaultGameObjectInjectionWorld.EntityManager;

        static T Singleton<T>() where T : unmanaged, IComponentData
        {
            using var query = Manager.CreateEntityQuery(typeof(T));
            return query.GetSingleton<T>();
        }

        static BattleSession Session => Singleton<BattleSession>();

        static uint Tick => Singleton<SimulationClock>().Tick;

        static (uint id, byte team)[] Roster
        {
            get
            {
                using var query = Manager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[] { typeof(CombatantId), typeof(CombatTeam) },
                    Options = EntityQueryOptions.IgnoreComponentEnabledState,
                });
                using var ids = query.ToComponentDataArray<CombatantId>(Allocator.Temp);
                using var teams = query.ToComponentDataArray<CombatTeam>(Allocator.Temp);
                var teamValues = teams.ToArray();
                return ids.ToArray().Select((id, i) => (id.Value, teamValues[i].Value)).OrderBy(c => c.Item1).ToArray();
            }
        }

        static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartup + 5f;
            while (!condition())
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "Expected state not reached.");
                yield return null;
            }
        }

        static IEnumerator NextTick()
        {
            var tick = Tick;
            yield return Until(() => Tick != tick);
        }

        static CombatSmokeBootstrap Create(int human, int ai)
        {
            var go = new GameObject("Roster bootstrap");
            go.SetActive(false);
            var bootstrap = go.AddComponent<CombatSmokeBootstrap>();
            bootstrap.HumanTeamSize = human;
            bootstrap.AiTeamSize = ai;
            return bootstrap;
        }

        [UnityTest]
        public IEnumerator TeamSizesSpawnOnStartAndRestartWithoutOldFightersAndEditsWaitForARestart()
        {
            yield return null;
            var bootstrap = Create(1, 1);
            try
            {
                Assert.That(bootstrap.TotalCombatants, Is.EqualTo(2));
                bootstrap.gameObject.SetActive(true);
                yield return NextTick();
                Assert.That(Roster.Length, Is.EqualTo(2), "2 = 1 + 1");
                var previous = Roster.Max(c => c.id);

                foreach (var (human, ai) in new[] { (50, 50), (200, 200), (3, 1), (1, 1) })
                {
                    bootstrap.HumanTeamSize = human;
                    bootstrap.AiTeamSize = ai;
                    var battle = Session.BattleNumber;
                    yield return NextTick();
                    yield return NextTick();
                    Assert.That(Session.BattleNumber, Is.EqualTo(battle), "Editing a size never restarts the running battle by itself.");
                    Assert.That(Roster.Max(c => c.id), Is.EqualTo(previous), "…nor respawns its fighters.");

                    bootstrap.RestartBattle();
                    yield return Until(() => Session.BattleNumber == battle + 1);
                    var roster = Roster;
                    Assert.That(roster.Length, Is.EqualTo(human + ai), $"{human} + {ai}");
                    Assert.That(roster.Count(c => c.team == 1), Is.EqualTo(human), "Human team size, leader included.");
                    Assert.That(roster.Count(c => c.team == 2), Is.EqualTo(ai), "AI team size, leader included.");
                    Assert.That(roster.All(c => c.id > previous), Is.True, "No fighter of the previous run survives; IDs are fresh.");
                    Assert.That(Session.HumanCombatant.Value, Is.EqualTo(previous + 1), "Same ID rules: human, AI, then the allies.");
                    previous = roster.Max(c => c.id);
                }

                // A full disable/enable cycle uses the stored sizes too.
                bootstrap.HumanTeamSize = 2;
                bootstrap.AiTeamSize = 98;
                bootstrap.gameObject.SetActive(false);
                yield return Until(() => !Session.IsActive);
                bootstrap.gameObject.SetActive(true);
                yield return Until(() => Session.IsActive);
                Assert.That(Roster.Length, Is.EqualTo(100));
            }
            finally
            {
                Object.Destroy(bootstrap.gameObject);
            }
            yield return Until(() => !Session.IsActive);
        }

        [UnityTest]
        public IEnumerator InvalidTeamSizesAreRejectedNotClampedAndKeepTheRunningBattle()
        {
            yield return null;
            var bootstrap = Create(2, 2);
            try
            {
                bootstrap.gameObject.SetActive(true);
                yield return NextTick();
                var battle = Session.BattleNumber;
                foreach (var (human, ai) in new[] { (0, 1), (1, 201), (-3, 5), (201, 201) })
                {
                    bootstrap.HumanTeamSize = human;
                    bootstrap.AiTeamSize = ai;
                    Assert.That(bootstrap.TryGetSetup(out _, out var error), Is.False);
                    Assert.That(error, Does.Contain("outside 1..200"));
                    LogAssert.Expect(LogType.Error, new Regex("outside 1\\.\\.200.*the running battle is kept"));
                    bootstrap.RestartBattle();
                    yield return NextTick();
                    Assert.That(Session.BattleNumber, Is.EqualTo(battle));
                    Assert.That(Roster.Length, Is.EqualTo(4), "Nothing clamped, nothing respawned.");
                    Assert.That(bootstrap.HumanTeamSize, Is.EqualTo(human), "The stored value is not modified either.");
                }
            }
            finally
            {
                Object.Destroy(bootstrap.gameObject);
            }
            yield return Until(() => !Session.IsActive);
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator TheProjectConfigPublishesItsStrikeWindowsAndEditsReachOnlyNewStrikes()
        {
            yield return null;
            var project = UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterAnimationConfig>(ConfigPath);
            var config = Object.Instantiate(project); // edits below never touch the project asset
            var bootstrap = Create(1, 1);
            try
            {
                bootstrap.AnimationConfig = config;
                bootstrap.StrikeCharacterId = "mage_female_01";
                LogAssert.Expect(LogType.Log, new Regex("strike windows .*punch_left 14/6/9, punch_right 14/6/9, kick_left 12/6/12, kick_right 12/6/12, kick_spin 20/5/5"));
                bootstrap.gameObject.SetActive(true);
                yield return NextTick();
                var table = Singleton<CombatStrikeSchedule>();
                Assert.That(table.Revision, Is.EqualTo(bootstrap.PublishedScheduleRevision));
                Assert.That((table.PunchLeft.WindupTicks, table.PunchLeft.ActiveTicks, table.PunchLeft.RecoveryTicks), Is.EqualTo((14, 6, 9)));
                Assert.That((table.KickLeft.WindupTicks, table.KickLeft.ActiveTicks, table.KickLeft.RecoveryTicks), Is.EqualTo((12, 6, 12)));
                Assert.That((table.KickRight.WindupTicks, table.KickRight.ActiveTicks, table.KickRight.RecoveryTicks), Is.EqualTo((12, 6, 12)));
                Assert.That((table.KickSpin.WindupTicks, table.KickSpin.ActiveTicks, table.KickSpin.RecoveryTicks), Is.EqualTo((20, 5, 5)));
                Assert.That(table.LandingRecoveryTicks, Is.EqualTo(12u));

                // Owner edit in Play Mode: punch_left lasts 1 s (6 flat frames of 10 ticks, phases 0..2 / 3 / 4..5). Published on the next frame.
                var entries = config.Characters.ToArray();
                var punch = entries[0].Animations.First(a => a.ClipId == "punch_left");
                punch.EndFrame = 5;
                punch.PreparationEndFrame = 2;
                punch.ActiveEndFrame = 3;
                punch.LandingStartFrame = -1;
                punch.SpeedCurve = AnimationCurve.Linear(0, 1, 1, 1);
                punch.TotalDurationSeconds = 1.0;
                config.Set(entries);
                LogAssert.Expect(LogType.Log, new Regex("strike windows .*punch_left 30/10/20"));
                yield return Until(() => Singleton<CombatStrikeSchedule>().Revision == table.Revision + 1);
                Assert.That(Singleton<CombatStrikeSchedule>().PunchLeft.End, Is.EqualTo(60));

                // A broken edit is reported once and keeps the last valid table.
                punch.ActiveEndFrame = 5;
                config.Set(entries);
                LogAssert.Expect(LogType.Error, new Regex("(?s)strike windows rejected.*punch_left.ActiveEndFrame: 5"));
                yield return null;
                yield return null;
                Assert.That(Singleton<CombatStrikeSchedule>().PunchLeft.End, Is.EqualTo(60), "Previous windows stay.");
            }
            finally
            {
                Object.Destroy(bootstrap.gameObject);
                Object.Destroy(config);
            }
            yield return Until(() => !Session.IsActive);
        }
#endif
    }
}
