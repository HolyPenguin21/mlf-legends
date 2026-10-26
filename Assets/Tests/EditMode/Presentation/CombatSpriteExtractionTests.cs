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
    // Stage 03 (#38): render extraction against a real isolated battle World (replay driver: lifecycle, clock, processor,
    // facing, movement). No graphics device needed; GPU submission is covered by the PlayMode graphics suite.
    public class CombatSpriteExtractionTests
    {
        const float Separation = 0.002f;

        ReplayWorld m_Replay;
        Texture2D[] m_Pages;
        SpriteAtlas m_Atlas;
        CombatSpriteExtraction m_Extraction;
        SpriteCardBasis m_Basis;

        [SetUp]
        public void SetUp()
        {
            m_Replay = new ReplayWorld("Sprites");
            m_Pages = TechnicalAtlasFixture.CreatePages();
            var errors = new List<string>();
            m_Atlas = SpriteAtlas.Load(TechnicalAtlasFixture.ManifestJson, TechnicalAtlasFixture.Bind(m_Pages), errors, null);
            Assert.That(m_Atlas, Is.Not.Null, string.Join("\n", errors));
            m_Extraction = new CombatSpriteExtraction();
            var rotation = Quaternion.Euler(40f, 0f, 0f);
            m_Basis = SpriteCardBasis.FromCamera(rotation * Vector3.forward, rotation * Vector3.up);
        }

        [TearDown]
        public void TearDown()
        {
            m_Extraction?.Dispose();
            m_Atlas?.Dispose();
            foreach (var page in m_Pages)
                Object.DestroyImmediate(page);
            m_Replay?.Dispose();
        }

        void Run(World world = null, uint defaultFrame = 0) =>
            m_Extraction.Run(world ?? m_Replay.World, m_Atlas, m_Basis, Separation, defaultFrame);

        static BattleSetup Setup(int perTeam) => new BattleSetup
        {
            Human = BattleSetup.Default.Human,
            Ai = BattleSetup.Default.Ai,
            HumanAllies = (ushort)(perTeam - 1),
            AiAllies = (ushort)(perTeam - 1),
        };

        Dictionary<uint, SpriteInstanceGpu> ById()
        {
            var result = new Dictionary<uint, SpriteInstanceGpu>();
            for (var i = 0; i < m_Extraction.Count; i++)
                result.Add(m_Extraction.InstanceIds[i], m_Extraction.Instances[i]);
            return result;
        }

        [TestCase(1)]
        [TestCase(50)]
        [TestCase(200)]
        public void EveryInCombatFighterIsOneInstanceWithItsEcsPositionAndFacing(int perTeam)
        {
            m_Replay.Request(BattleRequest.Start(Setup(perTeam)));
            for (uint r = 1; r <= 12; r++)
                m_Replay.Script.Player(r, r, ReplayRole.Human, moveX: -1, moveZ: 1, pressed: r == 2 ? CommandButtons.Jump : CommandButtons.None);
            m_Replay.Step(12);
            Run();

            var record = m_Replay.Records.Last();
            Assert.That(m_Extraction.Count, Is.EqualTo(perTeam * 2), "2 / 100 / 400 instances, one per fighter.");
            var instances = ById();
            foreach (var combatant in record.Combatants)
            {
                var instance = instances[combatant.Id];
                Assert.That(instance.Position, Is.EqualTo(combatant.Position), "Position copied from CombatPosition after the tick (jump Y included, nothing added).");
                Assert.That(instance.Facing, Is.EqualTo(combatant.Facing == FacingDirection.Left ? -1f : 1f));
                Assert.That(instance.Frame, Is.EqualTo(0u));
            }

            var human = record.Combatant(record.HumanId);
            Assert.That(human.Facing, Is.EqualTo(FacingDirection.Left), "Moved −X: CombatFacingSystem turned it; the renderer only shows it.");
            Assert.That(human.Position.y, Is.GreaterThan(0f), "Airborne.");
            Assert.That(m_Extraction.PageInstances(0), Is.EqualTo(perTeam * 2));
            Assert.That(m_Extraction.PageInstances(1), Is.EqualTo(0));
        }

        [Test]
        public void FramesArePerInstanceAndGroupedByPage()
        {
            m_Replay.Request(BattleRequest.Start(Setup(3)));
            m_Replay.Step();
            var session = m_Replay.Session;
            m_Extraction.Select(session.HumanCombatant, 2); // tech_c, page 1
            m_Extraction.Select(session.AiCombatant, 1); // tech_b, page 0, same batch as tech_a
            Run(defaultFrame: 0);

            var instances = ById();
            Assert.That(instances[session.HumanCombatant.Value].Frame, Is.EqualTo(2u));
            Assert.That(instances[session.AiCombatant.Value].Frame, Is.EqualTo(1u));
            Assert.That(instances.Values.Count(i => i.Frame == 0u), Is.EqualTo(4));
            Assert.That(m_Extraction.PageInstances(0), Is.EqualTo(5), "Different frames of one page share one draw.");
            Assert.That(m_Extraction.PageInstances(1), Is.EqualTo(1));
            Assert.That(m_Extraction.PageStart(0), Is.EqualTo(0));
            Assert.That(m_Extraction.PageStart(1), Is.EqualTo(5));
            for (var i = 0; i < 5; i++)
                Assert.That(m_Atlas.FramePages[(int)m_Extraction.Instances[i].Frame], Is.EqualTo(0));
            Assert.That(m_Extraction.InstanceIds[5], Is.EqualTo(session.HumanCombatant.Value));

            m_Extraction.Select(session.AiCombatant, 99); // outside the atlas
            m_Extraction.ClearSelection(session.HumanCombatant);
            Run(defaultFrame: 1);
            instances = ById();
            Assert.That(instances[session.AiCombatant.Value].Frame, Is.EqualTo(1u), "An invalid selection falls back to the default frame.");
            Assert.That(instances[session.HumanCombatant.Value].Frame, Is.EqualTo(1u));
            Assert.That(m_Extraction.PageInstances(1), Is.EqualTo(0));
        }

        [Test]
        public void DepthOrderFollowsDepthThenCombatantIdIndependentOfEntityOrder()
        {
            m_Replay.Request(BattleRequest.Start(Setup(30)));
            m_Replay.Step();
            using (var query = m_Replay.Manager.CreateEntityQuery(typeof(CombatPosition), typeof(InCombat)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
                foreach (var entity in entities)
                    m_Replay.Manager.SetComponentData(entity, new CombatPosition { Value = float3.zero });
            Run();
            var instances = ById();
            // Rendered depth = key − push: strictly back-to-front by (key, id), lowest ID in front on ties.
            var ordered = instances.OrderByDescending(p => m_Basis.DepthKey(p.Value.Position)).ThenByDescending(p => p.Key).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                var back = m_Basis.DepthKey(ordered[i - 1].Value.Position) - ordered[i - 1].Value.DepthOffset;
                var front = m_Basis.DepthKey(ordered[i].Value.Position) - ordered[i].Value.DepthOffset;
                Assert.That(back - front, Is.GreaterThan(0f), $"{ordered[i - 1].Key} behind {ordered[i].Key}");
            }

            Assert.That(m_Extraction.MaxDepthOffset, Is.LessThanOrEqualTo(CombatSpriteExtraction.DepthOffsetBudget));
            var leaders = m_Replay.Records.Last();
            Assert.That(m_Basis.DepthKey(instances[leaders.HumanId].Position), Is.EqualTo(m_Basis.DepthKey(instances[leaders.AiId].Position)),
                "Both leaders stand at Z = 0: an exact tie…");
            Assert.That(instances[leaders.HumanId].DepthOffset, Is.GreaterThan(instances[leaders.AiId].DepthOffset), "…resolved by the lower ID (human) in front.");

            // Despawning reorders chunks; the remaining fighters keep the same relative depth order.
            var before = ordered.Select(p => p.Key).ToArray();
            var removed = before[before.Length / 2];
            m_Replay.Request(BattleRequest.Despawn(new CombatantId { Value = removed }));
            m_Replay.Step();
            Run();
            var after = ById().OrderByDescending(p => m_Basis.DepthKey(p.Value.Position) - p.Value.DepthOffset).Select(p => p.Key).ToArray();
            Assert.That(after, Is.EqualTo(before.Where(id => id != removed).ToArray()));
        }

        [Test]
        public void SelectedTrimFacingAndConfiguredScaleDefineTheActualOverlap()
        {
            m_Replay.Request(BattleRequest.Start(new BattleSetup
            {
                Human = new CombatantSetup { Team = 1, Position = float3.zero, Facing = FacingDirection.Right },
                Ai = new CombatantSetup { Team = 2, Position = new float3(1f, 0f, 0f), Facing = FacingDirection.Left },
            }));
            m_Replay.Step();
            var human = m_Replay.Session.HumanCombatant;
            var ai = m_Replay.Session.AiCombatant;
            Run();
            Assert.That(ById()[human.Value].DepthOffset, Is.EqualTo(0f), "Default art scale: trimmed cards are disjoint.");
            m_Extraction.Run(m_Replay.World, m_Atlas, m_Basis, Separation, 0, pixelWorldSize: 0.125f);
            Assert.That(ById()[human.Value].DepthOffset, Is.GreaterThan(0f), "Larger configured scale: same cards overlap.");
            m_Extraction.Select(ai, 2); // tech_c is wholly left of its authored pivot; facing left puts it to the right
            m_Extraction.Run(m_Replay.World, m_Atlas, m_Basis, Separation, 0, pixelWorldSize: 0.125f);
            Assert.That(ById()[human.Value].DepthOffset, Is.EqualTo(0f), "Selected page-1 trim is mirrored away from the local card.");
        }

        [Test]
        public void FightersCrossingInDepthOrAlongXSwapFrontOnlyWhenTheirDepthOrderChanges()
        {
            // Real movement on fixed ticks: the human walks +Z and the AI −Z through each other (same X), then the human
            // walks along X past the AI at equal depth. Front = smaller Z; at equal Z the lower ID (human); never flickers.
            var setup = new BattleSetup
            {
                Human = new CombatantSetup { Team = 1, Position = new float3(0f, 0f, -1f), Facing = FacingDirection.Right },
                Ai = new CombatantSetup { Team = 2, Position = new float3(0f, 0f, 1f), Facing = FacingDirection.Left },
            };
            m_Replay.Request(BattleRequest.Start(setup));
            const uint crossing = 30, alongX = 30;
            for (uint r = 1; r <= crossing; r++)
                m_Replay.Script.Player(r, r, ReplayRole.Human, moveZ: 1).Ai(r, r, ReplayRole.Ai, moveZ: -1);
            for (uint r = crossing + 1; r <= crossing + alongX; r++)
                m_Replay.Script.Player(r, r, ReplayRole.Human, moveX: 1);

            var humanFrontTicks = 0;
            var aiFrontTicks = 0;
            var lastFront = 0u;
            var switches = 0;
            for (var tick = 0; tick < crossing + alongX; tick++)
            {
                m_Replay.Step();
                Run();
                var record = m_Replay.Records.Last();
                var human = record.Combatant(record.HumanId);
                var ai = record.Combatant(record.AiId);
                var instances = ById();
                var humanDepth = m_Basis.DepthKey(instances[record.HumanId].Position) - instances[record.HumanId].DepthOffset;
                var aiDepth = m_Basis.DepthKey(instances[record.AiId].Position) - instances[record.AiId].DepthOffset;
                var expectedHumanFront = human.Position.z < ai.Position.z || human.Position.z == ai.Position.z;
                Assert.That(humanDepth < aiDepth, Is.EqualTo(expectedHumanFront),
                    $"tick {tick}: human z {human.Position.z}, AI z {ai.Position.z}");
                var front = humanDepth < aiDepth ? record.HumanId : record.AiId;
                if (lastFront != 0 && front != lastFront)
                    switches++;
                lastFront = front;
                if (front == record.HumanId)
                    humanFrontTicks++;
                else
                    aiFrontTicks++;
            }

            Assert.That(aiFrontTicks, Is.GreaterThan(0), "The AI is in front after crossing.");
            Assert.That(humanFrontTicks, Is.GreaterThan(0), "The human is in front while it is nearer.");
            Assert.That(switches, Is.EqualTo(1), "Exactly one front change: the crossing; walking past along X changes nothing.");
        }

        [Test]
        public void EndRestartDefeatAndDespawnLeaveNoStaleInstances()
        {
            m_Replay.Request(BattleRequest.Start(Setup(2)));
            m_Replay.Step();
            Run();
            Assert.That(m_Extraction.Count, Is.EqualTo(4));
            var first = m_Replay.Session;
            m_Extraction.Select(first.HumanCombatant, 2);

            // Defeat flips InCombat off (simulated here; the real owner is a later stage).
            var manager = m_Replay.Manager;
            using (var query = manager.CreateEntityQuery(typeof(CombatantId)))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                var ai = entities.First(e => manager.GetComponentData<CombatantId>(e).Value == first.AiCombatant.Value);
                manager.SetComponentEnabled<InCombat>(ai, false);
            }

            Run();
            Assert.That(ById().Keys, Has.No.Member(first.AiCombatant.Value), "Disabled InCombat is not drawn.");
            Assert.That(m_Extraction.Count, Is.EqualTo(3));

            m_Replay.Request(BattleRequest.Despawn(first.HumanCombatant));
            m_Replay.Step();
            Run();
            Assert.That(m_Extraction.Count, Is.EqualTo(2));

            m_Replay.Request(BattleRequest.End());
            m_Replay.Step();
            Run();
            Assert.That(m_Extraction.Count, Is.EqualTo(0), "End: no geometry left.");
            Assert.That(m_Extraction.PageInstances(0) + m_Extraction.PageInstances(1), Is.EqualTo(0));

            m_Replay.Request(BattleRequest.Start(Setup(2)));
            m_Replay.Step();
            Run();
            var second = m_Replay.Session;
            Assert.That(m_Extraction.Count, Is.EqualTo(4));
            Assert.That(m_Extraction.SelectionCount, Is.EqualTo(0), "A new battle drops the previous selections.");
            Assert.That(ById().Keys.Min(), Is.EqualTo(second.HumanCombatant.Value));
            Assert.That(ById().Keys, Has.No.Member(first.HumanCombatant.Value));

            m_Extraction.Select(second.HumanCombatant, 1);
            m_Replay.Request(BattleRequest.Restart(Setup(1)));
            m_Replay.Step();
            Run();
            Assert.That(m_Extraction.Count, Is.EqualTo(2), "Restart in the same World replaces every instance.");
            Assert.That(ById().Values.All(i => i.Frame == 0u), Is.True);
        }

        [Test]
        public void MissingOrReplacedWorldsAreSafeAndIsolated()
        {
            m_Extraction.Run(null, m_Atlas, m_Basis, Separation, 0);
            Assert.That(m_Extraction.Count, Is.EqualTo(0));
            Assert.That(m_Extraction.BoundWorld, Is.Null);

            m_Replay.Request(BattleRequest.Start(Setup(1)));
            m_Replay.Step();
            using var other = new ReplayWorld("Sprites other");
            other.Request(BattleRequest.Start(Setup(5)));
            other.Step();

            Run();
            Assert.That(m_Extraction.Count, Is.EqualTo(2));
            m_Extraction.Select(m_Replay.Session.HumanCombatant, 2);
            Run(other.World);
            Assert.That(m_Extraction.Count, Is.EqualTo(10), "Another World is read on its own…");
            Assert.That(m_Extraction.SelectionCount, Is.EqualTo(0), "…and selections never cross Worlds.");
            Assert.That(m_Extraction.BoundWorld, Is.SameAs(other.World));

            other.Dispose();
            m_Extraction.Run(other.World, m_Atlas, m_Basis, Separation, 0);
            Assert.That(m_Extraction.Count, Is.EqualTo(0), "A disposed World draws nothing and throws nothing.");
            Assert.That(m_Extraction.BoundWorld, Is.Null);
            Run();
            Assert.That(m_Extraction.Count, Is.EqualTo(2), "A later World binds again.");

            m_Extraction.Run(m_Replay.World, null, m_Basis, Separation, 0);
            Assert.That(m_Extraction.Count, Is.EqualTo(0), "No atlas: zero instances, the battle keeps running.");
        }

        [Test]
        public void ExtractionIsReadOnlyAndLeavesTheSimulationIdentical()
        {
            // Same script in two Worlds; one is extracted between every frame. Every recorded tick must match.
            using var plain = new ReplayWorld("Sprites plain");
            foreach (var world in new[] { m_Replay, plain })
            {
                world.Request(BattleRequest.Start(Setup(200)));
                for (uint r = 1; r <= 40; r++)
                    world.Script.Player(r, r, ReplayRole.Human, moveX: (sbyte)(r % 3 - 1), moveZ: 1, pressed: r % 9 == 0 ? CommandButtons.Jump : CommandButtons.None)
                        .Ai(r, r, ReplayRole.Ai, moveX: -1);
            }

            var entityCount = -1;
            for (var frame = 0; frame < 40; frame++)
            {
                m_Replay.Step();
                plain.Step();
                var version = m_Replay.Manager.GetComponentOrderVersion<CombatPosition>();
                var tick = m_Replay.Tick;
                var queue = (m_Replay.PendingCount, m_Replay.ResultCount, m_Replay.SubmissionCount);
                Run();
                Run();
                Assert.That(m_Replay.Manager.GetComponentOrderVersion<CombatPosition>(), Is.EqualTo(version), "No structural change.");
                Assert.That(m_Replay.Tick, Is.EqualTo(tick), "No clock advance.");
                Assert.That((m_Replay.PendingCount, m_Replay.ResultCount, m_Replay.SubmissionCount), Is.EqualTo(queue), "Queue untouched.");
                if (entityCount < 0)
                    entityCount = m_Replay.EntityCount;
                Assert.That(m_Replay.EntityCount, Is.EqualTo(entityCount), "No entities added by presentation.");
            }

            Assert.That(m_Replay.Records.Count, Is.EqualTo(plain.Records.Count));
            for (var i = 0; i < plain.Records.Count; i++)
                Assert.That(m_Replay.Records[i].Raw, Is.EqualTo(plain.Records[i].Raw), $"Tick record {i}");
            Assert.That(m_Extraction.Count, Is.EqualTo(400));
        }
    }
}
