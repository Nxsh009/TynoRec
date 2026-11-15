using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace TynoRec
{
    public partial class App : Application
    {
        static Mutex single;

        public App()
        {
            DispatcherUnhandledException += (_, e) => { Report(e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, false);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception, false); e.SetObserved(); };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // une seule instance : deux TynoRec = deux captures + raccourcis en conflit
            single = new Mutex(true, "Local\\TynoRec.SingleInstance", out var created);
            if (!created) { MessageBox.Show("TynoRec est déjà ouvert.", "TynoRec"); Shutdown(); return; }
            base.OnStartup(e);
            // vérifie les mises à jour GitHub quelques secondes après le démarrage (silencieux si rien / pas de réseau)
            _ = System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ => Dispatcher.InvokeAsync(() => Updater.CheckAsync()), System.Threading.Tasks.TaskScheduler.Default);
        }

        static void Report(Exception ex, bool popup = true)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TynoRec");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now + "\n" + ex + "\n\n");
                // pas de popup si la fenêtre n'est pas au premier plan (ex. en pleine partie) : ça te sortirait du jeu
                var w = Current?.MainWindow;
                if (popup && w != null && w.IsActive) MessageBox.Show(ex?.ToString(), "TynoRec — erreur");
            }
            catch { }
        }
    }
}
