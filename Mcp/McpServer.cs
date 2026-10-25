using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace SnipSnip
{
    /// <summary>
    /// MCP-Server (Model Context Protocol) über stdin/stdout: KI-Assistenten wie Claude können damit Screenshots machen,
    /// Bilder zensieren, Bildschirmvideos aufnehmen, Studio-Projekte bearbeiten, exportieren und hochladen.
    /// Start: <c>SnipSnip.exe --mcp</c> (läuft als eigener Prozess neben der Tray-Instanz).
    /// </summary>
    internal sealed class McpServer
    {
        static readonly string[] Versions = { "2025-06-18", "2025-03-26", "2024-11-05" };

        readonly Dispatcher ui;
        readonly TextWriter output;
        readonly object writeGate = new();
        readonly List<Tool> tools;

        // laufende Aufnahme
        ScreenRecorder? recorder;
        RegionFrame? frame;
        Toast? recToast;

        sealed record Tool(string Name, string Title, string Description, JsonObject Schema, Func<JsonObject, Call, Task<JsonObject>> Run);

        /// <summary>Kontext eines Aufrufs (für Fortschrittsmeldungen).</summary>
        sealed class Call
        {
            public JsonNode? ProgressToken;
        }

        McpServer(Dispatcher ui, TextWriter output)
        {
            this.ui = ui;
            this.output = output;
            tools = BuildTools();
        }

        /// <summary>Startet den Server: liest Anfragen in einem eigenen Thread, beendet die App bei EOF.</summary>
        public static void Run(Application app)
        {
            // Antworten sind für Maschinen/KI: Zahlen immer mit Punkt
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var server = new McpServer(app.Dispatcher, stdout);
            var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            new Thread(() =>
            {
                try
                {
                    string? line;
                    while ((line = stdin.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        string l = line;
                        _ = Task.Run(() => server.HandleAsync(l));
                    }
                }
                catch { }
                app.Dispatcher.InvokeAsync(async () =>
                {
                    await server.CleanupAsync();
                    app.Shutdown(0);
                });
            }) { IsBackground = true, Name = "SnipSnip.Mcp" }.Start();
        }

        // ================================================================== JSON-RPC

        async Task HandleAsync(string line)
        {
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch { Send(Error(null, -32700, "Parse error")); return; }
            if (msg == null) return;

            var id = msg["id"]?.DeepClone();
            string method = msg["method"]?.GetValue<string>() ?? "";
            var prm = msg["params"] as JsonObject ?? new JsonObject();
            if (id == null) return; // Benachrichtigung (z. B. notifications/initialized)

            try
            {
                JsonNode result = method switch
                {
                    "initialize" => Initialize(prm),
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = new JsonArray(tools.Select(ToolJson).ToArray<JsonNode?>()) },
                    "tools/call" => await CallToolAsync(prm),
                    _ => throw new RpcException(-32601, $"Method not found: {method}")
                };
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (RpcException ex) { Send(Error(id, ex.Code, ex.Message)); }
            catch (Exception ex) { Send(Error(id, -32603, ex.Message)); }
        }

        sealed class RpcException : Exception
        {
            public int Code { get; }
            public RpcException(int code, string msg) : base(msg) => Code = code;
        }

        static JsonObject Error(JsonNode? id, int code, string message) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

        void Send(JsonObject msg)
        {
            string s = msg.ToJsonString();
            lock (writeGate) output.WriteLine(s);
        }

        JsonObject Initialize(JsonObject prm)
        {
            string requested = prm["protocolVersion"]?.GetValue<string>() ?? Versions[0];
            string version = Versions.Contains(requested) ? requested : Versions[0];
            return new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "snipsnip",
                    ["title"] = "SnipSnip",
                    ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0"
                },
                ["instructions"] =
                    "SnipSnip captures the Windows desktop. Use take_screenshot to see the screen (returns the image), " +
                    "censor_image to redact sensitive areas before sharing, start_recording/stop_recording for screen videos. " +
                    "Recordings become Studio projects: inspect with get_project, change style, texts, zooms and cuts with edit_project, " +
                    "check the look with render_frame and produce the video with export_project. Coordinates are physical screen pixels; " +
                    "project times are seconds of the original recording unless stated otherwise."
            };
        }

        static JsonObject ToolJson(Tool t) => new()
        {
            ["name"] = t.Name,
            ["title"] = t.Title,
            ["description"] = t.Description,
            ["inputSchema"] = t.Schema.DeepClone()
        };

        async Task<JsonNode> CallToolAsync(JsonObject prm)
        {
            string name = prm["name"]?.GetValue<string>() ?? "";
            var tool = tools.FirstOrDefault(t => t.Name == name) ?? throw new RpcException(-32602, $"Unknown tool: {name}");
            var args = prm["arguments"] as JsonObject ?? new JsonObject();
            var call = new Call { ProgressToken = prm["_meta"]?["progressToken"]?.DeepClone() };
            try
            {
                // alles mit WPF/Fenstern/Zwischenablage gehört auf den UI-Thread; Schweres läuft dort in Task.Run
                var op = await ui.InvokeAsync(() => tool.Run(args, call));
                return await op;
            }
            catch (Exception ex)
            {
                return new JsonObject
                {
                    ["content"] = new JsonArray(TextContent("Error: " + ex.Message)),
                    ["isError"] = true
                };
            }
        }

        void Progress(Call call, double value, string message)
        {
            if (call.ProgressToken == null) return;
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/progress",
                ["params"] = new JsonObject { ["progressToken"] = call.ProgressToken.DeepClone(), ["progress"] = Math.Round(value * 100, 1), ["total"] = 100, ["message"] = message }
            });
        }

        // ================================================================== Ergebnis-Bausteine

        static JsonObject TextContent(string text) => new() { ["type"] = "text", ["text"] = text };

        static JsonObject ImageContent(byte[] data, string mime) => new() { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mime };

        static JsonObject Result(params JsonObject[] content) => new() { ["content"] = new JsonArray(content.ToArray<JsonNode?>()) };

        static JsonObject JsonText(JsonNode data) => TextContent(data.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        /// <summary>Verkleinert ein Bild für die Antwort (spart Tokens) und kodiert es als PNG.</summary>
        static byte[] Preview(BitmapSource img, int maxWidth)
        {
            maxWidth = Math.Clamp(maxWidth, 200, 4000);
            BitmapSource b = img;
            if (img.PixelWidth > maxWidth)
            {
                double s = maxWidth / (double)img.PixelWidth;
                var t = new TransformedBitmap(img, new ScaleTransform(s, s));
                t.Freeze();
                b = t;
            }
            var png = ImageUtil.Encode(b, "png");
            // sehr große Bildschirmfotos (Fotos, Videos auf dem Schirm) lieber als JPEG übertragen
            return png.Length > 1_500_000 ? ImageUtil.Encode(b, "jpg") : png;
        }

        static string MimeOf(byte[] data) => data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8 ? "image/jpeg" : "image/png";

        // ================================================================== Schema-Bausteine

        static JsonObject Prop(string type, string description, params string[] enumValues)
        {
            var o = new JsonObject { ["type"] = type, ["description"] = description };
            if (enumValues.Length > 0) o["enum"] = new JsonArray(enumValues.Select(v => (JsonNode?)v).ToArray());
            return o;
        }

        static JsonObject Schema(string[] required, params (string Name, JsonObject Prop)[] props)
        {
            var p = new JsonObject();
            foreach (var (n, pr) in props) p[n] = pr;
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = p,
                ["required"] = new JsonArray(required.Select(r => (JsonNode?)r).ToArray()),
                ["additionalProperties"] = false
            };
        }

        static (string, JsonObject)[] TargetProps() => new[]
        {
            ("target", Prop("string", "What to capture: 'screen' (all monitors; for recordings the primary monitor), 'monitor', 'window' or 'region'.", "screen", "monitor", "window", "region")),
            ("monitor", Prop("integer", "Monitor index from list_screens (target='monitor'). Default: primary monitor.")),
            ("window", Prop("string", "target='window': part of the window title, the process name (e.g. 'chrome') or a handle from list_windows. Default: the foreground window.")),
            ("activate", Prop("boolean", "target='window': bring the window to the front before capturing (default true).")),
            ("x", Prop("integer", "target='region': left edge in physical screen pixels.")),
            ("y", Prop("integer", "target='region': top edge in physical screen pixels.")),
            ("width", Prop("integer", "target='region': width in pixels.")),
            ("height", Prop("integer", "target='region': height in pixels."))
        };

        static string? Str(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a[k]?.ToString();
        static int? Int(JsonObject a, string k) => a[k] is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : null) : null;
        static double? Num(JsonObject a, string k) => a[k] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : null) : null;
        static bool? Bool(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        // ================================================================== Werkzeuge

        List<Tool> BuildTools() => new()
        {
            new Tool("list_screens", "List monitors",
                "Lists all monitors with index, position and size in physical pixels.",
                Schema(Array.Empty<string>()), (_, _) => Task.FromResult(ListScreens())),

            new Tool("list_windows", "List windows",
                "Lists visible top-level windows (front to back) with handle, title, process and bounds.",
                Schema(Array.Empty<string>()), (_, _) => Task.FromResult(ListWindows())),

            new Tool("take_screenshot", "Take screenshot",
                "Captures the screen, a monitor, a window or a region. Saves a PNG (path in the result) and returns the image so you can see it.",
                Schema(Array.Empty<string>(), TargetProps().Concat(new[]
                {
                    ("save", Prop("boolean", "Save to the SnipSnip pictures folder (default true); otherwise a temporary file is used.")),
                    ("max_width", Prop("integer", "Maximum width of the returned preview image (default 1568). The saved file keeps full resolution."))
                }).ToArray()), TakeScreenshotAsync),

            new Tool("censor_image", "Censor image",
                "Redacts rectangular areas of an image (pixelate, blur or blackout) and saves the result. Pixels are recomputed, nothing readable remains.",
                Schema(new[] { "path", "regions" },
                    ("path", Prop("string", "Image file (PNG, JPG …), e.g. from take_screenshot.")),
                    ("regions", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Areas in image pixels.",
                        ["items"] = Schema(new[] { "x", "y", "width", "height" },
                            ("x", Prop("integer", "Left")), ("y", Prop("integer", "Top")), ("width", Prop("integer", "Width")), ("height", Prop("integer", "Height")))
                    }),
                    ("mode", Prop("string", "Effect (default 'pixelate').", "pixelate", "blur", "blackout")),
                    ("output", Prop("string", "Target file; default '<name>_zensiert.png' next to the original."))), CensorAsync),

            new Tool("copy_to_clipboard", "Copy to clipboard",
                "Copies an image (as picture) or any other file such as an MP4 (as file, ready to paste into chat apps) to the Windows clipboard.",
                Schema(new[] { "path" }, ("path", Prop("string", "File to copy."))), CopyAsync),

            new Tool("upload", "Upload",
                "Uploads an image, GIF or MP4 (max. 200 MB) to Imgur or catbox.moe and returns the public link. Only use when the user wants to share publicly.",
                Schema(new[] { "path" },
                    ("path", Prop("string", "File to upload.")),
                    ("provider", Prop("string", "Default: the provider configured in SnipSnip.", "imgur", "catbox"))), UploadAsync),

            new Tool("start_recording", "Start screen recording",
                "Starts a screen video recording (cursor, clicks and keys are captured separately for the Studio). A red frame marks the recorded area. " +
                "If duration_seconds is given, waits and stops automatically (returns like stop_recording).",
                Schema(Array.Empty<string>(), TargetProps().Concat(new[]
                {
                    ("system_audio", Prop("boolean", "Record system sound (default: SnipSnip setting).")),
                    ("microphone", Prop("boolean", "Record the microphone (default: SnipSnip setting).")),
                    ("duration_seconds", Prop("number", "Optional: stop automatically after this many seconds (max. 600).")),
                    ("export", Prop("boolean", "With duration_seconds: export an MP4 afterwards (default true)."))
                }).ToArray()), StartRecordingAsync),

            new Tool("stop_recording", "Stop screen recording",
                "Stops the running recording. Returns the Studio project and, unless export=false, exports an MP4 with the current style (auto zoom, background, cursor).",
                Schema(Array.Empty<string>(),
                    ("export", Prop("boolean", "Export a video right away (default true).")),
                    ("format", Prop("string", "Video format (default mp4).", "mp4", "gif"))), (a, c) => StopRecordingAsync(a, c)),

            new Tool("discard_recording", "Discard recording",
                "Stops and deletes the running recording.",
                Schema(Array.Empty<string>()), DiscardRecordingAsync),

            new Tool("recording_status", "Recording status",
                "Tells whether a recording is running and for how long.",
                Schema(Array.Empty<string>()), (_, _) => Task.FromResult(RecordingStatus())),

            new Tool("list_projects", "List Studio projects",
                "Lists the most recent Studio projects (recordings and imported videos).",
                Schema(Array.Empty<string>(), ("limit", Prop("integer", "Maximum number (default 15)."))), (a, _) => Task.FromResult(ListProjects(a))),

            new Tool("get_project", "Get project details",
                "Returns duration, clicks, shortcuts, clips, zooms, masks, texts and the full style of a Studio project.",
                Schema(new[] { "project" }, ("project", Prop("string", "Project folder, project name or 'latest'."))), (a, _) => Task.FromResult(GetProject(a))),

            new Tool("edit_project", "Edit project",
                "Edits a Studio project. 'style' merges StudioStyle properties, e.g. {\"Aspect\":\"9:16\",\"Background\":\"Gradient\",\"Preset\":2,\"Padding\":6," +
                "\"CursorStyle\":\"Dot\",\"CursorSize\":2,\"ClickEffect\":\"Ripple\",\"Spotlight\":true,\"Chrome\":\"MacDark\",\"TiltY\":10,\"FadeIn\":0.5,\"FadeOut\":0.8," +
                "\"CutTransition\":\"Fade\",\"Vignette\":0.3,\"ProgressBar\":true,\"AccentColor\":\"#FF4F8CFF\",\"ShowKeys\":true,\"AutoZoom\":true,\"ZoomLevel\":2," +
                "\"MotionBlur\":true,\"Normalize\":true,\"Noise\":\"Standard\"}. Call get_project to see all style keys. Times are seconds of the original recording.",
                Schema(new[] { "project" },
                    ("project", Prop("string", "Project folder, project name or 'latest'.")),
                    ("style", new JsonObject { ["type"] = "object", ["description"] = "StudioStyle properties to change (see description)." }),
                    ("texts", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Text overlays to add.",
                        ["items"] = Schema(new[] { "start", "end", "text" },
                            ("start", Prop("number", "Start (s)")), ("end", Prop("number", "End (s)")),
                            ("text", Prop("string", "Text; for 'LowerThird' line 1 = name, line 2 = description")),
                            ("style", Prop("string", "Default Title", "Title", "Subtitle", "Callout", "LowerThird")),
                            ("position", Prop("string", "Default Center", "Top", "Center", "Bottom")),
                            ("size", Prop("number", "Scale 0.5–2.5, default 1")))
                    }),
                    ("clear_texts", Prop("boolean", "Remove existing texts first.")),
                    ("zooms", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Zooms to add. Without focus the camera follows the cursor.",
                        ["items"] = Schema(new[] { "start", "end" },
                            ("start", Prop("number", "Start (s)")), ("end", Prop("number", "End (s)")),
                            ("level", Prop("number", "Zoom factor 1.1–5, default 2")),
                            ("focus_x", Prop("number", "Fixed focus 0..1 (relative to the recording)")),
                            ("focus_y", Prop("number", "Fixed focus 0..1")))
                    }),
                    ("clear_zooms", Prop("boolean", "Remove all existing zooms first.")),
                    ("regenerate_auto_zooms", Prop("boolean", "Recreate automatic zooms from clicks.")),
                    ("cut", new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Ranges to remove, e.g. [[3.0, 5.5]].",
                        ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 2, ["maxItems"] = 2 }
                    }),
                    ("trim_start", Prop("number", "Remove everything before this time (s).")),
                    ("trim_end", Prop("number", "Remove everything after this time (s).")),
                    ("speed_up_idle", Prop("boolean", "true: speed up idle parts 4×, false: reset."))), (a, _) => Task.FromResult(EditProject(a))),

            new Tool("render_frame", "Render preview frame",
                "Renders one frame of the finished video (with style, zoom, cursor, texts) so you can check edits visually.",
                Schema(new[] { "project" },
                    ("project", Prop("string", "Project folder, project name or 'latest'.")),
                    ("time", Prop("number", "Time in the finished video (s), default 1.")),
                    ("max_width", Prop("integer", "Width of the returned image (default 1280)."))), RenderFrameAsync),

            new Tool("export_project", "Export video",
                "Exports a Studio project as MP4 or GIF and returns the file path. Can take a while for long videos.",
                Schema(new[] { "project" },
                    ("project", Prop("string", "Project folder, project name or 'latest'.")),
                    ("format", Prop("string", "Default mp4.", "mp4", "gif")),
                    ("resolution", Prop("integer", "Short side in pixels: 480, 720, 1080 (default), 1440 or 2160.")),
                    ("fps", Prop("integer", "30 or 60 (default: SnipSnip setting).")),
                    ("output", Prop("string", "Target file; default in the SnipSnip videos folder."))), ExportProjectAsync),

            new Tool("save_project_file", "Save project as file",
                "Packs a Studio project (recording, audio, webcam, edits and referenced files) into one .snipsnip file to back it up or share it. " +
                "Open it again with open_in_snipsnip.",
                Schema(new[] { "project" },
                    ("project", Prop("string", "Project folder, project name or 'latest'.")),
                    ("output", Prop("string", "Target .snipsnip file; default in the SnipSnip videos folder."))), SaveProjectFileAsync),

            new Tool("open_in_snipsnip", "Open in SnipSnip",
                "Opens a Studio project in the Studio, a .snipsnip project file, an image in the editor or a video for import – in the running SnipSnip app.",
                Schema(new[] { "path" }, ("path", Prop("string", "Project folder ('latest' allowed), .snipsnip file, image or video file."))), (a, _) => Task.FromResult(OpenInApp(a)))
        };

        // ---------------------------------------------------------------- Bildschirme & Fenster

        static JsonObject ListScreens()
        {
            var arr = new JsonArray();
            var all = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i].Bounds;
                arr.Add(new JsonObject { ["index"] = i, ["name"] = all[i].DeviceName, ["primary"] = all[i].Primary, ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height });
            }
            return Result(JsonText(arr));
        }

        static JsonObject ListWindows()
        {
            var arr = new JsonArray();
            foreach (var (bounds, hwnd) in WindowFinder.GetWindows())
            {
                string title = WindowTitle(hwnd);
                if (title.Length == 0) continue;
                arr.Add(new JsonObject
                {
                    ["handle"] = $"0x{hwnd.ToInt64():X}",
                    ["title"] = title,
                    ["process"] = ProcessName(hwnd),
                    ["x"] = (int)bounds.X, ["y"] = (int)bounds.Y, ["width"] = (int)bounds.Width, ["height"] = (int)bounds.Height
                });
            }
            return Result(JsonText(arr));
        }

        /// <summary>Löst target/monitor/window/region in ein Rechteck (physische Pixel) auf.</summary>
        async Task<(Rect Rect, string Label)> ResolveTargetAsync(JsonObject a, bool forRecording)
        {
            string target = (Str(a, "target") ?? "screen").ToLowerInvariant();
            switch (target)
            {
                case "monitor":
                {
                    var all = System.Windows.Forms.Screen.AllScreens;
                    int idx = Int(a, "monitor") ?? Array.FindIndex(all, s => s.Primary);
                    if (idx < 0 || idx >= all.Length) throw new ArgumentException($"Monitor {idx} does not exist (0–{all.Length - 1}).");
                    var b = all[idx].Bounds;
                    return (new Rect(b.X, b.Y, b.Width, b.Height), $"Monitor {idx}");
                }
                case "window":
                {
                    var (bounds, hwnd) = FindWindow(Str(a, "window"));
                    if (Bool(a, "activate") ?? true)
                    {
                        SetForegroundWindow(hwnd);
                        await Task.Delay(400);
                        var again = WindowFinder.GetWindows().FirstOrDefault(w => w.Hwnd == hwnd);
                        if (again.Hwnd != IntPtr.Zero) bounds = again.Bounds;
                    }
                    return (bounds, $"Fenster „{WindowTitle(hwnd)}“");
                }
                case "region":
                {
                    int x = Int(a, "x") ?? throw new ArgumentException("x missing"), y = Int(a, "y") ?? throw new ArgumentException("y missing");
                    int w = Int(a, "width") ?? throw new ArgumentException("width missing"), h = Int(a, "height") ?? throw new ArgumentException("height missing");
                    if (w < 8 || h < 8) throw new ArgumentException("Region too small.");
                    return (new Rect(x, y, w, h), "Bereich");
                }
                default:
                {
                    if (forRecording) return (ScreenCapture.PrimaryMonitor(), "Hauptbildschirm");
                    var vb = System.Windows.Forms.SystemInformation.VirtualScreen;
                    return (new Rect(vb.X, vb.Y, vb.Width, vb.Height), "Bildschirm");
                }
            }
        }

        static (Rect Bounds, IntPtr Hwnd) FindWindow(string? query)
        {
            var wins = WindowFinder.GetWindows();
            var own = Process.GetCurrentProcess().Id;
            if (string.IsNullOrWhiteSpace(query))
            {
                var fg = GetForegroundWindow();
                var hit = wins.FirstOrDefault(w => w.Hwnd == fg);
                if (hit.Hwnd != IntPtr.Zero) return hit;
                return wins.FirstOrDefault(w => WindowTitle(w.Hwnd).Length > 0 && ProcessId(w.Hwnd) != own) is var first && first.Hwnd != IntPtr.Zero
                    ? first : throw new ArgumentException("No window found.");
            }
            string q = query.Trim();
            if (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(q[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h))
            {
                var byHandle = wins.FirstOrDefault(w => w.Hwnd.ToInt64() == h);
                if (byHandle.Hwnd != IntPtr.Zero) return byHandle;
            }
            foreach (var w in wins)
            {
                if (WindowTitle(w.Hwnd).Contains(q, StringComparison.OrdinalIgnoreCase)) return w;
            }
            foreach (var w in wins)
            {
                if (ProcessName(w.Hwnd).Equals(q.Replace(".exe", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)) return w;
            }
            throw new ArgumentException($"No visible window matches „{q}“. Use list_windows.");
        }

        // ---------------------------------------------------------------- Screenshot & Bilder

        async Task<JsonObject> TakeScreenshotAsync(JsonObject a, Call _)
        {
            var (rect, label) = await ResolveTargetAsync(a, forRecording: false);
            var (full, vb) = ScreenCapture.CaptureVirtualScreen();
            var ir = Clip(new Int32Rect((int)(rect.X - vb.X), (int)(rect.Y - vb.Y), (int)rect.Width, (int)rect.Height), full.PixelWidth, full.PixelHeight);
            if (ir.Width < 2 || ir.Height < 2) throw new ArgumentException("The area is outside the screen.");
            var img = new CroppedBitmap(full, ir);
            img.Freeze();

            string path;
            if (Bool(a, "save") ?? true) path = await Task.Run(() => ImageUtil.SaveToFolder(img));
            else
            {
                path = Path.Combine(Path.GetTempPath(), "SnipSnip", ImageUtil.DefaultFileName("png"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] png = await Task.Run(() => ImageUtil.Encode(img, "png"));
                await File.WriteAllBytesAsync(path, png);
            }
            byte[] preview = await Task.Run(() => Preview(img, Int(a, "max_width") ?? 1568));
            return Result(
                TextContent($"{label}: {img.PixelWidth} × {img.PixelHeight} px at ({(int)rect.X}, {(int)rect.Y}). Saved: {path}"),
                ImageContent(preview, MimeOf(preview)));
        }

        static Int32Rect Clip(Int32Rect r, int w, int h)
        {
            int x0 = Math.Clamp(r.X, 0, w), y0 = Math.Clamp(r.Y, 0, h);
            int x1 = Math.Clamp(r.X + r.Width, 0, w), y1 = Math.Clamp(r.Y + r.Height, 0, h);
            return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        async Task<JsonObject> CensorAsync(JsonObject a, Call _)
        {
            string path = Str(a, "path") ?? throw new ArgumentException("path missing");
            if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
            string mode = (Str(a, "mode") ?? "pixelate").ToLowerInvariant();
            var regions = (a["regions"] as JsonArray ?? throw new ArgumentException("regions missing")).OfType<JsonObject>().ToList();
            string outPath = Str(a, "output") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + "_zensiert.png");

            var (result, count) = await Task.Run(() =>
            {
                var src = ImageUtil.Load(path);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var buf = new byte[stride * h];
                src.CopyPixels(buf, stride, 0);
                int n = 0;
                foreach (var r in regions)
                {
                    var ir = Clip(new Int32Rect(Int(r, "x") ?? 0, Int(r, "y") ?? 0, Int(r, "width") ?? 0, Int(r, "height") ?? 0), w, h);
                    if (ir.Width < 1 || ir.Height < 1) continue;
                    byte[] patch;
                    if (mode == "blackout") patch = Enumerable.Repeat(new byte[] { 0, 0, 0, 255 }, ir.Width * ir.Height).SelectMany(p => p).ToArray();
                    else
                    {
                        int size = Math.Max(ir.Width, ir.Height);
                        var eff = mode == "blur" ? ImageEffects.Blur(src, ir, Math.Max(4, size / 12)) : ImageEffects.Pixelate(src, ir, Math.Max(6, size / 14));
                        patch = new byte[ir.Width * ir.Height * 4];
                        eff.CopyPixels(patch, ir.Width * 4, 0);
                    }
                    for (int y = 0; y < ir.Height; y++)
                        Buffer.BlockCopy(patch, y * ir.Width * 4, buf, (ir.Y + y) * stride + ir.X * 4, ir.Width * 4);
                    n++;
                }
                var res = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, stride);
                res.Freeze();
                string ext = Path.GetExtension(outPath).TrimStart('.').ToLowerInvariant();
                File.WriteAllBytes(outPath, ImageUtil.Encode(res, ext is "jpg" or "jpeg" or "bmp" ? ext : "png"));
                return (res, n);
            });
            byte[] preview = await Task.Run(() => Preview(result, 1280));
            return Result(TextContent($"{count} area(s) censored ({mode}). Saved: {outPath}"), ImageContent(preview, MimeOf(preview)));
        }

        Task<JsonObject> CopyAsync(JsonObject a, Call _)
        {
            string path = Path.GetFullPath(Str(a, "path") ?? throw new ArgumentException("path missing"));
            if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
            if (ImageUtil.IsImageFile(path) && !path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                ImageUtil.CopyToClipboard(ImageUtil.Load(path));
                return Task.FromResult(Result(TextContent("Image copied to the clipboard.")));
            }
            var list = new System.Collections.Specialized.StringCollection { path };
            Clipboard.SetFileDropList(list);
            return Task.FromResult(Result(TextContent("File copied to the clipboard (paste with Ctrl+V into chats or Explorer).")));
        }

        async Task<JsonObject> UploadAsync(JsonObject a, Call _)
        {
            string path = Str(a, "path") ?? throw new ArgumentException("path missing");
            if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
            long size = new FileInfo(path).Length;
            if (size > 200L * 1024 * 1024) throw new InvalidOperationException($"File is {size / 1024 / 1024} MB – the limit is 200 MB.");
            var provider = (Str(a, "provider") ?? "").ToLowerInvariant() switch
            {
                "imgur" => UploadProvider.Imgur,
                "catbox" => UploadProvider.Catbox,
                _ => App.Settings.Provider
            };
            if (provider == UploadProvider.Imgur && string.IsNullOrWhiteSpace(App.Settings.ImgurClientId))
                throw new InvalidOperationException("No Imgur client ID is configured in SnipSnip. Ask the user to add one in the settings, or use provider='catbox'.");
            var data = await File.ReadAllBytesAsync(path);
            var res = await Uploader.UploadAsync(data, provider, Path.GetFileName(path));
            string name = Uploader.Name(provider);
            UploadHistory.Add(new UploadRecord { Time = DateTime.Now, Provider = name, Url = res.Url, DeleteUrl = res.DeleteUrl });
            return Result(TextContent($"Uploaded to {name}: {res.Url}" + (res.DeleteUrl != null ? $"\nDelete link: {res.DeleteUrl}" : "")));
        }

        // ---------------------------------------------------------------- Aufnahme

        async Task<JsonObject> StartRecordingAsync(JsonObject a, Call call)
        {
            if (recorder != null) throw new InvalidOperationException("A recording is already running. Call stop_recording first.");
            if (!FFmpeg.IsAvailable) throw new InvalidOperationException("FFmpeg is missing. Open SnipSnip once and start a video recording – it offers to download FFmpeg (or: winget install Gyan.FFmpeg).");
            var (rect, label) = await ResolveTargetAsync(a, forRecording: true);

            var s = App.Settings; // nur im Speicher dieses Prozesses, wird nicht gespeichert
            if (Bool(a, "system_audio") is bool sys) s.RecordSystemAudio = sys;
            if (Bool(a, "microphone") is bool mic) s.RecordMicrophone = mic;

            string dir = StudioProject.NewDir();
            var rec = new ScreenRecorder(rect, dir, null);
            rec.Failed += ex => ui.InvokeAsync(() =>
            {
                if (recorder != rec) return;
                frame?.Close();
                frame = null;
                recToast?.Update("Aufnahme fehlgeschlagen", ex.Message, null, 10);
                recorder = null;
                _ = rec.DiscardAsync();
            });
            rec.Start();
            recorder = rec;
            frame = new RegionFrame(new Rect(rect.X, rect.Y, rec.Width, rec.Height));
            frame.Show();
            recToast = Toast.Notify("Aufnahme läuft (KI-Assistent)", $"{label} · {rec.Width} × {rec.Height} – gesteuert über MCP", null, null, 4);

            double? duration = Num(a, "duration_seconds");
            if (duration is > 0)
            {
                double d = Math.Min(600, duration.Value);
                var end = DateTime.Now.AddSeconds(d);
                while (DateTime.Now < end && recorder == rec)
                {
                    Progress(call, 1 - (end - DateTime.Now).TotalSeconds / d, "Aufnahme läuft");
                    await Task.Delay(500);
                }
                if (recorder != rec) throw new InvalidOperationException("The recording was stopped or failed.");
                return await StopRecordingAsync(new JsonObject { ["export"] = Bool(a, "export") ?? true }, call);
            }
            return Result(TextContent($"Recording started: {label}, {rec.Width} × {rec.Height} px. Project: {dir}\nCall stop_recording when done."));
        }

        async Task<JsonObject> StopRecordingAsync(JsonObject a, Call call)
        {
            var rec = recorder ?? throw new InvalidOperationException("No recording is running.");
            recorder = null;
            frame?.Close();
            frame = null;
            var elapsed = rec.Elapsed;
            string project = await rec.StopAsync();
            Toast.Notify("Aufnahme beendet", $"{RecordingSession.Format(elapsed)} – über MCP", null, null, 4);
            var text = new StringBuilder($"Recording stopped ({elapsed.TotalSeconds:0.0} s). Studio project: {project}");
            if (Bool(a, "export") ?? true)
            {
                var p = StudioProject.Load(project);
                bool gif = (Str(a, "format") ?? "mp4").Equals("gif", StringComparison.OrdinalIgnoreCase);
                string file = await ExportAsync(p, gif, null, null, null, call);
                text.Append($"\nExported: {file}");
            }
            return Result(TextContent(text.ToString()));
        }

        async Task<JsonObject> DiscardRecordingAsync(JsonObject a, Call _)
        {
            var rec = recorder ?? throw new InvalidOperationException("No recording is running.");
            recorder = null;
            frame?.Close();
            frame = null;
            await rec.DiscardAsync();
            return Result(TextContent("Recording discarded."));
        }

        JsonObject RecordingStatus() => recorder == null
            ? Result(TextContent("No recording is running."))
            : Result(TextContent($"Recording: {recorder.Elapsed.TotalSeconds:0.0} s, {recorder.Width} × {recorder.Height}, project {recorder.ProjectDir}"));

        async Task CleanupAsync()
        {
            if (recorder == null) return;
            // Client weg: laufende Aufnahme sauber sichern statt verwerfen
            frame?.Close();
            try { await recorder.StopAsync(); } catch { }
            recorder = null;
        }

        // ---------------------------------------------------------------- Projekte

        static StudioProject LoadProject(string? spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("project missing");
            string dir = spec;
            if (spec.Equals("latest", StringComparison.OrdinalIgnoreCase))
                dir = ProjectDirs().FirstOrDefault() ?? throw new InvalidOperationException("No projects yet.");
            else if (!Directory.Exists(dir))
                dir = Path.Combine(StudioProject.ProjectsRoot, spec);
            if (File.Exists(dir) && Path.GetFileName(dir).Equals("meta.json", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir)!;
            if (!File.Exists(Path.Combine(dir, "meta.json"))) throw new DirectoryNotFoundException($"No Studio project found: {spec}");
            return StudioProject.Load(dir);
        }

        static IEnumerable<string> ProjectDirs()
        {
            if (!Directory.Exists(StudioProject.ProjectsRoot)) return Enumerable.Empty<string>();
            return Directory.GetDirectories(StudioProject.ProjectsRoot)
                .Where(d => File.Exists(Path.Combine(d, "meta.json")))
                .OrderByDescending(Directory.GetCreationTime);
        }

        static JsonObject ListProjects(JsonObject a)
        {
            var arr = new JsonArray();
            foreach (var d in ProjectDirs().Take(Math.Clamp(Int(a, "limit") ?? 15, 1, 200)))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<RecordingMeta>(File.ReadAllText(Path.Combine(d, "meta.json")), AppSettings.Json)!;
                    arr.Add(new JsonObject
                    {
                        ["name"] = Path.GetFileName(d),
                        ["path"] = d,
                        ["created"] = Directory.GetCreationTime(d).ToString("s"),
                        ["duration_s"] = Math.Round(meta.Duration, 1),
                        ["size"] = $"{meta.Width}x{meta.Height}",
                        ["imported"] = meta.SourceFile != null
                    });
                }
                catch { }
            }
            return Result(JsonText(arr));
        }

        static JsonObject GetProject(JsonObject a)
        {
            var p = LoadProject(Str(a, "project"));
            var e = p.Edits;
            var o = new JsonObject
            {
                ["name"] = p.Name,
                ["folder"] = p.FolderName,
                ["path"] = p.Dir,
                ["size"] = $"{p.Meta.Width}x{p.Meta.Height}",
                ["fps"] = p.Meta.Fps,
                ["duration_s"] = Math.Round(p.Meta.Duration, 2),
                ["output_duration_s"] = Math.Round(p.OutputDuration, 2),
                ["has_cursor_data"] = p.Input.HasCursor,
                ["has_webcam"] = p.HasWebcam,
                ["system_audio"] = p.Meta.HasSystemAudio,
                ["microphone"] = p.Meta.HasMic,
                ["clicks_s"] = new JsonArray(p.Input.Clicks.Select(c => (JsonNode?)Math.Round(c, 2)).ToArray()),
                ["shortcuts"] = new JsonArray(p.Input.Keys.Where(k => k.Text.Length > 1).Select(k => (JsonNode?)$"{k.T:0.0}s {k.Text}").ToArray()),
                ["clips"] = JsonSerializer.SerializeToNode(e.Clips, AppSettings.Json),
                ["zooms"] = JsonSerializer.SerializeToNode(e.Zooms, AppSettings.Json),
                ["masks"] = e.Masks.Count,
                ["texts"] = JsonSerializer.SerializeToNode(e.Texts, AppSettings.Json),
                ["style"] = JsonSerializer.SerializeToNode(e.Style, AppSettings.Json)
            };
            return Result(JsonText(o));
        }

        static JsonObject EditProject(JsonObject a)
        {
            var p = LoadProject(Str(a, "project"));
            var e = p.Edits;
            var done = new List<string>();

            if (a["style"] is JsonObject style && style.Count > 0)
            {
                var cur = JsonSerializer.SerializeToNode(e.Style, AppSettings.Json)!.AsObject();
                foreach (var (k, v) in style)
                {
                    string norm = k.Replace("_", "");
                    var key = cur.Select(kv => kv.Key).FirstOrDefault(n => n.Equals(norm, StringComparison.OrdinalIgnoreCase))
                              ?? throw new ArgumentException($"Unknown style property: {k}");
                    cur[key] = v?.DeepClone();
                }
                e.Style = JsonSerializer.Deserialize<StudioStyle>(cur.ToJsonString(), AppSettings.Json) ?? e.Style;
                done.Add($"style ({string.Join(", ", style.Select(kv => kv.Key))})");
                if (style.Any(kv => kv.Key.Replace("_", "").Equals("AutoZoom", StringComparison.OrdinalIgnoreCase)))
                {
                    e.Zooms.RemoveAll(z => z.Auto);
                    if (e.Style.AutoZoom && p.Input.HasCursor) e.Zooms.AddRange(AutoZoom.Generate(p, e.Style));
                }
            }
            if (Bool(a, "clear_zooms") == true) { e.Zooms.Clear(); done.Add("zooms cleared"); }
            if (Bool(a, "regenerate_auto_zooms") == true && p.Input.HasCursor)
            {
                e.Zooms.RemoveAll(z => z.Auto);
                e.Zooms.AddRange(AutoZoom.Generate(p, e.Style));
                done.Add("auto zooms regenerated");
            }
            if (a["zooms"] is JsonArray zooms)
            {
                foreach (var z in zooms.OfType<JsonObject>())
                {
                    double s = Num(z, "start") ?? 0, en = Num(z, "end") ?? s + 2;
                    bool fixedFocus = Num(z, "focus_x") != null && Num(z, "focus_y") != null;
                    e.Zooms.Add(new ZoomSegment
                    {
                        Start = Math.Max(0, s), End = Math.Min(p.Meta.Duration, Math.Max(en, s + 0.3)),
                        Level = Math.Clamp(Num(z, "level") ?? e.Style.ZoomLevel, 1.1, 5),
                        FollowCursor = !fixedFocus && p.Input.HasCursor,
                        FocusX = Math.Clamp(Num(z, "focus_x") ?? 0.5, 0, 1),
                        FocusY = Math.Clamp(Num(z, "focus_y") ?? 0.5, 0, 1)
                    });
                }
                done.Add($"{zooms.Count} zoom(s) added");
            }
            e.Zooms.Sort((x, y) => x.Start.CompareTo(y.Start));

            if (Bool(a, "clear_texts") == true) { e.Texts.Clear(); done.Add("texts cleared"); }
            if (a["texts"] is JsonArray texts)
            {
                foreach (var t in texts.OfType<JsonObject>())
                {
                    double s = Num(t, "start") ?? 0, en = Num(t, "end") ?? s + 3;
                    e.Texts.Add(new TextSegment
                    {
                        Start = Math.Max(0, s), End = Math.Max(en, s + 0.3),
                        Text = Str(t, "text") ?? "",
                        Style = Enum.TryParse<TextStyle>(Str(t, "style"), true, out var ts) ? ts : TextStyle.Title,
                        Position = Enum.TryParse<TextPosition>(Str(t, "position"), true, out var tp) ? tp : TextPosition.Center,
                        Size = Math.Clamp(Num(t, "size") ?? 1, 0.4, 3)
                    });
                }
                done.Add($"{texts.Count} text(s) added");
            }

            void Remove(double s, double en)
            {
                s = Math.Max(0, s);
                en = Math.Min(p.Meta.Duration, en);
                if (en - s < 0.05) return;
                p.SplitClipAt(s);
                p.SplitClipAt(en);
                foreach (var c in e.Clips.Where(c => c.Start >= s - 1e-6 && c.End <= en + 1e-6)) c.Removed = true;
            }
            if (a["cut"] is JsonArray cuts)
            {
                foreach (var r in cuts.OfType<JsonArray>())
                    if (r.Count == 2) Remove(r[0]!.GetValue<double>(), r[1]!.GetValue<double>());
                done.Add($"{cuts.Count} range(s) cut");
            }
            if (Num(a, "trim_start") is double ts0 && ts0 > 0) { Remove(0, ts0); done.Add($"trimmed start to {ts0:0.##} s"); }
            if (Num(a, "trim_end") is double te && te < p.Meta.Duration) { Remove(te, p.Meta.Duration); done.Add($"trimmed end at {te:0.##} s"); }
            if (e.Clips.All(c => c.Removed)) throw new InvalidOperationException("That would remove the whole video.");

            if (Bool(a, "speed_up_idle") is bool idle)
            {
                if (idle) done.Add($"{p.ApplyIdleSpeedUp()} idle part(s) sped up");
                else { p.ResetIdleSpeedUp(); done.Add("idle speed-up reset"); }
            }

            p.Save();
            return Result(TextContent($"Saved {p.Name}: {(done.Count > 0 ? string.Join("; ", done) : "nothing changed")}. " +
                                      $"Video length now {p.OutputDuration:0.0} s. If the project is open in the Studio, reopen it to see the changes."));
        }

        async Task<JsonObject> RenderFrameAsync(JsonObject a, Call _)
        {
            var p = LoadProject(Str(a, "project"));
            double outT = Math.Clamp(Num(a, "time") ?? 1, 0, Math.Max(0, p.OutputDuration - 0.01));
            double src = p.SourceAt(outT);
            var screen = await FrameDecoder.GrabAsync(p.ScreenFile, src, p.Meta.Width, p.Meta.Height);
            SKImage? cam = p.HasWebcam ? await FrameDecoder.GrabAsync(p.WebcamFile!, p.Meta.WallAt(src) - p.Meta.WebcamStartEpoch, 1280, 720) : null;
            int maxW = Math.Clamp(Int(a, "max_width") ?? 1280, 320, 3840);
            byte[] png = await Task.Run(() =>
            {
                using var r = new StudioRenderer(p);
                var (w, h) = r.OutputSize(1080);
                using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(SKColors.Black);
                r.Render(surface.Canvas, w, h, src, outT, screen, cam, export: true);
                using var img = surface.Snapshot();
                if (w <= maxW)
                {
                    using var d = img.Encode(SKEncodedImageFormat.Jpeg, 88);
                    return d.ToArray();
                }
                int sh = (int)Math.Round(h * (double)maxW / w);
                using var small = SKSurface.Create(new SKImageInfo(maxW, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
                small.Canvas.DrawImage(img, SKRect.Create(0, 0, maxW, sh), new SKSamplingOptions(SKCubicResampler.Mitchell));
                using var si = small.Snapshot();
                using var data = si.Encode(SKEncodedImageFormat.Jpeg, 88);
                return data.ToArray();
            });
            screen?.Dispose();
            cam?.Dispose();
            return Result(TextContent($"{p.Name} at {outT:0.0} s of {p.OutputDuration:0.0} s (recording time {src:0.0} s)."), ImageContent(png, "image/jpeg"));
        }

        async Task<JsonObject> ExportProjectAsync(JsonObject a, Call call)
        {
            var p = LoadProject(Str(a, "project"));
            bool gif = (Str(a, "format") ?? "mp4").Equals("gif", StringComparison.OrdinalIgnoreCase);
            string file = await ExportAsync(p, gif, Int(a, "resolution"), Int(a, "fps"), Str(a, "output"), call);
            long size = new FileInfo(file).Length;
            return Result(TextContent($"Exported {p.OutputDuration:0.0} s: {file} ({size / 1024.0 / 1024.0:0.0} MB)"));
        }

        async Task<string> ExportAsync(StudioProject p, bool gif, int? resolution, int? fps, string? output, Call call)
        {
            if (!FFmpeg.IsAvailable) throw new InvalidOperationException("FFmpeg is missing.");
            string ext = gif ? "gif" : "mp4";
            output ??= Path.Combine(App.Settings.VideoFolder, $"{p.FileName}.{ext}");
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var opts = new StudioExportOptions
            {
                Gif = gif,
                ShortSide = resolution ?? (int.TryParse(App.Settings.ExportResolution, out int ss) ? ss : 1080),
                Fps = fps is 30 or 60 ? fps.Value : App.Settings.ExportFps,
                Crf = 20,
                GifFps = 15
            };
            await Task.Run(() => StudioExporter.ExportAsync(p, opts, output, (v, s) => Progress(call, v, s), CancellationToken.None));
            return output;
        }

        async Task<JsonObject> SaveProjectFileAsync(JsonObject a, Call call)
        {
            var p = LoadProject(Str(a, "project"));
            string output = Str(a, "output") ?? Path.Combine(App.Settings.VideoFolder, p.FileName + ProjectArchive.Extension);
            output = Path.GetFullPath(output);
            if (!ProjectArchive.IsArchive(output)) output += ProjectArchive.Extension;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await ProjectArchive.SaveAsync(p, output, new Progress<double>(v => Progress(call, v, "Projekt wird gepackt")));
            return Result(TextContent($"Saved project file: {output} ({new FileInfo(output).Length / 1024.0 / 1024.0:0.0} MB)"));
        }

        // ---------------------------------------------------------------- An die SnipSnip-App übergeben

        static JsonObject OpenInApp(JsonObject a)
        {
            string spec = Str(a, "path") ?? throw new ArgumentException("path missing");
            string path;
            if (spec.Equals("latest", StringComparison.OrdinalIgnoreCase) || Directory.Exists(spec) || !File.Exists(spec))
                path = Path.Combine(LoadProject(spec).Dir, "meta.json");
            else path = Path.GetFullPath(spec);

            if (Mutex.TryOpenExisting(App.MutexName, out var m))
            {
                m.Dispose();
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(App.PendingOpenFile, path);
                EventWaitHandle.OpenExisting(App.SignalName).Set();
            }
            else
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { path }, UseShellExecute = false });
            }
            return Result(TextContent($"Opened in SnipSnip: {path}"));
        }

        // ---------------------------------------------------------------- Win32

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        static string WindowTitle(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static int ProcessId(IntPtr h)
        {
            GetWindowThreadProcessId(h, out uint pid);
            return (int)pid;
        }

        static string ProcessName(IntPtr h)
        {
            try { return Process.GetProcessById(ProcessId(h)).ProcessName; }
            catch { return ""; }
        }
    }
}
