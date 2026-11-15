using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TynoRec
{
    /// <summary>Auto-update via GitHub Releases : vérifie la dernière release, télécharge un petit zip (sans ffmpeg), remplace les fichiers et relance.</summary>
    static class Updater
    {
        // ====== À REMPLIR ======
        public const string Repo = "Nxsh009/TynoRec";            // le dépôt doit être public
        const string AssetName = "TynoRec-update.zip";            // nom de l'asset joint à chaque release
        // ffmpeg n'est pas dans l'exe (trop gros) : il est téléchargé une seule fois au premier lancement depuis une release GitHub nommée "ffmpeg"
        public const string FFmpegUrl = "https://github.com/" + Repo + "/releases/download/ffmpeg/ffmpeg.zip";
        // =======================

        static readonly HttpClient http = CreateClient();
        static bool running;

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("TynoRec-Updater");
            return c;
        }

        public static Version Current => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

        /// <summary>À appeler depuis le thread UI. silent=true : ne dit rien s'il n'y a pas de mise à jour / pas de réseau.</summary>
        public static async Task CheckAsync(bool silent = true)
        {
            if (running) return;
            running = true;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=15");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) { if (!silent) Info("Impossible de vérifier les mises à jour (" + (int)resp.StatusCode + ")."); return; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                // la plus haute version publiée (on ignore les brouillons, les pré-releases et les releases sans numéro comme "ffmpeg")
                JsonElement root = default; Version latest = null;
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var dr) && dr.GetBoolean()) continue;
                    if (rel.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) continue;
                    var tg = rel.GetProperty("tag_name").GetString() ?? "";
                    if (!Version.TryParse(tg.TrimStart('v', 'V'), out var v)) continue;
                    if (latest == null || v > latest) { latest = v; root = rel; }
                }
                if (latest == null) { if (!silent) Info("Aucune version trouvée sur GitHub."); return; }
                if (Norm(latest) <= Norm(Current)) { if (!silent) Info("TynoRec est à jour (v" + Current.ToString(3) + ")."); return; }

                string url = null;
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                        url = a.GetProperty("browser_download_url").GetString();
                if (url == null) return;

                var notes = CleanNotes(root.TryGetProperty("body", out var b) ? b.GetString() : "");
                var win = new UpdateWindow(Norm(Current), Norm(latest), notes);
                win.Show();
                if (!await win.Ask()) { win.Close(); return; }

                try { await InstallAsync(url, win); }
                catch (Exception ex) { win.Fail("Échec de la mise à jour : " + ex.Message); }
            }
            catch (Exception ex)
            {
                if (!silent) Info("Échec de la mise à jour : " + ex.Message);
            }
            finally { running = false; }
        }

        static Version Norm(Version v) => new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        static void Info(string m) => MessageBox.Show(m, "TynoRec", MessageBoxButton.OK, MessageBoxImage.Information);

        /// <summary>Transforme les notes GitHub (markdown) en texte propre ; vide si rien d'utile.</summary>
        static string CleanNotes(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var cut = s.IndexOf("New Contributors", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0) s = s.Substring(0, cut);
            var sb = new StringBuilder();
            foreach (var raw in s.Replace("\r", "").Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.IndexOf("Full Changelog", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (Regex.IsMatch(l, @"^#+\s*What'?s Changed", RegexOptions.IgnoreCase)) continue;
                l = Regex.Replace(l, @" by @\S+ in \S+$", "");            // "… by @user in https://…"
                l = Regex.Replace(l, @"\[([^\]]+)\]\([^)]*\)", "$1");      // [texte](lien) -> texte
                l = l.Replace("**", "").Replace("`", "").TrimStart('#', ' ');
                if (l.StartsWith("* ") || l.StartsWith("- ")) l = "•  " + l.Substring(2);
                if (l.Length == 0) continue;
                sb.AppendLine(l);
            }
            var t = sb.ToString().Trim();
            return t.Length > 500 ? t.Substring(0, 500) + "…" : t;
        }

        static async Task DownloadAsync(string url, string path, UpdateWindow win)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            r.EnsureSuccessStatusCode();
            var total = r.Content.Headers.ContentLength ?? -1;
            using var s = await r.Content.ReadAsStreamAsync();
            using var fs = File.Create(path);
            var buf = new byte[81920]; long read = 0; int n;
            while ((n = await s.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                await fs.WriteAsync(buf, 0, n);
                read += n;
                if (total > 0) win.SetProgress((double)read / total);
            }
        }

        /// <summary>true si ffmpeg est déjà là (à côté de l'app, dans le dossier de données de TynoRec, ou dans le PATH).</summary>
        public static bool HasFFmpeg(string configured, string dataDir)
        {
            if (string.IsNullOrWhiteSpace(configured)) configured = "ffmpeg.exe";
            if (Path.IsPathRooted(configured)) return File.Exists(configured);
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, configured))) return true;
            if (File.Exists(Path.Combine(dataDir, configured))) return true;
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { if (d.Length > 0 && File.Exists(Path.Combine(d.Trim('"'), configured))) return true; } catch { }
            return false;
        }

        /// <summary>Premier lancement : télécharge ffmpeg.exe dans le dossier de données de TynoRec (une seule fois, jamais touché par les mises à jour).</summary>
        public static async Task EnsureFFmpegAsync(string dir)
        {
            var win = new UpdateWindow("Composant manquant", "TynoRec a besoin de ffmpeg pour enregistrer. Il sera téléchargé une seule fois (environ 90 Mo).", null, null, "", "Télécharger", "Plus tard");
            win.Show();
            if (!await win.Ask()) { win.Close(); return; }
            var closed = new TaskCompletionSource<bool>();
            win.Closed += (_, _) => closed.TrySetResult(true);
            try
            {
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(Path.GetTempPath(), "TynoRecFFmpeg.zip");
                await DownloadAsync(FFmpegUrl, zip, win);
                win.SetStatus("Installation…");
                await Task.Run(() =>
                {
                    using var z = ZipFile.OpenRead(zip);
                    var e = z.Entries.FirstOrDefault(x => x.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("ffmpeg.exe introuvable dans l'archive.");
                    e.ExtractToFile(Path.Combine(dir, "ffmpeg.exe"), true);
                });
                try { File.Delete(zip); } catch { }
                win.SetProgress(1); win.SetStatus("Prêt !");
                await Task.Delay(600);
                win.Close();
            }
            catch (Exception ex)
            {
                win.Fail("Téléchargement impossible : " + ex.Message);
                await closed.Task;
            }
        }

        static async Task InstallAsync(string url, UpdateWindow win)
        {
            var work = Path.Combine(Path.GetTempPath(), "TynoRecUpdate");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            var zip = Path.Combine(work, "update.zip");
            var src = Path.Combine(work, "files");

            await DownloadAsync(url, zip, win);

            win.SetStatus("Installation…");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, src, true));
            if (!File.Exists(Path.Combine(src, "TynoRec.exe"))) throw new Exception("archive de mise à jour invalide.");

            var dst = AppContext.BaseDirectory.TrimEnd('\\');
            var exe = Path.Combine(dst, "TynoRec.exe");
            var pid = Process.GetCurrentProcess().Id;
            var bat = Path.Combine(work, "apply.bat");

            // attend la fermeture de TynoRec, copie les fichiers (avec réessais), relance, nettoie
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("set tries=0");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)");
            sb.AppendLine(":copy");
            sb.AppendLine($"xcopy /y /e /q /i \"{src}\\*\" \"{dst}\\\" >nul");
            sb.AppendLine("if errorlevel 1 (set /a tries+=1 & if %tries% LSS 10 (timeout /t 1 /nobreak >nul & goto copy))");
            sb.AppendLine($"start \"\" \"{exe}\"");
            sb.AppendLine($"rmdir /s /q \"{src}\" >nul 2>&1");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            win.SetProgress(1); win.SetStatus("Redémarrage…");
            await Task.Delay(700);
            Environment.Exit(0);
        }
    }

    /// <summary>Fenêtre de mise à jour aux couleurs de l'app (fond sombre, accent bleu).</summary>
    class UpdateWindow : Window
    {
        readonly TaskCompletionSource<bool> choice = new();
        readonly Button yes, no;
        readonly TextBlock status;
        readonly Grid bar;
        readonly ColumnDefinition c1, c2;
        readonly StackPanel progress;
        bool busy;

        static Brush Hx(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);

        const string Tpl = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='10' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.86'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger></ControlTemplate.Triggers></ControlTemplate>";

        Button MakeButton(string text, bool primary)
        {
            Brush bg = primary
                ? new LinearGradientBrush(Color.FromRgb(0x5B, 0x9B, 0xFF), Color.FromRgb(0x3A, 0x6F, 0xF0), new Point(0, 0), new Point(1, 1))
                : Hx("#1F2430");
            return new Button
            {
                Content = text, Template = (ControlTemplate)XamlReader.Parse(Tpl), Cursor = Cursors.Hand,
                Background = bg, BorderBrush = primary ? Brushes.Transparent : Hx("#2D3445"), BorderThickness = new Thickness(primary ? 0 : 1),
                Foreground = primary ? Brushes.White : Hx("#B7BDCC"), FontWeight = FontWeights.SemiBold, FontSize = 13.5,
                Padding = new Thickness(22, 10, 22, 10), Margin = new Thickness(primary ? 10 : 0, 0, 0, 0), Focusable = false
            };
        }

        public UpdateWindow(Version cur, Version latest, string notes)
            : this("Mise à jour disponible", "Une nouvelle version de TynoRec est prête.", "v" + cur.ToString(3), "v" + latest.ToString(3), notes, "Mettre à jour", "Plus tard") { }

        public UpdateWindow(string title, string sub, string pillLeft, string pillRight, string notes, string yesText, string noText)
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height; Width = 448; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"); Foreground = Hx("#F1F4FA"); Title = "TynoRec — mise à jour";

            var body = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };

            // en-tête : logo + titre
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            var logo = new Image { Width = 46, Height = 46, Margin = new Thickness(0, 0, 14, 0) };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
            head.Children.Add(logo);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold });
            titles.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = Hx("#8B93A7"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(titles, 1); head.Children.Add(titles);
            body.Children.Add(head);

            // pastille de version (absente pour un simple téléchargement)
            if (pillRight != null)
            {
                var pill = new Border { CornerRadius = new CornerRadius(8), Background = Hx("#16284A"), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) };
                var pt = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hx("#6FA3FF") };
                pt.Inlines.Add(new System.Windows.Documents.Run(pillLeft) { Foreground = Hx("#8B93A7") });
                pt.Inlines.Add(new System.Windows.Documents.Run("   →   "));
                pt.Inlines.Add(new System.Windows.Documents.Run(pillRight));
                pill.Child = pt;
                body.Children.Add(pill);
            }

            // nouveautés (seulement s'il y en a)
            if (!string.IsNullOrWhiteSpace(notes))
            {
                var nb = new Border { CornerRadius = new CornerRadius(10), Background = Hx("#171B24"), BorderBrush = Hx("#232A38"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 0) };
                var sv = new ScrollViewer { MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 10, 14, 10) };
                sv.Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Hx("#B7BDCC"), LineHeight = 19 };
                nb.Child = sv;
                body.Children.Add(nb);
            }

            // progression (cachée tant que l'utilisateur n'a pas cliqué)
            progress = new StackPanel { Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
            status = new TextBlock { Text = "Téléchargement…", FontSize = 12.5, Foreground = Hx("#B7BDCC"), Margin = new Thickness(0, 0, 0, 8) };
            progress.Children.Add(status);
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Hx("#262D3C"), ClipToBounds = true };
            bar = new Grid();
            c1 = new ColumnDefinition { Width = new GridLength(0.0001, GridUnitType.Star) };
            c2 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(c1); bar.ColumnDefinitions.Add(c2);
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new LinearGradientBrush(Color.FromRgb(0x5B, 0x9B, 0xFF), Color.FromRgb(0x3A, 0x6F, 0xF0), 0) });
            track.Child = bar;
            progress.Children.Add(track);
            body.Children.Add(progress);

            // boutons
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
            no = MakeButton(noText, false);
            yes = MakeButton(yesText, true);
            no.Click += (_, _) => choice.TrySetResult(false);
            yes.Click += (_, _) => { busy = true; yes.IsEnabled = false; no.IsEnabled = false; progress.Visibility = Visibility.Visible; choice.TrySetResult(true); };
            btns.Children.Add(no); btns.Children.Add(yes);
            body.Children.Add(btns);

            Content = new Border
            {
                Margin = new Thickness(16), CornerRadius = new CornerRadius(16), Background = Hx("#12151C"),
                BorderBrush = Hx("#2A3141"), BorderThickness = new Thickness(1), Child = body,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.55 }
            };

            MouseLeftButtonDown += (_, e) => { try { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); } catch { } };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) { choice.TrySetResult(false); Close(); } };
            Closed += (_, _) => choice.TrySetResult(false);
        }

        public Task<bool> Ask() => choice.Task;

        public void SetStatus(string t) => status.Text = t;

        public void SetProgress(double p)
        {
            p = Math.Min(1, Math.Max(0.0001, p));
            c1.Width = new GridLength(p, GridUnitType.Star);
            c2.Width = new GridLength(1 - p, GridUnitType.Star);
            if (p < 1) status.Text = "Téléchargement… " + (int)(p * 100) + " %";
        }

        public void Fail(string msg)
        {
            busy = false;
            status.Text = msg; status.Foreground = Hx("#FF8097");
            no.Content = "Fermer"; no.IsEnabled = true; yes.Visibility = Visibility.Collapsed;
            no.Click += (_, _) => Close();
        }
    }
}
