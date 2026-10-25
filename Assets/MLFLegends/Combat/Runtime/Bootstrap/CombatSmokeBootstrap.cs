using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Scene entry point for a 1v1 battle in the default ECS world: requests a (re)start when enabled and the end
    /// of the battle when disabled or destroyed. Holds no combat state; the battle lives in ECS.
    /// </summary>
    /// <remarks>
    /// Only appends <see cref="BattleRequest"/>s; <see cref="BattleLifecycleSystem"/> creates and destroys the
    /// combatants. A restart is used on enable so that a second bootstrap, or a scene reload, replaces the
    /// fighters instead of duplicating them.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class CombatSmokeBootstrap : MonoBehaviour
    {
        [SerializeField] byte m_HumanTeam = 1;
        [SerializeField] Vector3 m_HumanPosition = new Vector3(-2f, 0f, 0f);
        [SerializeField] FacingDirection m_HumanFacing = FacingDirection.Right;
        [SerializeField] byte m_AiTeam = 2;
        [SerializeField] Vector3 m_AiPosition = new Vector3(2f, 0f, 0f);
        [SerializeField] FacingDirection m_AiFacing = FacingDirection.Left;

        public BattleSetup Setup => new BattleSetup
        {
            Human = new CombatantSetup { Team = m_HumanTeam, Position = (float3)m_HumanPosition, Facing = m_HumanFacing },
            Ai = new CombatantSetup { Team = m_AiTeam, Position = (float3)m_AiPosition, Facing = m_AiFacing },
        };

        void OnEnable()
        {
            var setup = Setup;
            if (!setup.IsValid)
            {
                Debug.LogError($"{nameof(CombatSmokeBootstrap)} on '{name}': both facings must be Left/Right and the teams must differ.", this);
                return;
            }

            if (TryGetEntityManager(out var entityManager))
                BattleRequest.Submit(entityManager, BattleRequest.Restart(setup));
        }

        void OnDisable()
        {
            // On Play Mode exit the default world may already be disposed; it takes its combatants with it.
            if (TryGetEntityManager(out var entityManager))
                BattleRequest.Submit(entityManager, BattleRequest.End());
        }

        static bool TryGetEntityManager(out EntityManager entityManager)
        {
            entityManager = default;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return false;

            entityManager = world.EntityManager;
            return !entityManager.CreateEntityQuery(typeof(BattleSession)).IsEmptyIgnoreFilter;
        }
    }
}
