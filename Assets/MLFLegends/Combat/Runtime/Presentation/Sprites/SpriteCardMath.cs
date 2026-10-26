using Unity.Mathematics;

namespace MLFLegends.Combat
{
    /// <summary>
    /// World axes of a sprite card for one fixed camera orientation. The card stands vertically above its support point
    /// and lies on the ground plane below it, both stretched so that the orthographic image of every source pixel is a
    /// square of <c>PixelWorldSize</c> on screen: the art is shown exactly as drawn (no second tilt), while depth matches
    /// an upright body at its <see cref="CombatPosition"/>. See SPRITES.md.
    /// </summary>
    public readonly struct SpriteCardBasis
    {
        /// <summary>Smallest |cos| / |sin| of the camera pitch used for the stretch (the camera clamps pitch to 5…90°).</summary>
        public const float MinAxisScale = 0.1f;

        /// <summary>Screen right on the ground plane (unit length): one source pixel to the right.</summary>
        public readonly float3 Right;

        /// <summary>World up divided by cos(pitch): one source pixel up on screen, above the support.</summary>
        public readonly float3 Up;

        /// <summary>Horizontal direction towards the camera divided by sin(pitch): one pixel down on screen, below the support.</summary>
        public readonly float3 Ground;

        /// <summary>Camera forward (unit length). Depth offsets move the card against it, which never changes its screen image.</summary>
        public readonly float3 ViewDirection;

        /// <summary>Horizontal camera forward (unit length); cards are planes perpendicular to it.</summary>
        public readonly float3 DepthAxis;

        /// <summary>cos(pitch) after clamping: view depth of a card = dot(position, DepthAxis) / CosPitch.</summary>
        public readonly float CosPitch;

        SpriteCardBasis(float3 right, float3 up, float3 ground, float3 view, float3 depthAxis, float cosPitch)
        {
            Right = right;
            Up = up;
            Ground = ground;
            ViewDirection = view;
            DepthAxis = depthAxis;
            CosPitch = cosPitch;
        }

        /// <summary>Basis for a camera with the given forward and up vectors (world space).</summary>
        public static SpriteCardBasis FromCamera(float3 forward, float3 up)
        {
            var view = math.normalizesafe(forward, new float3(0f, 0f, 1f));
            var horizontal = new float3(view.x, 0f, view.z);
            // Straight down: the camera's up vector gives the screen-up direction on the ground.
            if (math.lengthsq(horizontal) < 1e-8f)
                horizontal = new float3(up.x, 0f, up.z);
            var depthAxis = math.normalizesafe(horizontal, new float3(0f, 0f, 1f));
            var right = math.normalize(math.cross(new float3(0f, 1f, 0f), depthAxis));
            var cos = math.max(math.dot(view, depthAxis), MinAxisScale);
            var sin = math.max(-view.y, MinAxisScale);
            return new SpriteCardBasis(right, new float3(0f, 1f / cos, 0f), -depthAxis / sin, view, depthAxis, cos);
        }

        /// <summary>View depth key of a support point: larger is farther from the camera (the same for its whole card).</summary>
        public float DepthKey(float3 position) => math.dot(position, DepthAxis) / CosPitch;
    }

    /// <summary>
    /// Source-canvas → card → world conversion shared by frame geometry, anchors, bounds, tests and the debug overlay.
    /// <c>CombatSprite.shader</c> implements the same formulas on the GPU.
    /// </summary>
    public static class SpriteCardMath
    {
        /// <summary>+1 for Right, −1 for Left; any other value is drawn as authored (Right).</summary>
        public static float Sign(FacingDirection facing) => facing == FacingDirection.Left ? -1f : 1f;

        /// <summary>
        /// Offset of canvas point <paramref name="canvas"/> (top-left origin, Y down, continuous pixels) from the pivot in
        /// card pixels: x along screen right, y up. Facing left mirrors the canvas around the (mirrored) pivot.
        /// </summary>
        public static float2 CanvasToCard(float2 canvas, float2 pivot, FacingDirection facing) =>
            new float2((canvas.x - pivot.x) * Sign(facing), pivot.y - canvas.y);

        /// <summary>Direction in canvas axes (Y down) → card axes (Y up), mirrored for facing left.</summary>
        public static float2 CanvasDirectionToCard(float2 direction, FacingDirection facing) =>
            new float2(direction.x * Sign(facing), -direction.y);

        /// <summary>
        /// World point of card offset <paramref name="cardPx"/> for a support point. Above the support it lies on the vertical
        /// card, below it on the ground plane towards the camera; <paramref name="depthOffset"/> moves it towards the camera.
        /// </summary>
        public static float3 CardToWorld(in SpriteCardBasis basis, float3 support, float2 cardPx, float pixelWorldSize, float depthOffset = 0f)
        {
            var p = support + basis.Right * (cardPx.x * pixelWorldSize);
            p += cardPx.y >= 0f ? basis.Up * (cardPx.y * pixelWorldSize) : basis.Ground * (-cardPx.y * pixelWorldSize);
            return p - basis.ViewDirection * depthOffset;
        }

        public static float3 CanvasToWorld(in SpriteCardBasis basis, float3 support, float2 canvas, float2 pivot,
            FacingDirection facing, float pixelWorldSize, float depthOffset = 0f) =>
            CardToWorld(basis, support, CanvasToCard(canvas, pivot, facing), pixelWorldSize, depthOffset);

        /// <summary>World direction of a canvas direction on the upright card (not normalised to unit length if zero).</summary>
        public static float3 CanvasDirectionToWorld(in SpriteCardBasis basis, float2 direction, FacingDirection facing)
        {
            var card = CanvasDirectionToCard(direction, facing);
            return math.normalizesafe(basis.Right * card.x + basis.Up * card.y);
        }

        /// <summary>
        /// Authored (right-facing) card rectangle of a trimmed frame in pixels relative to the pivot: (left, bottom, right, top),
        /// y up. Independent of the rect's position on its page, so frame changes never move the support point.
        /// </summary>
        public static float4 FrameCardRect(int4 rect, int2 trimOffset, float2 pivot) => new float4(
            trimOffset.x - pivot.x,
            pivot.y - (trimOffset.y + rect.w),
            trimOffset.x + rect.z - pivot.x,
            pivot.y - trimOffset.y);

        /// <summary>
        /// GPU UV rectangle (u0, v0, u1, v1) of a top-left-origin page rect: Unity UV has its origin bottom-left, so the
        /// manifest's Y is flipped here, exactly once. v0 is the rect's bottom edge, v1 its top edge.
        /// </summary>
        public static float4 FrameUv(int4 rect, int2 pageSize) => new float4(
            (float)rect.x / pageSize.x,
            1f - (float)(rect.y + rect.w) / pageSize.y,
            (float)(rect.x + rect.z) / pageSize.x,
            1f - (float)rect.y / pageSize.y);
    }
}
