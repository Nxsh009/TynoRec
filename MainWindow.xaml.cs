using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Media;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TynoRec
{
    record Preset(string Name, int H, int Fps, int Mbps);

    class Cfg
    {
        public int ClipMod { get; set; } = 0;
        public string[] ExtraGames { get; set; } = Array.Empty<string>();   // noms de processus de jeux en plus (sans .exe) : l'amélioration des clips attend leur fermeture
        public int ClipVk { get; set; } = 0x77;
        public int RecMod { get; set; } = 0;
        public int RecVk { get; set; } = 0x78;
        public int Mode { get; set; } = 1;
        public int Res { get; set; } = 720;
        public int Fps { get; set; } = 60;
        public int Mbps { get; set; } = 15;
        public int GpuIdx { get; set; } = -1;
        public string Encoder { get; set; } = "GPU";
        public string Codec { get; set; } = "h264";
        public int Ver { get; set; } = 0;
        public int Monitor { get; set; } = 0;
        public int BufSec { get; set; } = 30;
        public string Audio { get; set; } = "";
        public int Noise { get; set; } = 2;           // réduction de bruit micro : 0 off, 1 légère, 2 moyenne, 3 forte
        public int Vibrance { get; set; } = 40;       // couleurs éclatantes des clips (0 = off, 100 = max), comme l'éclat numérique NVIDIA
        public int DfAtten { get; set; } = 45;        // limite d'atténuation de l'IA en dB (plus bas = voix mieux préservée, un peu plus de bruit ; 100 = max)
        public int AudioOffsetMs { get; set; } = 0;   // réglage fin de synchro micro (+ = micro plus tard), modifiable dans config.json
        public string Out { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TynoRec");
        public string FFmpeg { get; set; } = "ffmpeg.exe";
        public bool Mouse { get; set; } = true;
        public bool Sound { get; set; } = true;
        public bool AutoCap { get; set; } = true;
        public int AfterSec { get; set; } = 10;
        public string[] GpuCache { get; set; }
        public bool StartWin { get; set; } = false;
        public bool StartMin { get; set; } = false;
        public int AutoDelDays { get; set; } = 0;     // 0 = jamais ; sinon supprime les clips (hors favoris) plus vieux que N jours
        public bool CloudSync { get; set; } = true;     // sauvegarde des clips dans le compte
        public bool TrayTip { get; set; } = false;       // info "TynoRec reste en arrière-plan" déjà montrée
        public string EncQ { get; set; } = "balanced";   // qualité d'encodage GPU : "perf" (le plus léger) | "balanced" | "quality" ; repli automatique sur "perf" si le pilote refuse
        public string Capture { get; set; } = "wgc";   // "wgc" = Windows Graphics Capture (essayé en premier, repli auto sur ddagrab) ; "dda" = ddagrab uniquement
    }

    class Opt { public string T { get; set; } public Brush C { get; set; } = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xFA)); public object V { get; set; } public override string ToString() => T; }

    class ClipMeta { public string Game { get; set; } public double Dur { get; set; } public bool Fav { get; set; } public string Uid { get; set; } public string Title { get; set; } public bool EnhC { get; set; } public bool EnhD { get; set; } }

    class Vid : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void N(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public string Name { get; set; } public string Path { get; set; } public string Game { get; set; } public string Info { get; set; }
        string title = ""; public string Title { get => title; set { title = value ?? ""; N(nameof(Title)); } }
        ImageSource ic; public ImageSource Icon { get => ic; set { ic = value; N(nameof(Icon)); } }
        public string Shown => string.IsNullOrWhiteSpace(Title) ? Game : Title;
        public string Sub => Game + "  •  " + Info;
        public string Letter => string.IsNullOrEmpty(Game) ? "?" : Game.Substring(0, 1).ToUpper();
        string dur = ""; public string Duration { get => dur; set { dur = value; N(nameof(Duration)); } }
        bool sel, fav; public bool Sel { get => sel; set { sel = value; N(nameof(Sel)); } } public bool Fav { get => fav; set { fav = value; N(nameof(Fav)); } }
        ImageSource th; public ImageSource Thumb { get => th; set { th = value; N(nameof(Thumb)); } }
    }

    // Tue ffmpeg automatiquement si TynoRec plante ou est fermé de force (sinon il continue de capturer en fond et mange le GPU).
    static class JobKill
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);

        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)]
        struct Basic
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinWs, MaxWs; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct Ext { public Basic B; public IoCounters Io; public UIntPtr ProcMem, JobMem, PeakProc, PeakJob; }

        static IntPtr job;

        public static void Add(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var ext = new Ext(); ext.B.LimitFlags = 0x2000;
                    int len = Marshal.SizeOf<Ext>(); var ptr = Marshal.AllocHGlobal(len);
                    try { Marshal.StructureToPtr(ext, ptr, false); SetInformationJobObject(job, 9, ptr, (uint)len); }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }
    }

    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        Preset Cur() => cfg.Mode switch
        {
            0 => new("Faible", 360, 24, 3),
            1 => new("Standard", 720, 60, 10),
            2 => new("Haute qualité", 1080, 60, 20),
            _ => new("Personnalisé", cfg.Res, cfg.Fps, cfg.Mbps)
        };
        bool loadingQ;

        Cfg cfg;
        readonly string cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TynoRec", "config.json");
        readonly string bufDir = Path.Combine(Path.GetTempPath(), "TynoRecBuf");
        Process cap;
        DateTime? recStart;
        DateTime capStart;
        readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
        const int SegSec = 2;
        // Audio capturé dans un processus ffmpeg SÉPARÉ : un vrai micro (dshow) dans le même ffmpeg que la capture d'écran
        // faisait perdre des images (doublons/saccades). Les deux flux sont recollés à l'export avec un décalage mesuré.
        readonly string audDir = Path.Combine(Path.GetTempPath(), "TynoRecBufA");
        Process audP;
        readonly object epochLock = new();
        double vEpoch = double.NaN, aEpoch = double.NaN;
        readonly Queue<double> vq = new(), aq = new();
        // statistiques de capture vidéo (images, doublées, perdues) relevées toutes les 0,5 s : sert à détecter un "120 fps" qui n'en est pas un
        readonly Queue<(long f, long d, long x)> vstat = new(); long sFr, sDup, sDrop;
        readonly List<DateTime> audCrashes = new();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TynoRec");
        string metaPath => Path.Combine(dataDir, "meta.json");
        string thumbDir => Path.Combine(dataDir, "thumbs");
        Dictionary<string, ClipMeta> meta = new();
        string lastGame = "Bureau";
        readonly DispatcherTimer pt = new() { Interval = TimeSpan.FromMilliseconds(50) };
        Vid cur; bool playing, seeking, libBusy, libAgain;
        string[] gpus = Array.Empty<string>();
        volatile bool gpuDone;
        Task gpuTask;
        bool starting, recAfterStart, orphansDone, hkBusy;
        int gen, exporting, libLimit = 60;
        string runSig = "", capHint = "", flashText = "";
        DateTime flashUntil;
        readonly DispatcherTimer applyT = new() { Interval = TimeSpan.FromMilliseconds(600) };
        readonly DispatcherTimer flashT = new() { Interval = TimeSpan.FromSeconds(4) };
        readonly Dictionary<string, ImageSource> thumbCache = new();

        public MainWindow()
        {
            InitializeComponent();
            var avTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            avTimer.Tick += (a, b) => { if (UiLive) AvRefresh(); };
            avTimer.Start();
            SourceInitialized += (a2, b2) =>
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);      // coins arrondis
            };
            StateChanged += (a, b) =>
            {
                BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                maxIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            };
            try { cfg = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(cfgPath)); } catch { }
            cfg ??= new Cfg();
            SanitizeCfg();
            try { meta = JsonSerializer.Deserialize<Dictionary<string, ClipMeta>>(File.ReadAllText(metaPath)) ?? new(); } catch { }
            if (cfg.GpuCache != null && cfg.GpuCache.Length > 0) { gpus = cfg.GpuCache; gpuDone = true; }
            pt.Tick += (_, _) => PlayerTick();
            applyT.Tick += (_, _) => { applyT.Stop(); ApplyChange(); };
            flashT.Tick += (_, _) => { flashT.Stop(); Refresh(); };
            tick.Tick += (_, _) => Housekeeping();
            InitQual();
            LoadUi();
            verText.Text = "TynoRec v" + Updater.Current.ToString(3);   // version affichée en bas des Paramètres
            gpuTask = DetectGpusAsync();
            Loaded += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                HwndSource.FromHwnd(h).AddHook(Hook);
                IntroFx();
                InitTray();
                EnsureStartMenuShortcut();
                if (Environment.GetCommandLineArgs().Contains("--min")) HideToTray(true);   // lancement avec Windows : directement dans la zone de notification
                else WindowState = WindowState.Maximized;   // plein écran au lancement
                RegHotkeys();
                Refresh();
                InitAccount();
                RecentRefresh();
                BootCapture();
                _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.Invoke(AutoClean));
            };
            Closing += OnClosing;
            IsVisibleChanged += (_, _) => OnVisibility();
            StateChanged += (_, _) => OnVisibility();
            LibRefresh(null, null);
        }

        // Premier lancement : si ffmpeg est absent, on le télécharge (une fois) avant de démarrer la capture automatique.
        async void BootCapture()
        {
            try { var lg = Path.Combine(dataDir, "ffmpeg.log"); if (File.Exists(lg) && new FileInfo(lg).Length > 1_000_000) File.Delete(lg); } catch { }   // journal limité à ~1 Mo
            try { foreach (var f in Directory.GetFiles(cfg.Out, "*.mp4.enh")) File.Delete(f); foreach (var f in Directory.GetFiles(cfg.Out, "*.enh.mp4")) File.Delete(f); } catch { }   // restes d'une amélioration interrompue
            if (!Updater.HasFFmpeg(cfg.FFmpeg, dataDir)) await Updater.EnsureFFmpegAsync(dataDir);
            try
            {
                foreach (var kv in meta.ToList())
                {
                    if (!kv.Value.EnhC && !kv.Value.EnhD) continue;
                    var pth = Path.Combine(cfg.Out, kv.Key);
                    if (File.Exists(pth)) { lock (pendingEnh) pendingEnh.Add((pth, kv.Value.EnhC, kv.Value.EnhD)); }
                    else { kv.Value.EnhC = false; kv.Value.EnhD = false; }
                }
                RunEnhance();
            }
            catch { }
            if (cfg.AutoCap) StartCapture();
        }

        // L'interface ne travaille que si on la voit. Cachée / réduite : plus de mise à jour d'écran, animations coupées, mode éco, mémoire rendue à Windows.
        bool UiLive => IsVisible && WindowState != WindowState.Minimized;

        void OnVisibility()
        {
            bool live = UiLive;
            Perf.Eco(!live);
            dotFxState = "";
            if (live) { Refresh(); AvRefresh(); RecentRefresh(); LibRefresh(null, null); }
            else
            {
                try { dot.BeginAnimation(OpacityProperty, null); } catch { }
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { if (!UiLive) Task.Run(Perf.Trim); })));
            }
        }

        void SanitizeCfg()
        {
            var d = new Cfg();
            cfg.Audio = (cfg.Audio ?? "").Replace("\"", "").Trim();
            if (string.IsNullOrWhiteSpace(cfg.FFmpeg)) cfg.FFmpeg = "ffmpeg.exe";
            if (string.IsNullOrWhiteSpace(cfg.Out)) cfg.Out = d.Out;
            try { Directory.CreateDirectory(cfg.Out); } catch { cfg.Out = d.Out; try { Directory.CreateDirectory(cfg.Out); } catch { } }
            if (cfg.Mode < 0 || cfg.Mode > 3) cfg.Mode = 1;
            if (cfg.Res < 144) cfg.Res = 720;
            if (cfg.Fps < 1) cfg.Fps = 60;
            if (cfg.Mbps < 1) cfg.Mbps = 10;
            if (cfg.Monitor < 0) cfg.Monitor = 0;
            cfg.Vibrance = Math.Clamp(cfg.Vibrance, 0, 100);
            if (cfg.BufSec < 5) cfg.BufSec = 30;
            if (cfg.AfterSec < 0) cfg.AfterSec = 0;
            if (cfg.Ver < 2) { cfg.Codec = "h264"; cfg.Ver = 2; }   // v2 : h264 par défaut (moins de lag, lecture fluide)
            if (cfg.Ver < 3) { if (cfg.Vibrance == 80) cfg.Vibrance = 40; cfg.Ver = 3; }   // v3 : couleurs plus naturelles (l'ancien réglage par défaut était trop saturé)
            cfg.Codec = cfg.Codec == "hevc" ? "hevc" : "h264";
            cfg.Encoder = cfg.Encoder == "CPU" ? "CPU" : "GPU";
            if (cfg.EncQ != "perf" && cfg.EncQ != "quality") cfg.EncQ = "balanced";
            cfg.Capture = cfg.Capture == "dda" ? "dda" : "wgc";
        }

        void OnClosing(object s, CancelEventArgs e)
        {
            if (!exiting) { e.Cancel = true; HideToTray(); return; }   // croix / Alt+F4 : TynoRec reste en arrière-plan (Quitter = clic droit sur l'icône)
            if (recStart != null && !Confirm("Quitter TynoRec ?", "Un enregistrement est en cours, il sera perdu si tu quittes maintenant.", "Quitter", "⏺"))
            { exiting = false; e.Cancel = true; return; }
            RemoveTray();
            tick.Stop(); applyT.Stop(); gen++;
            var c = cap; cap = null;
            try { c?.Kill(); } catch { }
            try { audP?.Kill(); } catch { }
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, 1); UnregisterHotKey(h, 2);
            Persist(); SaveMeta();
        }

        IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312) { if (w.ToInt32() == 1) ToggleRec(null, null); else SaveClip(null, null); handled = true; }
            if (TrayHook(msg, l)) handled = true;
            return IntPtr.Zero;
        }

        void LoadUi()
        {
            tOut.Text = cfg.Out; tFF.Text = cfg.FFmpeg; cMouse.IsChecked = cfg.Mouse; cSound.IsChecked = cfg.Sound; cAuto.IsChecked = cfg.AutoCap; cCloud.IsChecked = cfg.CloudSync;
            cStartWin.IsChecked = cfg.StartWin; cStartMin.IsChecked = cfg.StartMin;
            var dl = new[] { O("Jamais", 0), O("Après 7 jours", 7), O("Après 30 jours", 30), O("Après 90 jours", 90) };
            cDel.ItemsSource = dl; cDel.SelectedItem = dl.FirstOrDefault(o => (int)o.V == cfg.AutoDelDays) ?? dl[0];
        }

        // écriture atomique : un plantage en plein milieu ne corrompt plus le fichier
        static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }

        void Persist() { try { WriteAtomic(cfgPath, JsonSerializer.Serialize(cfg)); } catch { } }

        void SaveSettings(object s, RoutedEventArgs e)
        {
            var outDir = tOut.Text.Trim().Trim('"');
            try
            {
                if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException();
                Directory.CreateDirectory(outDir); cfg.Out = outDir;
            }
            catch { MessageBox.Show("Dossier des clips invalide : l'ancien est conservé.", "TynoRec"); }
            var ff = tFF.Text.Trim().Trim('"'); cfg.FFmpeg = ff.Length == 0 ? "ffmpeg.exe" : ff;
            cfg.Mouse = cMouse.IsChecked == true; cfg.Sound = cSound.IsChecked == true; cfg.AutoCap = cAuto.IsChecked == true; cfg.CloudSync = cCloud.IsChecked == true;
            cfg.StartWin = cStartWin.IsChecked == true; cfg.StartMin = cStartMin.IsChecked == true;
            cfg.AutoDelDays = (cDel.SelectedItem as Opt)?.V is int dd ? dd : 0;
            ApplyAutoStart();
            LoadUi(); Persist(); QueueApply(); LibRefresh(null, null);
            Toast("✔ Réglages enregistrés"); InfoPopup("Réglages enregistrés", "Tes modifications sont prises en compte.", "✔"); QueueSync();
        }

        void HomeQual(object s, RoutedEventArgs e) => Nav(nQual, e);
        static SolidColorBrush HexBr(string h) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));

        void BuildWeek(List<FileInfo> all)
        {
            weekGrid.Children.Clear();
            var fr = new CultureInfo("fr-FR");
            var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)).ToList();
            var counts = days.Select(d => all.Count(f => f.LastWriteTime.Date == d)).ToList();
            int max = Math.Max(1, counts.Max());
            for (int i = 0; i < 7; i++)
            {
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = counts[i] > 0 ? counts[i].ToString() : "", FontSize = 11, Foreground = HexBr("#A3AABC"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
                sp.Children.Add(new Border { Width = 26, Height = Math.Max(4, 70.0 * counts[i] / max), CornerRadius = new CornerRadius(6), Background = HexBr(i == 6 ? "#4C8DFF" : "#284A8A"), HorizontalAlignment = HorizontalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = fr.DateTimeFormat.GetAbbreviatedDayName(days[i].DayOfWeek).TrimEnd('.'), FontSize = 11, Foreground = HexBr("#8B93A7"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
                Grid.SetColumn(sp, i); weekGrid.Children.Add(sp);
            }
            int tot = counts.Sum(); weekTot.Text = tot + (tot > 1 ? " clips" : " clip");
        }

        void BuildTop(List<FileInfo> all)
        {
            topGames.Children.Clear();
            var groups = all.Select(f => meta.TryGetValue(f.Name, out var m) ? m.Game : null).Where(g => !string.IsNullOrEmpty(g) && g != "Capture").GroupBy(g => g).OrderByDescending(g => g.Count()).Take(4).ToList();
            topEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int mx = groups.Count > 0 ? groups[0].Count() : 1;
            foreach (var gr in groups)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var ic = GameIcon(gr.Key);
                UIElement inner = ic != null ? new Image { Source = ic, Margin = new Thickness(4) } : new TextBlock { Text = gr.Key.Substring(0, 1).ToUpper(), FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var ib = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = HexBr("#262D3C"), Margin = new Thickness(0, 0, 12, 0), Child = inner };
                var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                mid.Children.Add(new TextBlock { Text = gr.Key, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 5) });
                double fr = (double)gr.Count() / mx;
                var bar = new Grid { Height = 4 };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fr, GridUnitType.Star) }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fr, GridUnitType.Star) });
                var track = new Border { CornerRadius = new CornerRadius(2), Background = HexBr("#1F2430") }; Grid.SetColumnSpan(track, 2); bar.Children.Add(track);
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = HexBr("#4C8DFF") });
                mid.Children.Add(bar);
                var cnt = new TextBlock { Text = gr.Count().ToString(), FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(mid, 1); Grid.SetColumn(cnt, 2);
                row.Children.Add(ib); row.Children.Add(mid); row.Children.Add(cnt); topGames.Children.Add(row);
            }
        }

        void BuildChips()
        {
            qChips.Children.Clear();
            foreach (var t in new[] { cfg.Res + "p", cfg.Fps + " FPS", cfg.Mbps + " Mbps", (cfg.Codec ?? "h264").ToUpper(), cfg.Encoder == "CPU" ? "CPU" : "GPU" })
                qChips.Children.Add(new Border { Background = HexBr("#1B202B"), BorderBrush = HexBr("#2A3141"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 8, 0), Child = new TextBlock { Text = t, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0) } });
        }

        void HomeSeeAll(object s, RoutedEventArgs e) => Nav(nLib, e);
        void RecentOpen(object s, MouseButtonEventArgs e) { if (((FrameworkElement)s).DataContext is Vid v) PlayClip(v); }

        void RecentRefresh()
        {
            try
            {
                var all = new DirectoryInfo(cfg.Out).GetFiles("*.mp4").Where(f => !meta.TryGetValue(f.Name, out var q0) || q0.Uid == null || q0.Uid == Cloud.UserId).ToList();
                var list = all.OrderByDescending(f => f.LastWriteTime).Take(3).Select(f =>
                {
                    meta.TryGetValue(f.Name, out var mm); thumbCache.TryGetValue(f.Name, out var th);
                    if (th == null) { var tp = Path.Combine(thumbDir, f.Name + ".jpg"); if (File.Exists(tp)) try { th = LoadImg(tp); thumbCache[f.Name] = th; } catch { } }
                    return new Vid { Name = f.Name, Path = f.FullName, Game = mm?.Game ?? "Capture", Title = mm?.Title ?? "", Icon = GameIcon(mm?.Game),
                        Info = $"{mm?.Game ?? "Capture"}  •  {Ago(f.LastWriteTime)}", Duration = mm != null && mm.Dur > 0 ? Fmt(mm.Dur) : "", Thumb = th };
                }).ToList();
                rcList.ItemsSource = list; rcEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                rcToday.Text = all.Count(f => f.LastWriteTime.Date == DateTime.Today).ToString();
                rcSize.Text = (all.Sum(f => f.Length) / 1073741824.0).ToString("0.0") + " Go";
                BuildWeek(all); BuildTop(all); BuildChips();
                var tips = new[] { "Appuie sur ta touche de clip juste après une belle action : TynoRec sauve aussi les secondes d'avant.", "Donne un titre à tes clips : clique sur « Sans titre » dans la bibliothèque.", "Clique sur le carré du logo d'un clip pour choisir le logo officiel du jeu.", "Les clips en favori ★ ne sont jamais supprimés par le nettoyage automatique.", "Active « Lancer avec Windows » dans Paramètres pour ne jamais rater un clip.", "Le niveau Maximum IA du micro supprime très bien le bruit de fond." };
                tipTxt.Text = tips[DateTime.Now.DayOfYear % tips.Length];
            }
            catch { }
        }

        // Raccourci dans le menu Démarrer : TynoRec devient trouvable dans la recherche Windows et la liste des applications (créé / corrigé à chaque lancement)
        void EnsureStartMenuShortcut()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || exe.Replace('/', '\\').Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)) return;   // build de développement : pas de raccourci
                var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "TynoRec.lnk");
                var t = Type.GetTypeFromProgID("WScript.Shell"); if (t == null) return;
                var sh = Activator.CreateInstance(t);
                var sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                var st = sc.GetType();
                if (File.Exists(lnk))
                {
                    var cur = st.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) as string;
                    if (string.Equals(cur, exe, StringComparison.OrdinalIgnoreCase)) return;   // déjà à jour
                }
                void Set(string n, string v) => st.InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { v });
                Set("TargetPath", exe); Set("WorkingDirectory", Path.GetDirectoryName(exe)); Set("Description", "TynoRec - enregistrement et clips"); Set("IconLocation", exe + ",0");
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }
        }

        void ApplyAutoStart()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (k == null) return;
                if (cfg.StartWin) k.SetValue("TynoRec", "\"" + Environment.ProcessPath + "\"" + (cfg.StartMin ? " --min" : ""));
                else k.DeleteValue("TynoRec", false);
            }
            catch { }
        }

        async Task StoreInfo()
        {
            setStore.Text = "Calcul de l'espace utilisé…";
            var dir = cfg.Out;
            var (n, b) = await Task.Run(() => { try { var fs = new DirectoryInfo(dir).GetFiles("*.mp4"); return (fs.Length, fs.Sum(f => f.Length)); } catch { return (0, 0L); } });
            setStore.Text = $"{n} clip{(n > 1 ? "s" : "")}  ·  {b / 1073741824.0:0.0} Go utilisés";
        }

        void AutoClean()
        {
            if (cfg.AutoDelDays <= 0) return;
            try
            {
                var lim = DateTime.Now.AddDays(-cfg.AutoDelDays); int n = 0;
                foreach (var f in new DirectoryInfo(cfg.Out).GetFiles("*.mp4"))
                {
                    if (f.LastWriteTime >= lim) continue;
                    if (meta.TryGetValue(f.Name, out var m) && (m.Fav || (m.Uid != null && m.Uid != Cloud.UserId))) continue;
                    try { f.Delete(); File.Delete(Path.Combine(thumbDir, f.Name + ".jpg")); meta.Remove(f.Name); CloudDelete(f.Name); n++; } catch { }
                }
                if (n > 0) { SaveMeta(); LibRefresh(null, null); Toast($"🧹 {n} ancien{(n > 1 ? "s" : "")} clip{(n > 1 ? "s" : "")} supprimé{(n > 1 ? "s" : "")}"); }
            }
            catch { }
        }

        void BrowseOut(object s, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier des clips" };
            if (d.ShowDialog() == true) { tOut.Text = d.FolderName; _ = StoreInfo(); }
        }
        void BrowseFF(object s, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "ffmpeg|ffmpeg.exe|Programmes|*.exe" };
            if (d.ShowDialog() == true) tFF.Text = d.FileName;
        }
        void ClearThumbs(object s, RoutedEventArgs e)
        {
            try { if (Directory.Exists(thumbDir)) Directory.Delete(thumbDir, true); Directory.CreateDirectory(thumbDir); } catch { }
            thumbCache.Clear(); LibRefresh(null, null); Toast("✔ Cache des miniatures vidé");
        }
        void ResetSettings(object s, RoutedEventArgs e)
        {
            if (!Confirm("Réinitialiser les réglages ?", "Qualité, raccourcis et options reviennent aux valeurs par défaut. Tes clips ne sont pas touchés.", "Réinitialiser", "↺")) return;
            var gc = cfg.GpuCache; cfg = new Cfg { GpuCache = gc }; SanitizeCfg();
            ApplyAutoStart(); LoadUi(); Persist(); RegHotkeys(); QueueApply(); LibRefresh(null, null); _ = StoreInfo();
            Toast("✔ Réglages réinitialisés");
        }

        void Nav(object s, RoutedEventArgs e)
        {
            var t = (string)((Button)s).Tag; hkPanel.Visibility = Visibility.Collapsed; capPanel.Visibility = Visibility.Collapsed; pProfile.Visibility = Visibility.Collapsed;
            var target = t == "rec" ? (FrameworkElement)pRec : t == "qual" ? (FrameworkElement)pQual : t == "set" ? (FrameworkElement)pSet : (FrameworkElement)pLib;
            bool wasShown = target.Visibility == Visibility.Visible;
            pRec.Visibility = t == "rec" ? Visibility.Visible : Visibility.Collapsed;
            pQual.Visibility = t == "qual" ? Visibility.Visible : Visibility.Collapsed;
            pSet.Visibility = t == "set" ? Visibility.Visible : Visibility.Collapsed;
            pLib.Visibility = t == "lib" ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (b, k) in new[] { (nRec, "rec"), (nQual, "qual"), (nLib, "lib"), (nSet, "set") })
            {
                b.Background = k == t ? new SolidColorBrush(Color.FromRgb(0x16, 0x28, 0x4A)) : Brushes.Transparent;
                b.Foreground = k == t ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB6));
                b.BorderBrush = k == t ? new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)) : Brushes.Transparent;
            }
            if (!wasShown) Enter(target);
            if (t == "lib") LibRefresh(null, null);
            if (t == "rec") RecentRefresh();
            if (t == "set") { _ = FillSetCombos(); _ = StoreInfo(); }
        }

        static Opt O(string t, object v, Brush c = null) => new Opt { T = t, V = v, C = c ?? new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xFA)) };
        static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE5, 0x5B, 0x5B)), Org = new SolidColorBrush(Color.FromRgb(0xD9, 0x9A, 0x00));

        void InitQual()
        {
            cRes.ItemsSource = new[] { O("Low (360p)", 360), O("Medium (480p)", 480), O("High (720p)", 720), O("Full HD (1080p)", 1080), O("QHD 2K (1440p)", 1440, Red), O("UHD 4K (2160p)", 2160, Red) };
            cFps.ItemsSource = new[] { O("24", 24), O("30", 30), O("60", 60), O("120", 120, Red), O("144", 144, Red) };
            cBit.ItemsSource = new[] { O("3M", 3), O("5M", 5), O("7M", 7), O("10M", 10), O("15M", 15), O("20M", 20), O("25M", 25), O("30M", 30, Org), O("50M", 50, Org), O("70M", 70, Red), O("100M", 100, Red) };
            cEnc.ItemsSource = new[] { O("GPU", "GPU"), O("CPU", "CPU") };
            cCodec.ItemsSource = new[] { O("H264", "h264"), O("H265", "hevc") };
            cQ.ItemsSource = new[] { O("Performance", "perf"), O("Équilibré", "balanced"), O("Qualité", "quality") };
            FillGpuCombo();
            var bl = new List<Opt>();
            foreach (var sec in new[] { 10, 15, 20, 30, 45, 60, 90, 120, 180, 300 }) bl.Add(O(sec < 60 ? sec + " secondes" : sec % 60 == 0 ? (sec / 60 == 1 ? "1 minute" : sec / 60 + " minutes") : sec + " secondes", sec));
            if (!bl.Any(o => (int)o.V == cfg.BufSec)) bl.Add(O(cfg.BufSec + " secondes", cfg.BufSec));
            var al = new List<Opt>();
            foreach (var sec in new[] { 0, 5, 10, 15, 20, 30, 60 }) al.Add(O(sec == 0 ? "Aucune" : sec < 60 ? sec + " secondes" : "1 minute", sec));
            if (!al.Any(o => (int)o.V == cfg.AfterSec)) al.Add(O(cfg.AfterSec + " secondes", cfg.AfterSec));
            loadingQ = true; cBuf.ItemsSource = bl; cBuf2.ItemsSource = bl; cAfter.ItemsSource = al; cAfter2.ItemsSource = al; loadingQ = false;
            SyncOpts();
            ShowQual();
        }

        void FillGpuCombo()
        {
            var l = new List<Opt> { O("Auto", -1) };
            for (int i = 0; i < gpus.Length; i++) l.Add(O(gpus[i], i));
            loadingQ = true; cGpu.ItemsSource = l; cGpu.SelectedValue = cfg.GpuIdx; if (cGpu.SelectedIndex < 0) cGpu.SelectedIndex = 0; loadingQ = false;
        }

        void SyncOpts()
        {
            loadingQ = true;
            cBuf.SelectedValue = cfg.BufSec; cBuf2.SelectedValue = cfg.BufSec; cAfter.SelectedValue = cfg.AfterSec; cAfter2.SelectedValue = cfg.AfterSec;
            loadingQ = false;
        }

        void BufSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingQ || cfg == null) return;
            if (((ComboBox)s).SelectedValue is int v) { cfg.BufSec = v; SyncOpts(); Persist(); UpdateKeyLabels(); if (cap != null) Refresh(); }
        }

        void AfterSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingQ || cfg == null) return;
            if (((ComboBox)s).SelectedValue is int v) { cfg.AfterSec = v; SyncOpts(); Persist(); UpdateKeyLabels(); if (cap != null) Refresh(); }
        }

        void ShowQual()
        {
            loadingQ = true;
            cRes.SelectedValue = cfg.Res; cFps.SelectedValue = cfg.Fps; cBit.SelectedValue = cfg.Mbps;
            cEnc.SelectedValue = cfg.Encoder == "CPU" ? "CPU" : "GPU"; cCodec.SelectedValue = cfg.Codec == "h264" ? "h264" : "hevc"; cGpu.SelectedValue = cfg.GpuIdx; cQ.SelectedValue = cfg.EncQ;
            if (cGpu.SelectedIndex < 0) cGpu.SelectedIndex = 0;
            loadingQ = false;
            var cards = new[] { qLow, qStd, qHigh, qCus };
            for (int i = 0; i < 4; i++)
            {
                cards[i].BorderBrush = new SolidColorBrush(i == cfg.Mode ? Color.FromRgb(0x4C, 0x8D, 0xFF) : Color.FromRgb(0x26, 0x2D, 0x3C));
                cards[i].Background = new SolidColorBrush(i == cfg.Mode ? Color.FromRgb(0x14, 0x26, 0x4A) : Color.FromRgb(0x12, 0x15, 0x1C));
            }
            rowCustom.Visibility = cfg.Mode == 3 ? Visibility.Visible : Visibility.Collapsed;
            cGpu.IsEnabled = cfg.Encoder != "CPU"; cQ.IsEnabled = cfg.Encoder != "CPU";
            var p = Cur();
            presetInfo.Text = $"{p.H}p · {p.Fps} fps · {RateMbps(p)} Mb/s · ~{RateMbps(p) * 0.45:0} Mo/min";
        }

        void QMode(object s, RoutedEventArgs e)
        {
            int m = int.Parse((string)((Button)s).Tag);
            if (m == 3 && cfg.Mode != 3) { var p = Cur(); cfg.Res = p.H; cfg.Fps = p.Fps; cfg.Mbps = p.Mbps; }
            cfg.Mode = m; QApply();
        }

        void QSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingQ || cfg == null) return;
            if (cRes.SelectedValue is int r) cfg.Res = r;
            if (cFps.SelectedValue is int f) cfg.Fps = f;
            if (cBit.SelectedValue is int b) cfg.Mbps = b;
            if (cEnc.SelectedValue is string en) cfg.Encoder = en;
            if (cCodec.SelectedValue is string c) cfg.Codec = c;
            if (cQ.SelectedValue is string eq) cfg.EncQ = eq;
            if (cGpu.SelectedValue is int g) cfg.GpuIdx = g;
            QApply();
        }

        void QApply() { ShowQual(); Persist(); QueueApply(); }

        // ---------- ffmpeg ----------
        string Encoder()
        {
            string enc = cfg.Encoder == "CPU" ? "cpu" : DetectGpu(), c = cfg.Codec == "h264" ? "h264" : "hevc";
            if (enc == "cpu") return c == "hevc" ? "libx265" : "libx264";
            return $"{c}_{enc}";
        }

        // Couleurs éclatantes : la capture ne contient pas le réglage "Éclat numérique" du panneau NVIDIA (appliqué en sortie d'écran),
        // donc les clips paraissent plus ternes que ce qu'on voit. On rajoute saturation + vibrance à l'export du clip.
        string ColorArgs()
        {
            int v = Math.Clamp(cfg.Vibrance, 0, 100);
            if (v == 0) return "-c:v copy";
            var inv = CultureInfo.InvariantCulture;
            string sat = (1 + v / 100.0 * 0.30).ToString("0.###", inv), con = (1 + v / 100.0 * 0.03).ToString("0.###", inv), vib = (v / 100.0 * 0.5).ToString("0.###", inv);   // dosage doux : rendu naturel, pas d'effet "numérique" trop saturé
            string vf = $"-threads 1 -filter_threads 1 -vf \"eq=saturation={sat}:contrast={con},vibrance=intensity={vib},format=yuv420p\"";
            var p = Cur(); var enc = Encoder(); int b = Math.Max(4, RateMbps(p));
            string rate = $"-b:v {b}M -maxrate {b * 2}M -bufsize {b * 2}M -g {Math.Max(1, p.Fps) * 2}";
            string v2 = enc.EndsWith("nvenc") ? $"-c:v {enc} -preset p2 -rc vbr -cq 21 {rate}"
                      : enc.EndsWith("amf") ? $"-c:v {enc} -quality speed -rc vbr_peak {rate}"
                      : enc.EndsWith("qsv") ? $"-c:v {enc} -preset veryfast {rate}"
                      : enc == "libx265" ? $"-c:v {enc} -preset ultrafast -crf 22"
                      : $"-c:v {enc} -preset ultrafast -crf 20";
            if (enc.StartsWith("hevc") || enc == "libx265") v2 += " -tag:v hvc1";
            return vf + " " + v2;
        }

        // Détection des GPU en arrière-plan (PowerShell est lent) + cache dans la config pour les lancements suivants.
        Task DetectGpusAsync() => Task.Run(() =>
        {
            string[] g;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_VideoController).Name\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                g = o.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            }
            catch { g = Array.Empty<string>(); }
            bool changed = g.Length > 0 && !g.SequenceEqual(gpus);
            if (g.Length > 0) { gpus = g; cfg.GpuCache = g; }
            gpuDone = true;
            if (changed) Dispatcher.BeginInvoke(new Action(FillGpuCombo));
        });

        string DetectGpu()
        {
            var g = gpus;
            var n = (cfg.GpuIdx >= 0 && cfg.GpuIdx < g.Length ? g[cfg.GpuIdx] : string.Join("|", g)).ToLower();
            if (n.Contains("nvidia")) return "nvenc";
            if (n.Contains("amd") || n.Contains("radeon")) return "amf";
            if (n.Contains("intel")) return "qsv";
            if (g.Length == 0)   // détection impossible : on devine d'après les pilotes installés plutôt que de tomber sur l'encodage CPU
            {
                var sys = Environment.SystemDirectory;
                if (File.Exists(Path.Combine(sys, "nvcuda.dll"))) return "nvenc";
                if (File.Exists(Path.Combine(sys, "amfrt64.dll"))) return "amf";
            }
            return "cpu";
        }

        string FF
        {
            get
            {
                if (Path.IsPathRooted(cfg.FFmpeg)) return cfg.FFmpeg;
                var p = Path.Combine(AppContext.BaseDirectory, cfg.FFmpeg);              // à côté de l'exe
                if (File.Exists(p)) return p;
                var d = Path.Combine(dataDir, cfg.FFmpeg);                                // téléchargé au premier lancement
                return File.Exists(d) ? d : cfg.FFmpeg;                                   // sinon PATH
            }
        }

        string NvGpu()
        {
            var g = gpus; if (cfg.GpuIdx < 0 || cfg.GpuIdx >= g.Length || !g[cfg.GpuIdx].ToLower().Contains("nvidia")) return "";
            return $" -gpu {g.Take(cfg.GpuIdx).Count(x => x.ToLower().Contains("nvidia"))}";
        }

        string BuildLegacy()
        {
            var p = Cur(); var enc = Encoder(); int b = RateMbps(p);
            bool hw = !enc.StartsWith("lib");
            bool zeroCopy = p.H == 0 && (enc.EndsWith("nvenc") || enc.EndsWith("amf"));
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-stats_period", "0.5" };
            a.Add($"-f lavfi -i ddagrab=output_idx={cfg.Monitor}:framerate={p.Fps}:draw_mouse={(cfg.Mouse ? 1 : 0)}");
            bool aud = false;   // l'audio est géré par LaunchAudio()
            if (aud) a.Add($"-thread_queue_size 1024 -use_wallclock_as_timestamps 1 -f dshow -audio_buffer_size 80 -i audio=\"{cfg.Audio}\"");
            if (!zeroCopy)
                a.Add($"-vf \"hwdownload,format=bgra{(p.H > 0 ? $",scale=-2:{p.H}:flags=bilinear" : "")},format=nv12\"");
            string rate = $"-b:v {b}M -maxrate {b * 3 / 2}M -bufsize {b * 2}M -g {p.Fps * 2} -bf 2";
            string v = enc switch
            {
                var e when e.EndsWith("nvenc") => $"-c:v {enc} -preset p2 -tune ll -rc vbr{NvGpu()} {rate}",
                var e when e.EndsWith("amf") => $"-c:v {enc} -quality balanced -rc vbr_peak {rate}",
                var e when e.EndsWith("qsv") => $"-c:v {enc} -preset medium {rate}",
                "libx265" => $"-c:v {enc} -preset ultrafast {rate}",
                "libsvtav1" => $"-c:v {enc} -preset 10 {rate}",
                _ => $"-c:v {enc} -preset ultrafast -tune zerolatency {rate}"
            };
            a.Add(v);
            if (aud) a.Add("-af aresample=async=1000:first_pts=0 -c:a aac -b:a 192k");
            Directory.CreateDirectory(bufDir);
            a.Add("-fps_mode cfr -max_muxing_queue_size 2048 -max_interleave_delta 1000000");
            a.Add($"-f segment -segment_time {SegSec} -segment_format mpegts -strftime 1 \"{bufDir}\\%Y%m%d_%H%M%S.ts\"");
            return string.Join(" ", a);
        }

        // ---------- pipeline GPU (faible impact sur les FPS) ----------
        // Ancien pipeline : ddagrab -> hwdownload (GPU->RAM à chaque image) -> scale CPU -> ré-upload encodeur.
        // Nouveau : l'image ne quitte jamais la carte graphique (capture -> redimensionnement GPU -> encodeur matériel).
        static int Even(double v) { int i = (int)Math.Round(v); return i % 2 == 0 ? i : i + 1; }

        (int w, int h) NativeSize()
        {
            int w = 0, h = 0;
            try
            {
                var psi = new ProcessStartInfo(FF, $"-hide_banner -f lavfi -i ddagrab=output_idx={cfg.Monitor}:framerate=5 -frames:v 1 -f null -") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var pr = Process.Start(psi);
                try { pr.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var errTask = pr.StandardError.ReadToEndAsync();
                if (!pr.WaitForExit(5000)) { try { pr.Kill(); } catch { } }
                var m = Regex.Match(errTask.Result, @"Video:[^\r\n]*?(\d{3,5})x(\d{3,5})");
                if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }
            }
            catch { }
            return (w, h);
        }

        string BuildGpu(bool scale, int nw, int nh, bool wgc = false, int q = 0)   // q : 0 = performance (le plus léger), 1 = équilibré, 2 = qualité
        {
            var p = Cur(); var enc = Encoder(); int b = RateMbps(p);
            bool nv = enc.EndsWith("nvenc"), amf = enc.EndsWith("amf"), qsv = enc.EndsWith("qsv");
            var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-stats_period", "0.5" };
            // wgc : Windows Graphics Capture (gfxcapture) - ne se fait pas affamer par un jeu qui sature le GPU comme ddagrab
            if (wgc) a.Add($"-f lavfi -i gfxcapture=monitor_idx={cfg.Monitor}:capture_cursor={(cfg.Mouse ? 1 : 0)}:max_framerate={p.Fps}");
            else a.Add($"-f lavfi -i ddagrab=output_idx={cfg.Monitor}:framerate={p.Fps}:draw_mouse={(cfg.Mouse ? 1 : 0)}");
            bool aud = false;   // l'audio est géré par LaunchAudio()
            if (aud) a.Add($"-thread_queue_size 1024 -use_wallclock_as_timestamps 1 -f dshow -audio_buffer_size 80 -i audio=\"{cfg.Audio}\"");

            int th = p.H, tw = nh > 0 ? Even(th * nw / (double)nh) : -2;
            string vf = null;
            if (nv) vf = scale ? $"hwmap=derive_device=cuda,scale_cuda=w={tw}:h={th}:format=nv12:interp_algo=bilinear" : null;
            else if (amf) vf = scale ? $"scale_d3d11=width={tw}:height={th}:format=nv12" : null;
            else if (qsv) vf = scale ? $"hwmap=derive_device=qsv,format=qsv,vpp_qsv=w={tw}:h={th}:format=nv12" : "hwmap=derive_device=qsv,format=qsv";
            if (vf != null) a.Add($"-vf \"{vf}\"");

            string rate = $"-b:v {b}M -maxrate {b * 3 / 2}M -bufsize {b * 2}M -g {p.Fps * 2} -bf {(q > 0 ? 2 : 0)}";
            string v;
            // un replay buffer n'a pas besoin de latence ultra faible : l'encodeur matériel (bloc séparé des coeurs du jeu) peut prendre plus de temps par image pour une image plus propre
            if (q > 0 && nv) v = $"-c:v {enc} -preset {(q == 1 ? "p4" : "p6")} -tune hq -rc vbr -multipass {(q == 1 ? "qres" : "fullres")} -spatial-aq 1{(q == 2 ? " -temporal-aq 1" : "")}{NvGpu()} {rate}";
            else if (q > 0 && amf) v = $"-c:v {enc} -usage transcoding -quality {(q == 1 ? "balanced" : "quality")} -rc vbr_peak {rate}";
            else if (q > 0 && qsv) v = $"-c:v {enc} -preset {(q == 1 ? "medium" : "slow")} {rate}";
            else v = nv ? $"-c:v {enc} -preset p1 -tune ull -rc vbr -multipass disabled -rc-lookahead 0 -zerolatency 1{NvGpu()} {rate}"
                     : amf ? $"-c:v {enc} -usage ultralowlatency -quality speed -rc vbr_peak {rate}"
                     : qsv ? $"-c:v {enc} -preset veryfast -async_depth 1 {rate}"
                     : $"-c:v {enc} -preset ultrafast{(enc == "libx264" ? " -tune zerolatency" : "")} {rate}";
            a.Add(v);
            if (aud) a.Add("-af aresample=async=1000:first_pts=0 -c:a aac -b:a 192k");
            Directory.CreateDirectory(bufDir);
            a.Add("-fps_mode cfr -max_muxing_queue_size 2048 -max_interleave_delta 1000000");
            a.Add($"-f segment -segment_time {SegSec} -segment_format mpegts -strftime 1 \"{bufDir}\\%Y%m%d_%H%M%S.ts\"");
            return string.Join(" ", a);
        }

        // Paliers essayés dans l'ordre. Si ffmpeg refuse un palier (filtre absent, pilote, etc.), on passe automatiquement au suivant.
        List<(string name, string args)> tiers = new();
        int tier; string tierName = "";
        readonly List<DateTime> crashes = new();

        List<(string name, string args)> BuildTiers()
        {
            var l = new List<(string, string)>();
            var enc = Encoder();
            if (!enc.StartsWith("lib"))
            {
                var (nw, nh) = NativeSize();
                var p = Cur();
                var combos = new List<(string n, bool scale, bool wgc)>();
                if (cfg.Capture == "wgc")
                {
                    if (nh == 0 || p.H < nh) combos.Add(("GPU direct (WGC)", true, true));
                    combos.Add(("GPU résolution native (WGC)", false, true));
                }
                if (nh == 0 || p.H < nh) combos.Add(("GPU direct", true, false));
                combos.Add(("GPU résolution native", false, false));
                int ql = cfg.EncQ == "quality" ? 2 : cfg.EncQ == "balanced" ? 1 : 0;
                foreach (var c in combos)
                {
                    if (ql > 0) l.Add((c.n + " · " + (ql == 2 ? "qualité" : "équilibré"), BuildGpu(c.scale, nw, nh, c.wgc, ql)));   // si le pilote refuse ces réglages, on passe au palier suivant = les réglages d'origine
                    l.Add((c.n, BuildGpu(c.scale, nw, nh, c.wgc, 0)));
                }
            }
            l.Add(("compatibilité", BuildLegacy()));
            return l;
        }

        void ToggleCapture(object s, RoutedEventArgs e) { if (cap == null && !starting) StartCapture(); else StopCapture(); }

        // Les changements de réglages sont regroupés (600 ms) et ne redémarrent ffmpeg que si quelque chose a vraiment changé.
        void QueueApply() { applyT.Stop(); applyT.Start(); }

        string Sig() => $"{Cur()}|{cfg.Encoder}|{cfg.Codec}|{cfg.EncQ}|{cfg.GpuIdx}|{cfg.Monitor}|{cfg.Audio}|{cfg.Noise}|{cfg.Mouse}|{cfg.FFmpeg}";

        async void ApplyChange()
        {
            if (cap == null && !starting) return;
            if (recStart != null) return;          // appliqué à la fin de l'enregistrement en cours
            if (Sig() == runSig) return;
            await StopCaptureAsync();
            StartCapture();
        }

        // ---------- audio : processus séparé ----------
        // Débit réellement utile : à 120/144 fps il faut proportionnellement plus de débit qu'à 60 fps, sinon l'image devient floue/blocs dès que ça bouge.
        static int RateMbps(Preset p) => p.Fps > 60 ? Math.Min(100, (int)Math.Round(p.Mbps * p.Fps / 60.0)) : p.Mbps;

        // Compare les images demandées aux images réellement capturées (les autres sont des doublons / pertes) sur la dernière minute.
        string FpsWarn()
        {
            long f0, d0, x0, f1, d1, x1;
            lock (epochLock)
            {
                if (vstat.Count < 20) return null;
                (f0, d0, x0) = vstat.First(); (f1, d1, x1) = vstat.Last();
            }
            long frames = f1 - f0; if (frames < 200) return null;
            double ratio = ((d1 - d0) + (x1 - x0)) / (double)frames;
            if (ratio < 0.10) return null;
            int want = Cur().Fps, real = (int)Math.Round(want * (1 - Math.Min(0.95, ratio)));
            return $"⚠ seulement ~{real} images/s réelles sur {want} (écran/jeu/GPU)";
        }

        void Progress(string line, bool video)
        {
            if (line == null) return;
            if (video)
            {
                lock (epochLock)
                {
                    if (line.StartsWith("frame=") && long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fr)) sFr = fr;
                    else if (line.StartsWith("dup_frames=") && long.TryParse(line.AsSpan(11).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var du)) sDup = du;
                    else if (line.StartsWith("drop_frames=") && long.TryParse(line.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dr)) sDrop = dr;
                    else if (line.StartsWith("progress=")) { vstat.Enqueue((sFr, sDup, sDrop)); if (vstat.Count > 120) vstat.Dequeue(); }
                }
            }
            if (!line.StartsWith("out_time_us=")) return;
            if (!long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0) return;
            double e = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds - us / 1e6;   // horloge murale correspondant à t=0 du flux
            lock (epochLock)
            {
                var q = video ? vq : aq;
                q.Enqueue(e); if (q.Count > 40) q.Dequeue();
                var m = q.Min();
                if (video) vEpoch = m; else aEpoch = m;
            }
        }

        // Réduction de bruit : coupe les graves (ventilos, bureau), débruite en continu (souffle, PC) et ferme le micro entre les mots (clavier, bruits de fond).
        // Si "voice.rnnn" (modèle RNNoise) est à côté de l'exe, il est utilisé en plus : c'est le plus efficace pour ne garder que la voix.
        bool HasOwnDenoiser()
        {
            var d = (cfg.Audio ?? "").ToLowerInvariant();
            return d.Contains("broadcast") || d.Contains("krisp") || d.Contains("rtx voice") || d.Contains("noise cancel");
        }

        string NoiseHint()
        {
            if (HasOwnDenoiser()) return "Micro avec suppression de bruit détecté : le filtre de TynoRec est désactivé pour ne pas filtrer deux fois.";
            if (cfg.Noise == 4) return "L'IA (DeepFilterNet) nettoie l'audio quand tu sauvegardes le clip (quelques secondes). Aucun impact sur le jeu.";
            var g = string.Join(" ", gpus).ToLowerInvariant();
            if (g.Contains("rtx")) return "Astuce : NVIDIA Broadcast (gratuit) supprime encore mieux le clavier. Installe-le et choisis son micro virtuel ci-dessus.";
            if (g.Contains("radeon") || g.Contains("amd")) return "Astuce : AMD Noise Suppression (logiciel Adrenalin) fait aussi très bien. Active-le et choisis son micro virtuel ci-dessus.";
            return "";
        }

        string VoiceModel()
        {
            foreach (var d in new[] { dataDir, AppContext.BaseDirectory })
            { var m = Path.Combine(d, "voice.rnnn"); if (File.Exists(m)) return m; }
            return null;
        }

        // Après l'IA : juste un peu de présence (3 kHz) et d'air, sans compresseur (le gain du compresseur amplifiait trop et déformait la voix), limiteur de sécurité.
        const string VoiceClarity = "highpass=f=90,equalizer=f=3000:width_type=q:width=1:g=2,treble=g=1.5:f=7000,alimiter=limit=0.97";

        string DfExe()
        {
            foreach (var d in new[] { dataDir, AppContext.BaseDirectory })
            { var m = Path.Combine(d, "deep-filter.exe"); if (File.Exists(m)) return m; }
            return null;
        }

        bool dfDl;
        // DeepFilterNet (MIT / Apache-2.0) : IA de suppression de bruit bien plus forte que RNNoise. Téléchargée une fois (~12 Mo).
        async void EnsureDeepFilter()
        {
            if (dfDl || DfExe() != null) return;
            dfDl = true;
            try
            {
                Toast("⏳ Téléchargement du module anti-bruit IA…");
                Directory.CreateDirectory(dataDir);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                var b = await http.GetByteArrayAsync("https://github.com/Rikorose/DeepFilterNet/releases/download/v0.5.6/deep-filter-0.5.6-x86_64-pc-windows-msvc.exe");
                if (b.Length > 3_000_000 && b[0] == (byte)'M' && b[1] == (byte)'Z')
                {
                    var tmp = Path.Combine(dataDir, "deep-filter.tmp");
                    await File.WriteAllBytesAsync(tmp, b);
                    File.Move(tmp, Path.Combine(dataDir, "deep-filter.exe"), true);
                    Toast("✔ IA anti-bruit installée");
                    if (cap != null && recStart == null) LaunchAudio();
                }
                else Toast("✖ Module anti-bruit invalide");
            }
            catch { Toast("✖ Téléchargement impossible (voir deep-filter.exe à la main)"); }
            finally { dfDl = false; }
        }

        bool modelDl;
        // Télécharge une fois le modèle RNNoise "voix + bruits généraux" (clavier, PC, ventilos) puis relance l'audio pour l'utiliser.
        async void EnsureModel()
        {
            if (modelDl || VoiceModel() != null) return;
            modelDl = true;
            try
            {
                Directory.CreateDirectory(dataDir);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                foreach (var u in new[] {
                    "https://raw.githubusercontent.com/GregorR/rnnoise-models/master/leavened-quisling-2018-08-31/lq.rnnn",
                    "https://cdn.jsdelivr.net/gh/GregorR/rnnoise-models/leavened-quisling-2018-08-31/lq.rnnn" })
                {
                    try
                    {
                        var b = await http.GetByteArrayAsync(u);
                        if (b.Length > 100000 && Encoding.ASCII.GetString(b, 0, 12) == "rnnoise-nu m")
                        {
                            var tmp = Path.Combine(dataDir, "voice.rnnn.tmp");
                            await File.WriteAllBytesAsync(tmp, b);
                            File.Move(tmp, Path.Combine(dataDir, "voice.rnnn"), true);
                            Toast("✔ Modèle anti-bruit installé");
                            if (cap != null && recStart == null) LaunchAudio();
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            finally { modelDl = false; }
        }

        string AudioFilter()
        {
            var f = "aresample=async=1000:first_pts=0";
            if (cfg.Noise <= 0 || HasOwnDenoiser()) return f;
            if (cfg.Noise == 4 && DfExe() != null) return f + ",highpass=f=100";   // l'IA DeepFilterNet nettoie à la sauvegarde du clip   // micro déjà débruité (NVIDIA Broadcast, Krisp…) : on ne filtre pas deux fois
            string rn = null;
            if (cfg.Noise >= 2)
            {
                var m = VoiceModel();
                if (m != null && m.IndexOfAny(new[] { '\'', '"' }) < 0)
                    rn = "arnndn=m='" + m.Replace('\\', '/').Replace(":", "\\:") + "'";
            }
            string chain;
            if (rn != null)
                // modèle RNNoise présent : il fait le gros du travail. Forte ajoute seulement une porte très douce (-48 dB) pour les restes lointains entre les mots,
                // sans toucher à la voix (pas de débruitage spectral ni de porte dure qui coupaient la parole).
                chain = cfg.Noise == 2 ? $"highpass=f=100,{rn}"
                      : cfg.Noise == 3 ? $"highpass=f=100,{rn},agate=threshold=0.004:ratio=2:attack=10:release=400"
                      // Maximum : RNNoise en 2 passes (supprime nettement plus le clavier PENDANT que tu parles) + coupe les aigus des claquements
                      : $"highpass=f=150,{rn},{rn},lowpass=f=7500,agate=threshold=0.004:ratio=2:attack=10:release=400";
            else
                chain = cfg.Noise switch
                {
                    1 => "highpass=f=80,afftdn=nr=10:nf=-50:tn=1",
                    2 => "highpass=f=100,afftdn=nr=15:nf=-45:tn=1",
                    _ => "highpass=f=100,afftdn=nr=22:nf=-42:tn=1,agate=threshold=0.012:ratio=3:attack=8:release=300"
                };
            return f + "," + chain;
        }

        void LaunchAudio()
        {
            StopAudio();
            if (string.IsNullOrWhiteSpace(cfg.Audio)) return;
            if (cfg.Noise >= 2) EnsureModel();
            if (cfg.Noise == 4) EnsureDeepFilter();
            try
            {
                Directory.CreateDirectory(audDir);
                lock (epochLock) { aq.Clear(); aEpoch = double.NaN; }
                var args = $"-hide_banner -loglevel error -y -progress pipe:1 -stats_period 0.5 -thread_queue_size 1024 -use_wallclock_as_timestamps 1 " +
                           $"-f dshow -audio_buffer_size 80 -i audio=\"{cfg.Audio}\" -af \"{AudioFilter()}\" -c:a aac -b:a 192k " +
                           $"-f segment -segment_time {SegSec} -segment_format mpegts -strftime 1 \"{audDir}\\%Y%m%d_%H%M%S.ts\"";
                var psi = new ProcessStartInfo(FF, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var proc = Process.Start(psi);
                JobKill.Add(proc); Perf.NoThrottle(proc);
                try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                proc.ErrorDataReceived += (_, e) => { };
                proc.OutputDataReceived += (_, e) => Progress(e.Data, false);
                proc.BeginErrorReadLine(); proc.BeginOutputReadLine();
                audP = proc; var started = DateTime.Now;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => OnAudExit(proc, started)));
            }
            catch { audP = null; }
        }

        void OnAudExit(Process proc, DateTime started)
        {
            if (!ReferenceEquals(audP, proc)) return;   // arrêt voulu
            audP = null;
            if (cap == null) return;
            if ((DateTime.Now - started).TotalSeconds < 5) Toast("⚠ Micro introuvable ou occupé");
            audCrashes.RemoveAll(d => (DateTime.Now - d).TotalSeconds > 60);
            if (audCrashes.Count < 3) { audCrashes.Add(DateTime.Now); LaunchAudio(); }
        }

        void StopAudio()
        {
            var p = audP; audP = null;
            if (p == null) return;
            Task.Run(() =>
            {
                try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch { }
                try { if (!p.WaitForExit(2500)) p.Kill(); } catch { }
            });
        }

        // début (s) et durée (s) d'un fichier, lus via ffmpeg -i
        (double start, double dur) ProbeSD(string file)
        {
            try
            {
                var psi = new ProcessStartInfo(FF, $"-hide_banner -i \"{file}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                using var p = Process.Start(psi); var err = p.StandardError.ReadToEnd(); p.WaitForExit(5000);
                var m = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?), start: (-?\d+(?:\.\d+)?)");
                if (!m.Success) return (-1, 0);
                double d = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                return (double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), d);
            }
            catch { return (-1, 0); }
        }

        void KillOrphans()
        {
            try
            {
                var mine = Path.GetFullPath(FF);
                foreach (var p in Process.GetProcessesByName("ffmpeg"))
                {
                    try { if (string.Equals(p.MainModule?.FileName, mine, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        async void StartCapture()
        {
            if (cap != null) return;
            int my = ++gen; starting = true; Refresh();
            List<(string name, string args)> t;
            try
            {
                if (!gpuDone) await gpuTask;
                bool skipClean = exporting > 0;
                t = await Task.Run(() =>
                {
                    if (!orphansDone) { orphansDone = true; KillOrphans(); }
                    if (!skipClean)
                    {
                        try { if (Directory.Exists(bufDir)) foreach (var f in Directory.GetFiles(bufDir)) try { File.Delete(f); } catch { } } catch { }
                        try { if (Directory.Exists(audDir)) foreach (var f in Directory.GetFiles(audDir)) try { File.Delete(f); } catch { } } catch { }
                    }
                    return BuildTiers();
                });
            }
            catch (Exception ex)
            {
                if (my == gen) { starting = false; recAfterStart = false; Refresh(); MessageBox.Show("Impossible de préparer la capture : " + ex.Message, "TynoRec"); }
                return;
            }
            if (my != gen) return;            // remplacé par un démarrage / arrêt plus récent
            starting = false;
            crashes.Clear(); tiers = t; tier = 0; runSig = Sig();
            Launch();
            if (cap != null) LaunchAudio();
            if (recAfterStart)
            {
                recAfterStart = false;
                if (cap != null) { recStart = DateTime.Now; Snd("recon"); Refresh(); RecNotice("● Enregistrement démarré"); }
            }
        }

        void Launch()
        {
            var t = tiers[tier];
            try
            {
                var errs = new StringBuilder();
                var psi = new ProcessStartInfo(FF, t.args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                lock (epochLock) { vq.Clear(); vEpoch = double.NaN; vstat.Clear(); sFr = sDup = sDrop = 0; }
                var proc = Process.Start(psi);
                JobKill.Add(proc); Perf.NoThrottle(proc);
                try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; proc.PriorityBoostEnabled = true; } catch { }
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errs) { if (errs.Length < 4000) errs.AppendLine(e.Data); } };
                proc.BeginErrorReadLine();
                proc.OutputDataReceived += (_, e) => Progress(e.Data, true);
                proc.BeginOutputReadLine();
                var started = DateTime.Now;
                cap = proc; capStart = started; tierName = t.name;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => OnCapExit(proc, errs, started)));
                tick.Start();
            }
            catch (Exception ex) { MessageBox.Show("Impossible de lancer ffmpeg : " + ex.Message + "\nMets ffmpeg.exe à côté de l'app ou indique son chemin dans Paramètres."); cap = null; recAfterStart = false; }
            Refresh();
        }

        void OnCapExit(Process proc, StringBuilder errs, DateTime started)
        {
            if (!ReferenceEquals(cap, proc)) return;   // arrêt voulu (StopCapture)
            cap = null; tick.Stop();
            try { proc.WaitForExit(); } catch { }
            string err; lock (errs) err = errs.ToString().Trim();
            try
            {
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "ffmpeg.log"), $"{DateTime.Now} [{tiers[tier].name}]\n{err}\n\n");
            }
            catch { }
            bool quick = (DateTime.Now - started).TotalSeconds < 6;
            if (quick && tier + 1 < tiers.Count) { tier++; Launch(); return; }        // palier refusé -> palier suivant
            if (!quick && tiers[tier].name.Contains(" · ") && tier + 1 < tiers.Count) { tier++; Launch(); return; }   // réglages qualité trop lourds pour cette machine (plantage en cours de route) -> retour aux réglages d'origine, les plus légers
            if (!quick && tiers[tier].name.Contains("WGC")) { int nx = tiers.FindIndex(tier + 1, x => !x.name.Contains("WGC")); if (nx >= 0) { tier = nx; Launch(); return; } }   // la capture WGC a lâché en cours de route -> on repasse sur ddagrab
            if (!quick)                                                                // plantage en cours de route -> relance silencieuse (3 max / minute)
            {
                crashes.RemoveAll(d => (DateTime.Now - d).TotalSeconds > 60);
                if (crashes.Count < 3) { crashes.Add(DateTime.Now); Launch(); return; }
            }
            recStart = null; StopAudio(); Refresh();
            if (quick && err.Length > 0) MessageBox.Show(err, "ffmpeg");              // pas de popup en pleine partie pour un simple plantage
        }

        void StopCapture() { _ = StopCaptureAsync(); }

        async Task StopCaptureAsync()
        {
            gen++; starting = false; recStart = null; recAfterStart = false;
            StopAudio();
            var c = cap; cap = null; tick.Stop();
            Refresh();
            if (c == null) { RunEnhance(); return; }
            await Task.Run(() =>
            {
                try { c.StandardInput.Write("q"); c.StandardInput.Flush(); } catch { }
                try { if (!c.WaitForExit(2500)) c.Kill(); } catch { }
            });
            RunEnhance();
        }

        async void Housekeeping()
        {
            if (hkBusy) return;
            hkBusy = true;
            try
            {
                var c = cap; var started = capStart;
                bool rec = recStart != null, busy = exporting > 0; int keep = cfg.BufSec + cfg.AfterSec + 10;
                bool stale = await Task.Run(() =>
                {
                    CurrentGame();
                    var newest = DateTime.MinValue;
                    try
                    {
                        if (Directory.Exists(bufDir))
                        {
                            var limit = DateTime.Now.AddSeconds(-keep);
                            foreach (var fi in new DirectoryInfo(bufDir).EnumerateFiles("*.ts"))   // une seule énumération : les dates arrivent avec les noms (pas un appel disque par fichier)
                            {
                                var w = fi.LastWriteTime;
                                if (w > newest) newest = w;
                                if (!rec && !busy && w < limit) try { fi.Delete(); } catch { }   // jamais pendant un enregistrement ou un export
                            }
                        }
                    }
                    catch { }
                    try
                    {
                        if (!rec && !busy && Directory.Exists(audDir))
                        {
                            var limitA = DateTime.Now.AddSeconds(-keep);
                            foreach (var fi in new DirectoryInfo(audDir).EnumerateFiles("*.ts")) if (fi.LastWriteTime < limitA) try { fi.Delete(); } catch { }
                        }
                    }
                    catch { }
                    // chien de garde : ffmpeg tourne mais n'écrit plus rien (veille, UAC, changement de mode d'écran…)
                    var last = newest == DateTime.MinValue ? started : newest;
                    return c != null && (DateTime.Now - started).TotalSeconds > 15 && (DateTime.Now - last).TotalSeconds > 12;
                });
                if (UiLive) UpdateNow();
                if (stale && ReferenceEquals(cap, c)) { try { c.Kill(); } catch { } }   // OnCapExit relance proprement
            }
            finally { hkBusy = false; }
            if (UiLive) Refresh();
        }

        void Toast(string t)
        {
            if (DateTime.Now < notifPause && t.StartsWith("✔")) return;
            flashText = t; flashUntil = DateTime.Now.AddSeconds(4);
            flashT.Stop(); flashT.Start(); Refresh();
        }

        string Hint() => capHint.Length > 0 ? capHint : "Appuie sur une touche…";

        void UpdateNow()
        {
            bool on = cap != null;
            nowLbl.Text = on ? (recStart != null ? "Enregistrement en cours" : "Capture en cours") : "Capture inactive";
            nowLbl.Foreground = new SolidColorBrush(on ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0x8B, 0x93, 0xA7));
            nowGame.Text = on ? lastGame : "—";
            nowGame.Foreground = new SolidColorBrush(on ? Colors.White : Color.FromRgb(0x7A, 0x82, 0x96));
            if (capPanel.Visibility == Visibility.Visible) FillCap();
        }

        void FillCap()
        {
            bool on = cap != null;
            capToggle.Content = on ? "ON" : "OFF";
            capGame.Text = on ? lastGame : "Aucun jeu en capture";
            capState.Text = starting ? "Démarrage…" : on ? (recStart != null ? "● Enregistrement long en cours" : $"● Replay buffer actif ({cfg.BufSec} s)") : "Capture désactivée";
            capTile.BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x3D, 0xDC, 0x84) : Color.FromRgb(0x2A, 0x31, 0x41));
            capSrc.Text = screenNames != null && cfg.Monitor < screenNames.Count ? screenNames[cfg.Monitor] : cfg.Monitor == 0 ? "Écran principal" : $"Écran {cfg.Monitor}";
            var p = Cur(); capQual.Text = $"{p.H}p {p.Fps} FPS";
            capAud.Text = string.IsNullOrWhiteSpace(cfg.Audio) ? "Désactivé" : cfg.Audio;
        }

        void OpenCapPop(object s, RoutedEventArgs e)
        {
            if (capPanel.Visibility == Visibility.Visible) { capPanel.Visibility = Visibility.Collapsed; return; }
            hkPanel.Visibility = Visibility.Collapsed; capturing = null; capSrcBox.Visibility = capAudBox.Visibility = Visibility.Collapsed; FillCap();
            capPanel.Visibility = Visibility.Visible; if (screenNames == null) _ = LoadScreens();
        }
        void CloseCapPop(object s, RoutedEventArgs e) => capPanel.Visibility = Visibility.Collapsed;
        void CapToggle(object s, RoutedEventArgs e) { ToggleCapture(null, null); FillCap(); }
        void CapGo(object s, RoutedEventArgs e) { capPanel.Visibility = Visibility.Collapsed; Nav(s, e); }

        // ---------- listes d'écrans et de périphériques audio ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);

        List<string> screenNames, audioDevs;

        async Task LoadScreens()
        {
            var list = await Task.Run(() =>
            {
                var res = new List<(string code, string fallback, bool primary)>();
                for (uint i = 0; ; i++)
                {
                    var ad = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(null, i, ref ad, 0)) break;
                    if ((ad.StateFlags & 1) == 0) continue;   // pas relié au bureau
                    var md = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    string code = "", fb = "Écran " + res.Count;
                    if (EnumDisplayDevices(ad.DeviceName, 0, ref md, 0))
                    {
                        var parts = (md.DeviceID ?? "").Split('\\'); if (parts.Length > 1) code = parts[1];
                        if (!string.IsNullOrWhiteSpace(md.DeviceString) && !md.DeviceString.StartsWith("Generic", StringComparison.OrdinalIgnoreCase)) fb = md.DeviceString;
                    }
                    res.Add((code, fb, (ad.StateFlags & 4) != 0));
                }
                // vrais noms (ex. DELL SE2426HG) via WMI, rapprochés par code matériel
                var wmi = new List<(string code, string name)>();
                try
                {
                    var ps = @"Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID | ForEach-Object { $n=(($_.UserFriendlyName | Where-Object {$_ -ne 0} | ForEach-Object {[char]$_}) -join ''); (($_.InstanceName -split '\\')[1]) + '|' + $n }";
                    var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
                    var psi = new ProcessStartInfo("powershell", "-NoProfile -EncodedCommand " + enc) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi); var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(6000);
                    foreach (var ln in o.Split('\n')) { var a = ln.Trim().Split('|'); if (a.Length == 2 && a[1].Length > 0) wmi.Add((a[0], a[1])); }
                }
                catch { }
                var names = new List<string>();
                foreach (var r in res)
                {
                    var k = wmi.FindIndex(w => string.Equals(w.code, r.code, StringComparison.OrdinalIgnoreCase));
                    string nm = r.fallback; if (k >= 0) { nm = wmi[k].name; wmi.RemoveAt(k); }
                    names.Add(r.primary ? nm + "  ·  principal" : nm);
                }
                if (names.Count == 0) names.Add("Écran principal");
                return names;
            });
            screenNames = list; FillCap();
        }

        async Task LoadAudio()
        {
            audioDevs = await Task.Run(() =>
            {
                var l = new List<string>();
                try
                {
                    var psi = new ProcessStartInfo(FF, "-hide_banner -list_devices true -f dshow -i dummy") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    using var p = Process.Start(psi); var err = p.StandardError.ReadToEnd(); p.WaitForExit(8000);
                    bool inAudio = false;
                    foreach (var ln in err.Split('\n'))
                    {
                        if (ln.Contains("DirectShow audio devices")) { inAudio = true; continue; }
                        if (ln.Contains("DirectShow video devices")) { inAudio = false; continue; }
                        if (ln.Contains("Alternative name")) continue;
                        var m = Regex.Match(ln, "\"([^\"]+)\"\\s*(\\((audio|video|none)\\))?");
                        if (!m.Success) continue;
                        bool isAudio = m.Groups[3].Success ? m.Groups[3].Value == "audio" : inAudio;
                        if (isAudio && !l.Contains(m.Groups[1].Value)) l.Add(m.Groups[1].Value);
                    }
                }
                catch { }
                return l;
            });
        }

        void FillList(StackPanel host, IEnumerable<(string text, bool sel, Action pick)> items)
        {
            host.Children.Clear();
            foreach (var (text, sel, pick) in items)
            {
                var dp = new DockPanel();
                if (sel) { var ck = new TextBlock { Text = "✓", Foreground = new SolidColorBrush(Color.FromRgb(0x9D, 0xC1, 0xFF)), Margin = new Thickness(10, 0, 0, 0) }; DockPanel.SetDock(ck, Dock.Right); dp.Children.Add(ck); }
                dp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = sel ? new SolidColorBrush(Color.FromRgb(0x9D, 0xC1, 0xFF)) : new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xFA)) });
                var b = new Button { Style = (Style)FindResource("Chip"), Content = dp, Margin = new Thickness(0, 0, 0, 2) };
                b.Click += (_, _) => pick();
                host.Children.Add(b);
            }
        }

        async void CapSrcToggle(object s, RoutedEventArgs e)
        {
            capAudBox.Visibility = Visibility.Collapsed;
            if (capSrcBox.Visibility == Visibility.Visible) { capSrcBox.Visibility = Visibility.Collapsed; return; }
            capSrcBox.Visibility = Visibility.Visible;
            if (screenNames == null) { FillList(capSrcList, new[] { ("Recherche des écrans…", false, (Action)(() => { })) }); await LoadScreens(); }
            var names = screenNames;
            FillList(capSrcList, names.Select((n, i) => (n, cfg.Monitor == i, (Action)(() =>
            {
                cfg.Monitor = i; Persist(); LoadUi(); QueueApply(); capSrcBox.Visibility = Visibility.Collapsed; FillCap();
            }))));
        }

        async void CapAudToggle(object s, RoutedEventArgs e)
        {
            capSrcBox.Visibility = Visibility.Collapsed;
            if (capAudBox.Visibility == Visibility.Visible) { capAudBox.Visibility = Visibility.Collapsed; return; }
            capAudBox.Visibility = Visibility.Visible;
            FillList(capAudList, new[] { ("Recherche des périphériques…", false, (Action)(() => { })) });
            await LoadAudio();
            if (capAudBox.Visibility != Visibility.Visible) return;
            var items = new List<(string, bool, Action)> { ("Désactivé (sans audio)", string.IsNullOrEmpty(cfg.Audio), () => PickAudio("")) };
            foreach (var d in audioDevs) { var dd = d; items.Add((dd, cfg.Audio == dd, () => PickAudio(dd))); }
            if (audioDevs.Count == 0) items.Add(("Aucun périphérique trouvé", false, () => { }));
            FillList(capAudList, items);
        }

        bool loadingSet;
        async Task FillSetCombos()
        {
            if (screenNames == null) await LoadScreens();
            await LoadAudio();
            loadingSet = true;
            try
            {
                cMon.ItemsSource = screenNames.Select((n, i) => O(n, i)).ToList();
                cMon.SelectedValue = cfg.Monitor;
                var a = new List<Opt> { O("Désactivé (sans audio)", "") };
                foreach (var d in audioDevs) a.Add(O(d, d));
                if (!string.IsNullOrEmpty(cfg.Audio) && !audioDevs.Contains(cfg.Audio)) a.Add(O(cfg.Audio, cfg.Audio));
                cAud.ItemsSource = a; cAud.SelectedValue = cfg.Audio ?? "";
                cColor.ItemsSource = new List<Opt> { O("Désactivées (couleurs d'origine)", 0), O("Très légères", 20), O("Légères (recommandé)", 40), O("Éclatantes", 70), O("Très éclatantes", 100) };
                { var cv = new[] { 0, 20, 40, 70, 100 }.OrderBy(x => Math.Abs(x - cfg.Vibrance)).First(); cColor.SelectedValue = cv; }
                cNoise.ItemsSource = new List<Opt> { O("Désactivée", 0), O("Légère", 1), O("Moyenne (recommandé)", 2), O("Forte", 3), O("Maximum IA — voix uniquement", 4) };
                cNoise.SelectedValue = Math.Clamp(cfg.Noise, 0, 4);
                noiseHint.Text = NoiseHint();
                var dfv = new[] { 18, 30, 45, 100 };
                cDf.ItemsSource = new List<Opt> { O("Douce — voix préservée", 18), O("Équilibrée", 30), O("Forte (recommandé)", 45), O("Maximum — peut couper des débuts de mots", 100) };
                var near = dfv.OrderBy(x => Math.Abs(x - cfg.DfAtten)).First();
                if (near != cfg.DfAtten) { cfg.DfAtten = near; Persist(); }
                cDf.SelectedValue = near;
                dfBox.Visibility = cfg.Noise == 4 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { loadingSet = false; }
        }
        void MonSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingSet || cMon.SelectedValue is not int v || v == cfg.Monitor) return;
            cfg.Monitor = v; Persist(); QueueApply(); FillCap();
        }
        void ColSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingSet || cColor.SelectedValue is not int v || v == cfg.Vibrance) return;
            cfg.Vibrance = v; Persist();   // appliqué à la prochaine sauvegarde de clip, pas de redémarrage
        }
        void DfSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingSet || cDf.SelectedValue is not int v || v == cfg.DfAtten) return;
            cfg.DfAtten = v; Persist();   // appliqué à la prochaine sauvegarde de clip, pas de redémarrage
        }
        void NoiseSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingSet || cNoise.SelectedValue is not int v || v == cfg.Noise) return;
            cfg.Noise = v; Persist(); QueueApply();
            if (v >= 2) EnsureModel();
            if (v == 4) EnsureDeepFilter();
            noiseHint.Text = NoiseHint();
            dfBox.Visibility = v == 4 ? Visibility.Visible : Visibility.Collapsed;
        }
        void AudSel(object s, SelectionChangedEventArgs e)
        {
            if (loadingSet || cAud.SelectedValue is not string v || v == cfg.Audio) return;
            cfg.Audio = v; Persist(); QueueApply(); FillCap(); noiseHint.Text = NoiseHint();
        }

        void PickAudio(string name)
        {
            cfg.Audio = name; Persist(); LoadUi(); QueueApply(); capAudBox.Visibility = Visibility.Collapsed; FillCap();
        }

        void Refresh()
        {
            UpdateNow();
            var p = Cur();
            bCap.Content = cap == null && !starting ? "Activer la capture" : "Désactiver la capture";
            bRec.Content = recStart == null ? $"● REC  ({KeyName(cfg.RecMod, cfg.RecVk)})" : $"■ STOP  ({KeyName(cfg.RecMod, cfg.RecVk)})";
            status.Text = DateTime.Now < flashUntil ? flashText
                : clipWait ? "✂ Clip en cours…"
                : starting ? "Démarrage…"
                : cap == null ? "Inactif"
                : recStart != null ? $"● Enregistrement {(DateTime.Now - recStart.Value):mm\\:ss}"
                : $"Replay buffer actif ({cfg.BufSec}s)";
            var dotCol = cap == null ? Color.FromRgb(0x59, 0x62, 0x77) : recStart != null ? Color.FromRgb(0xFF, 0x4D, 0x6A) : Color.FromRgb(0x3D, 0xDC, 0x84);
            dot.Fill = new SolidColorBrush(dotCol); DotFx(cap == null ? "off" : recStart != null ? "rec" : "on", dotCol);
            info.Text = $"{p.Name}" + (gpuDone ? $" · encodeur {Encoder()}" : "") + (cap != null ? $" · {tierName}" : "");
        }

        static byte[] Tone(params (double hz, double sec)[] notes)
        {
            const int sr = 44100; var pcm = new List<short>();
            foreach (var (hz, sec) in notes)
            {
                int n = (int)(sr * sec);
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)sr, env = Math.Min(1.0, i / (sr * 0.012)) * Math.Exp(-4.2 * i / n);
                    double v = (Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Sin(4 * Math.PI * hz * t)) * env * 0.2;
                    pcm.Add((short)(v * short.MaxValue));
                }
            }
            var ms = new MemoryStream(); var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + pcm.Count * 2); w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(pcm.Count * 2); foreach (var s in pcm) w.Write(s);
            return ms.ToArray();
        }

        static readonly Dictionary<string, byte[]> Sounds = new()
        {
            ["press"] = Tone((784, 0.16)),
            ["saved"] = Tone((659, 0.14), (988, 0.28)),
            ["recon"] = Tone((587, 0.12), (880, 0.24)),
            ["recoff"] = Tone((880, 0.12), (587, 0.24)),
            ["err"] = Tone((330, 0.22)),
        };

        void Snd(string k)
        {
            if (!cfg.Sound || DateTime.Now < notifPause) return;
            Task.Run(() => { try { using var p = new SoundPlayer(new MemoryStream(Sounds[k])); p.PlaySync(); } catch { } });
        }

        void ToggleRec(object s, RoutedEventArgs e)
        {
            if (cap == null)
            {
                recAfterStart = true;                 // l'enregistrement démarre dès que la capture est prête
                if (!starting) StartCapture();
                return;
            }
            if (recStart == null) { recStart = DateTime.Now; Snd("recon"); Refresh(); RecNotice("● Enregistrement démarré"); return; }
            else { Snd("recoff"); var t = recStart.Value; recStart = null; _ = ExportLater(t, "Rec", CurrentGame()); QueueApply(); }
            Refresh(); RecNotice("■ Enregistrement arrêté · sauvegarde en cours…");
        }

        async Task ExportLater(DateTime t, string prefix, string game)
        {
            exporting++;                               // empêche le ménage du buffer pendant l'attente
            try { await Task.Delay(SegSec * 1000 + 400); } finally { exporting--; }
            Export(t, DateTime.Now, prefix, game);
        }

        bool clipWait;

        // confirmation visible : message dans l'app + notification Windows (infobulle) quand la fenêtre est cachée (en jeu)
        void RecNotice(string t) { Toast(t); if (!UiLive) TrayBalloon("TynoRec", t); }

        async void SaveClip(object s, RoutedEventArgs e)
        {
            if (cap == null) { Snd("err"); Toast("Capture inactive"); return; }
            if (clipWait) return;
            Snd("press");
            var press = DateTime.Now; var game = CurrentGame();
            clipWait = true; Refresh(); Toast("✂ Clip enregistré, sauvegarde dans quelques secondes…");
            await Task.Delay((cfg.AfterSec + SegSec) * 1000 + 300);
            clipWait = false;
            Export(press.AddSeconds(-cfg.BufSec), press.AddSeconds(cfg.AfterSec), "Clip", game);
            Refresh();
        }

        // l'export n'a droit qu'à UN seul coeur logique (le dernier) : impossible de voler du CPU au jeu
        static void OneCore(Process p)
        {
            try { int n = Environment.ProcessorCount; if (n > 2) p.ProcessorAffinity = (IntPtr)(1L << (n - 1)); } catch { }
        }

        static string Q(string f) => f.Replace("\\", "/").Replace("'", "'\\''");

        void Export(DateTime from, DateTime to, string prefix, string game)
        {
            List<string> files;
            try { files = Directory.GetFiles(bufDir, "*.ts").Where(f => { var w = File.GetLastWriteTime(f); return w >= from.AddSeconds(-SegSec) && w <= to.AddSeconds(SegSec + 1); }).OrderBy(f => f).ToList(); }
            catch { files = new List<string>(); }
            if (files.Count == 0) { Snd("err"); Toast("✖ Rien à sauvegarder"); return; }
            var baseStamp = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}"; var stamp = baseStamp; int n = 1;
            while (File.Exists(Path.Combine(cfg.Out, stamp + ".mp4"))) stamp = baseStamp + "_" + (++n);
            var outFile = Path.Combine(cfg.Out, stamp + ".mp4");
            // Les segments .ts ont des horodatages continus : on les colle octet par octet (comme HLS) puis on remux une seule fois.
            // Plus de raccords entre fichiers (c'est ce qui donnait les micro-sauts de la vidéo).
            var join = Path.Combine(Path.GetTempPath(), $"TynoRecJoin_{stamp}.ts");
            var ajoin = Path.Combine(Path.GetTempPath(), $"TynoRecJoinA_{stamp}.ts");
            List<string> afiles = new(); double vE, aE;
            lock (epochLock) { vE = vEpoch; aE = aEpoch; }
            if (!double.IsNaN(vE) && !double.IsNaN(aE))
            {
                try { afiles = Directory.GetFiles(audDir, "*.ts").Where(f => { var w = File.GetLastWriteTime(f); return w >= from.AddSeconds(-SegSec - 2) && w <= to.AddSeconds(SegSec + 3); }).OrderBy(f => f).ToList(); }
                catch { afiles = new List<string>(); }
            }
            int audMs = cfg.AudioOffsetMs;
            string vcol = ColorArgs();
            string dfExe = cfg.Noise == 4 ? DfExe() : null;
            int dfAtt = Math.Clamp(cfg.DfAtten, 6, 100);
            var dfDir = Path.Combine(Path.GetTempPath(), $"TynoRecDF_{stamp}");
            // En jeu : sauvegarde instantanée en simple copie (aucun lag). Couleurs + IA audio sont appliquées plus tard, quand la capture est arrêtée.
            bool wantColor = vcol != "-c:v copy", wantDf = dfExe != null;
            bool deferEnh = (GameRecent() || FullscreenInFront()) && (wantColor || wantDf);
            if (deferEnh) { vcol = "-c:v copy"; dfExe = null; }
            exporting++;
            Task.Run(() =>
            {
                Perf.BgBegin();
                try
                {
                    Directory.CreateDirectory(cfg.Out);
                    static void Join(string dst, List<string> src)
                    {
                        using var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                        foreach (var f in src)
                            try { using var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20); i.CopyTo(o); } catch { }
                    }
                    int Run(string args)
                    {
                        var psi = new ProcessStartInfo(FF, args) { UseShellExecute = false, CreateNoWindow = true };
                        using var pr = Process.Start(psi); JobKill.Add(pr);
                        try { pr.PriorityClass = ProcessPriorityClass.Idle; } catch { } OneCore(pr);   // export en priorité minimale : le jeu passe toujours avant
                        pr.WaitForExit();
                        return pr.ExitCode;
                    }
                    int RunExe(string exe, string args)
                    {
                        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
                        psi.Environment["RAYON_NUM_THREADS"] = "1"; psi.Environment["OMP_NUM_THREADS"] = "1";
                        using var pr = Process.Start(psi); JobKill.Add(pr);
                        try { pr.PriorityClass = ProcessPriorityClass.Idle; } catch { } OneCore(pr);
                        if (!pr.WaitForExit(15 * 60 * 1000)) { try { pr.Kill(); } catch { } return -1; }
                        return pr.ExitCode;
                    }
                    // Lecture directe des segments par ffmpeg (protocole concat : mêmes octets que la jonction, mais sans écrire de gros fichier temporaire).
                    // Moins d'écritures disque = moins de scan antivirus et de saccades en jeu. Trop de segments (REC long) : ancien fichier de jonction.
                    string ConcatOrJoin(string dst, List<string> src)
                    {
                        var ca = "concat:" + string.Join("|", src);
                        if (ca.Length < 7000) return ca;
                        Join(dst, src); return dst;
                    }
                    string jin = ConcatOrJoin(join, files), ain = null;
                    int Mux(string vArgs)
                    {
                    string hw = vArgs.Contains("-vf") ? "-hwaccel auto -threads 1 " : "";
                    string videoOnly = $"-y -loglevel error -fflags +genpts {hw}-i \"{jin}\" {vArgs} -avoid_negative_ts make_zero -movflags +faststart \"{outFile}\"";
                    if (afiles.Count > 0)
                    {
                        try
                        {
                            ain = ConcatOrJoin(ajoin, afiles);
                            var (vs, vd) = ProbeSD(jin); var (asn, _) = ProbeSD(ain);
                            if (vs >= 0 && asn >= 0 && vd > 0)
                            {
                                // t=0 de chaque flux est connu en heure murale (vE / aE) : on aligne l'audio sur la première image de la vidéo
                                double off = (aE + asn) - (vE + vs) + audMs / 1000.0;
                                if (Math.Abs(off) < 20)
                                {
                                    var inv = CultureInfo.InvariantCulture;
                                    string pre = off >= 0 ? $"-itsoffset {off.ToString("0.###", inv)} " : $"-ss {(-off).ToString("0.###", inv)} ";
                                    string aIn = ain, acodec = vArgs + " -c:a copy";
                                    if (dfExe != null)
                                    {
                                        try
                                        {
                                            var outDir = Path.Combine(dfDir, "out"); Directory.CreateDirectory(outDir);
                                            var wav = Path.Combine(dfDir, "a.wav");
                                            if (Run($"-y -loglevel error -i \"{ain}\" -vn -ac 1 -ar 48000 -c:a pcm_s16le \"{wav}\"") == 0)
                                            {
                                                // -a : l'IA ne peut pas couper plus de dfAtt dB, donc un début de mot pris à tort pour du bruit reste audible
                                                int drc = RunExe(dfExe, $"-D -a {dfAtt} -o \"{outDir}\" \"{wav}\"");
                                                if (drc != 0) drc = RunExe(dfExe, $"-a {dfAtt} -o \"{outDir}\" \"{wav}\"");
                                                if (drc != 0) drc = RunExe(dfExe, $"-o \"{outDir}\" \"{wav}\"");
                                                var res = Path.Combine(outDir, "a.wav");
                                                if (drc == 0 && File.Exists(res)) { aIn = res; acodec = "-af \"" + VoiceClarity + "\" " + vArgs + " -c:a aac -b:a 192k"; }
                                            }
                                        }
                                        catch { }
                                    }
                                    int rc = Run($"-y -loglevel error -fflags +genpts {hw}-i \"{jin}\" {pre}-i \"{aIn}\" -map 0:v:0 -map 1:a:0 {acodec} -t {vd.ToString("0.###", inv)} -avoid_negative_ts make_zero -movflags +faststart \"{outFile}\"");
                                    if (rc == 0 && File.Exists(outFile)) return 0;
                                    try { File.Delete(outFile); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                    return Run(videoOnly);   // pas d'audio exploitable : on sauve au moins la vidéo
                    }
                    int r0 = Mux(vcol);
                    if (r0 != 0 && vcol != "-c:v copy") { try { File.Delete(outFile); } catch { } r0 = Mux("-c:v copy"); }   // filtre couleur refusé -> on sauve sans
                    return r0;
                }
                catch { return -1; }
                finally { Perf.BgEnd(); }
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                exporting--;
                int code = t.IsFaulted ? -1 : t.Result;
                try { File.Delete(join); } catch { }
                try { File.Delete(ajoin); } catch { }
                try { if (Directory.Exists(dfDir)) Directory.Delete(dfDir, true); } catch { }
                if (code == 0 && File.Exists(outFile))
                {
                    meta[stamp + ".mp4"] = new ClipMeta { Game = game, Uid = Cloud.UserId, EnhC = deferEnh && wantColor, EnhD = deferEnh && wantDf }; SaveMeta();
                    if (deferEnh) { lock (pendingEnh) pendingEnh.Add((outFile, wantColor, wantDf)); } else QueueSync();
                    Snd("saved"); var fw = FpsWarn(); Toast("✔ Clip sauvegardé" + (deferEnh ? " · amélioration (couleurs / audio) appliquée plus tard" : "") + (fw != null ? "  " + fw : ""));
                    if (fw != null) { try { Directory.CreateDirectory(dataDir); File.AppendAllText(Path.Combine(dataDir, "ffmpeg.log"), $"{DateTime.Now} [{tierName}] {fw}\n\n"); } catch { } }
                }
                else
                {
                    try { File.Delete(outFile); } catch { }
                    Snd("err"); Toast("✖ Échec de la sauvegarde");
                }
                if (UiLive) { RecentRefresh(); LibRefresh(null, null); }   // fenêtre cachée (en jeu) : rien à rafraîchir, ça se fera à l'ouverture
                RunEnhance();
            })));
        }

        // ---------- amélioration différée (couleurs + IA audio) ----------
        readonly List<(string path, bool color, bool df)> pendingEnh = new();
        bool enhBusy;
        // l'amélioration ne démarre qu'une minute après la fin d'un jeu (jamais pendant une partie, même si la capture reste active)
        DateTime gameSeen = DateTime.MinValue;
        bool GameRecent() => GameRunning() || (DateTime.UtcNow - gameSeen).TotalSeconds < 60;

        // un programme vu en plein écran est mémorisé comme jeu (liste ExtraGames de la config) : plus besoin de la remplir à la main
        void LearnGame(string n)
        {
            try
            {
                if (GameAlias.TryGetValue(n, out var al) && al == "Navigateur") return;
                if (!FullscreenInFront()) return;
                var cur = cfg.ExtraGames ?? Array.Empty<string>();
                if (cur.Length >= 50 || cur.Contains(n, StringComparer.OrdinalIgnoreCase) || GameProcs.Contains(n, StringComparer.OrdinalIgnoreCase)) return;
                cfg.ExtraGames = cur.Append(n).ToArray(); Persist();
            }
            catch { }
        }

        static int ExecIdle(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            psi.Environment["RAYON_NUM_THREADS"] = "2";
            using var pr = Process.Start(psi); JobKill.Add(pr);
            try { pr.PriorityClass = ProcessPriorityClass.Idle; } catch { }
            if (!pr.WaitForExit(15 * 60 * 1000)) { try { pr.Kill(); } catch { } return -1; }
            return pr.ExitCode;
        }

        [StructLayout(LayoutKind.Sequential)] struct RectX { public int L, T, R, B; }
        [DllImport("user32.dll", EntryPoint = "GetWindowRect")] static extern bool GetWindowRectX(IntPtr h, out RectX r);
        [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")] static extern int GetSysMetricX(int i);
        DispatcherTimer enhT;

        // un jeu connu est ouvert (même en fenêtré / alt-tab / 2e écran) : l'amélioration des clips attend sa fermeture.
        // FullscreenInFront() seul ne suffisait pas : réencodage (CPU + NVDEC/NVENC) en pleine partie => chute de FPS.
        static readonly string[] GameProcs = { "FortniteClient-Win64-Shipping", "FortniteLauncher", "VALORANT-Win64-Shipping", "cs2", "RocketLeague", "Overwatch", "r5apex", "RainbowSix", "GTA5", "eldenring", "Cyberpunk2077", "TslGame", "League of Legends", "dota2", "destiny2", "RustClient", "EscapeFromTarkov", "Marvel-Win64-Shipping", "Minecraft.Windows", "FC25", "FC26" };
        DateTime grAt = DateTime.MinValue; bool grVal;
        bool GameRunning()
        {
            if ((DateTime.UtcNow - grAt).TotalSeconds < 5) return grVal;
            grAt = DateTime.UtcNow; grVal = false;
            try
            {
                foreach (var n0 in GameProcs.Concat(cfg.ExtraGames ?? Array.Empty<string>()))
                {
                    var n = (n0 ?? "").Trim(); if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4]; if (n.Length == 0) continue;
                    var ps = Process.GetProcessesByName(n);
                    bool on = ps.Length > 0; foreach (var x in ps) x.Dispose();
                    if (on) { grVal = true; break; }
                }
                if (!grVal && lastGame != null && lastGame != "Bureau" && lastGame != "Navigateur" && FullscreenAnywhere()) grVal = true;
            }
            catch { }
            if (grVal) gameSeen = DateTime.UtcNow;
            return grVal;
        }
        // plein écran sur n'importe quel moniteur (le jeu peut être sur l'écran secondaire)
        bool FullscreenAnywhere() => FullscreenInFront();

        // une fenêtre plein écran au premier plan (jeu) : on ne fait surtout pas de calcul lourd
        bool FullscreenInFront()
        {
            try
            {
                var hw = GetForegroundWindow(); if (hw == IntPtr.Zero) return false;
                GetWindowThreadProcessId(hw, out var pid); if (pid == (uint)Environment.ProcessId) return false;
                if (!GetWindowRectX(hw, out var r)) return false;
                return r.R - r.L >= GetSysMetricX(0) && r.B - r.T >= GetSysMetricX(1);
            }
            catch { return false; }
        }

        void RunEnhance()
        {
            bool any; lock (pendingEnh) any = pendingEnh.Count > 0;
            if (any && enhT == null) { enhT = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; enhT.Tick += (_, _) => RunEnhance(); }
            if (enhT != null) { if (any || enhBusy) enhT.Start(); else enhT.Stop(); }
            if (enhBusy || recStart != null || exporting > 0 || !any || FullscreenInFront() || GameRecent()) return;
            (string path, bool color, bool df) job;
            lock (pendingEnh) { if (pendingEnh.Count == 0) return; job = pendingEnh[0]; pendingEnh.RemoveAt(0); }
            enhBusy = true;
            string ff = FF, vnow = job.color ? ColorArgs() : "-c:v copy", dfx = job.df ? DfExe() : null;
            int att = Math.Clamp(cfg.DfAtten, 6, 100);
            Task.Run(() =>
            {
                var tmp = job.path + ".enh"; var dir = Path.Combine(Path.GetTempPath(), "TynoRecEnh_" + Guid.NewGuid().ToString("N"));
                try
                {
                    if (!File.Exists(job.path)) return false;
                    Directory.CreateDirectory(dir); string aIn = null;
                    if (dfx != null)
                    {
                        var wav = Path.Combine(dir, "a.wav"); var od = Path.Combine(dir, "out"); Directory.CreateDirectory(od);
                        if (ExecIdle(ff, $"-y -loglevel error -i \"{job.path}\" -vn -ac 1 -ar 48000 -c:a pcm_s16le \"{wav}\"") == 0)
                        {
                            int rc = ExecIdle(dfx, $"-D -a {att} -o \"{od}\" \"{wav}\"");
                            if (rc != 0) rc = ExecIdle(dfx, $"-a {att} -o \"{od}\" \"{wav}\"");
                            if (rc != 0) rc = ExecIdle(dfx, $"-o \"{od}\" \"{wav}\"");
                            var res = Path.Combine(od, "a.wav"); if (rc == 0 && File.Exists(res)) aIn = res;
                        }
                    }
                    if (aIn == null && vnow == "-c:v copy") return false;
                    string hw = vnow.Contains("-vf") ? "-hwaccel auto " : "";
                    string args = aIn != null
                        ? $"-y -loglevel error {hw}-i \"{job.path}\" -i \"{aIn}\" -map 0:v:0 -map 1:a:0 {vnow} -af \"{VoiceClarity}\" -c:a aac -b:a 192k -shortest -f mp4 -movflags +faststart \"{tmp}\""
                        : $"-y -loglevel error {hw}-i \"{job.path}\" -map 0:v:0 -map 0:a:0? {vnow} -c:a copy -f mp4 -movflags +faststart \"{tmp}\"";
                    if (ExecIdle(ff, args) != 0 || !File.Exists(tmp)) return false;
                    File.Move(tmp, job.path, true);
                    return true;
                }
                catch { return false; }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
            }).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
            {
                enhBusy = false;
                try { if (meta.TryGetValue(Path.GetFileName(job.path), out var em)) { em.EnhC = false; em.EnhD = false; SaveMeta(); } } catch { }
                if (!t.IsFaulted && t.Result) { Toast("✨ Clip amélioré"); LibRefresh(null, null); }
                QueueSync();
                RunEnhance();
            })));
        }

        // ---------- jeu détecté ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

        static readonly HashSet<string> IgnoredProcs = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "SearchHost", "SearchApp", "ShellExperienceHost", "StartMenuExperienceHost", "dwm", "TextInputHost",
            "ApplicationFrameHost", "LockApp", "ShellHost", "SnippingTool", "ScreenClippingHost"
        };

        static readonly Dictionary<string, string> GameAlias = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FortniteClient-Win64-Shipping"] = "Fortnite", ["VALORANT-Win64-Shipping"] = "Valorant", ["cs2"] = "Counter-Strike 2",
            ["RocketLeague"] = "Rocket League", ["javaw"] = "Minecraft", ["LeagueClient"] = "League of Legends", ["League of Legends"] = "League of Legends",
            ["chrome"] = "Navigateur", ["msedge"] = "Navigateur", ["firefox"] = "Navigateur", ["brave"] = "Navigateur", ["opera"] = "Navigateur",
        };

        [DllImport("kernel32.dll", EntryPoint = "OpenProcess")] static extern IntPtr OpenProcTy(uint acc, bool inh, uint pid);
        [DllImport("kernel32.dll", EntryPoint = "CloseHandle")] static extern bool CloseProcTy(IntPtr h);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)] static extern bool QueryImgTy(IntPtr h, int flags, StringBuilder sb, ref int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ShFileInfoTy { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)] static extern IntPtr ShGetFileInfoTy(string path, uint attr, ref ShFileInfoTy fi, uint size, uint flags);
        [DllImport("user32.dll", EntryPoint = "DestroyIcon")] static extern bool DestroyIconTy(IntPtr h);

        string iconDir => Path.Combine(dataDir, "icons");
        string IconPath(string g) => Path.Combine(iconDir, Regex.Replace(g ?? "", @"[^\w\- ]", "_") + ".png");
        readonly Dictionary<string, ImageSource> iconCache = new();

        // Logo du jeu = icône de son .exe, sauvegardée une fois (tu peux aussi déposer ton propre PNG nommé "<jeu>.png" dans le dossier icons)
        void SaveGameIcon(string game, uint pid)
        {
            if (string.IsNullOrEmpty(game) || game == "Bureau") return;
            var png = IconPath(game); if (File.Exists(png)) return;
            string exe = null;
            var h = OpenProcTy(0x1000, false, pid);
            if (h == IntPtr.Zero) return;
            try { var sb = new StringBuilder(1024); int sz = sb.Capacity; if (QueryImgTy(h, 0, sb, ref sz)) exe = sb.ToString(); } finally { CloseProcTy(h); }
            if (exe == null || !File.Exists(exe)) return;
            var fi = new ShFileInfoTy();
            if (ShGetFileInfoTy(exe, 0, ref fi, (uint)Marshal.SizeOf<ShFileInfoTy>(), 0x100) == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return;
            BitmapSource bs;
            try { bs = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); } finally { DestroyIconTy(fi.hIcon); }
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bs));
            Directory.CreateDirectory(iconDir);
            using var fs = File.Create(png); enc.Save(fs);
        }

        ImageSource GameIcon(string g)
        {
            if (string.IsNullOrEmpty(g)) return null;
            if (iconCache.TryGetValue(g, out var i) && i != null) return i;
            var p = IconPath(g); if (!File.Exists(p)) return null;
            try { i = LoadImg(p); } catch { return null; }
            return iconCache[g] = i;
        }

        // Clic sur le logo = choisir l'image (PNG/JPG) du jeu ; s'applique à tous les clips de ce jeu
        void LogoPick(object s, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (((FrameworkElement)s).DataContext is not Vid v || string.IsNullOrEmpty(v.Game)) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.ico;*.webp|Tous|*.*", Title = "Logo de " + v.Game };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(dlg.FileName); bmp.DecodePixelWidth = 128; bmp.EndInit();
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
                Directory.CreateDirectory(iconDir);
                using (var fs = File.Create(IconPath(v.Game))) enc.Save(fs);
                iconCache.Remove(v.Game);
                var ic = GameIcon(v.Game);
                foreach (var x in Items.Where(x => x.Game == v.Game)) x.Icon = ic;
            }
            catch { Toast("Image illisible"); }
        }

        // Popup de confirmation aux couleurs de l'app
        bool Confirm(string title, string msg, string yes, string emoji)
        {
            bool ok = false;
            var w = new Window { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xFA)), FontFamily = FontFamily, FontSize = 14 };
            var bYes = new Button { Content = yes, MinWidth = 110, Padding = new Thickness(20, 9, 20, 9), Margin = new Thickness(0), IsDefault = true, Style = (Style)FindResource("Dan") };
            var bNo = new Button { Content = "Annuler", MinWidth = 110, Padding = new Thickness(20, 9, 20, 9), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            if (TryFindResource(typeof(Button)) is Style bs) bNo.Style = bs;
            bYes.Click += (_, _) => { ok = true; w.Close(); };
            bNo.Click += (_, _) => w.Close();
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            btns.Children.Add(bNo); btns.Children.Add(bYes);
            var ico = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x16, 0x20)), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = emoji, FontSize = 20, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var txt = new StackPanel();
            txt.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            txt.Children.Add(new TextBlock { Text = msg, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xAA, 0xBC)), TextWrapping = TextWrapping.Wrap, MaxWidth = 300, Margin = new Thickness(0) });
            var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(ico); row.Children.Add(txt);
            var root = new StackPanel(); root.Children.Add(row); root.Children.Add(btns);
            w.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x31, 0x41)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(26), Margin = new Thickness(24), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.6 }, Child = root };
            w.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
            w.ShowDialog();
            return ok;
        }

        // Pop-up de confirmation : s'affiche au centre de l'app, disparaît seul après ~2,5 s (ou au clic). Seulement quand la fenêtre est visible.
        void InfoPopup(string title, string msg, string emoji)
        {
            if (!UiLive) return;
            try
            {
                var w = new Window { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, ShowActivated = false, Opacity = 0, Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xFA)), FontFamily = FontFamily, FontSize = 14 };
                var ico = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = new SolidColorBrush(Color.FromRgb(0x14, 0x2B, 0x20)), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = emoji, FontSize = 19, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                txt.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3) });
                txt.Children.Add(new TextBlock { Text = msg, FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0xAA, 0xBC)), TextWrapping = TextWrapping.Wrap, MaxWidth = 280, Margin = new Thickness(0) });
                var row = new StackPanel { Orientation = Orientation.Horizontal }; row.Children.Add(ico); row.Children.Add(txt);
                w.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 18, 26, 18), Margin = new Thickness(22), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.55 }, Child = row };
                w.MouseLeftButtonDown += (_, _) => { try { w.Close(); } catch { } };
                w.Show();
                w.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
                    fade.Completed += (_, _) => { try { w.Close(); } catch { } };
                    try { w.BeginAnimation(OpacityProperty, fade); } catch { }
                };
                t.Start();
            }
            catch { }
        }

        void TitleLost(object s, RoutedEventArgs e)
        {
            if (s is not TextBox tb || tb.DataContext is not Vid v) return;
            if (!meta.TryGetValue(v.Name, out var m)) meta[v.Name] = m = new ClipMeta { Game = v.Game };
            m.Title = (tb.Text ?? "").Trim(); SaveMeta();
        }
        void TitleKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter || e.Key == Key.Escape) { Keyboard.Focus(this); e.Handled = true; } }

        uint cgPid; DateTime cgAt;
        string CurrentGame()
        {
            try
            {
                var hw = GetForegroundWindow();
                GetWindowThreadProcessId(hw, out var pid);
                if (pid != 0 && pid != Environment.ProcessId)
                {
                    // même programme qu'il y a moins de 30 s : inutile de refaire la liste des processus Windows toutes les 2 s pendant la partie
                    if (pid == cgPid && (DateTime.UtcNow - cgAt).TotalSeconds < 30) return lastGame;
                    cgPid = pid; cgAt = DateTime.UtcNow;
                    using var p = Process.GetProcessById((int)pid);
                    var n = p.ProcessName;
                    if (!IgnoredProcs.Contains(n))
                    {
                        var sb = new StringBuilder(256);
                        GetWindowText(hw, sb, 256);
                        var t = sb.ToString().Trim();
                        lastGame = GameAlias.TryGetValue(n, out var nice) ? nice : string.IsNullOrEmpty(t) ? n : (t.Length > 40 ? t[..40] : t);
                        try { SaveGameIcon(lastGame, pid); } catch { }
                        TrackGame(p, lastGame); LearnGame(n);
                    }
                }
            }
            catch { }
            return lastGame;
        }

        void SaveMeta() { try { WriteAtomic(metaPath, JsonSerializer.Serialize(meta)); } catch { } }

        // ---------- bibliothèque ----------
        static string Ago(DateTime d)
        {
            var t = DateTime.Now - d;
            return t.TotalSeconds < 60 ? $"il y a {(int)t.TotalSeconds} s" : t.TotalMinutes < 60 ? $"il y a {(int)t.TotalMinutes} min"
                 : t.TotalHours < 24 ? $"il y a {(int)t.TotalHours} h" : $"il y a {(int)t.TotalDays} j";
        }

        static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        static BitmapImage LoadImg(string p)
        {
            var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = 360; b.UriSource = new Uri(p); b.EndInit(); b.Freeze(); return b;
        }

        double Probe(string file, string thumb, double dur)
        {
            if (dur <= 0)
            {
                try
                {
                    var psi = new ProcessStartInfo(FF, $"-hide_banner -i \"{file}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    using var p = Process.Start(psi); try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { } var err = p.StandardError.ReadToEnd(); p.WaitForExit();
                    var m = Regex.Match(err, @"Duration: (\d+):(\d+):(\d+)\.(\d+)");
                    if (m.Success) dur = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value)
                                       + double.Parse("0." + m.Groups[4].Value, CultureInfo.InvariantCulture);
                }
                catch { }
            }
            if (!File.Exists(thumb))
            {
                try
                {
                    var ss = (dur / 3).ToString("0.##", CultureInfo.InvariantCulture);
                    var psi = new ProcessStartInfo(FF, $"-y -loglevel error -threads 1 -ss {ss} -i \"{file}\" -frames:v 1 -vf scale=360:-2 -q:v 4 \"{thumb}\"") { UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi); try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { } p.WaitForExit();
                }
                catch { }
            }
            return dur;
        }

        void LibMore(object s, RoutedEventArgs e) { libLimit += 60; LibRefresh(null, null); }

        async void LibRefresh(object s, RoutedEventArgs e)
        {
            if (!Directory.Exists(cfg.Out)) return;
            if (libBusy) { libAgain = true; return; }
            libBusy = true;
            try
            {
                var dir = cfg.Out;
                var selected = new HashSet<string>(Items.Where(x => x.Sel).Select(x => x.Name));
                FileInfo[] files;
                try { files = await Task.Run(() => new DirectoryInfo(dir).GetFiles("*.mp4")); } catch { files = Array.Empty<FileInfo>(); }
                Directory.CreateDirectory(thumbDir);
                var sorted = files.Where(f => !meta.TryGetValue(f.Name, out var q0) || q0.Uid == null || q0.Uid == Cloud.UserId).OrderByDescending(f => meta.TryGetValue(f.Name, out var q) && q.Fav).ThenByDescending(f => f.LastWriteTime).ToList();
                var names = new HashSet<string>(sorted.Select(f => f.Name));
                foreach (var old in thumbCache.Keys.Where(kk => !names.Contains(kk)).ToList()) thumbCache.Remove(old);
                bMore.Visibility = sorted.Count > libLimit ? Visibility.Visible : Visibility.Collapsed;
                bMore.Content = $"Afficher plus ({sorted.Count - libLimit})";
                var items = sorted.Take(libLimit).Select(f =>
                {
                    meta.TryGetValue(f.Name, out var mm);
                    thumbCache.TryGetValue(f.Name, out var cached);
                    return new Vid
                    {
                        Name = f.Name, Path = f.FullName, Game = mm?.Game ?? "Capture", Fav = mm?.Fav ?? false, Title = mm?.Title ?? "", Icon = GameIcon(mm?.Game),
                        Info = $"{f.Length / 1048576.0:0.0} Mo • {Ago(f.LastWriteTime)}",
                        Duration = mm != null && mm.Dur > 0 ? Fmt(mm.Dur) : "",
                        Sel = selected.Contains(f.Name), Thumb = cached
                    };
                }).ToList();
                lib.ItemsSource = items; UpdateSel();
                foreach (var v in items)
                {
                    var thumb = Path.Combine(thumbDir, v.Name + ".jpg");
                    meta.TryGetValue(v.Name, out var m);
                    var known = m?.Dur ?? 0;
                    if (v.Thumb != null && known > 0) continue;
                    var file = v.Path;
                    var (dur, img) = await Task.Run(() =>
                    {
                        var d = Probe(file, thumb, known);
                        ImageSource im2 = null;
                        if (File.Exists(thumb)) try { im2 = LoadImg(thumb); } catch { }
                        return (d, im2);
                    });
                    if (dur > 0)
                    {
                        v.Duration = Fmt(dur);
                        if (m == null) meta[v.Name] = m = new ClipMeta { Game = "Capture" };
                        m.Dur = dur;
                    }
                    if (img != null) { v.Thumb = img; thumbCache[v.Name] = img; }
                }
                SaveMeta(); RecentRefresh();
            }
            finally { libBusy = false; if (libAgain) { libAgain = false; LibRefresh(null, null); } }
        }

        async void CheckUpdate(object s, RoutedEventArgs e) => await Updater.CheckAsync(false);

        void LibFolder(object s, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(cfg.Out) { UseShellExecute = true });

        // ---------- lecteur intégré ----------
        void FavClick(object s, RoutedEventArgs e)
        {
            if (((FrameworkElement)s).DataContext is not Vid v) return;
            if (!meta.TryGetValue(v.Name, out var m)) meta[v.Name] = m = new ClipMeta { Game = "Capture" };
            m.Fav = v.Fav; SaveMeta(); CloudFav(v.Name, v.Fav);
        }

        IEnumerable<Vid> Items => lib.ItemsSource as IEnumerable<Vid> ?? Enumerable.Empty<Vid>();

        void SelClick(object s, RoutedEventArgs e) => UpdateSel();

        void UpdateSel()
        {
            int n = Items.Count(v => v.Sel);
            selBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            selTxt.Text = n == 1 ? "1 sélectionné" : n + " sélectionnés";
        }

        void ClearSel(object s, RoutedEventArgs e) { foreach (var v in Items) v.Sel = false; UpdateSel(); }

        void DelSel(object s, RoutedEventArgs e)
        {
            var l = Items.Where(v => v.Sel).ToList(); if (l.Count == 0) return;
            if (!Confirm(l.Count == 1 ? "Supprimer ce clip ?" : $"Supprimer {l.Count} clips ?", "Cette action est définitive : " + (l.Count == 1 ? "le fichier sera supprimé" : "les fichiers seront supprimés") + " de ton ordinateur.", "Supprimer", "🗑")) return;
            foreach (var v in l) { try { File.Delete(v.Path); File.Delete(Path.Combine(thumbDir, v.Name + ".jpg")); Directory.Delete(Path.Combine(thumbDir, "tl_" + v.Name), true); } catch { } meta.Remove(v.Name); CloudDelete(v.Name); }
            SaveMeta(); UpdateSel(); LibRefresh(null, null);
        }

        void LibPlay(object s, RoutedEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (src == null) return;
            for (var o = src; o is Visual; o = VisualTreeHelper.GetParent(o))
                if (o is System.Windows.Controls.Primitives.ToggleButton || o is TextBox || (o is FrameworkElement fe0 && fe0.Name == "logoBtn")) return;
            if ((ItemsControl.ContainerFromElement(lib, src) as ListBoxItem)?.DataContext is not Vid v) return;   // double-clic dans le vide = rien
            PlayClip(v);
        }

        void PlayClip(Vid v)
        {
            cur = v; pTitle.Text = $"{(string.IsNullOrWhiteSpace(v.Title) ? v.Game : v.Title)}  ·  {v.Duration}";
            player.Visibility = Visibility.Visible;
            filmGen++; film.Children.Clear(); ruler.Children.Clear(); PlaceHead(0); pTime.Text = "0:00 / " + v.Duration;
            media.Source = new Uri(v.Path); media.Volume = vol.Value; media.Play();
            playing = true; bPlay.Content = MkIcon(PauseD, 20, true); pt.Start();
        }

        void TogglePlay(object s, RoutedEventArgs e)
        {
            if (playing) { media.Pause(); bPlay.Content = MkIcon(PlayD, 20, true); } else { media.Play(); bPlay.Content = MkIcon(PauseD, 20, true); }
            playing = !playing;
        }

        void MediaOpened(object s, RoutedEventArgs e) { if (cur == null || Dur <= 0) return; DrawRuler(); PlaceHead(0); TrimReset(); BuildFilm(cur.Path, cur.Name, Dur); }
        void MediaEnded(object s, RoutedEventArgs e) { media.Position = TimeSpan.Zero; media.Pause(); playing = false; bPlay.Content = MkIcon(PlayD, 20, true); }
        void MediaFailed(object s, ExceptionRoutedEventArgs e)
        {
            var path = cur?.Path;
            PlayerClose(null, null);
            if (path == null) return;
            // codec non géré par le lecteur intégré (ex. HEVC sans extension) : on ouvre avec le lecteur par défaut de Windows
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { Toast("✖ Lecture impossible"); }
        }

        double Dur => media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan.TotalSeconds : 0;
        int filmGen;

        void PlayerTick()
        {
            if (!media.NaturalDuration.HasTimeSpan) return;
            if (!seeking) PlaceHead(media.Position.TotalSeconds);
            if (playing && !seeking && trimDrag == null && (trimA > 0.05 || trimB < Dur - 0.05) && media.Position.TotalSeconds >= trimB) media.Position = TimeSpan.FromSeconds(trimA);
            if (!seeking) pTime.Text = $"{Fmt(media.Position.TotalSeconds)} / {Fmt(Dur)}";
        }

        void PlaceHead(double t)
        {
            double d = Dur, w = tl.ActualWidth;
            double x = d > 0 && w > 0 ? Math.Max(0, Math.Min(w, t / d * w)) : 0;
            Canvas.SetLeft(headLine, x - 1); Canvas.SetLeft(headKnob, x - 6);
            headLine.Height = Math.Max(0, tl.ActualHeight - 16);
        }

        DispatcherTimer tlT;
        void TlSize(object s, SizeChangedEventArgs e)
        {
            PlaceHead(media.Position.TotalSeconds);   // la tête de lecture suit tout de suite
            if (tlT == null) { tlT = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) }; tlT.Tick += (_, _) => { tlT.Stop(); DrawRuler(); TrimLayout(); }; }
            tlT.Stop(); tlT.Start();                  // la règle (des dizaines d'éléments) n'est reconstruite qu'une fois la taille stabilisée, pas à chaque pixel
        }

        void DrawRuler()
        {
            ruler.Children.Clear();
            double d = Dur, w = tl.ActualWidth; if (d <= 0 || w <= 0) return;
            double[] steps = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };
            double step = steps[^1];
            foreach (var st in steps) if (st / d * w >= 80) { step = st; break; }
            double minor = step / 5;
            var tickBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x46, 0x59));
            for (int i = 0; i * minor <= d + 0.001; i++)
            {
                double t = i * minor, x = Math.Min(w - 0.5, t / d * w); bool major = i % 5 == 0;
                var ln = new System.Windows.Shapes.Rectangle { Width = 1, Height = major ? 14 : 7, Fill = tickBrush };
                Canvas.SetLeft(ln, x); Canvas.SetTop(ln, 30 - ln.Height); ruler.Children.Add(ln);
                if (!major) continue;
                var tb = new TextBlock { Text = Fmt(t), FontSize = 11, Margin = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA7)) };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, Math.Max(0, Math.Min(w - tb.DesiredSize.Width, x - tb.DesiredSize.Width / 2))); Canvas.SetTop(tb, 0);
                ruler.Children.Add(tb);
            }
        }

        // Miniatures de la timeline : une image extraite toutes les d/N secondes, mises en cache dans %AppData%\TynoRec\thumbs\tl_<clip>
        async void BuildFilm(string file, string name, double d)
        {
            int g = ++filmGen; film.Children.Clear();
            const int N = 24;
            film.Columns = N;
            var imgs = new Image[N];
            for (int i = 0; i < N; i++) { imgs[i] = new Image { Stretch = Stretch.UniformToFill }; film.Children.Add(imgs[i]); }
            var dir = Path.Combine(thumbDir, "tl_" + name);
            for (int i = 0; i < N; i++)
            {
                if (g != filmGen) return;
                double t = Math.Max(0, Math.Min(d - 0.1, (i + 0.5) / N * d));
                var f = Path.Combine(dir, $"{i:00}.jpg");
                var bmp = await Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        if (!File.Exists(f))
                        {
                            var ss = t.ToString("0.##", CultureInfo.InvariantCulture);
                            var psi = new ProcessStartInfo(FF, $"-y -loglevel error -threads 1 -ss {ss} -i \"{file}\" -frames:v 1 -vf \"scale=192:108:force_original_aspect_ratio=increase,crop=192:108\" -q:v 5 \"{f}\"") { UseShellExecute = false, CreateNoWindow = true };
                            using var p = Process.Start(psi); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } p.WaitForExit();
                        }
                        if (File.Exists(f)) return (ImageSource)LoadImg(f);
                    }
                    catch { }
                    return null;
                });
                if (g != filmGen) return;
                if (bmp != null) imgs[i].Source = bmp;
            }
        }

        bool wasPlaying; DateTime lastSeek;
        double TlTime(double x) { double d = Dur, w = tl.ActualWidth; return d <= 0 || w <= 0 ? 0 : Math.Max(0, Math.Min(d, x / w * d)); }
        void TlShow(double t) { PlaceHead(t); pTime.Text = $"{Fmt(t)} / {Fmt(Dur)}"; }
        void TlDown(object s, MouseButtonEventArgs e)
        {
            if (Dur <= 0) return;
            seeking = true; wasPlaying = playing; if (playing) media.Pause();   // on met en pause pendant le glissement : fini les saccades
            tl.CaptureMouse(); lastSeek = DateTime.MinValue;
            var t = TlTime(e.GetPosition(tl).X); TlShow(t); media.Position = TimeSpan.FromSeconds(t);
        }
        void TlMove(object s, MouseEventArgs e)
        {
            if (!seeking) return;
            var t = TlTime(e.GetPosition(tl).X); TlShow(t);
            if ((DateTime.Now - lastSeek).TotalMilliseconds < 90) return;   // un seek toutes les ~90 ms max
            lastSeek = DateTime.Now; media.Position = TimeSpan.FromSeconds(t);
        }
        void TlUp(object s, MouseButtonEventArgs e)
        {
            if (!seeking) return;
            tl.ReleaseMouseCapture();
            var t = TlTime(e.GetPosition(tl).X); TlShow(t); media.Position = TimeSpan.FromSeconds(t);
            seeking = false; if (wasPlaying) media.Play();
        }

        void Skip(double dt) { if (Dur <= 0) return; var t = Math.Max(0, Math.Min(Dur, media.Position.TotalSeconds + dt)); media.Position = TimeSpan.FromSeconds(t); PlaceHead(t); }
        void Back5(object s, RoutedEventArgs e) => Skip(-5);
        void Fwd5(object s, RoutedEventArgs e) => Skip(5);
        void VolChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (media != null) media.Volume = vol.Value; }

        void PlayerClose(object s, RoutedEventArgs e)
        {
            seeking = false; filmGen++; film.Children.Clear(); pt.Stop(); media.Stop(); media.Close(); media.Source = null; playing = false;
            player.Visibility = Visibility.Collapsed;
        }

        void PlayerDelete(object s, RoutedEventArgs e)
        {
            var v = cur; if (v == null) return;
            if (!Confirm("Supprimer ce clip ?", "Cette action est définitive : le fichier sera supprimé de ton ordinateur.", "Supprimer", "🗑")) return;
            PlayerClose(null, null);
            try { File.Delete(v.Path); File.Delete(Path.Combine(thumbDir, v.Name + ".jpg")); Directory.Delete(Path.Combine(thumbDir, "tl_" + v.Name), true); } catch { }
            meta.Remove(v.Name); CloudDelete(v.Name); SaveMeta(); LibRefresh(null, null);
        }

        string capturing;

        static string KeyName(int mod, int vk)
        {
            var l = new List<string>();
            if ((mod & 2) != 0) l.Add("Ctrl"); if ((mod & 1) != 0) l.Add("Alt"); if ((mod & 4) != 0) l.Add("Maj"); if ((mod & 8) != 0) l.Add("Win");
            l.Add(KeyInterop.KeyFromVirtualKey(vk).ToString());
            return string.Join(" + ", l);
        }

        void RegHotkeys()
        {
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, 1); UnregisterHotKey(h, 2);
            bool a = RegisterHotKey(h, 1, (uint)cfg.RecMod | 0x4000, (uint)cfg.RecVk);
            bool b = RegisterHotKey(h, 2, (uint)cfg.ClipMod | 0x4000, (uint)cfg.ClipVk);
            UpdateKeyLabels();
            if (!a || !b) MessageBox.Show("Un de ces raccourcis est déjà utilisé par un autre programme. Choisis-en un autre dans Paramètres.", "TynoRec");
        }

        void UpdateKeyLabels()
        {
            var c = KeyName(cfg.ClipMod, cfg.ClipVk); var r = KeyName(cfg.RecMod, cfg.RecVk);
            kClip.Content = capturing == "clip" ? Hint() : c;
            kRec.Content = capturing == "rec" ? Hint() : r;
            chipClip.Text = c; chipRec.Text = r;
            sideTip.Text = $"{c} clip  ·  {r} enregistrer";
            bClip.Content = $"✂  Sauver le clip  ({c})";
            chipTopClip.Text = c; chipTopRec.Text = r;
            string D(int sec) => sec >= 60 && sec % 60 == 0 ? sec / 60 + " min" : sec + " s";
            topClipLbl.Text = cfg.AfterSec > 0 ? $"Clip {D(cfg.BufSec)} + {D(cfg.AfterSec)}" : $"Clip {D(cfg.BufSec)}";
            var pt2 = (string)kPop.Tag;
            kPop.Content = capturing != null && capturing == pt2 ? Hint() : pt2 == "rec" ? r : c;
            bRec.Content = recStart == null ? $"● REC  ({r})" : $"■ STOP  ({r})";
        }

        void OpenPop(string mode)
        {
            if (hkPanel.Visibility == Visibility.Visible && (string)kPop.Tag == mode) { hkPanel.Visibility = Visibility.Collapsed; return; }
            kPop.Tag = mode; capturing = null; capPanel.Visibility = Visibility.Collapsed;
            hkTitle.Text = mode == "clip" ? "CLIPS" : "ENREGISTREMENT LONG";
            hkDesc.Text = mode == "clip" ? "Appuie sur la touche : TynoRec garde les secondes d'avant, continue un peu après, puis s'arrête tout seul." : "Appuie sur la touche pour démarrer, puis pour arrêter l'enregistrement.";
            hkClipOpts.Visibility = mode == "clip" ? Visibility.Visible : Visibility.Collapsed;
            hkPanel.Visibility = Visibility.Visible; UpdateKeyLabels();
        }
        void OpenClipPop(object s, RoutedEventArgs e) => OpenPop("clip");
        void OpenRecPop(object s, RoutedEventArgs e) => OpenPop("rec");
        void ClosePop(object s, RoutedEventArgs e) { hkPanel.Visibility = Visibility.Collapsed; capturing = null; UpdateKeyLabels(); }

        void KeyPick(object s, RoutedEventArgs e) { capturing = (string)((Button)s).Tag; capHint = ""; UpdateKeyLabels(); }

        void OnKey(object s, KeyEventArgs e)
        {
            if (capturing != null)
            {
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                e.Handled = true;
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
                if (k == Key.Escape) { capturing = null; UpdateKeyLabels(); return; }
                int mod = 0; var m = Keyboard.Modifiers;
                if (m.HasFlag(ModifierKeys.Alt)) mod |= 1; if (m.HasFlag(ModifierKeys.Control)) mod |= 2; if (m.HasFlag(ModifierKeys.Shift)) mod |= 4; if (m.HasFlag(ModifierKeys.Windows)) mod |= 8;
                int vk = KeyInterop.VirtualKeyFromKey(k);
                bool fkey = (vk >= 0x70 && vk <= 0x87) || vk == 0x13 || vk == 0x91 || vk == 0x2C;   // F1-F24, Pause, Arrêt défil, Impr écran
                if (mod == 0 && !fkey) { capHint = "Ajoute Ctrl / Alt / Maj"; UpdateKeyLabels(); return; }   // une lettre seule bloquerait la frappe partout
                capHint = "";
                if (capturing == "clip") { cfg.ClipMod = mod; cfg.ClipVk = vk; } else { cfg.RecMod = mod; cfg.RecVk = vk; }
                capturing = null; Persist(); RegHotkeys();
                return;
            }
            if (player.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Escape) { PlayerClose(null, null); e.Handled = true; }
            else if (e.Key == Key.Space) { TogglePlay(null, null); e.Handled = true; }
            else if (e.Key == Key.Left) { Skip(-5); e.Handled = true; }
            else if (e.Key == Key.Right) { Skip(5); e.Handled = true; }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        const string PlayD = "M7,4 L20,12 L7,20 Z";
        const string PauseD = "M7,4 H10 V20 H7 Z M14,4 H17 V20 H14 Z";
        static System.Windows.UIElement MkIcon(string d, double size, bool fill)
        {
            var p = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse(d), StrokeThickness = 1.8,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round, StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round };
            System.Windows.Data.Binding B() => new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Button), 1) };
            p.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, B());
            if (fill) p.SetBinding(System.Windows.Shapes.Shape.FillProperty, B());
            var cv = new System.Windows.Controls.Canvas { Width = 24, Height = 24 };
            cv.Children.Add(p);
            return new System.Windows.Controls.Viewbox { Width = size, Height = size, Child = cv };
        }

        // ---------- recadrage (rognage) ----------
        double trimA, trimB; string trimDrag; bool trimBusy; DateTime trimSeek = DateTime.MinValue;

        void TrimReset() { trimA = 0; trimB = Dur; TrimLayout(); }

        void TrimLayout()
        {
            double d = Dur, w = tl.ActualWidth;
            if (d <= 0 || w <= 0) { trimCv.Visibility = Visibility.Collapsed; bTrim.Visibility = Visibility.Collapsed; return; }
            trimCv.Visibility = Visibility.Visible;
            double xa = trimA / d * w, xb = trimB / d * w;
            Canvas.SetLeft(dimL, 0); dimL.Width = Math.Max(0, xa);
            Canvas.SetLeft(dimR, xb); dimR.Width = Math.Max(0, w - xb);
            Canvas.SetLeft(trimFrame, xa); trimFrame.Width = Math.Max(0, xb - xa);
            Canvas.SetLeft(hL, xa); Canvas.SetLeft(hR, Math.Max(xa, xb - 14));
            bTrim.Visibility = (!trimBusy && (trimA > 0.05 || trimB < d - 0.05)) ? Visibility.Visible : Visibility.Collapsed;
        }

        void TrimDown(object s, MouseButtonEventArgs e)
        {
            trimDrag = (string)((FrameworkElement)s).Tag;
            if (playing) media.Pause();
            ((UIElement)s).CaptureMouse(); e.Handled = true;
        }
        void TrimMove(object s, MouseEventArgs e)
        {
            if (trimDrag == null) return;
            double t = TlTime(e.GetPosition(tl).X);
            if (trimDrag == "a") trimA = Math.Max(0, Math.Min(t, trimB - 0.5)); else trimB = Math.Min(Dur, Math.Max(t, trimA + 0.5));
            TrimLayout();
            double pv = trimDrag == "a" ? trimA : trimB;
            TlShow(pv);
            if ((DateTime.Now - trimSeek).TotalMilliseconds > 90) { trimSeek = DateTime.Now; media.Position = TimeSpan.FromSeconds(pv); }
            e.Handled = true;
        }
        void TrimUp(object s, MouseButtonEventArgs e)
        {
            if (trimDrag == null) return;
            ((UIElement)s).ReleaseMouseCapture();
            media.Position = TimeSpan.FromSeconds(trimDrag == "a" ? trimA : trimB);
            trimDrag = null; e.Handled = true;
            if (playing) media.Play();
        }

        async void TrimSave(object s, RoutedEventArgs e)
        {
            if (cur == null || Dur <= 0 || trimBusy) return;
            trimBusy = true; TrimLayout();
            double a = trimA, b = trimB; var src = cur.Path; var game = cur.Game;
            var baseStamp = $"Clip_{DateTime.Now:yyyyMMdd_HHmmss}"; var stamp = baseStamp; int n = 1;
            while (File.Exists(Path.Combine(cfg.Out, stamp + ".mp4"))) stamp = baseStamp + "_" + (++n);
            var outFile = Path.Combine(cfg.Out, stamp + ".mp4");
            Toast("Rognage en cours…");
            int code = await Task.Run(() =>
            {
                int Run(string args)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(FF, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                        using var p = Process.Start(psi); try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode;
                    }
                    catch { return -1; }
                }
                var ci = CultureInfo.InvariantCulture;
                string ss = a.ToString("0.###", ci), t = (b - a).ToString("0.###", ci);
                int rc = Run($"-y -loglevel error -ss {ss} -i \"{src}\" -t {t} -c:v libx264 -preset fast -crf 17 -c:a aac -b:a 192k -movflags +faststart \"{outFile}\"");
                if (rc != 0 || !File.Exists(outFile))
                {
                    try { File.Delete(outFile); } catch { }
                    rc = Run($"-y -loglevel error -ss {ss} -i \"{src}\" -t {t} -c copy -movflags +faststart \"{outFile}\"");
                }
                return rc;
            });
            trimBusy = false;
            if (code == 0 && File.Exists(outFile))
            {
                meta[stamp + ".mp4"] = new ClipMeta { Game = game, Uid = Cloud.UserId }; SaveMeta(); QueueSync();
                LibRefresh(null, null); Toast("✔ Clip rogné sauvegardé");
            }
            else { try { File.Delete(outFile); } catch { } Toast("✖ Rognage impossible"); }
            TrimLayout();
        }

        void WinMin(object s, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void WinMax(object s, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        void WinClose(object s, RoutedEventArgs e) { Close(); }
        void BellClick(object s, RoutedEventArgs e) { bellPop.IsOpen = true; }
        void TopDrag(object s, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { WinMax(s, e); return; }
            if (e.LeftButton != MouseButtonState.Pressed) return;
            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    var pos = e.GetPosition(this);
                    double ratio = pos.X / ActualWidth;
                    var sp = PresentationSource.FromVisual(this).CompositionTarget.TransformFromDevice.Transform(PointToScreen(pos));
                    WindowState = WindowState.Normal;
                    Left = sp.X - Width * ratio;
                    Top = sp.Y - pos.Y;
                }
                DragMove();
            }
            catch { }
        }

        string avFor;
        string AvDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TynoRec");
        string AvPath => System.IO.Path.Combine(AvDir, "avatar_" + Cloud.UserId + ".png");
        void AvLoad()
        {
            avFor = Cloud.UserId;
            try
            {
                if (!string.IsNullOrEmpty(avFor) && System.IO.File.Exists(AvPath))
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.UriSource = new Uri(AvPath); bi.EndInit(); bi.Freeze();
                    avBtn.Background = new System.Windows.Media.ImageBrush(bi) { Stretch = System.Windows.Media.Stretch.UniformToFill };
                    avLetter.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            catch { }
            avBtn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0x8D, 0xFF));
            avLetter.Visibility = Visibility.Visible;
        }
        void AvPick(object s, RoutedEventArgs e)
        {
            if (!Cloud.LoggedIn) return;
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog() != true) return;
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit(); bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 256; bi.UriSource = new Uri(d.FileName); bi.EndInit();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bi));
                System.IO.Directory.CreateDirectory(AvDir);
                using (var fs = System.IO.File.Create(AvPath)) enc.Save(fs);
                AvLoad(); if (pProfile.Visibility == Visibility.Visible) ProfRefresh();
            }
            catch { MessageBox.Show("Image illisible."); }
        }
        void AvClick(object s, RoutedEventArgs e) { AvRefresh(); avPop.IsOpen = true; }
        DateTime notifPause = DateTime.MinValue;
        void NotifPauseClick(object s, RoutedEventArgs e) { avSub.IsOpen = !avSub.IsOpen; }
        void NotifPauseSet(object s, RoutedEventArgs e)
        {
            int m = int.Parse((string)((Button)s).Tag);
            notifPause = m > 0 ? DateTime.Now.AddMinutes(m) : DateTime.MinValue;
            avPauseTxt.Text = m > 0 ? "En pause jusqu'à " + notifPause.ToString("HH:mm") : "Mettre les notifications en pause";
            avSub.IsOpen = false; avPop.IsOpen = false;
        }
        void AvSettings(object s, RoutedEventArgs e) { avPop.IsOpen = false; Nav(nSet, e); }

        void OpenProfile(object s, RoutedEventArgs e)
        {
            avPop.IsOpen = false; avSub.IsOpen = false;
            hkPanel.Visibility = Visibility.Collapsed; capPanel.Visibility = Visibility.Collapsed;
            pRec.Visibility = pQual.Visibility = pSet.Visibility = pLib.Visibility = Visibility.Collapsed;
            foreach (var b in new[] { nRec, nQual, nLib, nSet }) { b.Background = Brushes.Transparent; b.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB6)); }
            pProfile.Visibility = Visibility.Visible; ProfRefresh();
            if (profT == null) { profT = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; profT.Tick += (_, _) => { if (pProfile.Visibility != Visibility.Visible) profT.Stop(); else BubbleUpdate(); }; }
            profT.Start();
        }

        // ---- bulle "Lecture <jeu> <temps>" : uniquement un vrai jeu lancé ----
        DispatcherTimer profT;
        static readonly HashSet<string> NonGames = new(StringComparer.OrdinalIgnoreCase) { "Navigateur", "Bureau", "Capture", "—" };
        string curGame; int curGamePid; DateTime curGameStart;

        void TrackGame(Process p, string name)
        {
            if (NonGames.Contains(name)) return;
            DateTime st; try { st = p.StartTime; } catch { st = curGamePid == p.Id ? curGameStart : DateTime.Now; }
            curGame = name; curGamePid = p.Id; curGameStart = st;
        }

        void ScanGames()
        {
            foreach (var kv in GameAlias)
            {
                if (NonGames.Contains(kv.Value)) continue;
                var ps = Process.GetProcessesByName(kv.Key);
                try { if (ps.Length > 0) { TrackGame(ps[0], kv.Value); return; } }
                finally { foreach (var q in ps) q.Dispose(); }
            }
        }

        bool GameAlive()
        {
            if (curGamePid == 0) return false;
            try { using var p = Process.GetProcessById(curGamePid); return !p.HasExited; } catch { return false; }
        }

        void BubbleUpdate()
        {
            try { ScanGames(); } catch { }
            bool on = GameAlive();
            pNowBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            var t = DateTime.Now - curGameStart; if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            pNow.Text = curGame;
            pPlayed.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
            pNowIc.Source = GameIcon(curGame);
        }

        string BanPath => System.IO.Path.Combine(AvDir, "banner_" + Cloud.UserId + ".png");
        void ProfRefresh()
        {
            AvRefresh();
            pName.Text = avName.Text; pAv.Background = avBtn.Background; pAvLetter.Text = avLetter.Text; pAvLetter.Visibility = avLetter.Visibility;
            var mine = meta.Values.Where(q => q.Uid == null || q.Uid == Cloud.UserId).ToList();
            pClips.Text = mine.Count + (mine.Count > 1 ? " clips" : " clip"); pFavs.Text = mine.Count(q => q.Fav) + " favoris";
            BubbleUpdate();
            try
            {
                if (File.Exists(BanPath))
                {
                    var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(BanPath); bi.EndInit(); bi.Freeze();
                    banner.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill }; banHint.Visibility = Visibility.Collapsed; return;
                }
            }
            catch { }
            banner.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x2A, 0x38)); banHint.Visibility = Visibility.Visible;
        }
        void BannerPick(object s, MouseButtonEventArgs e)
        {
            if (!Cloud.LoggedIn) return;
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.webp" };
            if (d.ShowDialog() != true) return;
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.DecodePixelWidth = 1400; bi.UriSource = new Uri(d.FileName); bi.EndInit();
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bi));
                Directory.CreateDirectory(AvDir);
                using (var fs = File.Create(BanPath)) enc.Save(fs);
                ProfRefresh();
            }
            catch { Toast("Image illisible"); }
        }

        void AvLogout(object s, RoutedEventArgs e) { avPop.IsOpen = false; Logout(s, e); }
        void AvRefresh()
        {
            if (avFor != Cloud.UserId) AvLoad();
            var n = string.IsNullOrEmpty(Cloud.Username) ? Cloud.Email : Cloud.Username;
            avName.Text = n ?? "Non connecté";
            avLetter.Text = string.IsNullOrEmpty(n) ? "?" : n.Substring(0, 1).ToUpper();
            avPopLetter.Text = avLetter.Text; avPopLetter.Visibility = avLetter.Visibility; avPopAv.Background = avBtn.Background;
        }
    }
}
