using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>Starting calibration, not balance. Mass remains solely in CombatBody.</summary>
    public static class CombatContactTuning
    {
        public const float Skin = CombatArenaTuning.Skin;
        public const float ControlledMobility = .2f;
        public const float ControlledDistanceEpsilon = 4 * Skin; // Joint world/body tolerance must not advance run stride.
        public const float CorrectionBudget = .5f;
        // Accepted leftover after the iteration budget: a swept violation up to this fraction of the smaller radius
        // (.015 for the default body) is published and resolved as initial overlap next tick instead of rolling back
        // the whole island. Far below any crossing; deeper leftovers still freeze with diagnostics.
        public const float SlopRadiusFraction = .05f;
        // Escalation before an island rollback: hold only the bodies of pairs still violating beyond slop at their start
        // positions for this tick, then re-solve the rest around them. Bounded rounds × extra iterations.
        public const int MaxHoldRounds = 6;
        public const int HoldIterations = 16;
        public const float AllyYieldSpeed = 3f;
        public const int MaxYieldAttempts = 4;
        public const float TinyVerticalSpeed = 1e-12f;
        public const float TinyPlanarSpeedSquared = 1e-20f;
        public const float TinyInwardDistance = 1e-12f;
        public const float TinyCorrectionSquared = 1e-16f;
        public const int MaxIterations = 48;
        // Final-velocity pass (#69): a recorded contact still within this gap of touching exchanges normal velocity; the
        // pass stops when no velocity changes by more than VelocityTolerance (u/s) or after MaxIterations passes.
        public const float ContactGap = 4 * Skin;
        public const float VelocityTolerance = 1e-5f;
        // One budget of pair visits per Movement call, shared by the position solve and the final-velocity pass.
        public const int MaxPairVisits = 4000000;
        public const long MaxCandidateVisits = 32000000;
        public const int MaxBodies = BattleSetup.MaxCombatantsPerTeam * 2;
    }

    /// <summary>
    /// Stage 04.4 external-impulse calibration (technical, not balance); the only place of its coefficients. Per Movement tick:
    /// pending J → Δv = J/Mass (X/Z into ExternalVelocity, Y into VerticalVelocity) → explicit displacement ExternalVelocity·dt
    /// through the same world/body constraints → final velocity (#69): the post-impulse velocity loses its inward component
    /// along every world surface that blocked the body this tick, and every body pair that came into contact exchanges
    /// normal velocity as a perfectly inelastic, mass-weighted collision (held/frozen bodies are immovable); free tangential
    /// motion stays, walls are frictionless → Coulomb deceleration on the final support state (ground or air), then zero
    /// below StopSpeed. Achieved displacement is position only, never a velocity source.
    /// </summary>
    public static class CombatImpulseTuning
    {
        /// <summary>Largest accepted |X/Z external velocity| and |vertical Δv| of one tick's impulses, units/s (.4 per tick).</summary>
        /// <remarks>Matches the world substep budget (a .01-radius body needs &lt;=256 substeps) and the reserved swept-grid margin.
        /// An impulse that would exceed it is rejected with a diagnostic, never clipped.</remarks>
        public const float MaxExternalSpeed = 24f;
        public const float GroundDeceleration = 24f;
        public const float AirDeceleration = 2f;
        public const float StopSpeed = .05f;
    }

    public struct CombatContactDiagnostics
    {
        public uint Tick, BattleNumber;
        public long Candidates, PairVisits;
        public int PeakPairs, Iterations, FrozenBodies, WorldRejections, YieldAttempts;
        public float ResidualPenetration, UnresolvedPenetration, ResidualConstraint;
        public bool ResidualComplete;
        public bool WorkLimit;
        public bool KernelRanManaged;
        // Stage 04.4: every consumed entry is counted exactly once as applied, dropped (other battle / no such enabled body)
        // or rejected (non-finite / speed limit). Discarded = bodies whose external motion was rolled back with their island.
        public int ImpulsesApplied, ImpulsesDropped, ImpulsesRejected, PhysicalBodies, DiscardedVelocities, OverSpeedBodies;
        public float MaxExternalSpeed;
        public int SlopAcceptedPairs;
        public float SlopAcceptedDepth;
        // LocalHolds: bodies held at their start for this tick by escalation; RolledBackBodies: bodies of whole-island rollbacks.
        public int LocalHolds, RolledBackBodies;
        public int MotionSlices, TraceHolds;
        // Final-velocity pass (#69): recorded body contacts, passes run and the largest change of the last pass (u/s).
        public int VelocityContacts, VelocityIterations;
        public float VelocityResidual;
        // #70: false when the shared pair-visit budget ran out inside the velocity pass; ResidualComplete describes positions
        // only. Then every non-frozen body of a recorded contact publishes zero external velocity (VelocityDiscarded): the
        // solved positions stay, no partly exchanged velocity is published, no impulse is re-applied, no time re-integrated.
        public bool VelocityComplete;
        public int VelocityDiscarded;
        // #70: bodies whose blocking normals overflowed CombatWallSet (numerically degenerate input): their velocity is zero.
        public int WallOverflowBodies;
    }

    // Scratch rows, not ECS components or an alternative position source. Lifetime: one synchronous Movement call.
    internal struct CombatContactActor
    {
        public Entity Entity;
        public uint Id;
        public byte Team;
        public bool Collidable, Controlled, Legacy, Jump, Frozen, CanRestoreSupport;
        public CombatBody Body, MovementBody;
        public float3 Start;
        public CombatMotion Motion;
        public float2 Desired;
        // Stage 04.4: External = ExternalVelocity·dt after this tick's impulses, Lift = vertical Δv of those impulses.
        // Physical accumulates the achieved displacement caused by physics (world move + physical contact shares only).
        public float2 Velocity, External, Physical, VelocityOut;
        // Active blocking normals of every move of this call (see CombatArenaMove.Walls); rebuilt per call, never carried.
        public CombatWallSet Walls;
        public float Lift;
        public bool HasPhysics, Trace;
        public float RequestedHeight, InitialHeight;
        public CombatArenaMove Result;
        public float CorrectionUsed, YieldUsed;
        public int Island, YieldAttempts;
        public CombatSpatialBounds Swept;
    }

    /// <summary>Exact finite-cylinder tests; no ECS, memory ownership, clock or Unity Physics.</summary>
    public static class CombatBodyContacts
    {
        public static float Penetration(float3 a, in CombatBody ab, float3 b, in CombatBody bb)
        {
            if (a.y >= b.y + bb.Height || b.y >= a.y + ab.Height) return 0;
            return math.max(0, ab.Radius + bb.Radius - math.distance(a.xz, b.xz));
        }

        // Intersection of the relative disk quadratic and the OPEN vertical interval over [0,1].
        // Both motions participate. Endpoints alone cannot detect exchanging sides in a tick.
        public static bool Sweep(float3 a0, float3 a1, in CombatBody ab, float3 b0, float3 b1,
            in CombatBody bb, uint minId, uint maxId, out float2 normal, out float depth)
        {
            normal = default; depth = 0;
            var q = b0 - a0;
            var v = (b1 - b0) - (a1 - a0);
            var enter = 0f; var exit = 1f;
            if (math.abs(v.y) < CombatContactTuning.TinyVerticalSpeed)
            {
                if (q.y <= -bb.Height || q.y >= ab.Height) return false;
            }
            else
            {
                var y0 = (-bb.Height - q.y) / v.y;
                var y1 = (ab.Height - q.y) / v.y;
                enter = math.max(enter, math.min(y0, y1));
                exit = math.min(exit, math.max(y0, y1));
                if (enter >= exit) return false;
            }
            var radius = ab.Radius + bb.Radius;
            var aa = math.lengthsq(v.xz);
            var cc = math.lengthsq(q.xz) - radius * radius;
            if (aa < CombatContactTuning.TinyPlanarSpeedSquared)
            {
                if (cc > 0) return false;
            }
            else
            {
                var bb2 = math.dot(q.xz, v.xz);
                var discriminant = bb2 * bb2 - aa * cc;
                if (discriminant < 0) return false;
                var root = math.sqrt(math.max(0, discriminant));
                enter = math.max(enter, (-bb2 - root) / aa);
                exit = math.min(exit, (-bb2 + root) / aa);
                if (enter > exit) return false;
            }
            if (exit < 0 || enter > 1) return false;
            var contact = q.xz + v.xz * math.max(0, enter);
            // Id-based tie break for coincident centres; independent of Entity, chunk and hash order.
            var fallback = ((minId ^ maxId) & 1) == 0 ? new float2(1, 0) : new float2(0, 1);
            normal = math.normalizesafe(contact, fallback);
            depth = math.max(0, radius - math.dot((b1 - a1).xz, normal));
            return depth > CombatContactTuning.Skin;
        }
    }

    // Explicit Burst job boundary. The ISystem and grid owner orchestrate on main thread; numerical work runs here.
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal struct CombatContactJob : IJob
    {
        public NativeList<CombatContactActor> Actors;
        public NativeParallelMultiHashMap<int2, int> Cells;
        public NativeList<uint> Seen;
        public NativeList<int2> Pairs;
        public NativeList<int2> Contacts; // Pairs that violated in any pass of this call; owner scratch, cleared per call.
        [ReadOnly] public NativeArray<CombatArenaShape> Shapes;
        [ReadOnly] public NativeArray<CombatImpulse> Impulses;
        public NativeArray<CombatContactDiagnostics> Diagnostics;
        public uint Tick, LandingTicks, BattleNumber;
        public long PairVisitBudget; // CombatContactTuning.MaxPairVisits; the owner lowers it only for guard checks.
        public float DeltaTime;
        float StepDuration => DeltaTime > 0 ? DeltaTime : SimulationTiming.TickDuration;
        public bool Physics; // Any external motion or pending impulse in this call: selects the reserved physical budget.

        struct ByPair : IComparer<int2>
        {
            public int Compare(int2 a, int2 b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x);
        }

        [BurstDiscard]
        void MarkManaged(ref CombatContactDiagnostics d) => d.KernelRanManaged = true;

        public void Execute()
        {
            var d = new CombatContactDiagnostics { Tick = Tick, BattleNumber = BattleNumber, VelocityComplete = true };
            MarkManaged(ref d);
            ApplyImpulses(ref d);
            for (var i = 0; i < Actors.Length; i++)
            {
                var a = Actors[i]; a.Island = i;
                a.External = a.Velocity * StepDuration;
                a.HasPhysics = math.any(a.External != 0) || a.Lift != 0;
                if (a.HasPhysics) d.PhysicalBodies++;
                if (!a.Frozen && !a.HasPhysics)
                {
                    // Exactly the Stage 04.3 proposal when there is no external motion.
                    a.Result = CombatArenaGeometry.Move(Shapes, a.MovementBody, a.Start, a.Desired,
                        a.Motion.VerticalVelocity, a.Motion.Grounded, a.Jump, LandingTicks, a.Legacy, a.Motion.GroundHeight, deltaTime: StepDuration);
                    if (a.Result.Failure != CombatArenaFailure.None) a.Frozen = true;
                    else a.Walls.Add(a.Result.Walls);
                }
                else if (!a.Frozen)
                {
                    // Physics first, with time: gravity/jump/lift/landing and the external displacement through the world.
                    // A landing lock never cancels physics; it suppresses only the control proposal that follows.
                    var physics = CombatArenaGeometry.Move(Shapes, a.MovementBody, a.Start, a.External, a.Motion.VerticalVelocity,
                        a.Motion.Grounded, a.Jump, 0, a.Legacy, a.Motion.GroundHeight, lift: a.Lift, deltaTime: StepDuration);
                    var control = physics.Landed && LandingTicks > 0 ? float2.zero : a.Desired;
                    a.Result = physics.Failure != CombatArenaFailure.None ? physics : CombatArenaGeometry.Move(Shapes, a.MovementBody,
                        physics.Position, control, physics.Velocity, physics.Grounded, false, 0, a.Legacy, physics.GroundHeight, integrateTime: false);
                    if (a.Result.Failure != CombatArenaFailure.None) a.Frozen = true;
                    else
                    {
                        a.Result.Landed = physics.Landed;
                        a.Physical = physics.Displacement; // Control travel (Result.ControlledDistance) is attributed separately.
                        a.Walls.Add(physics.Walls); a.Walls.Add(a.Result.Walls);
                    }
                }
                // Solver iterates constraints, not elapsed time: a temporary loss of support must be reversible.
                a.CanRestoreSupport = a.Result.Grounded || (a.Motion.Grounded && !a.Jump && a.Lift <= 0);
                Actors[i] = a;
            }
            var solved = false;
            var holdRounds = 0;
            var iterationLimit = CombatContactTuning.MaxIterations;
            for (var iteration = 0; iteration < iterationLimit; iteration++)
            {
                if (!BuildPairs(ref d)) break;
                d.Iterations++;
                var violation = false;
                var actualResidual = 0f;
                for (var p = 0; p < Pairs.Length; p++)
                {
                    if (++d.PairVisits > PairVisitBudget) { d.WorkLimit = true; break; }
                    var pair = Pairs[p];
                    var a = Actors[pair.x]; var b = Actors[pair.y];
                    actualResidual = math.max(actualResidual, CombatBodyContacts.Penetration(a.Result.Position, a.Body, b.Result.Position, b.Body));
                    if (!CombatBodyContacts.Sweep(a.Start, a.Result.Position, a.Body, b.Start, b.Result.Position, b.Body,
                        a.Id, b.Id, out var n, out var depth)) continue;
                    violation = true;
                    Union(pair.x, pair.y);
                    RecordContact(pair);
                    var initialDepth = CombatBodyContacts.Penetration(a.Start, a.Body, b.Start, b.Body);
                    var initial = initialDepth > CombatContactTuning.Skin;
                    if (initial)
                    {
                        // Only the overlap the tick started with is depenetrated by physical mass (no role bias, no velocity).
                        // Motion added on top keeps its own rules below: an accepted slop never lets walking bulldoze an enemy.
                        var part = math.min(depth, initialDepth);
                        depth -= part;
                        var small = math.min(a.Body.Mass, b.Body.Mass);
                        var ia = small / a.Body.Mass; var ib = small / b.Body.Mass;
                        if (a.Team != b.Team)
                        {
                            // An enemy pressing inward this tick owns the leftover overlap too (no slow bulldozing).
                            var la = math.max(0, math.dot((a.Result.Position - a.Start).xz - a.Physical, n));
                            var lb = math.max(0, -math.dot((b.Result.Position - b.Start).xz - b.Physical, n));
                            if (la + lb >= CombatContactTuning.TinyInwardDistance) { ia = la; ib = lb; }
                        }
                        if (a.Frozen) ia = 0;
                        if (b.Frozen) ib = 0;
                        if (ia + ib > 0)
                        {
                            Correct(ref a, -n * part * (ia / (ia + ib)), ref d);
                            Correct(ref b, n * part * (ib / (ia + ib)), ref d);
                        }
                    }
                    // Split the remaining violation by cause. The physical share (external motion approaching along n, either
                    // team) is a mass-weighted inelastic normal response and becomes velocity; locomotion keeps the Stage 04.3
                    // rules below and never becomes velocity. Without external motion the share is zero.
                    var physicalShare = depth > 0 ? PhysicalShare(a, b, n) : 0;
                    if (physicalShare > 0)
                    {
                        var physicalDepth = depth * physicalShare;
                        depth -= physicalDepth;
                        var small = math.min(a.Body.Mass, b.Body.Mass);
                        var pa = a.Frozen ? 0 : small / a.Body.Mass; var pb = b.Frozen ? 0 : small / b.Body.Mass;
                        if (pa + pb > 0)
                        {
                            var physical = pa / (pa + pb);
                            a.Physical += Correct(ref a, -n * physicalDepth * physical, ref d);
                            b.Physical += Correct(ref b, n * physicalDepth * (1 - physical), ref d);
                        }
                    }
                    float wa, wb;
                    if (a.Team == b.Team)
                    {
                        // Normalise without storing inverse mass or overflowing 1/Mass for tiny valid Mass.
                        var small = math.min(a.Body.Mass, b.Body.Mass);
                        wa = small / a.Body.Mass; wb = small / b.Body.Mass;
                        if (a.Controlled || b.Controlled)
                        {
                            if (a.Controlled) wa *= CombatContactTuning.ControlledMobility;
                            if (b.Controlled) wb *= CombatContactTuning.ControlledMobility;
                        }
                        else
                        {
                            // Two uncontrolled allies: the one whose own command presses into the other takes it, so a rear
                            // row cannot shove a front row that the enemy holds (mass halves converged one row per pass and
                            // froze large pressing fronts). Commands only, so a passive chain pushed by the controller still
                            // shares by mass; no pressing → mass.
                            var ca = math.max(0, math.dot(a.Desired, n)); var cb = math.max(0, -math.dot(b.Desired, n));
                            if (ca + cb >= CombatContactTuning.TinyInwardDistance) { wa = ca; wb = cb; }
                        }
                    }
                    else
                    {
                        // Cancel inward locomotion, never transfer it into a passive enemy as a bulldozer impulse.
                        // Physical displacement is not locomotion: it was answered by the inelastic share above.
                        wa = math.max(0, math.dot((a.Result.Position - a.Start).xz - a.Physical, n));
                        wb = math.max(0, -math.dot((b.Result.Position - b.Start).xz - b.Physical, n));
                        if (wa + wb < CombatContactTuning.TinyInwardDistance) // vertical entry, not locomotion: physical mass response
                        {
                            var small = math.min(a.Body.Mass, b.Body.Mass);
                            wa = small / a.Body.Mass; wb = small / b.Body.Mass;
                        }
                    }
                    if (a.Frozen) wa = 0;
                    if (b.Frozen) wb = 0;
                    if (wa + wb <= 0 || depth <= 0) { a.Island = Actors[pair.x].Island; b.Island = Actors[pair.y].Island; Actors[pair.x] = a; Actors[pair.y] = b; continue; }
                    var share = wa / (wa + wb);
                    var yielded = false;
                    if (a.Team == b.Team)
                    {
                        // A finite normal priority alone pushes a collinear queue forever. Yield sideways as well,
                        // under the same world/body budgets, so a free ally can actually let the controller pass.
                        if (a.Controlled) yielded = Yield(ref a, ref b, depth * (1 - share), ref d);
                        else if (b.Controlled) yielded = Yield(ref b, ref a, depth * share, ref d);
                    }
                    if (!yielded || CombatBodyContacts.Sweep(a.Start, a.Result.Position, a.Body, b.Start, b.Result.Position, b.Body,
                        a.Id, b.Id, out n, out depth))
                    {
                        Correct(ref a, -n * depth * share, ref d);
                        Correct(ref b, n * depth * (1 - share), ref d);
                    }
                    // Union updates rows; do not overwrite their union-find parents with the cached copies.
                    a.Island = Actors[pair.x].Island; b.Island = Actors[pair.y].Island;
                    Actors[pair.x] = a; Actors[pair.y] = b;
                }
                if (d.WorkLimit) break;
                if (!violation)
                {
                    // No correction happened in this pass: its residual is the final, coherent state.
                    d.ResidualPenetration = actualResidual; d.ResidualComplete = true;
                    solved = true; break;
                }
                if (iteration == iterationLimit - 1 && holdRounds < CombatContactTuning.MaxHoldRounds && HoldViolators(ref d))
                {
                    holdRounds++;
                    iterationLimit += CombatContactTuning.HoldIterations;
                }
            }
            // Closure: hold every body still in a new violation beyond slop until none remains. Each pass holds at least
            // one more body, so at most one pass per body, inside the same pair-visit budget. Held bodies stay at their
            // valid start; everybody else keeps a violation-free move. Island rollback remains for work-limit/carried cases.
            for (var pass = 0; !solved && pass < Actors.Length && HoldViolators(ref d); pass++) { }
            // A tight island must stop instead of publishing penetration. Other islands continue moving.
            // Every candidate edge is unioned while building, including new neighbours after a correction.
            if (!solved)
            {
                if (!d.WorkLimit) BuildPairs(ref d);
                for (var p = 0; !d.WorkLimit && p < Pairs.Length; p++)
                {
                    if (++d.PairVisits > PairVisitBudget) { d.WorkLimit = true; break; }
                    var pair = Pairs[p]; var a = Actors[pair.x]; var b = Actors[pair.y];
                    if (!CombatBodyContacts.Sweep(a.Start, a.Result.Position, a.Body, b.Start, b.Result.Position, b.Body,
                        a.Id, b.Id, out _, out var depth)) continue;
                    d.ResidualConstraint = math.max(d.ResidualConstraint, depth);
                    // Two held bodies at their own starts carry only the overlap this tick began with: not a new violation.
                    var carried = a.Frozen && b.Frozen && a.Result.Position.Equals(a.Start) && b.Result.Position.Equals(b.Start);
                    if (carried || depth <= CombatContactTuning.SlopRadiusFraction * math.min(a.Body.Radius, b.Body.Radius))
                    {
                        // Slow Gauss-Seidel convergence in long pressing chains, not a crossing: publish, diagnose.
                        d.SlopAcceptedPairs++; d.SlopAcceptedDepth = math.max(d.SlopAcceptedDepth, depth);
                        continue;
                    }
                    d.UnresolvedPenetration = math.max(d.UnresolvedPenetration,
                        CombatBodyContacts.Penetration(a.Result.Position, a.Body, b.Result.Position, b.Body));
                    FreezeIsland(Root(pair.x), ref d);
                }
                // Full working envelopes contain every rollback endpoint, so this same complete pair list
                // also covers final penetrations. Keep diagnostics inside the same explicit work budget.
                for (var p = 0; !d.WorkLimit && p < Pairs.Length; p++)
                {
                    if (++d.PairVisits > PairVisitBudget) { d.WorkLimit = true; break; }
                    var a = Actors[Pairs[p].x]; var b = Actors[Pairs[p].y];
                    d.ResidualPenetration = math.max(d.ResidualPenetration,
                        CombatBodyContacts.Penetration(a.Result.Position, a.Body, b.Result.Position, b.Body));
                }
                d.ResidualComplete = !d.WorkLimit;
                if (d.WorkLimit)
                {
                    for (var i = 0; i < Actors.Length; i++) Freeze(i);
                    d.ResidualPenetration = 0; // Unknown, not a claimed zero: ResidualComplete is false.
                }
            }
            for (var i = 0; i < Actors.Length; i++)
            {
                var a = Actors[i];
                if (a.Frozen)
                {
                    d.FrozenBodies++;
                    // Explicit bounded fallback: a rolled-back island publishes its original Position/Motion with zero
                    // external velocity. This tick's consumed impulses are not retried (no repeated J) and are counted.
                    if (a.HasPhysics || math.any(a.Motion.ExternalVelocity != 0)) d.DiscardedVelocities++;
                    a.VelocityOut = float2.zero;
                    Actors[i] = a;
                    continue;
                }
                CombatArenaGeometry.AddTouchingWalls(Shapes, a.MovementBody, a.Result.Position, CombatContactTuning.ContactGap, ref a.Walls);
                if (a.Walls.Overflow) d.WallOverflowBodies++;
                a.VelocityOut = ClipWalls(a, a.Velocity); // Inward velocity into a surface that blocked the body is gone.
                if (!a.Legacy && a.CanRestoreSupport)
                {
                    // Final support, not an intermediate iteration, determines grounded/landing output.
                    a.Result.Grounded = a.Result.Velocity <= 0 && CombatArenaGeometry.Support(Shapes,
                        a.Result.Position, a.MovementBody.Radius, a.Result.Position.y, a.Result.Position.y, true, out _);
                    if (a.Result.Grounded)
                    {
                        a.Result.Velocity = 0;
                        a.Result.GroundHeight = a.Result.Position.y;
                    }
                    a.Result.Landed &= a.Result.Grounded;
                }
                Actors[i] = a;
            }
            ContactVelocities(ref d);
            for (var i = 0; i < Actors.Length; i++)
            {
                var a = Actors[i];
                if (a.Frozen) continue;
                a.VelocityOut = Decelerate(a, a.VelocityOut, ref d);
                Actors[i] = a;
            }
            Diagnostics[0] = d;
        }

        // Final velocity of bodies in contact (#69). The average displacement of a tick is NOT a velocity: a body that hits a
        // wall after part of the tick has travelled, yet its normal velocity after an inelastic impact is zero. Every pair that
        // violated in this call and still touches exchanges its approaching normal velocity as a perfectly inelastic,
        // mass-weighted collision (momentum conserved, kinetic energy never created; a held/frozen body is immovable), then
        // each body's velocity loses its inward part along the surfaces that blocked it. Sequential passes in sorted pair
        // order (deterministic) until no change exceeds VelocityTolerance or MaxIterations. Bodies without velocity next to
        // partners without velocity are untouched, so control, yielding and depenetration never become velocity.
        // Budget exhaustion is not a partial success: see CombatContactDiagnostics.VelocityComplete.
        void ContactVelocities(ref CombatContactDiagnostics d)
        {
            // A positional work limit already froze every body with zero velocity: nothing is left to exchange.
            if (Contacts.Length == 0 || d.WorkLimit) return;
            CompactContacts();
            d.VelocityContacts = Contacts.Length;
            for (var pass = 0; pass < CombatContactTuning.MaxIterations; pass++)
            {
                var change = 0f;
                for (var c = 0; c < Contacts.Length; c++)
                {
                    if (++d.PairVisits > PairVisitBudget) { d.WorkLimit = true; DiscardContactVelocities(ref d); return; }
                    var pair = Contacts[c];
                    var a = Actors[pair.x]; var b = Actors[pair.y];
                    if (a.Frozen && b.Frozen) continue;
                    var pa = a.Result.Position; var pb = b.Result.Position;
                    if (pa.y >= pb.y + b.Body.Height || pb.y >= pa.y + a.Body.Height) continue;
                    if (math.distance(pa.xz, pb.xz) - a.Body.Radius - b.Body.Radius > CombatContactTuning.ContactGap) continue;
                    var fallback = ((a.Id ^ b.Id) & 1) == 0 ? new float2(1, 0) : new float2(0, 1);
                    var n = math.normalizesafe((pb - pa).xz, fallback);
                    var approach = math.dot(a.VelocityOut - b.VelocityOut, n);
                    if (approach <= CombatContactTuning.VelocityTolerance) continue;
                    var small = math.min(a.Body.Mass, b.Body.Mass);
                    var wa = a.Frozen ? 0 : small / a.Body.Mass; var wb = b.Frozen ? 0 : small / b.Body.Mass;
                    var va = ClipWalls(a, a.VelocityOut - n * (approach * wa / (wa + wb)));
                    var vb = ClipWalls(b, b.VelocityOut + n * (approach * wb / (wa + wb)));
                    change = math.max(change, math.max(math.length(va - a.VelocityOut), math.length(vb - b.VelocityOut)));
                    a.VelocityOut = va; b.VelocityOut = vb;
                    Actors[pair.x] = a; Actors[pair.y] = b;
                }
                d.VelocityIterations = pass + 1;
                d.VelocityResidual = change;
                if (change <= CombatContactTuning.VelocityTolerance) break;
            }
        }

        void DiscardContactVelocities(ref CombatContactDiagnostics d)
        {
            d.VelocityComplete = false;
            for (var i = 0; i < Actors.Length; i++)
            {
                var a = Actors[i];
                if (a.Frozen || !InContact(i)) continue;
                if (math.any(a.VelocityOut != 0)) d.VelocityDiscarded++;
                a.VelocityOut = float2.zero;
                Actors[i] = a;
            }
        }

        bool InContact(int i)
        {
            for (var c = 0; c < Contacts.Length; c++) if (Contacts[c].x == i || Contacts[c].y == i) return true;
            return false;
        }

        // The owner reserves capacity for every distinct pair (n(n-1)/2), so a full list of distinct pairs already holds any
        // new one: compaction on overflow keeps the call allocation-free.
        void RecordContact(int2 pair)
        {
            if (Contacts.Length == Contacts.Capacity) CompactContacts();
            if (Contacts.Length < Contacts.Capacity) Contacts.AddNoResize(pair);
        }

        void CompactContacts()
        {
            Contacts.Sort(new ByPair());
            var unique = 0;
            for (var c = 0; c < Contacts.Length; c++)
                if (unique == 0 || !Contacts[c].Equals(Contacts[unique - 1])) Contacts[unique++] = Contacts[c];
            Contacts.Length = unique;
        }

        // Exact projection onto the cone of every active blocking normal (#70): satisfies all of them at once and keeps the
        // tangent along a single wall; replaces two sequential passes that pushed velocity back into an acute corner.
        static float2 ClipWalls(in CombatContactActor a, float2 v) => a.Frozen ? float2.zero : a.Walls.Clip(v);

        // Coulomb deceleration on the final support; nothing is stored for later release.
        float2 Decelerate(in CombatContactActor a, float2 velocity, ref CombatContactDiagnostics d)
        {
            var speed = math.length(velocity);
            if (speed <= 0) return float2.zero;
            if (speed > CombatImpulseTuning.MaxExternalSpeed * 1.0001f) d.OverSpeedBodies++; // Diagnosed, not clipped.
            var deceleration = a.Result.Grounded ? CombatImpulseTuning.GroundDeceleration : CombatImpulseTuning.AirDeceleration;
            var next = speed - deceleration * StepDuration;
            if (next <= CombatImpulseTuning.StopSpeed) return float2.zero;
            d.MaxExternalSpeed = math.max(d.MaxExternalSpeed, next);
            return velocity * (next / speed);
        }

        int Root(int i)
        {
            while (Actors[i].Island != i) i = Actors[i].Island;
            return i;
        }
        void Union(int a, int b)
        {
            a = Root(a); b = Root(b);
            if (a == b) return;
            var row = Actors[math.max(a, b)]; row.Island = math.min(a, b); Actors[math.max(a, b)] = row;
        }
        void FreezeIsland(int root, ref CombatContactDiagnostics d)
        {
            for (var i = 0; i < Actors.Length; i++)
                if (Root(i) == root) { if (!Actors[i].Frozen) d.RolledBackBodies++; Freeze(i); }
        }

        // Holds (restores to start, zero mobility) both bodies of every pair still violating beyond slop. Start positions
        // are inside every working envelope, so the held bodies act as fixed obstacles for the following iterations.
        bool HoldViolators(ref CombatContactDiagnostics d)
        {
            if (d.WorkLimit || !BuildPairs(ref d)) return false;
            var held = false;
            for (var p = 0; p < Pairs.Length; p++)
            {
                if (++d.PairVisits > PairVisitBudget) { d.WorkLimit = true; return false; }
                var pair = Pairs[p]; var a = Actors[pair.x]; var b = Actors[pair.y];
                if (!CombatBodyContacts.Sweep(a.Start, a.Result.Position, a.Body, b.Start, b.Result.Position, b.Body,
                    a.Id, b.Id, out _, out var depth)
                    || depth <= CombatContactTuning.SlopRadiusFraction * math.min(a.Body.Radius, b.Body.Radius)) continue;
                if (!a.Frozen) { Freeze(pair.x); d.LocalHolds++; held = true; }
                if (!Actors[pair.y].Frozen) { Freeze(pair.y); d.LocalHolds++; held = true; }
            }
            return held;
        }
        void Freeze(int i)
        {
            var a = Actors[i]; a.Frozen = true;
            a.Result.Position = a.Start; a.Result.ControlledDistance = 0;
            a.Physical = float2.zero; // Rolled-back physics publishes no velocity (counted once at output).
            Actors[i] = a;
        }

        bool Yield(ref CombatContactActor controller, ref CombatContactActor ally, float pressure,
            ref CombatContactDiagnostics d)
        {
            if (controller.Frozen || ally.Frozen || ally.YieldAttempts >= CombatContactTuning.MaxYieldAttempts
                || math.lengthsq(controller.Desired) < CombatContactTuning.TinyPlanarSpeedSquared) return false;
            var forward = math.normalizesafe(controller.Desired);
            var separation = (ally.Result.Position - controller.Result.Position).xz;
            if (math.dot(separation, forward) <= 0) return false; // No automatic motion for allies behind/away from control.
            var sideways = new float2(-forward.y, forward.x);
            var offset = math.dot(separation, sideways);
            var sign = math.abs(offset) > CombatContactTuning.Skin ? math.sign(offset)
                : ((controller.Id ^ ally.Id) & 1) == 0 ? 1f : -1f;
            var limit = CombatContactTuning.AllyYieldSpeed * StepDuration;
            var startingUsed = ally.YieldUsed;
            for (var attempt = 0; attempt < 2 && ally.YieldAttempts < CombatContactTuning.MaxYieldAttempts; attempt++)
            {
                var amount = math.min(pressure, math.max(0, limit - ally.YieldUsed));
                if (amount <= CombatContactTuning.Skin) return ally.YieldUsed > startingUsed;
                var before = ally.Result.Position;
                ally.YieldAttempts++; d.YieldAttempts++;
                Correct(ref ally, sideways * (sign * amount), ref d);
                var achieved = math.distance(before.xz, ally.Result.Position.xz);
                ally.YieldUsed += achieved;
                if (achieved > CombatContactTuning.Skin) return true;
                sign = -sign; // Preferred side is obstructed: bounded opposite-side world probe, not navigation.
            }
            return ally.YieldUsed > startingUsed;
        }

        // Achieved X/Z correction; the caller decides whether it is physical (velocity) or locomotion/depenetration.
        float2 Correct(ref CombatContactActor a, float2 correction, ref CombatContactDiagnostics d)
        {
            if (a.Frozen || math.lengthsq(correction) < CombatContactTuning.TinyCorrectionSquared) return float2.zero;
            var remaining = CorrectionBudget(a) - a.CorrectionUsed;
            if (remaining <= 0) return float2.zero; // Residual is diagnosed and island rolled back, never silently accepted.
            var length = math.length(correction);
            if (length > remaining) correction *= remaining / length;
            var before = a.Result;
            var after = CombatArenaGeometry.Move(Shapes, a.MovementBody, before.Position, correction,
                before.Velocity, before.Grounded, false, 0, a.Legacy, before.GroundHeight, integrateTime: false);
            if (after.Failure != CombatArenaFailure.None) { d.WorldRejections++; return float2.zero; }
            a.Walls.Add(after.Walls);
            var achieved = (after.Position - before.Position).xz;
            a.CorrectionUsed += math.length(achieved);
            // Passive displacement can never create running distance, even during landing or an attack.
            var controlDirection = math.normalizesafe(a.Desired);
            after.ControlledDistance = math.max(0, before.ControlledDistance + math.min(0, math.dot(achieved, controlDirection)));
            after.Landed = before.Landed;
            a.Result = after;
            return achieved;
        }

        // Sum-of-corrections budget. Without physics in this call it is exactly the Stage 04.3 budget; with physics every body
        // may additionally receive one tick of the largest supported external speed by transfer. The owner reserves the
        // working-grid margin from the same terms (CombatSpatialIndexSystem.CalculateMovement).
        internal static float CorrectionBudget(in CombatContactActor a, bool physics, float fraction = 1) =>
            math.length(a.Desired) + math.length(a.External) + math.min(a.Body.Radius, CombatContactTuning.CorrectionBudget) * fraction
            + (physics ? CombatImpulseTuning.MaxExternalSpeed * SimulationTiming.TickDuration * fraction : 0);
        float CorrectionBudget(in CombatContactActor a) => CorrectionBudget(a, Physics, StepDuration / SimulationTiming.TickDuration);

        // Fraction of a pair's violation caused by external motion approaching along n (current physical displacement),
        // against the remaining approach (locomotion, yielding). Zero when neither body has physical displacement.
        static float PhysicalShare(in CombatContactActor a, in CombatContactActor b, float2 n)
        {
            var physical = math.max(0, math.dot(a.Physical, n)) + math.max(0, -math.dot(b.Physical, n));
            if (physical <= CombatContactTuning.TinyInwardDistance) return 0;
            var other = math.max(0, math.dot((a.Result.Position - a.Start).xz - a.Physical, n))
                + math.max(0, -math.dot((b.Result.Position - b.Start).xz - b.Physical, n));
            return physical / (physical + other);
        }

        // Applies this tick's pending impulses in buffer order to the sorted rows (binary search by Id). Every entry is
        // counted once; a rejected entry never partially changes velocity. No clipping.
        void ApplyImpulses(ref CombatContactDiagnostics d)
        {
            for (var k = 0; k < Impulses.Length; k++)
            {
                var impulse = Impulses[k];
                var i = impulse.BattleNumber == BattleNumber ? Find(impulse.Target.Value) : -1;
                if (i < 0) { d.ImpulsesDropped++; continue; }
                var a = Actors[i];
                var mass = a.Body.Mass;
                var dv = impulse.Impulse / mass;
                var velocity = a.Velocity + dv.xz;
                var lift = a.Lift + dv.y;
                if (!(mass > 0) || !math.all(math.isfinite(dv)) || !math.all(math.isfinite(velocity)) || !math.isfinite(lift)
                    || math.length(velocity) > CombatImpulseTuning.MaxExternalSpeed || math.abs(lift) > CombatImpulseTuning.MaxExternalSpeed)
                { d.ImpulsesRejected++; continue; }
                a.Velocity = velocity; a.Lift = lift;
                Actors[i] = a;
                d.ImpulsesApplied++;
            }
        }

        int Find(uint id)
        {
            int lo = 0, hi = Actors.Length - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var value = Actors[mid].Id;
                if (value == id) return mid;
                if (value < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        bool BuildPairs(ref CombatContactDiagnostics d)
        {
            Cells.Clear(); Pairs.Clear();
            long entries = 0;
            for (var i = 0; i < Actors.Length; i++)
            {
                Seen[i] = 0;
                var a = Actors[i];
                if (!a.Collidable) continue;
                CombatSpatialBounds.TryFromBody(a.Start, a.Body, out var start);
                CombatSpatialBounds.TryFromBody(a.Result.Position, a.Body, out var end);
                a.Swept = new CombatSpatialBounds { Min = math.min(start.Min, end.Min), Max = math.max(start.Max, end.Max) };
                Actors[i] = a;
                if (!CombatSpatialBounds.TryCells(a.Swept, out var min, out var max, out var count)
                    || count > Cells.Capacity - entries) { d.WorkLimit = true; return false; }
                entries += count;
                for (long x = min.x; x <= max.x; x++)
                for (long z = min.y; z <= max.y; z++) Cells.Add(new int2((int)x, (int)z), i);
            }
            for (var i = 0; i < Actors.Length; i++)
            {
                var a = Actors[i]; if (!a.Collidable) continue;
                CombatSpatialBounds.TryCells(a.Swept, out var min, out var max, out _);
                for (long x = min.x; x <= max.x; x++)
                for (long z = min.y; z <= max.y; z++)
                {
                    if (!Cells.TryGetFirstValue(new int2((int)x, (int)z), out var j, out var it)) continue;
                    do
                    {
                        if (++d.Candidates > CombatContactTuning.MaxCandidateVisits) { d.WorkLimit = true; return false; }
                        if (j <= i || Seen[j] == (uint)i + 1) continue;
                        Seen[j] = (uint)i + 1;
                        if (!a.Swept.Overlaps(Actors[j].Swept)) continue;
                        Pairs.Add(new int2(i, j));
                        Union(i, j);
                    } while (Cells.TryGetNextValue(out j, ref it));
                }
            }
            Pairs.Sort(new ByPair());
            d.PeakPairs = math.max(d.PeakPairs, Pairs.Length);
            return true;
        }
    }
}
