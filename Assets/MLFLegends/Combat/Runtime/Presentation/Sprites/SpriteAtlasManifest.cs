using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Serialization.Json;

namespace MLFLegends.Combat
{
    /// <summary>
    /// Parser and validator of atlas manifest v1 (Docs/SPRITE_ATLAS_CONTRACT.md). All-or-nothing: any violation returns
    /// null and fills the error list with messages that name the field path and id.
    /// </summary>
    /// <remarks>
    /// Tokenizing uses the installed <c>com.unity.serialization</c> reader (no second JSON framework). Its object view
    /// enumerates every member in source order, so duplicate keys are reported before any dictionary is built. Values are
    /// validated against the strict JSON number grammar here (no NaN/Infinity, integers without fraction or exponent).
    /// Runs on load / explicit catalog change only, never per render frame.
    /// </remarks>
    public static class SpriteAtlasManifest
    {
        /// <summary>Messages collected before parsing gives up reporting (the result is rejected either way).</summary>
        public const int MaxErrors = 64;

        public const int MaxSourceSide = 4096;
        public const int MaxPageSide = 2048;

        /// <summary>Extrusion the artist supplies around every rect (pixels); grown rects must not overlap.</summary>
        public const int Extrusion = 2;

        static readonly Regex s_Number = new Regex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);
        static readonly Regex s_Integer = new Regex(@"^-?(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant);

        /// <summary>Parses and validates <paramref name="json"/>; null if anything is wrong (see <paramref name="errors"/>).</summary>
        public static SpriteAtlasDefinition Parse(string json, List<string> errors)
        {
            if (errors == null)
                throw new ArgumentNullException(nameof(errors));
            var context = new Context(errors);
            // A UTF-8 byte order mark from the exporter is not part of the JSON text.
            json = json?.TrimStart((char)0xFEFF);
            if (string.IsNullOrWhiteSpace(json))
            {
                context.Error("manifest", "is empty");
                return null;
            }

            var root = Tokenize(json, context);
            if (root == null || context.Failed)
                return null;
            var definition = Build(root, context);
            return context.Failed ? null : definition;
        }

        // ---------------------------------------------------------------- JSON tree (from com.unity.serialization views)

        enum Kind : byte { Object, Array, String, Number, Bool, Null }

        sealed class Node
        {
            public Kind Kind;
            public string Text;
            public List<KeyValuePair<string, Node>> Members;
            public List<Node> Items;
        }

        static Node Tokenize(string json, Context context)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var configuration = SerializedObjectReaderConfiguration.Default;
            configuration.UseReadAsync = false;
            configuration.ValidationType = JsonValidationType.Standard;
            configuration.BlockBufferSize = math.max(bytes.Length, 16);
            configuration.TokenBufferSize = math.max(json.Length / 2, 16);
            configuration.OutputBufferSize = math.max(bytes.Length * 2, 16);
            configuration.StripStringEscapeCharacters = true;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = new SerializedObjectReader(stream, configuration, Allocator.Persistent) { RequiresExplicitExceptionHandling = true };
                reader.Read(out var view);
                reader.CheckAndThrowInvalidJsonException();
                if (view.Type != TokenType.Object)
                {
                    context.Error("manifest", "root must be a JSON object");
                    return null;
                }

                return Convert(view, "", context);
            }
            catch (Exception e)
            {
                context.Error("manifest", "is not valid JSON: " + e.Message);
                return null;
            }
        }

        static Node Convert(SerializedValueView view, string path, Context context)
        {
            switch (view.Type)
            {
                case TokenType.Object:
                {
                    var node = new Node { Kind = Kind.Object, Members = new List<KeyValuePair<string, Node>>() };
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in view.AsObjectView())
                    {
                        var name = member.Name().ToString();
                        var memberPath = Join(path, name);
                        if (!seen.Add(name))
                        {
                            context.Error(memberPath, "duplicate key");
                            continue;
                        }

                        node.Members.Add(new KeyValuePair<string, Node>(name, Convert(member.Value(), memberPath, context)));
                    }

                    return node;
                }
                case TokenType.Array:
                {
                    var node = new Node { Kind = Kind.Array, Items = new List<Node>() };
                    var i = 0;
                    foreach (var item in view.AsArrayView())
                        node.Items.Add(Convert(item, $"{path}[{i++}]", context));
                    return node;
                }
                case TokenType.String:
                    return new Node { Kind = Kind.String, Text = view.AsStringView().ToString() };
                case TokenType.Primitive:
                {
                    var text = view.AsPrimitiveView().AsString();
                    switch (text)
                    {
                        case "true":
                        case "false":
                            return new Node { Kind = Kind.Bool, Text = text };
                        case "null":
                            return new Node { Kind = Kind.Null, Text = text };
                    }

                    if (!s_Number.IsMatch(text))
                        context.Error(path, $"'{text}' is not valid JSON (not a number, true, false or null)");
                    return new Node { Kind = Kind.Number, Text = text };
                }
                default:
                    context.Error(path, $"unsupported token {view.Type}");
                    return new Node { Kind = Kind.Null, Text = "null" };
            }
        }

        static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

        // ---------------------------------------------------------------- validation

        sealed class Context
        {
            readonly List<string> m_Errors;
            int m_Count;

            public Context(List<string> errors) => m_Errors = errors;

            public bool Failed => m_Count > 0;

            public void Error(string path, string message)
            {
                m_Count++;
                if (m_Count < MaxErrors)
                    m_Errors.Add($"{path}: {message}");
                else if (m_Count == MaxErrors)
                    m_Errors.Add("… further errors omitted");
            }
        }

        /// <summary>Reads the members of one object, rejecting unknown keys and reporting missing required ones.</summary>
        readonly struct Fields
        {
            readonly Node m_Node;
            readonly string m_Path;
            readonly Context m_Context;

            public Fields(Node node, string path, Context context, params string[] allowed)
            {
                m_Node = node;
                m_Path = path;
                m_Context = context;
                foreach (var member in node.Members)
                    if (Array.IndexOf(allowed, member.Key) < 0)
                        context.Error(Join(path, member.Key), "unknown key");
            }

            public Node Optional(string name)
            {
                foreach (var member in m_Node.Members)
                    if (member.Key == name)
                        return member.Value;
                return null;
            }

            public Node Required(string name)
            {
                var node = Optional(name);
                if (node == null)
                    m_Context.Error(Join(m_Path, name), "is required");
                return node;
            }
        }

        static bool IsObject(Node node, string path, Context context)
        {
            if (node == null)
                return false;
            if (node.Kind == Kind.Object)
                return true;
            context.Error(path, "must be an object");
            return false;
        }

        static bool TryString(Node node, string path, Context context, out string value)
        {
            value = null;
            if (node == null)
                return false;
            if (node.Kind != Kind.String || node.Text.Length == 0)
            {
                context.Error(path, "must be a non-empty string");
                return false;
            }

            value = node.Text;
            return true;
        }

        static bool TryBool(Node node, string path, Context context, out bool value)
        {
            value = false;
            if (node == null)
                return false;
            if (node.Kind != Kind.Bool)
            {
                context.Error(path, "must be true or false");
                return false;
            }

            value = node.Text == "true";
            return true;
        }

        static bool TryInt(Node node, string path, Context context, int min, int max, out int value)
        {
            value = 0;
            if (node == null)
                return false;
            if (node.Kind != Kind.Number || !s_Integer.IsMatch(node.Text)
                || !long.TryParse(node.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                context.Error(path, $"must be an integer, got {Describe(node)}");
                return false;
            }

            if (parsed < min || parsed > max)
            {
                context.Error(path, $"{parsed} is outside [{min}, {max}]");
                return false;
            }

            value = (int)parsed;
            return true;
        }

        static bool TryNumber(Node node, string path, Context context, out float value)
        {
            value = 0f;
            if (node == null)
                return false;
            if (node.Kind != Kind.Number || !s_Number.IsMatch(node.Text))
            {
                context.Error(path, $"must be a number, got {Describe(node)}");
                return false;
            }

            // Grammar-valid but out of range (e.g. 1e400) fails to parse or parses to infinity, depending on the runtime.
            var parsedOk = double.TryParse(node.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
            value = (float)parsed;
            if (!parsedOk || !float.IsFinite(value))
            {
                context.Error(path, $"{node.Text} is not finite");
                return false;
            }

            return true;
        }

        static bool TryArray(Node node, string path, Context context, int length, out List<Node> items)
        {
            items = null;
            if (node == null)
                return false;
            if (node.Kind != Kind.Array || (length >= 0 && node.Items.Count != length))
            {
                context.Error(path, length >= 0 ? $"must be an array of {length} values" : "must be an array");
                return false;
            }

            items = node.Items;
            return true;
        }

        static bool TryInt2(Node node, string path, Context context, int min, int max, out int2 value)
        {
            value = default;
            if (!TryArray(node, path, context, 2, out var items))
                return false;
            var ok = TryInt(items[0], path + "[0]", context, min, max, out value.x);
            ok &= TryInt(items[1], path + "[1]", context, min, max, out value.y);
            return ok;
        }

        static bool TryFloat2(Node node, string path, Context context, out float2 value)
        {
            value = default;
            if (!TryArray(node, path, context, 2, out var items))
                return false;
            var ok = TryNumber(items[0], path + "[0]", context, out value.x);
            ok &= TryNumber(items[1], path + "[1]", context, out value.y);
            return ok;
        }

        static string Describe(Node node) => node.Kind switch
        {
            Kind.String => $"\"{node.Text}\"",
            Kind.Object => "an object",
            Kind.Array => "an array",
            _ => node.Text,
        };

        static SpriteAtlasDefinition Build(Node root, Context context)
        {
            var top = new Fields(root, "", context, "schema_version", "character_id", "source_size", "nominal_height_px",
                "pivot", "authored_facing", "pages", "frames", "clips", "compatible_transitions", "meta");

            // An unknown version is reported alone: its other fields follow another contract.
            var versionNode = top.Required("schema_version");
            if (versionNode == null)
                return null;
            if (versionNode.Kind != Kind.Number || versionNode.Text != SpriteAtlasDefinition.SupportedSchemaVersion.ToString(CultureInfo.InvariantCulture))
            {
                context.Error("schema_version", $"unsupported schema_version {Describe(versionNode)} (supported: {SpriteAtlasDefinition.SupportedSchemaVersion})");
                return null;
            }

            TryString(top.Required("character_id"), "character_id", context, out var characterId);
            var hasSource = TryInt2(top.Required("source_size"), "source_size", context, 1, MaxSourceSide, out var sourceSize);
            if (TryNumber(top.Required("nominal_height_px"), "nominal_height_px", context, out var nominal)
                && (nominal <= 0f || (hasSource && nominal > sourceSize.y)))
                context.Error("nominal_height_px", $"{nominal} must be > 0 and <= source height");
            if (TryFloat2(top.Required("pivot"), "pivot", context, out var pivot) && hasSource
                && (pivot.x < 0f || pivot.y < 0f || pivot.x > sourceSize.x || pivot.y > sourceSize.y))
                context.Error("pivot", $"[{pivot.x}, {pivot.y}] lies outside the source canvas {sourceSize.x}x{sourceSize.y}");
            if (TryString(top.Required("authored_facing"), "authored_facing", context, out var facing) && facing != "right")
                context.Error("authored_facing", $"\"{facing}\" is not supported (v1: \"right\")");

            var pages = BuildPages(top.Required("pages"), context);
            var frames = BuildFrames(top.Required("frames"), pages, hasSource ? sourceSize : default, hasSource, context);
            var clips = BuildClips(top.Required("clips"), frames, context);
            var transitions = BuildTransitions(top.Required("compatible_transitions"), clips, context);
            if (context.Failed)
                return null;
            return new SpriteAtlasDefinition(characterId, sourceSize, nominal, pivot, pages, frames, clips, transitions);
        }

        static SpriteAtlasPage[] BuildPages(Node node, Context context)
        {
            if (!TryArray(node, "pages", context, -1, out var items))
                return Array.Empty<SpriteAtlasPage>();
            if (items.Count == 0)
                context.Error("pages", "must contain at least one page");
            var pages = new SpriteAtlasPage[items.Count];
            var files = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                var path = $"pages[{i}]";
                if (!IsObject(items[i], path, context))
                    continue;
                var fields = new Fields(items[i], path, context, "file", "width", "height");
                if (TryString(fields.Required("file"), path + ".file", context, out var file) && !files.Add(file))
                    context.Error(path + ".file", $"duplicate page file \"{file}\"");
                TryInt(fields.Required("width"), path + ".width", context, 1, MaxPageSide, out var width);
                TryInt(fields.Required("height"), path + ".height", context, 1, MaxPageSide, out var height);
                pages[i] = new SpriteAtlasPage(file, new int2(width, height));
            }

            return pages;
        }

        static SpriteAtlasFrame[] BuildFrames(Node node, SpriteAtlasPage[] pages, int2 source, bool hasSource, Context context)
        {
            if (!IsObject(node, "frames", context))
                return Array.Empty<SpriteAtlasFrame>();
            if (node.Members.Count == 0)
                context.Error("frames", "must contain at least one frame");
            var frames = new SpriteAtlasFrame[node.Members.Count];
            var valid = new bool[frames.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                var id = node.Members[i].Key;
                var path = "frames." + id;
                if (id.Length == 0)
                    context.Error(path, "frame id must be non-empty");
                if (!IsObject(node.Members[i].Value, path, context))
                    continue;
                var fields = new Fields(node.Members[i].Value, path, context, "page", "rect", "trim_offset", "anchors");
                var ok = TryInt(fields.Required("page"), path + ".page", context, 0, int.MaxValue, out var page);
                if (ok && page >= pages.Length)
                {
                    context.Error(path + ".page", $"page {page} does not exist ({pages.Length} pages)");
                    ok = false;
                }

                var rect = default(int4);
                if (TryArray(fields.Required("rect"), path + ".rect", context, 4, out var r))
                {
                    var rectOk = TryInt(r[0], path + ".rect[0]", context, 0, MaxPageSide, out rect.x);
                    rectOk &= TryInt(r[1], path + ".rect[1]", context, 0, MaxPageSide, out rect.y);
                    rectOk &= TryInt(r[2], path + ".rect[2]", context, 1, MaxPageSide, out rect.z);
                    rectOk &= TryInt(r[3], path + ".rect[3]", context, 1, MaxPageSide, out rect.w);
                    if (rectOk && ok)
                    {
                        var size = pages[page].Size;
                        if (rect.x + rect.z > size.x || rect.y + rect.w > size.y)
                        {
                            context.Error(path + ".rect", $"[{rect.x},{rect.y},{rect.z},{rect.w}] exceeds page {page} ({size.x}x{size.y})");
                            rectOk = false;
                        }
                        else if (rect.x < Extrusion || rect.y < Extrusion || rect.x + rect.z + Extrusion > size.x || rect.y + rect.w + Extrusion > size.y)
                        {
                            context.Error(path + ".rect", $"[{rect.x},{rect.y},{rect.z},{rect.w}] leaves no room for {Extrusion} px extrusion inside page {page} ({size.x}x{size.y})");
                            rectOk = false;
                        }
                    }

                    ok &= rectOk;
                }
                else
                    ok = false;

                if (TryInt2(fields.Required("trim_offset"), path + ".trim_offset", context, 0, MaxSourceSide, out var trim))
                {
                    if (ok && hasSource && (trim.x + rect.z > source.x || trim.y + rect.w > source.y))
                    {
                        context.Error(path + ".trim_offset", $"[{trim.x},{trim.y}] + size [{rect.z},{rect.w}] exceeds source_size {source.x}x{source.y}");
                        ok = false;
                    }
                }
                else
                    ok = false;

                var anchors = BuildAnchors(fields.Required("anchors"), path + ".anchors", context, out var left, out var right, out var cast, out var hasCast);
                frames[i] = new SpriteAtlasFrame(id, page, rect, trim, left, right, cast, hasCast);
                valid[i] = ok && anchors;
            }

            // Grown rects (useful area + extrusion) of frames on the same page must not overlap.
            for (var a = 0; a < frames.Length; a++)
            {
                if (!valid[a])
                    continue;
                for (var b = a + 1; b < frames.Length; b++)
                {
                    if (!valid[b] || frames[a].Page != frames[b].Page)
                        continue;
                    var ra = frames[a].Rect;
                    var rb = frames[b].Rect;
                    if (ra.x - Extrusion < rb.x + rb.z + Extrusion && rb.x - Extrusion < ra.x + ra.z + Extrusion
                        && ra.y - Extrusion < rb.y + rb.w + Extrusion && rb.y - Extrusion < ra.y + ra.w + Extrusion)
                        context.Error($"frames.{frames[b].Id}.rect",
                            $"overlaps frame \"{frames[a].Id}\" on page {frames[a].Page} (rects including {Extrusion} px extrusion must not overlap)");
                }
            }

            return frames;
        }

        static bool BuildAnchors(Node node, string path, Context context, out SpriteAnchor left, out SpriteAnchor right,
            out SpriteAnchor cast, out bool hasCast)
        {
            left = right = cast = default;
            hasCast = false;
            if (!IsObject(node, path, context))
                return false;
            var fields = new Fields(node, path, context, "hand_left", "hand_right", "cast_origin");
            var ok = BuildAnchor(fields.Required("hand_left"), path + ".hand_left", context, out left);
            ok &= BuildAnchor(fields.Required("hand_right"), path + ".hand_right", context, out right);
            var castNode = fields.Required("cast_origin");
            if (castNode == null)
                return false;
            if (castNode.Kind != Kind.Null)
            {
                ok &= BuildAnchor(castNode, path + ".cast_origin", context, out cast);
                hasCast = true;
            }

            return ok;
        }

        static bool BuildAnchor(Node node, string path, Context context, out SpriteAnchor anchor)
        {
            anchor = default;
            if (!IsObject(node, path, context))
                return false;
            var fields = new Fields(node, path, context, "position", "direction", "layer", "visible");
            var ok = TryFloat2(fields.Required("position"), path + ".position", context, out var position);
            if (TryFloat2(fields.Required("direction"), path + ".direction", context, out var direction))
            {
                if (direction.x == 0f && direction.y == 0f)
                {
                    context.Error(path + ".direction", "must not be [0, 0]");
                    ok = false;
                }
            }
            else
                ok = false;

            var layer = SpriteAnchorLayer.Front;
            if (TryString(fields.Required("layer"), path + ".layer", context, out var layerText))
            {
                if (layerText == "back")
                    layer = SpriteAnchorLayer.Back;
                else if (layerText != "front")
                {
                    context.Error(path + ".layer", $"\"{layerText}\" must be \"front\" or \"back\"");
                    ok = false;
                }
            }
            else
                ok = false;

            ok &= TryBool(fields.Required("visible"), path + ".visible", context, out var visible);
            anchor = new SpriteAnchor(position, direction, layer, visible);
            return ok;
        }

        static SpriteAtlasClip[] BuildClips(Node node, SpriteAtlasFrame[] frames, Context context)
        {
            if (!IsObject(node, "clips", context))
                return Array.Empty<SpriteAtlasClip>();
            var frameIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < frames.Length; i++)
                if (frames[i].Id != null)
                    frameIndices[frames[i].Id] = i;

            var clips = new SpriteAtlasClip[node.Members.Count];
            for (var c = 0; c < clips.Length; c++)
            {
                var id = node.Members[c].Key;
                var path = "clips." + id;
                if (id.Length == 0)
                    context.Error(path, "clip id must be non-empty");
                var indices = Array.Empty<int>();
                var durations = Array.Empty<float>();
                var markers = new Dictionary<string, int[]>(StringComparer.Ordinal);
                var loop = false;
                var holdLast = false;
                if (IsObject(node.Members[c].Value, path, context))
                {
                    var fields = new Fields(node.Members[c].Value, path, context, "frames", "loop", "hold_last", "markers");
                    if (TryArray(fields.Required("frames"), path + ".frames", context, -1, out var items))
                    {
                        if (items.Count == 0)
                            context.Error(path + ".frames", "must contain at least one frame");
                        indices = new int[items.Count];
                        durations = new float[items.Count];
                        for (var i = 0; i < items.Count; i++)
                        {
                            var itemPath = $"{path}.frames[{i}]";
                            indices[i] = -1;
                            if (!IsObject(items[i], itemPath, context))
                                continue;
                            var item = new Fields(items[i], itemPath, context, "id", "duration_ms");
                            if (TryString(item.Required("id"), itemPath + ".id", context, out var frameId)
                                && !frameIndices.TryGetValue(frameId, out indices[i]))
                            {
                                indices[i] = -1;
                                context.Error(itemPath + ".id", $"frame \"{frameId}\" does not exist");
                            }

                            if (TryNumber(item.Required("duration_ms"), itemPath + ".duration_ms", context, out durations[i]) && durations[i] <= 0f)
                                context.Error(itemPath + ".duration_ms", $"{durations[i]} must be > 0");
                        }
                    }

                    TryBool(fields.Required("loop"), path + ".loop", context, out loop);
                    TryBool(fields.Required("hold_last"), path + ".hold_last", context, out holdLast);
                    if (loop && holdLast)
                        context.Error(path, "loop and hold_last must not both be true");

                    var markersNode = fields.Required("markers");
                    if (IsObject(markersNode, path + ".markers", context))
                    {
                        foreach (var marker in markersNode.Members)
                        {
                            var markerPath = $"{path}.markers.{marker.Key}";
                            if (marker.Key.Length == 0)
                                context.Error(markerPath, "marker name must be non-empty");
                            if (!TryArray(marker.Value, markerPath, context, -1, out var locals))
                                continue;
                            if (locals.Count == 0)
                                context.Error(markerPath, "must list at least one local frame index");
                            var values = new int[locals.Count];
                            for (var i = 0; i < locals.Count; i++)
                            {
                                if (TryInt(locals[i], $"{markerPath}[{i}]", context, 0, Math.Max(0, indices.Length - 1), out values[i])
                                    && Array.IndexOf(values, values[i], 0, i) >= 0)
                                    context.Error($"{markerPath}[{i}]", $"repeats local index {values[i]}");
                            }

                            markers[marker.Key] = values;
                        }
                    }
                }

                clips[c] = new SpriteAtlasClip(id, indices, durations, loop, holdLast, markers);
            }

            return clips;
        }

        static SpriteAtlasTransition[] BuildTransitions(Node node, SpriteAtlasClip[] clips, Context context)
        {
            if (!TryArray(node, "compatible_transitions", context, -1, out var items))
                return Array.Empty<SpriteAtlasTransition>();
            var transitions = new SpriteAtlasTransition[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var path = $"compatible_transitions[{i}]";
                if (!IsObject(items[i], path, context))
                    continue;
                var fields = new Fields(items[i], path, context, "from_clip", "exit_frame", "to_clip", "entry_frame");
                var from = ClipIndex(fields.Required("from_clip"), path + ".from_clip", clips, context);
                var to = ClipIndex(fields.Required("to_clip"), path + ".to_clip", clips, context);
                var exitFrame = LocalIndex(fields.Required("exit_frame"), path + ".exit_frame", from, clips, context);
                var entryFrame = LocalIndex(fields.Required("entry_frame"), path + ".entry_frame", to, clips, context);
                transitions[i] = new SpriteAtlasTransition(from, exitFrame, to, entryFrame);
                for (var j = 0; j < i; j++)
                    if (transitions[j].Equals(transitions[i]) && from >= 0 && to >= 0)
                        context.Error(path, $"duplicates compatible_transitions[{j}]");
            }

            return transitions;
        }

        static int ClipIndex(Node node, string path, SpriteAtlasClip[] clips, Context context)
        {
            if (!TryString(node, path, context, out var id))
                return -1;
            for (var i = 0; i < clips.Length; i++)
                if (clips[i].Id == id)
                    return i;
            context.Error(path, $"clip \"{id}\" does not exist");
            return -1;
        }

        static int LocalIndex(Node node, string path, int clip, SpriteAtlasClip[] clips, Context context)
        {
            if (clip < 0)
                return -1;
            var max = Math.Max(0, clips[clip].Frames.Count - 1);
            return TryInt(node, path, context, 0, max, out var value) ? value : -1;
        }
    }
}
