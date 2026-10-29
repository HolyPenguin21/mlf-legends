using System.Collections.Generic;
using System.Linq;
using MLFLegends.Combat;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Tests.EditMode
{
    // Independent brute-force hit oracle (test-only). It reads the same final ECS positions after the spatial snapshot and
    // before CombatHitSystem, enumerates EVERY in-combat fighter with its own copy of the Stage 05.1 box rule (no index,
    // no CombatHitSystem.Inside/StrikeQuery call) and predicts the HP/ledger mutation. HitOracleCheck compares the real
    // owner's result in Publication of the same tick, so catch-up frames are checked per tick.
    [DisableAutoCreation]
    [UpdateInGroup(typeof(CombatStateUpdateSystemGroup))]
    [UpdateAfter(typeof(CombatSpatialIndexSystem))]
    [UpdateBefore(typeof(CombatHitSystem))]
    public partial class HitOracleProbe : SystemBase
    {
        public readonly List<(uint Tick, uint Attacker, uint Target)> Expected = new List<(uint, uint, uint)>();
        public readonly Dictionary<uint, int> HealthBefore = new Dictionary<uint, int>();
        public readonly Dictionary<uint, uint[]> LedgerBefore = new Dictionary<uint, uint[]>();
        public uint Tick;
        public int ActiveAttackers;

        struct Row
        {
            public uint Id; public byte Team; public float3 Position; public FacingDirection Facing;
            public CombatAttackState Attack; public bool HasHealth, CanAttack; public int Health; public uint[] Ledger;
        }

        public static bool OracleInside(in CombatAttackDefinition s, float3 attacker, FacingDirection facing, float3 target)
        {
            var dx = target.x - attacker.x;
            var forward = facing == FacingDirection.Left ? -dx : dx;
            return forward >= s.ReachMin && forward <= s.ReachMax && math.abs(target.z - attacker.z) <= s.DepthHalfWidth
                && math.abs(target.y - attacker.y) <= s.HeightTolerance;
        }

        protected override void OnUpdate()
        {
            Tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            HealthBefore.Clear(); LedgerBefore.Clear(); ActiveAttackers = 0;
            using var query = EntityManager.CreateEntityQuery(typeof(CombatantId), typeof(CombatTeam), typeof(CombatPosition), typeof(InCombat));
            using var entities = query.ToEntityArray(Allocator.Temp);
            var rows = entities.ToArray().Select(e => new Row
            {
                Id = EntityManager.GetComponentData<CombatantId>(e).Value, Team = EntityManager.GetComponentData<CombatTeam>(e).Value,
                Position = EntityManager.GetComponentData<CombatPosition>(e).Value,
                Facing = EntityManager.HasComponent<CombatFacing>(e) ? EntityManager.GetComponentData<CombatFacing>(e).Value : FacingDirection.Right,
                Attack = EntityManager.HasComponent<CombatAttackState>(e) ? EntityManager.GetComponentData<CombatAttackState>(e) : default,
                CanAttack = EntityManager.HasComponent<CombatAttackState>(e) && EntityManager.HasComponent<CombatFacing>(e) && EntityManager.HasBuffer<CombatHitRecord>(e),
                HasHealth = EntityManager.HasComponent<CombatHealth>(e),
                Health = EntityManager.HasComponent<CombatHealth>(e) ? EntityManager.GetComponentData<CombatHealth>(e).Current : 0,
                Ledger = EntityManager.HasBuffer<CombatHitRecord>(e)
                    ? EntityManager.GetBuffer<CombatHitRecord>(e, true).AsNativeArray().ToArray().Select(h => h.Target.Value).ToArray() : new uint[0],
            }).OrderBy(r => r.Id).ToArray();
            foreach (var r in rows) { if (r.HasHealth) HealthBefore[r.Id] = r.Health; LedgerBefore[r.Id] = r.Ledger; }
            foreach (var a in rows)
            {
                if (!a.CanAttack || a.Attack.Kind == CombatAttackKind.None
                    || a.Attack.Timing.PhaseAt(Tick - a.Attack.StartTick) != CombatAttackPhase.Active) continue;
                ActiveAttackers++;
                var strike = CombatAttackTuning.Get(a.Attack.Kind);
                foreach (var t in rows)
                    if (t.HasHealth && t.Team != a.Team && t.Id != a.Id && !a.Ledger.Contains(t.Id)
                        && OracleInside(strike, a.Position, a.Facing, t.Position))
                        Expected.Add((Tick, a.Id, t.Id));
            }
        }
    }

    [DisableAutoCreation]
    public partial class HitOracleCheck : SystemBase
    {
        public HitOracleProbe Probe;
        public readonly List<string> Mismatches = new List<string>();
        public int CheckedTicks, CheckedHits, ActiveTicks;

        protected override void OnUpdate()
        {
            var tick = SystemAPI.GetSingleton<SimulationClock>().Tick;
            if (Probe.Tick != tick) return; // State did not run (paused, no battle).
            var expected = Probe.Expected.Where(e => e.Tick == tick).ToArray();
            using var query = EntityManager.CreateEntityQuery(typeof(CombatantId));
            using var entities = query.ToEntityArray(Allocator.Temp);
            var byId = entities.ToArray().ToDictionary(e => EntityManager.GetComponentData<CombatantId>(e).Value);
            foreach (var pair in Probe.LedgerBefore)
            {
                if (!byId.TryGetValue(pair.Key, out var entity) || !EntityManager.HasBuffer<CombatHitRecord>(entity)) continue;
                var after = EntityManager.GetBuffer<CombatHitRecord>(entity, true).AsNativeArray().ToArray().Select(h => h.Target.Value).ToArray();
                var want = pair.Value.Concat(expected.Where(e => e.Attacker == pair.Key).Select(e => e.Target)).ToArray();
                if (!after.SequenceEqual(want))
                    Mismatches.Add($"tick {tick} ledger #{pair.Key}: [{string.Join(",", after)}] != oracle [{string.Join(",", want)}]");
            }
            foreach (var pair in Probe.HealthBefore)
            {
                var damage = expected.Where(e => e.Target == pair.Key)
                    .Sum(e => CombatAttackTuning.Get(Kind(byId, e.Attacker)).Damage);
                var hp = EntityManager.GetComponentData<CombatHealth>(byId[pair.Key]).Current;
                var want = pair.Value;
                foreach (var e in expected.Where(x => x.Target == pair.Key))
                    want = math.max(0, want - CombatAttackTuning.Get(Kind(byId, e.Attacker)).Damage);
                if (hp != want) Mismatches.Add($"tick {tick} hp #{pair.Key}: {hp} != oracle {want} (damage {damage})");
            }
            CheckedTicks++; CheckedHits += expected.Length;
            if (Probe.ActiveAttackers > 0) ActiveTicks++;
            Probe.Expected.RemoveAll(e => e.Tick == tick);
        }

        CombatAttackKind Kind(Dictionary<uint, Entity> byId, uint attacker) =>
            EntityManager.GetComponentData<CombatAttackState>(byId[attacker]).Kind;
    }

    public static class HitOracle
    {
        /// <summary>Adds the probe (State, before Hit) and its check (Publication) to a replay World.</summary>
        public static HitOracleCheck Attach(ReplayWorld w)
        {
            var probe = w.AddToPhase<HitOracleProbe>(typeof(CombatStateUpdateSystemGroup));
            var check = w.AddToPhase<HitOracleCheck>(typeof(CombatPublicationSystemGroup));
            check.Probe = probe;
            return check;
        }
    }
}
