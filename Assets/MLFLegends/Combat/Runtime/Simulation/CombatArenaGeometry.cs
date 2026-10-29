using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace MLFLegends.Combat
{
    public enum CombatArenaKind : byte { Box, Ramp }

    // Numeric world geometry. Ramp rises along local +X from Bottom to Top, with a flat underside.
    [InternalBufferCapacity(0)]
    public struct CombatArenaShape : IBufferElementData
    {
        public CombatArenaKind Kind;
        public float2 Center;
        public float2 HalfSize;
        public float Bottom, Top;
        public float2 AxisX; // unit world X/Z vector; local Z = (-AxisX.y, AxisX.x)
        public bool IsValid => (Kind == CombatArenaKind.Box || Kind == CombatArenaKind.Ramp)
            && math.all(math.isfinite(Center)) && math.all(math.isfinite(HalfSize))
            && math.all(HalfSize >= CombatArenaTuning.MinFeature * .5f)
            && math.cmax(math.abs(Center) + math.abs(AxisX) * HalfSize.x + math.abs(new float2(-AxisX.y, AxisX.x)) * HalfSize.y) <= CombatArenaTuning.MaxCoordinate
            && math.isfinite(Bottom) && math.isfinite(Top) && math.isfinite(Top - Bottom)
            && math.max(math.abs(Bottom), math.abs(Top)) <= CombatArenaTuning.MaxCoordinate
            && Top - Bottom >= CombatArenaTuning.MinFeature
            && math.all(math.isfinite(AxisX)) && math.abs(math.lengthsq(AxisX) - 1) < .0001f;
        public float Slope => (Top - Bottom) / (2 * HalfSize.x);
        public float2 Local(float2 p)
        {
            var d = p - Center;
            return new float2(math.dot(d, AxisX), math.dot(d, new float2(-AxisX.y, AxisX.x)));
        }
        public float2 WorldVector(float2 p) => AxisX * p.x + new float2(-AxisX.y, AxisX.x) * p.y;
    }

    public static class CombatArenaTuning
    {
        public const float Skin = .0002f;
        public const float MinFeature = .01f;
        public const float MaxSlope = 1f; // tan(45 degrees), inclusive
        public const int MaxShapes = 128;
        public const int MaxSubsteps = 256;
        public const int MaxIterations = 12;
        public const float MaxInitialCorrection = .5f;
        public const float MaxCoordinate = 1024f;
        public const float MaxBodyDimension = 32f;
    }

    public enum CombatArenaFailure : byte { None, ArenaUnavailable, InvalidBody, WorkLimit, InitialOverlap, UnresolvedContact }

    public struct CombatArenaMove
    {
        public float3 Position;
        public float Velocity, GroundHeight, ControlledDistance;
        public bool Grounded, Landed;
        public CombatArenaFailure Failure;
    }

    // No ECS writes, authoring objects, clock or allocations. Only Movement publishes this result.
    public static class CombatArenaGeometry
    {
        static float2 Xz(float3 p) => new float2(p.x, p.z);
        static float3 WithXz(float3 p, float2 xz) => new float3(xz.x, p.y, xz.y);

        // Exact disk/rectangle overlap, including rounded exterior corners (no expanded-box approximation).
        public static bool DiskRectangle(float2 p, float radius, float2 lo, float2 hi, out float2 correction)
        {
            correction = default;
            var q = math.clamp(p, lo, hi);
            var delta = p - q;
            var squared = math.lengthsq(delta);
            if (squared >= radius * radius) return false;
            if (squared > 1e-14f)
            {
                var distance = math.sqrt(squared);
                correction = delta * ((radius - distance) / distance);
                return true;
            }
            var depths = new float4(p.x - lo.x, hi.x - p.x, p.y - lo.y, hi.y - p.y);
            var minimum = math.cmin(depths);
            correction = minimum == depths.x ? new float2(-minimum - radius, 0)
                : minimum == depths.y ? new float2(minimum + radius, 0)
                : minimum == depths.z ? new float2(0, -minimum - radius) : new float2(0, minimum + radius);
            return true;
        }

        // Highest point of the supported top over the disk footprint, including finite ramp edges.
        public static bool TopAt(in CombatArenaShape s, float2 center, float radius, out float height)
        {
            height = 0;
            var p = s.Local(center);
            var nearest = math.clamp(p, -s.HalfSize, s.HalfSize);
            if (math.lengthsq(p - nearest) >= radius * radius) return false;
            if (s.Kind == CombatArenaKind.Box) { height = s.Top; return true; }
            var dz = math.max(math.abs(p.y) - s.HalfSize.y, 0);
            var right = math.min(s.HalfSize.x, p.x + math.sqrt(math.max(0, radius * radius - dz * dz)));
            height = s.Bottom + (right + s.HalfSize.x) * s.Slope;
            return true;
        }

        public static bool Support(NativeArray<CombatArenaShape> shapes, float3 p, float radius,
            float maxHeight, float minHeight, bool walkableOnly, out float height)
        {
            height = float.NegativeInfinity;
            if (!shapes.IsCreated) return false;
            foreach (var s in shapes)
                if ((!walkableOnly || s.Kind == CombatArenaKind.Box || s.Slope <= CombatArenaTuning.MaxSlope)
                    && TopAt(s, Xz(p), radius, out var top) && top <= maxHeight + CombatArenaTuning.Skin
                    && top >= minHeight - CombatArenaTuning.Skin && top > height) height = top;
            return math.isfinite(height);
        }

        static bool Ground(NativeArray<CombatArenaShape> shapes, float3 p, float radius, float maxHeight,
            float minHeight, bool legacy, float legacyHeight, out float height)
        {
            if (!legacy) return Support(shapes, p, radius, maxHeight, minHeight, true, out height);
            height = legacyHeight;
            return height <= maxHeight + CombatArenaTuning.Skin && height >= minHeight - CombatArenaTuning.Skin;
        }

        static bool Side(in CombatArenaShape s, float3 p, in CombatBody body, out float2 correction)
        {
            correction = default;
            if (p.y >= s.Top - CombatArenaTuning.Skin || p.y + body.Height <= s.Bottom + CombatArenaTuning.Skin) return false;
            var lo = -s.HalfSize;
            // At this foot height the solid wedge cross-section is an exact rectangle.
            if (s.Kind == CombatArenaKind.Ramp && p.y > s.Bottom)
                lo.x = math.max(lo.x, (p.y - s.Bottom) / s.Slope - s.HalfSize.x);
            if (!DiskRectangle(s.Local(Xz(p)), body.Radius, lo, s.HalfSize, out var local)) return false;
            correction = s.WorldVector(local);
            return math.lengthsq(correction) > CombatArenaTuning.Skin * CombatArenaTuning.Skin;
        }

        static bool Overlap(in CombatArenaShape s, float3 p, in CombatBody body, out float3 correction)
        {
            correction = default;
            if (!Side(s, p, body, out var side) || !TopAt(s, Xz(p), body.Radius, out var top)) return false;
            correction = new float3(side.x, 0, side.y);
            var up = top - p.y;
            var down = p.y + body.Height - s.Bottom;
            if (up < math.length(side)) correction = new float3(0, up, 0);
            if (down < math.length(correction)) correction = new float3(0, -down, 0);
            return math.lengthsq(correction) > CombatArenaTuning.Skin * CombatArenaTuning.Skin;
        }

        static bool Depenetrate(NativeArray<CombatArenaShape> shapes, in CombatBody body, ref float3 p)
        {
            if (!shapes.IsCreated) return true;
            var origin = p;
            for (var iteration = 0; iteration < CombatArenaTuning.MaxIterations; iteration++)
            {
                var found = false;
                foreach (var s in shapes)
                    if (Overlap(s, p, body, out var correction))
                    {
                        p += correction;
                        found = true;
                        if (math.distance(p, origin) > math.min(CombatArenaTuning.MaxInitialCorrection, body.Radius)) return false;
                    }
                if (!found) return true;
            }
            return false;
        }

        // Bounded displacement substeps: each is <= 1/4 of BOTH body dimensions AND every solid feature.
        // A failed budget freezes the entire tick, never silently truncates displacement or crosses geometry.
        public static CombatArenaMove Move(NativeArray<CombatArenaShape> shapes, in CombatBody body, float3 start,
            float2 desired, float velocity, bool grounded, bool jump, uint landingTicks, bool legacy = false, float legacyHeight = 0)
        {
            var result = new CombatArenaMove { Position = start, Velocity = velocity, Grounded = grounded };
            if (!math.all(math.isfinite(start)) || math.cmax(math.abs(start)) > CombatArenaTuning.MaxCoordinate
                || !math.isfinite(body.Radius) || !math.isfinite(body.Height) || !math.isfinite(body.Mass) || body.Mass <= 0
                || body.Radius < CombatArenaTuning.MinFeature
                || body.Height < CombatArenaTuning.MinFeature || body.Radius > CombatArenaTuning.MaxBodyDimension
                || body.Height > CombatArenaTuning.MaxBodyDimension || !math.all(math.isfinite(desired)) || !math.isfinite(velocity))
            { result.Failure = CombatArenaFailure.InvalidBody; return result; }
            var p = start;
            if (!Depenetrate(shapes, body, ref p)) { result.Failure = CombatArenaFailure.InitialOverlap; return result; }
            grounded = grounded && Ground(shapes, p, body.Radius, p.y, p.y, legacy, legacyHeight, out _);
            if (grounded && jump) { velocity = CombatMovementTuning.JumpImpulse; grounded = false; }
            if (!grounded) velocity -= CombatMovementTuning.Gravity * SimulationTiming.TickDuration;
            else velocity = 0;
            var vertical = velocity * SimulationTiming.TickDuration;
            var feature = math.min(body.Radius, body.Height);
            if (shapes.IsCreated) foreach (var s in shapes) feature = math.min(feature, math.min(math.cmin(s.HalfSize) * 2, s.Top - s.Bottom));
            // Also bound grazing-corner chord error below Skin (conservative circle sagitta bound).
            var stepSize = math.min(feature * .25f, math.sqrt(4 * body.Radius * CombatArenaTuning.Skin));
            var work = math.ceil((math.length(desired) + math.abs(vertical)) / stepSize);
            if (!math.isfinite(work) || work > CombatArenaTuning.MaxSubsteps)
            { result.Failure = CombatArenaFailure.WorkLimit; return result; }
            var count = math.max(1, (int)work);
            var planar = desired / count;
            var dy = vertical / count;
            var traveled = 0f;
            var landed = false;
            for (var step = 0; step < count; step++)
            {
                var before = p;
                // Resolve vertical contact before control, preserving no-slide on the contact tick.
                if (!grounded)
                {
                    var nextY = p.y + dy;
                    if (dy <= 0 && Ground(shapes, p, body.Radius, p.y, nextY, legacy, legacyHeight, out var floor))
                    { p.y = floor; grounded = true; velocity = 0; dy = 0; landed = true; }
                    else if (dy > 0)
                    {
                        var ceiling = float.PositiveInfinity;
                        if (shapes.IsCreated) foreach (var s in shapes)
                            if (p.y + body.Height <= s.Bottom + CombatArenaTuning.Skin
                                && nextY + body.Height >= s.Bottom && TopAt(s, Xz(p), body.Radius, out _))
                                ceiling = math.min(ceiling, s.Bottom - body.Height);
                        if (math.isfinite(ceiling)) { p.y = ceiling; velocity = 0; dy = 0; }
                        else p.y = nextY;
                    }
                    else p.y = nextY;
                }
                var control = landed && landingTicks > 0 ? float2.zero : planar;
                var candidate = WithXz(p, Xz(p) + control);
                // Only continuous walkable slopes may raise the foot; boxes never step up.
                if (grounded)
                {
                    var rise = math.length(control) * CombatArenaTuning.MaxSlope;
                    if (Ground(shapes, candidate, body.Radius, p.y + rise, p.y - rise, legacy, legacyHeight, out var ground))
                    {
                        var mayRise = ground <= p.y + CombatArenaTuning.Skin;
                        if (shapes.IsCreated) foreach (var s in shapes)
                            if (s.Kind == CombatArenaKind.Ramp && s.Slope <= CombatArenaTuning.MaxSlope
                                && TopAt(s, Xz(candidate), body.Radius, out var top) && math.abs(top - ground) <= CombatArenaTuning.Skin) mayRise = true;
                        if (mayRise) candidate.y = ground;
                    }
                }
                var resolved = false;
                for (var iteration = 0; iteration < CombatArenaTuning.MaxIterations; iteration++)
                {
                    var touched = false;
                    if (shapes.IsCreated) foreach (var s in shapes)
                        if (Side(s, candidate, body, out var correction))
                        { candidate = WithXz(candidate, Xz(candidate) + correction); touched = true; }
                    if (!touched) { resolved = true; break; }
                }
                if (!resolved) { result.Failure = CombatArenaFailure.UnresolvedContact; return result; }
                p = candidate;
                traveled += math.distance(Xz(before), Xz(p));
                if (grounded && !Ground(shapes, p, body.Radius, p.y, p.y, legacy, legacyHeight, out _))
                {
                    grounded = false;
                    velocity = -CombatMovementTuning.Gravity * SimulationTiming.TickDuration;
                    dy = velocity * SimulationTiming.TickDuration / count;
                }
            }
            if (math.cmax(math.abs(p)) > CombatArenaTuning.MaxCoordinate)
            { result.Failure = CombatArenaFailure.WorkLimit; return result; }
            result.Position = p; result.Velocity = velocity; result.Grounded = grounded; result.Landed = landed;
            result.GroundHeight = grounded ? p.y : legacyHeight;
            result.ControlledDistance = landed && landingTicks > 0 ? 0 : traveled;
            return result;
        }
    }
}
