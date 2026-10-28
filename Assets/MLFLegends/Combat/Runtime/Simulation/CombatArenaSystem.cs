using System;
using Unity.Collections;
using Unity.Entities;

namespace MLFLegends.Combat
{
    public struct CombatArenaState : IComponentData
    {
        public ulong Lease, NextLease;
        public uint Revision;
        public bool Required, Valid, Pending, PendingValid, Released;
        public uint ReleasedBattleNumber;
    }
    [InternalBufferCapacity(0)]
    public struct PendingCombatArenaShape : IBufferElementData { public CombatArenaShape Shape; }
    public struct CombatArenaDiagnostics : IComponentData
    {
        public uint Tick, Revision;
        public int FailedBodies;
        public CombatArenaFailure LastFailure;
    }

    // Owns the ECS storage and publication. Staging methods never change the active buffer.
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(BattleLifecycleSystem))]
    public partial class CombatArenaSystem : SystemBase
    {
        Entity m_Data;
        EntityQuery m_Session;
        protected override void OnCreate()
        {
            using var query = EntityManager.CreateEntityQuery(typeof(CombatArenaState));
            if (query.CalculateEntityCount() != 0) throw new InvalidOperationException("One CombatArenaSystem per World.");
            m_Data = EntityManager.CreateEntity(typeof(CombatArenaState), typeof(CombatArenaDiagnostics));
            m_Session = GetEntityQuery(typeof(BattleSession));
            EntityManager.AddBuffer<CombatArenaShape>(m_Data);
            EntityManager.AddBuffer<PendingCombatArenaShape>(m_Data);
        }
        protected override void OnUpdate()
        {
            var state = EntityManager.GetComponentData<CombatArenaState>(m_Data);
            if (state.Released && (m_Session.CalculateEntityCount() == 0 || !m_Session.GetSingleton<BattleSession>().IsActive
                || m_Session.GetSingleton<BattleSession>().BattleNumber != state.ReleasedBattleNumber))
            {
                state.Required = false; state.Released = false; state.Pending = true; state.PendingValid = false;
            }
            if (!state.Pending) return;
            var pending = EntityManager.GetBuffer<PendingCombatArenaShape>(m_Data);
            var active = EntityManager.GetBuffer<CombatArenaShape>(m_Data);
            active.Clear();
            if (state.Revision == uint.MaxValue) state.PendingValid = false; // Epoch never wraps; replace the World if exhausted.
            if (state.PendingValid)
                foreach (var row in pending) active.Add(row.Shape);
            pending.Clear();
            state.Pending = false;
            state.Valid = state.PendingValid;
            if (state.Revision != uint.MaxValue) state.Revision++;
            EntityManager.SetComponentData(m_Data, state);
        }
        protected override void OnDestroy()
        {
            if (EntityManager.Exists(m_Data)) EntityManager.DestroyEntity(m_Data);
        }
        static Entity Data(EntityManager manager)
        {
            using var query = manager.CreateEntityQuery(typeof(CombatArenaState));
            if (query.CalculateEntityCount() != 1) throw new InvalidOperationException("CombatArenaSystem required.");
            return query.GetSingletonEntity();
        }
        // Call outside fixed steps on the main thread. A new adapter acquires a new lease, not a static World registry.
        public static ulong Acquire(EntityManager manager)
        {
            var entity = Data(manager);
            var state = manager.GetComponentData<CombatArenaState>(entity);
            if (state.NextLease == ulong.MaxValue) throw new InvalidOperationException("Arena lease exhausted.");
            state.Lease = ++state.NextLease;
            state.Required = true; state.Released = false;
            // Fail closed until a valid source is staged, including missing/invalid production geometry.
            state.Pending = true; state.PendingValid = false;
            manager.GetBuffer<PendingCombatArenaShape>(entity).Clear();
            manager.SetComponentData(entity, state);
            return state.Lease;
        }
        public static bool Stage(EntityManager manager, ulong lease, CombatArenaShape[] shapes)
        {
            var entity = Data(manager);
            var state = manager.GetComponentData<CombatArenaState>(entity);
            if (lease == 0 || state.Lease != lease) return false;
            var valid = shapes != null && shapes.Length > 0 && shapes.Length <= CombatArenaTuning.MaxShapes;
            if (valid) foreach (var shape in shapes) valid &= shape.IsValid;
            var pending = manager.GetBuffer<PendingCombatArenaShape>(entity);
            pending.Clear();
            if (valid) foreach (var shape in shapes) pending.Add(new PendingCombatArenaShape { Shape = shape });
            state.Pending = true; state.PendingValid = valid;
            manager.SetComponentData(entity, state);
            return valid;
        }
        public static bool Release(EntityManager manager, ulong lease)
        {
            var entity = Data(manager);
            var state = manager.GetComponentData<CombatArenaState>(entity);
            if (lease == 0 || state.Lease != lease) return false;
            using var session = manager.CreateEntityQuery(typeof(BattleSession));
            state.ReleasedBattleNumber = session.CalculateEntityCount() == 1 ? session.GetSingleton<BattleSession>().BattleNumber : 0;
            state.Lease = 0; state.Released = true; // Retire the token; late Rebuild cannot revive it.
            state.Pending = true; state.PendingValid = false;
            manager.GetBuffer<PendingCombatArenaShape>(entity).Clear();
            manager.SetComponentData(entity, state);
            return true;
        }
    }
    // Same geometry owner, forwarded at paused-frame setup boundary before the existing lifecycle proxy.
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BattleLifecycleInitializationProxy))]
    public partial class CombatArenaInitializationProxy : SystemBase
    {
        protected override void OnUpdate() => World.GetExistingSystemManaged<CombatArenaSystem>()?.Update();
    }

}
