using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace TynoRec
{
    /// <summary>Auto-update via GitHub Releases : vérifie la dernière release, télécharge un petit zip (sans ffmpeg), remplace les fichiers et relance.</summary>
    static class Updater
    {
        // ====== À REMPLIR ======
        public const string Repo = "Nxsh009/TynoRec";   // ex : "mango/TynoRec" (le dépôt doit être public)
        const string AssetName = "TynoRec-update.zip";            // nom de l'asset joint à chaque release
        // =======================

        static readonly HttpClient http = CreateClient();
        static bool running;

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("TynoRec-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
                using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                if (!resp.IsSuccessStatusCode) { if (!silent) Info("Impossible de vérifier les mises à jour (" + (int)resp.StatusCode + ")."); return; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var tag = root.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return;
                if (Norm(latest) <= Norm(Current)) { if (!silent) Info("TynoRec est à jour (v" + Current.ToString(3) + ")."); return; }

                string url = null;
                foreach (var a in root.GetProperty("assets").EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                        url = a.GetProperty("browser_download_url").GetString();
                if (url == null) return;

                var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
                var ask = MessageBox.Show(
                    $"Une nouvelle version de TynoRec est disponible : v{latest.ToString(3)} (tu as la v{Current.ToString(3)}).\n\n{notes}\n\nInstaller maintenant ? TynoRec va redémarrer.",
                    "TynoRec — mise à jour", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ask != MessageBoxResult.Yes) return;

                await InstallAsync(url);
            }
            catch (Exception ex)
            {
                if (!silent) Info("Échec de la mise à jour : " + ex.Message);
            }
            finally { running = false; }
        }

        static Version Norm(Version v) => new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        static void Info(string m) => MessageBox.Show(m, "TynoRec", MessageBoxButton.OK, MessageBoxImage.Information);

        static async Task InstallAsync(string url)
        {
            var work = Path.Combine(Path.GetTempPath(), "TynoRecUpdate");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            var zip = Path.Combine(work, "update.zip");
            var src = Path.Combine(work, "files");
            using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                r.EnsureSuccessStatusCode();
                using var fs = File.Create(zip);
                await r.Content.CopyToAsync(fs);
            }
            ZipFile.ExtractToDirectory(zip, src, true);

            // le zip doit contenir l'exe : sinon on n'écrase rien
            if (!File.Exists(Path.Combine(src, "TynoRec.exe"))) throw new Exception("Archive de mise à jour invalide.");

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
            Environment.Exit(0);
        }
    }
}
