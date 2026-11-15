using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TynoRec
{
    // Icône dans la zone de notification (à côté de l'horloge) : fermer la fenêtre garde TynoRec actif en arrière-plan.
    public partial class MainWindow
    {
        const int WM_TRAY = 0x8001;
        bool exiting, trayAdded; uint wmTaskbarCreated; System.Drawing.Icon trayIcon; Window trayMenu;

        [StructLayout(LayoutKind.Sequential)] struct TrayPt { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct TrayRect { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct TrayMonInfo { public int cbSize; public TrayRect rcMonitor, rcWork; public int dwFlags; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TrayData
        {
            public int cbSize; public IntPtr hWnd; public int uID; public int uFlags; public int uCallbackMessage; public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] static extern bool TrayNotify(int msg, ref TrayData d);
        [DllImport("user32.dll", EntryPoint = "GetCursorPos")] static extern bool TrayGetCursorPos(out TrayPt p);
        [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")] static extern IntPtr TrayMonFromPt(TrayPt p, int flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] static extern bool TrayGetMonInfo(IntPtr h, ref TrayMonInfo mi);
        [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] static extern bool TraySetFg(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] static extern uint TrayRegMsg(string s);

        void InitTray()
        {
            wmTaskbarCreated = TrayRegMsg("TaskbarCreated");          // l'explorateur a redémarré : on remet l'icône
            if (Application.Current != null) Application.Current.SessionEnding += (_, _) => exiting = true;   // extinction de Windows : pas de blocage
            AddTray();
        }

        TrayData NewTray()
        {
            var d = new TrayData { hWnd = new WindowInteropHelper(this).Handle, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };
            d.cbSize = Marshal.SizeOf<TrayData>();
            return d;
        }

        void AddTray()
        {
            try
            {
                trayIcon ??= System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (trayIcon == null) return;
                var d = NewTray();
                d.uFlags = 1 | 2 | 4;                                  // message + icône + infobulle
                d.uCallbackMessage = WM_TRAY; d.hIcon = trayIcon.Handle; d.szTip = "TynoRec";
                trayAdded = TrayNotify(0, ref d);
            }
            catch { trayAdded = false; }
        }

        void RemoveTray()
        {
            try { if (trayAdded) { var d = NewTray(); TrayNotify(2, ref d); trayAdded = false; } } catch { }
            try { trayMenu?.Close(); } catch { }
        }

        void TrayBalloon(string title, string text)
        {
            try { var d = NewTray(); d.uFlags = 0x10; d.szInfoTitle = title; d.szInfo = text; d.dwInfoFlags = 1; TrayNotify(1, ref d); } catch { }
        }

        // appelé par Hook() pour chaque message Windows ; renvoie true si c'était pour l'icône
        bool TrayHook(int msg, IntPtr l)
        {
            if (wmTaskbarCreated != 0 && msg == (int)wmTaskbarCreated) { trayAdded = false; AddTray(); return false; }
            if (msg != WM_TRAY) return false;
            int ev = (int)(l.ToInt64() & 0xFFFF);
            if (ev == 0x202 || ev == 0x203) Dispatcher.BeginInvoke(new Action(ShowFromTray));            // clic gauche / double-clic : ouvrir
            else if (ev == 0x205) { TrayGetCursorPos(out var pt); Dispatcher.BeginInvoke(new Action(() => ShowTrayMenu(pt))); }   // clic droit : menu
            return true;
        }

        void HideToTray(bool silent = false)
        {
            if (!trayAdded) AddTray();
            if (!trayAdded) { WindowState = WindowState.Minimized; return; }   // pas d'icône = pas de fenêtre cachée sans moyen de revenir
            try { trayMenu?.Close(); } catch { }
            Hide();
            if (!silent && !cfg.TrayTip)
            {
                cfg.TrayTip = true; Persist();
                TrayBalloon("TynoRec tourne en arrière-plan", "La capture et tes raccourcis restent actifs. Clic sur l'icône pour rouvrir, clic droit pour quitter.");
            }
        }

        void ShowFromTray()
        {
            try { trayMenu?.Close(); } catch { }
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
            Activate(); Topmost = true; Topmost = false; Focus();
        }

        void QuitApp()
        {
            if (recStart != null) ShowFromTray();     // la confirmation s'affiche sur la fenêtre
            exiting = true; Close();
        }

        // ---------- menu clic droit ----------
        static Brush Hx(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        Window BuildTrayMenu()
        {
            var red = Hx("#FF8097");
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, Opacity = 0, FontFamily = FontFamily
            };
            var root = new StackPanel();

            // en-tête : logo + nom + état
            string state; Brush dot;
            if (recStart != null) { state = "Enregistrement en cours"; dot = Hx("#FF5C73"); }
            else if (cap != null) { state = "Capture active"; dot = Hx("#4ADE80"); }
            else { state = "Capture arrêtée"; dot = Hx("#8B93A7"); }
            var head = new Grid { Margin = new Thickness(6, 4, 6, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var logo = new Image { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            head.Children.Add(logo);
            var names = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = "TynoRec", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Hx("#F1F4FA") });
            var st = new TextBlock { FontSize = 12, Foreground = Hx("#9AA2B6"), Margin = new Thickness(0, 1, 0, 0) };
            st.Inlines.Add(new System.Windows.Documents.Run("● ") { Foreground = dot });
            st.Inlines.Add(new System.Windows.Documents.Run(state));
            names.Children.Add(st);
            Grid.SetColumn(names, 1); head.Children.Add(names);
            root.Children.Add(head);
            root.Children.Add(Sep());

            root.Children.Add(Item(w, "\uE8A7", "Ouvrir TynoRec", ShowFromTray));
            root.Children.Add(Item(w, "\uE74E", "Sauvegarder un clip", () => SaveClip(null, null), false, cap != null));
            root.Children.Add(Item(w, recStart != null ? "\uE71A" : "\uE768", recStart != null ? "Arrêter l'enregistrement" : "Démarrer l'enregistrement", () => ToggleRec(null, null)));
            root.Children.Add(Item(w, "\uE713", "Paramètres", () => { ShowFromTray(); Nav(nSet, new RoutedEventArgs()); }));
            root.Children.Add(Sep());
            root.Children.Add(Item(w, "\uE7E8", "Quitter", QuitApp, true));

            var card = new Border
            {
                Width = 270, Background = Hx("#12151C"), BorderBrush = Hx("#2A3141"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(8),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.55, Color = Colors.Black }, Child = root
            };
            w.Content = new Grid { Margin = new Thickness(14), Children = { card } };
            return w;
        }

        static Border Sep() => new Border { Height = 1, Background = Hx("#1F2430"), Margin = new Thickness(6, 4, 6, 4) };

        Border Item(Window w, string glyph, string text, Action act, bool danger = false, bool enabled = true)
        {
            var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Width = 22, VerticalAlignment = VerticalAlignment.Center, Foreground = danger ? Hx("#FF8097") : Hx("#9DC1FF") };
            var tx = new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = danger ? Hx("#FF8097") : Hx("#F1F4FA") };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(ic); sp.Children.Add(tx);
            var b = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 1, 0, 1), Child = sp, Opacity = enabled ? 1 : 0.4 };
            if (enabled)
            {
                b.Cursor = Cursors.Hand;
                var hover = Hx(danger ? "#2A1620" : "#1B202B");
                b.MouseEnter += (_, _) => b.Background = hover;
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
                b.MouseLeftButtonUp += (_, _) => { try { w.Close(); } catch { } Dispatcher.BeginInvoke(act); };
            }
            return b;
        }

        void ShowTrayMenu(TrayPt pt)
        {
            try { trayMenu?.Close(); } catch { }
            var w = BuildTrayMenu(); trayMenu = w;
            w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.Deactivated += (_, _) => { try { w.Close(); } catch { } };
            w.Closed += (_, _) => { if (ReferenceEquals(trayMenu, w)) trayMenu = null; };
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };       // filet de sécurité si Windows refuse de donner le focus
            guard.Tick += (_, _) => { if (!w.IsVisible) guard.Stop(); else if (!w.IsActive) { guard.Stop(); try { w.Close(); } catch { } } };
            w.Loaded += (_, _) =>
            {
                try
                {
                    var src = PresentationSource.FromVisual(w);
                    double sx = src?.CompositionTarget.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget.TransformFromDevice.M22 ?? 1;
                    var mi = new TrayMonInfo { cbSize = Marshal.SizeOf<TrayMonInfo>() };
                    TrayGetMonInfo(TrayMonFromPt(pt, 2), ref mi);
                    double wl = mi.rcWork.L * sx, wt = mi.rcWork.T * sy, wr = mi.rcWork.R * sx, wb = mi.rcWork.B * sy;
                    double cx = pt.X * sx, cy = pt.Y * sy, ww = w.ActualWidth, hh = w.ActualHeight, m = 14 - 8;   // 14 = marge de l'ombre, 8 = écart avec le bord
                    w.Left = Math.Min(Math.Max(cx - ww / 2, wl - m), wr - ww + m);
                    w.Top = cy >= wb ? wb - hh + m : cy <= wt ? wt - m : Math.Min(Math.Max(cy - hh, wt - m), wb - hh + m);
                }
                catch { }
                w.Opacity = 1; w.Activate();
                try { TraySetFg(new WindowInteropHelper(w).Handle); } catch { }
                guard.Start();
            };
            w.Show();
        }
    }
}
