using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>A committed straight interval, never a solver proposal/correction. Times are fractions of this fixed tick.</summary>
    public struct CombatMotionSegment
    {
        public CombatSpatialBody Actor;
        public float3 From, To;
        public float Begin, End;
        public bool GroundedFrom, GroundedTo;
    }

    public static class CombatMotionGeometry
    {
        // Intersection with the interior of the supported convex solid. Expanding by the body's bounding prism is
        // conservative at rounded corners: an uncertain chord is held, never published through a solid. Ramp top uses
        // the same highest-footprint plane as support. A tangent to a floor/ramp is not an interior crossing.
        public static bool SegmentClear(NativeArray<CombatArenaShape> shapes, float3 from, float3 to,
            float radius = 0, float height = 0)
        {
            foreach (var s in shapes)
            {
                var a = s.Local(from.xz); var b = s.Local(to.xz);
                var lo = 0f; var hi = 1f;
                if (!Clip(a.x, b.x, s.HalfSize.x + radius, ref lo, ref hi)
                    || !Clip(-a.x, -b.x, s.HalfSize.x + radius, ref lo, ref hi)
                    || !Clip(a.y, b.y, s.HalfSize.y + radius, ref lo, ref hi)
                    || !Clip(-a.y, -b.y, s.HalfSize.y + radius, ref lo, ref hi)
                    || !Clip(-from.y, -to.y, height - s.Bottom, ref lo, ref hi)) continue;
                var slope = s.Kind == CombatArenaKind.Ramp ? s.Slope : 0;
                var top = s.Kind == CombatArenaKind.Ramp ? s.Bottom + s.HalfSize.x * slope + radius * slope : s.Top;
                if (Clip(from.y - a.x * slope, to.y - b.x * slope, top, ref lo, ref hi) && lo < hi) return false;
            }
            return true;
        }

        static bool Clip(float a, float b, float limit, ref float lo, ref float hi)
        {
            limit -= CombatArenaTuning.Skin;
            var d = b - a;
            if (math.abs(d) < 1e-12f) return a < limit;
            var t = (limit - a) / d;
            if (d > 0) hi = math.min(hi, t); else lo = math.max(lo, t);
            return lo < hi;
        }

        /// <summary>Relative moving point against the frozen closed box, clipped to the same interval for both actors.</summary>
        public static bool Sweep(in CombatStrikeParameters strike, float facing, float3 a0, float3 a1,
            float3 b0, float3 b1, float lo, float hi, out float contact)
        {
            var start = b0 - a0; start.x *= facing; start.y -= strike.HitOffsetY;
            var end = b1 - a1; end.x *= facing; end.y -= strike.HitOffsetY;
            if (Axis(start.x, end.x, strike.ReachMin, strike.ReachMax, ref lo, ref hi)
                && Axis(start.y, end.y, -strike.HeightTolerance, strike.HeightTolerance, ref lo, ref hi)
                && Axis(start.z, end.z, -strike.DepthHalfWidth, strike.DepthHalfWidth, ref lo, ref hi))
            { contact = lo; return true; }
            contact = 0; return false;
        }

        static bool Axis(float a, float b, float min, float max, ref float lo, ref float hi)
        {
            var v = b - a;
            if (v == 0) return a >= min && a <= max;
            var enter = (min - a) / v; var exit = (max - a) / v;
            lo = math.max(lo, math.min(enter, exit)); hi = math.min(hi, math.max(enter, exit));
            return lo <= hi;
        }
    }

    public partial class CombatSpatialIndexSystem
    {
        const int AbilitySlices = 16; // bounded numerical integration inside the existing tick/solver, not another clock
        NativeList<CombatMotionSegment> m_MotionSegments;
        NativeList<CombatContactActor> m_TickInputs;
        NativeList<CombatSpatialBody> m_MotionBodies;
        uint m_MotionTick, m_MotionBattle;
        int m_MotionSlices;
        // Jobs require a valid safety handle even when no swept attacker will read this field.
        internal NativeArray<CombatMotionSegment> EmptyMotionSegments => m_MotionSegments.AsArray().GetSubArray(0, 0);
        public int MotionSegmentCount => m_MotionSegments.Length;
        public long MotionCapacityBytes => (long)m_MotionSegments.Capacity * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CombatMotionSegment>()
            + (long)m_TickInputs.Capacity * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CombatContactActor>()
            + (long)m_MotionBodies.Capacity * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CombatSpatialBody>();

        void SolveMotion(NativeArray<CombatArenaShape> shapes, uint tick, uint landingTicks, uint battle, bool physics)
        {
            m_MotionSegments.Clear(); m_TickInputs.Clear(); m_MotionBodies.Clear();
            m_MotionTick = tick; m_MotionBattle = battle;
            m_MotionSlices = 1;
            for (var i = 0; i < m_Movement.Length; i++)
                if (m_Movement[i].Trace) m_MotionSlices = AbilitySlices;
            m_TickInputs.AddRange(m_Movement.AsArray());
            var combined = new CombatContactDiagnostics { Tick = tick, BattleNumber = battle, VelocityComplete = true, ResidualComplete = true };
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            long remaining = PairVisitBudgetOverride > 0 ? PairVisitBudgetOverride : CombatContactTuning.MaxPairVisits;
            for (var slice = 0; slice < m_MotionSlices; slice++)
            {
                for (var i = 0; i < m_Movement.Length; i++)
                {
                    var a = m_Movement[i]; var original = m_TickInputs[i];
                    if (slice > 0)
                    {
                        a.Start = a.Result.Position;
                        a.Motion.VerticalVelocity = a.Result.Velocity; a.Motion.Grounded = a.Result.Grounded;
                        a.Motion.GroundHeight = a.Result.GroundHeight;
                        a.Velocity = a.VelocityOut;
                    }
                    a.Desired = original.Result.Landed && landingTicks > 0 ? float2.zero : original.Desired / m_MotionSlices;
                    a.Jump = slice == 0 && original.Jump;
                    a.CorrectionUsed = a.YieldUsed = 0; a.YieldAttempts = 0;
                    a.Physical = a.External = float2.zero; a.Lift = 0; a.Walls = default;
                    m_Movement[i] = a;
                }
                var skipped = remaining <= 0;
                var d = new CombatContactDiagnostics { Tick = tick, BattleNumber = battle, WorkLimit = true };
                if (!skipped)
                {
                    new CombatContactJob { Actors = m_Movement, Cells = m_Cells, Seen = m_Seen, Pairs = m_ContactPairs, Contacts = m_Contacts,
                        Shapes = shapes, Impulses = slice == 0 ? m_Impulses.AsArray() : m_Impulses.AsArray().GetSubArray(0, 0),
                        Diagnostics = m_ContactDiagnostics, Tick = tick, LandingTicks = landingTicks, BattleNumber = battle,
                        DeltaTime = SimulationTiming.TickDuration / m_MotionSlices, Physics = physics, PairVisitBudget = remaining
                    }.Schedule().Complete();
                    d = m_ContactDiagnostics[0]; remaining -= d.PairVisits;
                }
                // Freeze an unrepresentable chord as one atomic slice. Previously accepted slices remain real travel;
                // solver iterations and failed proposals are never appended to the path.
                // A velocity-only budget failure already discards velocity, but its positions are solved. Preserve them,
                // including on the ordinary single-step path. Spending the exact budget is not itself a failed solve.
                var hold = skipped || (d.WorkLimit && !d.ResidualComplete);
                if (m_MotionSlices > 1)
                    for (var i = 0; i < m_Movement.Length && !hold; i++)
                    {
                        var a = m_Movement[i];
                        hold |= !a.Frozen && !CombatMotionGeometry.SegmentClear(shapes, a.Start, a.Result.Position, a.Body.Radius, a.Body.Height);
                    }
                for (var i = 0; i < m_Movement.Length; i++)
                {
                    var a = m_Movement[i];
                    if (hold || a.Frozen)
                    {
                        a.Result.Position = a.Start; a.Result.ControlledDistance = 0;
                        a.Result.Velocity = a.Motion.VerticalVelocity; a.Result.Grounded = a.Motion.Grounded;
                        a.Result.Landed = false; a.VelocityOut = float2.zero;
                    }
                    var actor = new CombatSpatialBody { Entity = a.Entity, Id = new CombatantId { Value = a.Id },
                        Team = new CombatTeam { Value = a.Team }, Body = a.Body, Position = a.Result.Position };
                    m_MotionSegments.Add(new CombatMotionSegment { Actor = actor, From = a.Start, To = a.Result.Position,
                        Begin = slice / (float)m_MotionSlices, End = (slice + 1f) / m_MotionSlices,
                        GroundedFrom = a.Motion.Grounded, GroundedTo = a.Result.Grounded });
                    var original = m_TickInputs[i];
                    original.Result.ControlledDistance += a.Result.ControlledDistance;
                    original.Result.Landed |= a.Result.Landed;
                    // A solver freeze remains authoritative on the ordinary path. In a sliced solve it holds this slice;
                    // later slices may retry from that accepted start (never from the rejected proposal).
                    if (m_MotionSlices > 1) a.Frozen = original.Frozen;
                    m_TickInputs[i] = original; m_Movement[i] = a;
                }
                Add(ref combined, d); if (hold) combined.TraceHolds++;
                m_Contacts.Clear();
            }
            for (var i = 0; i < m_Movement.Length; i++)
            {
                var a = m_Movement[i]; var original = m_TickInputs[i];
                a.Result.ControlledDistance = original.Result.ControlledDistance;
                a.Result.Landed = original.Result.Landed && a.Result.Grounded;
                a.Start = original.Start; a.Motion = original.Motion; a.Desired = original.Desired;
                m_Movement[i] = a;
                CombatSpatialBounds.TryFromBody(a.Result.Position, a.Body, out var bounds);
                for (var slice = 0; slice < m_MotionSlices; slice++)
                {
                    var segment = m_MotionSegments[slice * m_Movement.Length + i];
                    CombatSpatialBounds.TryFromBody(segment.From, a.Body, out var b0);
                    CombatSpatialBounds.TryFromBody(segment.To, a.Body, out var b1);
                    bounds.Min = math.min(bounds.Min, math.min(b0.Min, b1.Min)); bounds.Max = math.max(bounds.Max, math.max(b0.Max, b1.Max));
                }
                m_MotionBodies.Add(new CombatSpatialBody { Entity = a.Entity, Id = new CombatantId { Value = a.Id },
                    Team = new CombatTeam { Value = a.Team }, Position = a.Result.Position, Body = a.Body, Bounds = bounds });
            }
            combined.MotionSlices = m_MotionSlices;
            LastContacts = combined; LastMovementKernelMicroseconds = MicrosSince(started);
        }

        // Same owner, one bounded conservative scan of derived motion envelopes; no second grid. Live membership/epoch
        // must pass the FINAL snapshot guard before anyone can consume it. Caller batches all active attacks.
        internal bool TryMotionBatch(NativeArray<CombatSpatialBounds> queries, NativeList<CombatSpatialBody> results,
            NativeList<int2> ranges, out NativeArray<CombatMotionSegment> segments, out int bodyCount)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            ResetQueryDiagnostics();
            results.Clear(); ranges.Clear(); segments = default; bodyCount = 0;
            if (!IsCurrent() || m_Info.Tick != m_MotionTick || m_Info.BattleNumber != m_MotionBattle
                || m_MotionBodies.Length != m_Bodies.Length) return false;
            foreach (var query in queries) ValidateQuery(query);
            LastQueryUsedFullScan = queries.Length > 0;
            for (var q = 0; q < queries.Length; q++)
            {
                var start = results.Length;
                for (var i = 0; i < m_MotionBodies.Length; i++)
                {
                    LastQueryEntriesVisited++;
                    if (queries[q].Overlaps(m_MotionBodies[i].Bounds)) results.Add(m_MotionBodies[i]);
                }
                ranges.Add(new int2(start, results.Length - start));
            }
            segments = m_MotionSegments.AsArray(); bodyCount = m_MotionBodies.Length;
            LastQueryCandidates = results.Length;
            LastQueryMicroseconds = MicrosSince(started);
            return true;
        }

        internal bool TryDamageBounds(uint id, in CombatStrikeParameters strike, float facing, float lo, float hi,
            out CombatSpatialBounds bounds)
        {
            bounds = new CombatSpatialBounds { Min = new float3(float.PositiveInfinity), Max = new float3(float.NegativeInfinity) };
            if (m_Info.Tick != m_MotionTick || m_Info.BattleNumber != m_MotionBattle) return false;
            var found = false;
            foreach (var segment in m_MotionSegments)
            {
                if (segment.Actor.Id.Value != id || segment.End <= lo || segment.Begin >= hi) continue;
                var span = segment.End - segment.Begin;
                var from = math.lerp(segment.From, segment.To, math.max(0, (lo - segment.Begin) / span));
                var to = math.lerp(segment.From, segment.To, math.min(1, (hi - segment.Begin) / span));
                var a = CombatHitSystem.StrikeQuery(strike, from, facing, segment.Actor.Body.Height);
                var b = CombatHitSystem.StrikeQuery(strike, to, facing, segment.Actor.Body.Height);
                bounds.Min = math.min(bounds.Min, math.min(a.Min, b.Min));
                bounds.Max = math.max(bounds.Max, math.max(a.Max, b.Max)); found = true;
            }
            return found;
        }

        internal bool CopyMotionSegments(NativeList<CombatMotionSegment> output)
        {
            output.Clear();
            if (!IsCurrent() || m_Info.Tick != m_MotionTick || m_Info.BattleNumber != m_MotionBattle) return false;
            output.AddRange(m_MotionSegments.AsArray()); return true;
        }

        static void Add(ref CombatContactDiagnostics a, in CombatContactDiagnostics b)
        {
            a.Candidates += b.Candidates; a.PairVisits += b.PairVisits; a.PeakPairs = math.max(a.PeakPairs, b.PeakPairs);
            a.Iterations += b.Iterations; a.FrozenBodies += b.FrozenBodies; a.WorldRejections += b.WorldRejections;
            a.YieldAttempts += b.YieldAttempts; a.ResidualPenetration = math.max(a.ResidualPenetration, b.ResidualPenetration);
            a.UnresolvedPenetration = math.max(a.UnresolvedPenetration, b.UnresolvedPenetration);
            a.ResidualConstraint = math.max(a.ResidualConstraint, b.ResidualConstraint); a.ResidualComplete &= b.ResidualComplete;
            a.WorkLimit |= b.WorkLimit; a.KernelRanManaged |= b.KernelRanManaged; a.ImpulsesApplied += b.ImpulsesApplied;
            a.ImpulsesDropped += b.ImpulsesDropped; a.ImpulsesRejected += b.ImpulsesRejected;
            a.PhysicalBodies = math.max(a.PhysicalBodies, b.PhysicalBodies); a.DiscardedVelocities += b.DiscardedVelocities;
            a.OverSpeedBodies += b.OverSpeedBodies; a.MaxExternalSpeed = math.max(a.MaxExternalSpeed, b.MaxExternalSpeed);
            a.SlopAcceptedPairs += b.SlopAcceptedPairs; a.SlopAcceptedDepth = math.max(a.SlopAcceptedDepth, b.SlopAcceptedDepth);
            a.LocalHolds += b.LocalHolds; a.RolledBackBodies += b.RolledBackBodies;
            a.VelocityContacts += b.VelocityContacts; a.VelocityIterations += b.VelocityIterations;
            a.VelocityResidual = math.max(a.VelocityResidual, b.VelocityResidual); a.VelocityComplete &= b.VelocityComplete;
            a.VelocityDiscarded += b.VelocityDiscarded; a.WallOverflowBodies += b.WallOverflowBodies;
        }
    }
}
