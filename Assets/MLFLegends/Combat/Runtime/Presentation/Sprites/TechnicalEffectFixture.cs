using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>Procedural diagnostic sweep and contact flash; deliberately technical, replaceable through slot bindings.</summary>
    public static class TechnicalEffectFixture
    {
        public const string ManifestJson = @"{""schema_version"": 1, ""character_id"": ""technical_effects"", ""source_size"": [64, 64], ""nominal_height_px"": 32, ""pivot"": [32, 32], ""authored_facing"": ""right"", ""pages"": [{""file"": ""swing.png"", ""width"": 144, ""height"": 36}, {""file"": ""impact.png"", ""width"": 144, ""height"": 36}], ""frames"": {""swing_0"": {""page"": 0, ""rect"": [2, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""swing_1"": {""page"": 0, ""rect"": [38, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""swing_2"": {""page"": 0, ""rect"": [74, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""swing_3"": {""page"": 0, ""rect"": [110, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""impact_0"": {""page"": 1, ""rect"": [2, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""impact_1"": {""page"": 1, ""rect"": [38, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""impact_2"": {""page"": 1, ""rect"": [74, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}, ""impact_3"": {""page"": 1, ""rect"": [110, 2, 32, 32], ""trim_offset"": [16, 16], ""anchors"": {""hand_left"": {""position"": [32, 32], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": false}, ""hand_right"": {""position"": [32, 32], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": false}, ""cast_origin"": null}}}, ""clips"": {""swing"": {""frames"": [{""id"": ""swing_0"", ""duration_ms"": 25}, {""id"": ""swing_1"", ""duration_ms"": 50}, {""id"": ""swing_2"", ""duration_ms"": 75}, {""id"": ""swing_3"", ""duration_ms"": 100}], ""loop"": false, ""hold_last"": false, ""markers"": {}}, ""impact"": {""frames"": [{""id"": ""impact_0"", ""duration_ms"": 25}, {""id"": ""impact_1"", ""duration_ms"": 50}, {""id"": ""impact_2"", ""duration_ms"": 75}, {""id"": ""impact_3"", ""duration_ms"": 100}], ""loop"": false, ""hold_last"": false, ""markers"": {}}}, ""compatible_transitions"": []}";
        public static Texture2D[] CreatePages()
        {
            var pages = new Texture2D[2];
            for (var page = 0; page < 2; page++)
            {
                var pixels = new Color32[144 * 36];
                for (var frame = 0; frame < 4; frame++)
                    for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
                    {
                        var dx = x - 15.5f; var dy = y - 15.5f;
                        var radius = Mathf.Sqrt(dx * dx + dy * dy);
                        bool visible;
                        if (page == 0)
                        {
                            var angle = Mathf.Atan2(dy, dx);
                            visible = radius > 9 && radius < 14 && angle > -1.1f + frame * .25f && angle < 1.1f + frame * .25f;
                        }
                        else visible = radius < 13 - frame * 2 && (Mathf.Abs(dx) < 2 || Mathf.Abs(dy) < 2 || Mathf.Abs(Mathf.Abs(dx) - Mathf.Abs(dy)) < 1.5f);
                        if (visible) pixels[(35 - (2 + y)) * 144 + frame * 36 + 2 + x] = page == 0
                            ? new Color32(75, 210, 255, (byte)(240 - frame * 35)) : new Color32(255, 215, 80, (byte)(255 - frame * 45));
                    }
                var t = new Texture2D(144, 36, TextureFormat.RGBA32, false) { name = page == 0 ? "Technical Swing" : "Technical Impact",
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                t.SetPixels32(pixels); t.Apply(false, true); pages[page] = t;
            }
            return pages;
        }
        public static SpriteAtlasPageBinding[] Bind(Texture2D[] pages) => new[]
        {
            new SpriteAtlasPageBinding { File = "swing.png", Texture = pages[0] },
            new SpriteAtlasPageBinding { File = "impact.png", Texture = pages[1] },
        };
    }
}
