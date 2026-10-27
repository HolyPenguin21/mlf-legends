using UnityEngine;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Reproducible <b>technical</b> atlas generated from code (not a character): asymmetric colour blocks with a transparent
    /// hole, three different trims (one hanging below the pivot, one floating left of and above it), shared frames in two
    /// clips and two pages of different sizes. Retained for regression tests and explicit manual previews; CombatSmoke now binds the real idle catalog.
    /// </summary>
    /// <remarks>
    /// Stricter than the art contract: everything outside a rect and its 2 px extrusion is opaque magenta
    /// (<see cref="Sentinel"/>), so a UV that strays past the extrusion is visible in a readback. The caller owns the
    /// textures from <see cref="CreatePages"/> and must destroy them. No PNG is written or committed.
    /// </remarks>
    public static class TechnicalAtlasFixture
    {
        public const string CharacterId = "technical_fixture";
        public const string Page0File = "technical_page0.png";
        public const string Page1File = "technical_page1.png";

        public static readonly Color32 Sentinel = new Color32(255, 0, 255, 255);
        public static readonly Color32 Red = new Color32(255, 0, 0, 255);
        public static readonly Color32 Green = new Color32(0, 255, 0, 255);
        public static readonly Color32 Blue = new Color32(0, 0, 255, 255);
        public static readonly Color32 Yellow = new Color32(255, 255, 0, 255);
        public static readonly Color32 Cyan = new Color32(0, 255, 255, 255);
        public static readonly Color32 Orange = new Color32(255, 128, 0, 255);
        public static readonly Color32 White = new Color32(255, 255, 255, 255);
        public static readonly Color32 Navy = new Color32(0, 0, 128, 255);
        public static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        /// <summary>
        /// tech_a (page 0, 40×80 at canvas 70,80): card x −26…14, y 0…80; left half red, right half green, top 10 rows blue,
        /// transparent 8×8 hole at rect (16…24, 40…48). tech_b (page 0, 60×40 at canvas 96,130): x 0…60, y −10…30; top 20 rows
        /// yellow, bottom 20 cyan. tech_c (page 1, 50×100 at canvas 30,40): x −66…−16, y 20…120; left 25 columns orange, right
        /// 25 white, bottom 10 rows navy.
        /// </summary>
        public const string ManifestJson = @"{
  ""schema_version"": 1,
  ""character_id"": ""technical_fixture"",
  ""source_size"": [192, 192],
  ""nominal_height_px"": 96,
  ""pivot"": [96, 160],
  ""authored_facing"": ""right"",
  ""meta"": { ""generator"": ""TechnicalAtlasFixture"", ""note"": ""technical test atlas, not a character"" },
  ""pages"": [
    { ""file"": ""technical_page0.png"", ""width"": 128, ""height"": 128 },
    { ""file"": ""technical_page1.png"", ""width"": 96, ""height"": 160 }
  ],
  ""frames"": {
    ""tech_a"": {
      ""page"": 0, ""rect"": [4, 4, 40, 80], ""trim_offset"": [70, 80],
      ""anchors"": {
        ""hand_left"": { ""position"": [72, 120], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": true },
        ""hand_right"": { ""position"": [108, 118], ""direction"": [1, 0.5], ""layer"": ""front"", ""visible"": true },
        ""cast_origin"": null
      }
    },
    ""tech_b"": {
      ""page"": 0, ""rect"": [52, 4, 60, 40], ""trim_offset"": [96, 130],
      ""anchors"": {
        ""hand_left"": { ""position"": [100, 140], ""direction"": [0, 1], ""layer"": ""back"", ""visible"": false },
        ""hand_right"": { ""position"": [150, 135], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": true },
        ""cast_origin"": { ""position"": [156, 131], ""direction"": [1, -0.5], ""layer"": ""front"", ""visible"": true }
      }
    },
    ""tech_c"": {
      ""page"": 1, ""rect"": [8, 8, 50, 100], ""trim_offset"": [30, 40],
      ""anchors"": {
        ""hand_left"": { ""position"": [35, 100], ""direction"": [-1, 0], ""layer"": ""back"", ""visible"": true },
        ""hand_right"": { ""position"": [75, 100], ""direction"": [1, 0], ""layer"": ""front"", ""visible"": true },
        ""cast_origin"": null
      }
    }
  },
  ""clips"": {
    ""tech_idle"": {
      ""frames"": [ { ""id"": ""tech_a"", ""duration_ms"": 100 }, { ""id"": ""tech_b"", ""duration_ms"": 62.5 } ],
      ""loop"": true, ""hold_last"": false, ""markers"": {}
    },
    ""tech_cast"": {
      ""frames"": [ { ""id"": ""tech_c"", ""duration_ms"": 150 }, { ""id"": ""tech_a"", ""duration_ms"": 100 } ],
      ""loop"": false, ""hold_last"": true, ""markers"": { ""release_pose"": [0], ""return"": [1] }
    }
  },
  ""compatible_transitions"": [
    { ""from_clip"": ""tech_idle"", ""exit_frame"": 1, ""to_clip"": ""tech_cast"", ""entry_frame"": 0 }
  ]
}";

        /// <summary>Colour of local pixel (x, y) (top-left origin) of a fixture frame; <see cref="Clear"/> = transparent.</summary>
        public static Color32 FramePixel(string frameId, int x, int y)
        {
            switch (frameId)
            {
                case "tech_a":
                    if (x >= 16 && x < 24 && y >= 40 && y < 48)
                        return Clear;
                    if (y < 10)
                        return Blue;
                    return x < 20 ? Red : Green;
                case "tech_b":
                    return y < 20 ? Yellow : Cyan;
                case "tech_c":
                    if (y >= 90)
                        return Navy;
                    return x < 25 ? Orange : White;
                default:
                    return Clear;
            }
        }

        /// <summary>Creates both pages (RGBA32 sRGB, point, clamp, no mipmaps, non-readable). The caller destroys them.</summary>
        public static Texture2D[] CreatePages()
        {
            var page0 = CreatePage("technical_page0", 128, 128, ("tech_a", 4, 4, 40, 80), ("tech_b", 52, 4, 60, 40));
            var page1 = CreatePage("technical_page1", 96, 160, ("tech_c", 8, 8, 50, 100));
            return new[] { page0, page1 };
        }

        public static SpriteAtlasPageBinding[] Bind(Texture2D[] pages) => new[]
        {
            new SpriteAtlasPageBinding(Page0File, pages[0]),
            new SpriteAtlasPageBinding(Page1File, pages[1]),
        };

        static Texture2D CreatePage(string name, int width, int height, params (string id, int x, int y, int w, int h)[] frames)
        {
            var pixels = new Color32[width * height];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = Sentinel;
            foreach (var (id, rx, ry, rw, rh) in frames)
            {
                // Rect plus 2 px extrusion: outside the rect the nearest edge pixel is repeated.
                for (var y = ry - SpriteAtlasManifest.Extrusion; y < ry + rh + SpriteAtlasManifest.Extrusion; y++)
                for (var x = rx - SpriteAtlasManifest.Extrusion; x < rx + rw + SpriteAtlasManifest.Extrusion; x++)
                {
                    var local = FramePixel(id, Mathf.Clamp(x - rx, 0, rw - 1), Mathf.Clamp(y - ry, 0, rh - 1));
                    pixels[(height - 1 - y) * width + x] = local; // Texture2D rows start at the bottom.
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }
    }
}
