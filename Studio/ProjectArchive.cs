using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SnipSnip
{
    /// <summary>
    /// Studio-Projekte als einzelne Datei (*.snipsnip, ein ZIP): Aufnahme, Ton, Webcam, Eingaben, Bearbeitung –
    /// und alles, worauf das Projekt verweist (importiertes Video, Hintergrundbild, Musik, Logo).
    /// So lässt sich ein Projekt sichern, weitergeben und auf einem anderen PC weiterbearbeiten.
    /// </summary>
    internal static class ProjectArchive
    {
        public const string Extension = ".snipsnip";

        /// <summary>Dateien, die nur Zwischenspeicher sind und neu erzeugt werden.</summary>
        static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "preview.wav", "thumb.jpg" };

        static readonly string[] StylePaths = { "ImagePath", "MusicPath", "WatermarkPath" };

        public static bool IsArchive(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>Packt ein Projekt (vorher speichern!) in eine .snipsnip-Datei.</summary>
        public static async Task SaveAsync(StudioProject p, string file, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            p.Save();
            string tmp = file + ".tmp";
            await Task.Run(() =>
            {
                try
                {
                    var files = Directory.GetFiles(p.Dir, "*", SearchOption.AllDirectories)
                        .Where(f => !Skip.Contains(Path.GetFileName(f)))
                        .Where(f => !Path.GetFileName(f).Equals("meta.json", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals("project.json", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Verweise nach außen: ins Archiv aufnehmen und relativ eintragen
                    var external = new List<(string Source, string Entry)>();
                    string AddExternal(string path, string folder)
                    {
                        string entry = $"{folder}/{Path.GetFileName(path)}";
                        for (int i = 2; external.Any(e => e.Entry.Equals(entry, StringComparison.OrdinalIgnoreCase)); i++)
                            entry = $"{folder}/{Path.GetFileNameWithoutExtension(path)}_{i}{Path.GetExtension(path)}";
                        external.Add((path, entry));
                        return entry;
                    }

                    var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(p.Dir, "meta.json")))!.AsObject();
                    if (meta["SourceFile"]?.GetValue<string>() is string src && File.Exists(src))
                        meta["SourceFile"] = AddExternal(src, "source");

                    var edits = JsonSerializer.SerializeToNode(p.Edits, AppSettings.Json)!.AsObject();
                    if (edits["Style"] is JsonObject style)
                        foreach (var key in StylePaths)
                            if (style[key]?.GetValue<string>() is string sp && File.Exists(sp))
                                style[key] = AddExternal(sp, "assets");

                    long total = files.Sum(f => new FileInfo(f).Length) + external.Sum(e => new FileInfo(e.Source).Length) + 1;
                    long done = 0;
                    using (var fs = File.Create(tmp))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        void Text(string name, string content)
                        {
                            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                            using var w = new StreamWriter(e.Open());
                            w.Write(content);
                        }
                        Text("snipsnip.json", new JsonObject { ["format"] = "snipsnip-project", ["version"] = 1, ["title"] = p.Name, ["created"] = DateTime.Now.ToString("s") }.ToJsonString());
                        Text("meta.json", meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        Text("project.json", edits.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                        void Copy(string source, string entry)
                        {
                            ct.ThrowIfCancellationRequested();
                            // Videos und Bilder sind schon komprimiert – nur WAV & Co. lohnen sich
                            string ext = Path.GetExtension(source).ToLowerInvariant();
                            var level = ext is ".wav" or ".jsonl" or ".json" ? CompressionLevel.Fastest : CompressionLevel.NoCompression;
                            var e = zip.CreateEntry(entry, level);
                            using var input = File.OpenRead(source);
                            using var output = e.Open();
                            var buf = new byte[1 << 20];
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                output.Write(buf, 0, n);
                                done += n;
                                progress?.Report(Math.Min(1, done / (double)total));
                            }
                        }
                        foreach (var f in files) Copy(f, Path.GetRelativePath(p.Dir, f).Replace('\\', '/'));
                        foreach (var (source, entry) in external) Copy(source, entry);
                    }
                    File.Move(tmp, file, true);
                    progress?.Report(1);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;
                }
            }, ct);
        }

        /// <summary>Entpackt eine .snipsnip-Datei als neues Projekt und gibt den Projektordner zurück.</summary>
        public static async Task<string> OpenAsync(string file, IProgress<double>? progress = null)
        {
            return await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(file);
                if (zip.GetEntry("meta.json") == null) throw new InvalidDataException("Das ist keine SnipSnip-Projektdatei.");

                string baseName = Path.Combine(StudioProject.ProjectsRoot, StudioProject.SafeFileName(Path.GetFileNameWithoutExtension(file)));
                string dir = baseName;
                for (int i = 2; Directory.Exists(dir); i++) dir = $"{baseName}_{i}";
                Directory.CreateDirectory(dir);
                try
                {
                    string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                    long total = zip.Entries.Sum(e => e.Length) + 1, done = 0;
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith('/')) continue;
                        string target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                        // Schutz vor manipulierten Archiven („../“)
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        e.ExtractToFile(target, true);
                        done += e.Length;
                        progress?.Report(done / (double)total);
                    }

                    // relative Verweise wieder absolut machen
                    string metaPath = Path.Combine(dir, "meta.json");
                    var meta = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
                    if (meta["SourceFile"]?.GetValue<string>() is string src && !Path.IsPathRooted(src))
                        meta["SourceFile"] = Path.GetFullPath(Path.Combine(dir, src));
                    File.WriteAllText(metaPath, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                    string editsPath = Path.Combine(dir, "project.json");
                    if (File.Exists(editsPath))
                    {
                        var edits = JsonNode.Parse(File.ReadAllText(editsPath))!.AsObject();
                        if (edits["Style"] is JsonObject style)
                            foreach (var key in StylePaths)
                                if (style[key]?.GetValue<string>() is string sp && !Path.IsPathRooted(sp))
                                    style[key] = Path.GetFullPath(Path.Combine(dir, sp));
                        File.WriteAllText(editsPath, edits.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    File.Delete(Path.Combine(dir, "snipsnip.json"));
                    // gilt als vollständig: kein Wiederherstellungs-Hinweis
                    Directory.SetCreationTime(dir, DateTime.Now);
                    return dir;
                }
                catch
                {
                    try { Directory.Delete(dir, true); } catch { }
                    throw;
                }
            });
        }
    }
}
