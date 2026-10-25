using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SnipSnip
{
    /// <summary>Übersicht aller Studio-Projekte: öffnen, umbenennen, als Datei speichern, löschen.</summary>
    public partial class ProjectsWindow : Window
    {
        public sealed class Item : INotifyPropertyChanged
        {
            public string Dir { get; init; } = "";
            public string Title { get; set; } = "";
            public string Info { get; init; } = "";
            public DateTime Created { get; init; }
            BitmapSource? thumb;
            public BitmapSource? Thumb
            {
                get => thumb;
                set { thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
            }
            public void Renamed(string t)
            {
                Title = t;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        readonly ObservableCollection<Item> items = new();
        List<Item> all = new();

        public ProjectsWindow()
        {
            InitializeComponent();
            List.ItemsSource = items;
            Loaded += async (_, _) => await ReloadAsync();
            Activated += async (_, _) => { if (IsLoaded) await RefreshIfChangedAsync(); };
            Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files) App.Instance.OpenPath(f);
            };
            UpdateButtons();
        }

        string signature = "";

        async Task RefreshIfChangedAsync()
        {
            if (Signature() != signature) await ReloadAsync();
        }

        static string Signature()
        {
            try
            {
                return Directory.Exists(StudioProject.ProjectsRoot)
                    ? string.Join("|", Directory.GetDirectories(StudioProject.ProjectsRoot).Select(d => d + Directory.GetLastWriteTimeUtc(d).Ticks))
                    : "";
            }
            catch { return ""; }
        }

        public async Task ReloadAsync()
        {
            signature = Signature();
            string? selected = (List.SelectedItem as Item)?.Dir;
            all = await Task.Run(Scan);
            ApplyFilter();
            if (selected != null) List.SelectedItem = items.FirstOrDefault(i => i.Dir == selected);
            _ = LoadThumbsAsync(all.ToList());
        }

        static List<Item> Scan()
        {
            var list = new List<Item>();
            if (!Directory.Exists(StudioProject.ProjectsRoot)) return list;
            foreach (var d in Directory.GetDirectories(StudioProject.ProjectsRoot))
            {
                try
                {
                    string metaFile = Path.Combine(d, "meta.json");
                    if (!File.Exists(metaFile)) continue;
                    var meta = JsonSerializer.Deserialize<RecordingMeta>(File.ReadAllText(metaFile), AppSettings.Json)!;
                    string? title = null;
                    string editsFile = Path.Combine(d, "project.json");
                    if (File.Exists(editsFile))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(editsFile));
                        if (doc.RootElement.TryGetProperty("Title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
                    }
                    var created = Directory.GetCreationTime(d);
                    string kind = meta.SourceFile != null ? "Import" : meta.Complete ? "Aufnahme" : "unvollständig";
                    list.Add(new Item
                    {
                        Dir = d,
                        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(d) : title!,
                        Created = created,
                        Info = $"{created:dd.MM.yyyy HH:mm}  ·  {Fmt(meta.Duration)}" + (kind == "Aufnahme" ? "" : $"  ·  {kind}")
                    });
                }
                catch { /* kaputtes Projekt überspringen */ }
            }
            return list.OrderByDescending(i => i.Created).ToList();
        }

        /// <summary>Für die README-Bilder: synchron laden, erstes Projekt markieren.</summary>
        internal void DocsLoad()
        {
            signature = Signature();
            all = Scan();
            ApplyFilter();
            foreach (var it in all) it.Thumb = LoadThumb(Path.Combine(it.Dir, "thumb.jpg"));
            List.SelectedIndex = 0;
        }

        static BitmapSource? LoadThumb(string file)
        {
            if (!File.Exists(file)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(file);
            bi.DecodePixelWidth = 424;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }

        static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

        void ApplyFilter()
        {
            string q = SearchBox.Text.Trim();
            items.Clear();
            foreach (var i in all.Where(i => q.Length == 0 || i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Info.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                items.Add(i);
            CountText.Text = all.Count == 1 ? "1 Projekt" : $"{all.Count} Projekte";
            EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }

        /// <summary>Vorschaubilder: aus thumb.jpg im Projekt, sonst einmalig per FFmpeg erzeugen und ablegen.</summary>
        async Task LoadThumbsAsync(List<Item> list)
        {
            foreach (var it in list)
            {
                if (!IsVisible && IsLoaded) return;
                string thumbFile = Path.Combine(it.Dir, "thumb.jpg");
                try
                {
                    if (!File.Exists(thumbFile) && FFmpeg.IsAvailable)
                    {
                        var p = await Task.Run(() => StudioProject.Load(it.Dir));
                        if (File.Exists(p.ScreenFile))
                        {
                            double t = Math.Min(1.0, p.Meta.Duration / 2);
                            await FFmpeg.RunAsync(new[] { "-ss", t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", p.ScreenFile, "-frames:v", "1", "-vf", "scale=424:-2", thumbFile });
                        }
                    }
                    if (LoadThumb(thumbFile) is { } bi) it.Thumb = bi;
                }
                catch { }
            }
        }

        Item? Selected => List.SelectedItem as Item;

        void UpdateButtons()
        {
            bool sel = Selected != null;
            OpenBtn.IsEnabled = RenameBtn.IsEnabled = SaveBtn.IsEnabled = FolderBtn.IsEnabled = DeleteBtn.IsEnabled = sel;
            if (!sel) RenamePanel.Visibility = Visibility.Collapsed;
        }

        void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RenamePanel.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }

        void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
        }

        void List_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter: OpenSelected(); e.Handled = true; break;
                case Key.F2: Rename_Click(sender, e); e.Handled = true; break;
                case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            }
        }

        void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

        void OpenSelected()
        {
            if (Selected is not { } it) return;
            var open = StudioWindow.Find(it.Dir);
            if (open != null)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            App.Instance.OpenPath(it.Dir);
        }

        void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } it) return;
            RenameBox.Text = it.Title;
            RenamePanel.Visibility = Visibility.Visible;
            RenameBox.Focus();
            RenameBox.SelectAll();
        }

        void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RenamePanel.Visibility = Visibility.Collapsed; List.Focus(); e.Handled = true; }
        }

        void RenameOk_Click(object sender, RoutedEventArgs e) => CommitRename();

        void CommitRename()
        {
            if (Selected is not { } it) return;
            string name = RenameBox.Text.Trim();
            RenamePanel.Visibility = Visibility.Collapsed;
            if (name.Length == 0 || name == it.Title) return;
            try
            {
                // Ist das Projekt im Studio offen, dort umbenennen (das Studio speichert seine Fassung)
                var open = StudioWindow.Find(it.Dir);
                if (open != null) open.Rename(name);
                else
                {
                    var p = StudioProject.Load(it.Dir);
                    p.Edits.Title = name;
                    p.Save();
                }
                it.Renamed(name);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SnipSnip – Umbenennen"); }
        }

        async void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } it) return;
            var open = StudioWindow.Find(it.Dir);
            var p = open?.Project ?? StudioProject.Load(it.Dir);
            await StudioWindow.SaveProjectFileAsync(p, this);
        }

        void Folder_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is { } it) App.OpenFolder(it.Dir);
        }

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (Selected is not { } it) return;
            if (StudioWindow.Find(it.Dir) != null)
            {
                MessageBox.Show(this, "Das Projekt ist gerade im Studio geöffnet. Bitte zuerst das Studio-Fenster schließen.", "SnipSnip");
                return;
            }
            if (MessageBox.Show(this, $"„{it.Title}“ in den Papierkorb verschieben?\n\nAufnahme, Ton und alle Bearbeitungen des Projekts werden entfernt. " +
                                      "Bereits exportierte Videos bleiben erhalten.", "SnipSnip – Projekt löschen",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(it.Dir,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                await ReloadAsync();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "SnipSnip – Löschen fehlgeschlagen"); }
        }

        void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Projektdatei öffnen", Filter = $"SnipSnip-Projekt (*{ProjectArchive.Extension})|*{ProjectArchive.Extension}" };
            if (dlg.ShowDialog(this) == true) App.Instance.OpenPath(dlg.FileName);
        }

        void New_Click(object sender, RoutedEventArgs e) => App.Instance.StartCapture(this, video: true);
    }
}
