using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TynoRec
{
    // Comptes (email + mot de passe + réinitialisation) et sauvegarde des clips dans le compte.
    public partial class MainWindow
    {
        string authMode = "signup", resetEmail = "";
        bool authBusy, recoveryVerified, syncing, syncAgain;
        List<string> pendingDel = new();                       // suppressions à répercuter sur le serveur ("uid|nom.mp4")
        readonly HashSet<string> tooBig = new();               // clips refusés par le serveur (taille) : pas de nouvelle tentative
        string pendingPath => Path.Combine(dataDir, "pending_del.json");

        // ---------- démarrage ----------
        async void InitAccount()
        {
            try { pendingDel = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(pendingPath)) ?? new(); } catch { pendingDel = new(); }
            bool ok = false;
            try { ok = await Cloud.Restore(); } catch { }
            if (ok) { AuthDone(); return; }
            authMode = "signup"; AuthLayout();
            authWait.Visibility = Visibility.Collapsed; authForm.Visibility = Visibility.Visible;
            aEmail.Focus();
        }

        void AuthDone()
        {
            authLayer.Visibility = Visibility.Collapsed;
            aEmail.Text = aUser.Text = aCode.Text = ""; aPass.Password = ""; aPassTxt.Text = "";
            AuthErr(null); AuthInfo(null); recoveryVerified = false;
            AccountUi();
            LibRefresh(null, null);
            QueueSync();
        }

        void AccountUi()
        {
            var n = string.IsNullOrWhiteSpace(Cloud.Username) ? Cloud.Email : Cloud.Username;
            sideAcc.Text = Cloud.LoggedIn ? "👤  " + n : "";
            setAcc.Text = Cloud.LoggedIn ? $"{n}\n{Cloud.Email}" : "Non connecté";
            cCloud.IsChecked = cfg.CloudSync;
        }

        async void Logout(object s, RoutedEventArgs e)
        {
            if (MessageBox.Show("Se déconnecter ? Tes clips restent liés à ton compte.", "TynoRec", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await Cloud.SignOut();
            AccountUi(); LibRefresh(null, null);
            authMode = "login"; AuthLayout();
            authWait.Visibility = Visibility.Collapsed; authForm.Visibility = Visibility.Visible; authLayer.Visibility = Visibility.Visible;
        }

        // ---------- écran de connexion ----------
        string Pw => aEye.IsChecked == true ? aPassTxt.Text : aPass.Password;

        void AuthErr(string t) { aErr.Text = t ?? ""; aErr.Visibility = string.IsNullOrEmpty(t) ? Visibility.Collapsed : Visibility.Visible; if (t != null) AuthInfo(null); }
        void AuthInfo(string t) { aInfo.Text = t ?? ""; aInfo.Visibility = string.IsNullOrEmpty(t) ? Visibility.Collapsed : Visibility.Visible; }

        void AuthLayout()
        {
            bool signup = authMode == "signup", login = authMode == "login", forgot = authMode == "forgot", reset = authMode == "reset";
            static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
            aTitle.Text = signup ? "Créer un compte" : login ? "Connexion" : forgot ? "Mot de passe oublié" : "Nouveau mot de passe";
            aHint.Text = forgot ? "Entre ton email : on t'envoie un code pour choisir un nouveau mot de passe."
                       : reset ? "Entre le code reçu par email, puis ton nouveau mot de passe." : "";
            aHint.Visibility = V(aHint.Text.Length > 0);
            aFEmail.Visibility = V(!reset);
            aFUser.Visibility = V(signup);
            aFCode.Visibility = V(reset && !recoveryVerified);
            aFPass.Visibility = V(!forgot);
            aPassLbl.Text = reset ? "NOUVEAU MOT DE PASSE" : "MOT DE PASSE";
            aGo.Content = signup ? "Créer mon compte" : login ? "Se connecter" : forgot ? "Envoyer le code" : "Changer le mot de passe";
            aLinkForgot.Visibility = V(login);
            aLinkSwitch.Text = signup ? "Tu as déjà un compte ? Connecte-toi ici" : login ? "Pas encore de compte ? Crée-en un" : "← Retour à la connexion";
        }

        void AuthSwitch(object s, MouseButtonEventArgs e)
        {
            authMode = authMode == "login" ? "signup" : "login";
            recoveryVerified = false; AuthErr(null); AuthInfo(null); AuthLayout();
        }

        void AuthForgot(object s, MouseButtonEventArgs e) { authMode = "forgot"; AuthErr(null); AuthInfo(null); AuthLayout(); }

        void AuthEye(object s, RoutedEventArgs e)
        {
            if (aEye.IsChecked == true)
            {
                aPassTxt.Text = aPass.Password; aPass.Visibility = Visibility.Collapsed; aPassTxt.Visibility = Visibility.Visible;
                aPassTxt.Focus(); aPassTxt.CaretIndex = aPassTxt.Text.Length;
            }
            else
            {
                aPass.Password = aPassTxt.Text; aPassTxt.Visibility = Visibility.Collapsed; aPass.Visibility = Visibility.Visible; aPass.Focus();
            }
        }

        void AuthKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { AuthGo(null, null); e.Handled = true; } }

        async void AuthGo(object s, RoutedEventArgs e)
        {
            if (authBusy) return;
            var email = aEmail.Text.Trim(); var pw = Pw; string err = null;
            if (authMode != "reset" && !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) err = "Entre une adresse email valide.";
            else if (authMode == "signup" && aUser.Text.Trim().Length < 3) err = "Nom d'utilisateur : 3 caractères minimum.";
            else if ((authMode == "signup" || authMode == "reset") && pw.Length < 8) err = "Le mot de passe doit faire au moins 8 caractères.";
            else if (authMode == "login" && pw.Length == 0) err = "Entre ton mot de passe.";
            else if (authMode == "reset" && !recoveryVerified && aCode.Text.Trim().Length < 6) err = "Entre le code reçu par email.";
            if (err != null) { AuthErr(err); return; }

            authBusy = true; aGo.IsEnabled = false; aGo.Content = "Patiente…"; AuthErr(null); AuthInfo(null);
            try
            {
                switch (authMode)
                {
                    case "signup":
                        if (await Cloud.SignUp(email, aUser.Text.Trim(), pw)) AuthDone();
                        else { authMode = "login"; AuthLayout(); AuthInfo("Compte créé. Confirme ton email (lien reçu par mail), puis connecte-toi."); }
                        break;
                    case "login":
                        await Cloud.SignIn(email, pw); AuthDone();
                        break;
                    case "forgot":
                        await Cloud.SendReset(email);
                        resetEmail = email; recoveryVerified = false; authMode = "reset"; AuthLayout();
                        AuthInfo($"Code envoyé à {email} (pense à regarder les spams).");
                        break;
                    case "reset":
                        if (!recoveryVerified) { await Cloud.VerifyRecovery(resetEmail, aCode.Text.Trim()); recoveryVerified = true; }
                        await Cloud.SetPassword(pw);
                        recoveryVerified = false; AuthDone();
                        break;
                }
            }
            catch (CloudException ex) { AuthErr(ex.Message); }
            catch (Exception ex) { AuthErr("Erreur : " + ex.Message); }
            finally { authBusy = false; aGo.IsEnabled = true; AuthLayout(); }
        }

        // ---------- synchronisation des clips ----------
        void SyncStatus(string t) { syncStat.Text = t; }

        void SavePending() { try { WriteAtomic(pendingPath, JsonSerializer.Serialize(pendingDel)); } catch { } }

        void CloudDelete(string name)
        {
            if (!Cloud.LoggedIn) return;
            pendingDel.Add(Cloud.UserId + "|" + name); SavePending(); QueueSync();
        }

        void CloudFav(string name, bool fav)
        {
            if (!Cloud.LoggedIn || !cfg.CloudSync) return;
            _ = Task.Run(async () => { try { await Cloud.SetFav(name, fav); } catch { } });
        }

        System.Windows.Threading.DispatcherTimer syncT;

        void QueueSync()
        {
            if (!Cloud.LoggedIn || !cfg.CloudSync) return;
            // jamais d'envoi (réseau + disque) pendant qu'un jeu est au premier plan : on attend la fin de la partie
            if (FullscreenInFront() || exporting > 0 || enhBusy)
            {
                if (syncT == null)
                {
                    syncT = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                    syncT.Tick += (_, _) => { if (!FullscreenInFront() && exporting == 0 && !enhBusy) { syncT.Stop(); QueueSync(); } };
                }
                syncT.Start(); return;
            }
            if (syncing) { syncAgain = true; return; }
            _ = SyncAsync();
        }

        async Task SyncAsync()
        {
            syncing = true;
            try
            {
                do { syncAgain = false; await SyncOnce(); } while (syncAgain && Cloud.LoggedIn && cfg.CloudSync);
            }
            catch (CloudException ex) { SyncStatus(ex.Network ? "☁ Hors ligne : nouvelle tentative au prochain clip" : "☁ " + ex.Message); }
            catch (Exception) { SyncStatus("☁ Erreur de synchronisation"); }
            finally { syncing = false; }
        }

        async Task SyncOnce()
        {
            var uid = Cloud.UserId; if (uid == null) return;
            SyncStatus("☁ Synchronisation…");

            // 0. suppressions faites hors ligne
            foreach (var p in pendingDel.Where(x => x.StartsWith(uid + "|")).ToList())
            {
                try { await Cloud.DeleteClip(p.Substring(uid.Length + 1)); pendingDel.Remove(p); SavePending(); }
                catch (CloudException ex) when (!ex.Network) { }
            }

            var remote = await Cloud.ListClips();
            var remoteSet = new HashSet<string>(remote.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var dir = cfg.Out; Directory.CreateDirectory(dir); Directory.CreateDirectory(thumbDir);

            // 1. envoi des clips locaux absents du compte (y compris ceux faits avant la création du compte)
            //    pas pendant un export : le fichier en cours d'écriture serait envoyé à moitié
            if (exporting == 0)
            {
                var locals = new DirectoryInfo(dir).GetFiles("*.mp4")
                    .Where(f => !meta.TryGetValue(f.Name, out var q) || q.Uid == null || q.Uid == uid)
                    .Where(f => !remoteSet.Contains(f.Name) && !tooBig.Contains(f.Name))
                    .OrderByDescending(f => f.LastWriteTime).ToList();
                int i = 0;
                foreach (var f in locals)
                {
                    if (Cloud.UserId != uid || exporting > 0) return;
                    i++; SyncStatus($"☁ Envoi {i}/{locals.Count}…");
                    if (!meta.TryGetValue(f.Name, out var m)) meta[f.Name] = m = new ClipMeta { Game = "Capture" };
                    m.Uid = uid;
                    var known = m.Dur; var file = f.FullName; var thumb = Path.Combine(thumbDir, f.Name + ".jpg");
                    var dur = await Task.Run(() => Probe(file, thumb, known));
                    if (dur > 0) m.Dur = dur;
                    try { await Cloud.UploadClip(file, f.Name, m.Game ?? "Capture", m.Dur, m.Fav); }
                    catch (CloudException ex) when (ex.Status == 413)
                    {
                        Toast("☁ " + ex.Message + " Les clips restent en local.");
                        SyncStatus("☁ " + ex.Message); SaveMeta(); break;
                    }
                    SaveMeta();
                }
            }

            // 2. restauration : clips du compte absents de ce PC (nouveau PC, réinstallation…)
            var missing = remote.Where(r => !string.IsNullOrEmpty(r.Name) && Path.GetFileName(r.Name) == r.Name
                                            && r.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                                            && !File.Exists(Path.Combine(dir, r.Name))
                                            && !pendingDel.Contains(uid + "|" + r.Name)).ToList();
            int k = 0;
            foreach (var r in missing)
            {
                if (Cloud.UserId != uid) return;
                k++; SyncStatus($"☁ Restauration {k}/{missing.Count}…");
                await Cloud.DownloadClip(r.Name, Path.Combine(dir, r.Name));
                meta[r.Name] = new ClipMeta { Game = r.Game ?? "Capture", Dur = r.Dur ?? 0, Fav = r.Fav ?? false, Uid = uid };
                SaveMeta(); LibRefresh(null, null);
            }
            if (missing.Count > 0) Toast($"☁ {missing.Count} clip(s) restauré(s)");
            SyncStatus($"☁ Synchronisé • {DateTime.Now:HH:mm}");
        }
    }
}
