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
        bool m_Alive;
        EntityQuery m_Session;
        protected override void OnCreate()
        {
            using var query = EntityManager.CreateEntityQuery(typeof(CombatArenaState));
            if (query.CalculateEntityCount() != 0) throw new InvalidOperationException("One CombatArenaSystem per World.");
            m_Data = EntityManager.CreateEntity(typeof(CombatArenaState), typeof(CombatArenaDiagnostics));
            m_Session = GetEntityQuery(typeof(BattleSession));
            EntityManager.AddBuffer<CombatArenaShape>(m_Data);
            EntityManager.AddBuffer<PendingCombatArenaShape>(m_Data);
            m_Alive = true;
        }
        protected override void OnUpdate()
        {
            var state = EntityManager.GetComponentData<CombatArenaState>(m_Data);
            if (state.Released && (m_Session.CalculateEntityCount() == 0 || !m_Session.GetSingleton<BattleSession>().IsActive))
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
            m_Alive = false;
            if (EntityManager.Exists(m_Data)) EntityManager.DestroyEntity(m_Data);
        }
        // Call outside fixed steps on the main thread. A new adapter acquires a new lease, not a static World registry.
        public ulong Acquire()
        {
            if (!m_Alive) throw new InvalidOperationException("Arena owner is disposed.");
            var manager = EntityManager;
            var entity = m_Data;
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
        public bool Stage(ulong lease, CombatArenaShape[] shapes)
        {
            if (!m_Alive) return false;
            var manager = EntityManager;
            var entity = m_Data;
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
        public bool Release(ulong lease)
        {
            if (!m_Alive) return false;
            var manager = EntityManager;
            var entity = m_Data;
            var state = manager.GetComponentData<CombatArenaState>(entity);
            if (lease == 0 || state.Lease != lease) return false;
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
