using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TynoRec
{
    class CloudException : Exception
    {
        public int Status; public bool Network;
        public CloudException(string m, int status = 0, bool network = false) : base(m) { Status = status; Network = network; }
    }

    class RemoteClip
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("game")] public string Game { get; set; }
        [JsonPropertyName("dur")] public double? Dur { get; set; }
        [JsonPropertyName("fav")] public bool? Fav { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }

    /// <summary>Client Supabase minimaliste (REST, sans dépendance) : comptes + sauvegarde des clips.</summary>
    static class Cloud
    {
        // ====== À REMPLIR (Supabase > Project Settings > API) ======
        // La clé "anon" est faite pour être publique : ce sont les règles RLS (supabase_setup.sql) qui protègent les données.
        public const string Url = "https://mwybzdrdqjwaipotrcay.supabase.co";
        public const string AnonKey = "sb_publishable_H82Wn3yfXQqLHA2XZA9TWg_DC36ae_G";
        // Adresse du Worker Cloudflare (worker.js) qui donne les URL signées pour les vidéos (stockées dans R2)
        public const string WorkerUrl = "https://tynorec-sign.mangoooot911.workers.dev";
        // ============================================================

        static readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
        static readonly SemaphoreSlim gate = new(1, 1);
        static readonly string sessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TynoRec", "session.bin");

        static string access, refresh;
        static DateTime expires = DateTime.MinValue;
        public static string UserId { get; private set; }
        public static string Email { get; private set; }
        public static string Username { get; private set; }
        public static bool LoggedIn => !string.IsNullOrEmpty(UserId);

        // ---------- session (refresh token chiffré avec DPAPI : lisible uniquement par ton compte Windows) ----------
        class Saved { public string Refresh { get; set; } public string Uid { get; set; } public string Email { get; set; } public string Name { get; set; } }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionFile));
                var json = JsonSerializer.SerializeToUtf8Bytes(new Saved { Refresh = refresh, Uid = UserId, Email = Email, Name = Username });
                File.WriteAllBytes(sessionFile, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
            }
            catch { }
        }

        static void Clear()
        {
            access = refresh = null; expires = DateTime.MinValue; UserId = Email = Username = null;
            try { File.Delete(sessionFile); } catch { }
        }

        /// <summary>Reprend la session précédente. false = il faut se connecter. Hors ligne : on garde l'identité enregistrée.</summary>
        public static async Task<bool> Restore()
        {
            try
            {
                var s = JsonSerializer.Deserialize<Saved>(ProtectedData.Unprotect(File.ReadAllBytes(sessionFile), null, DataProtectionScope.CurrentUser));
                if (string.IsNullOrEmpty(s?.Refresh) || string.IsNullOrEmpty(s.Uid)) return false;
                refresh = s.Refresh; UserId = s.Uid; Email = s.Email; Username = s.Name;
            }
            catch { return false; }
            try { await RefreshNow(); return true; }
            catch (CloudException ex) when (ex.Network) { return true; }
            catch (CloudException) { Clear(); return false; }
        }

        static void Apply(JsonElement j)
        {
            access = j.GetProperty("access_token").GetString();
            refresh = j.GetProperty("refresh_token").GetString();
            int ex = j.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600;
            expires = DateTime.UtcNow.AddSeconds(Math.Max(30, ex - 60));
            if (j.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                UserId = u.GetProperty("id").GetString();
                if (u.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String) Email = em.GetString();
                if (u.TryGetProperty("user_metadata", out var um) && um.ValueKind == JsonValueKind.Object
                    && um.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String) Username = un.GetString();
            }
            Save();
        }

        static async Task RefreshNow()
        {
            await gate.WaitAsync();
            try
            {
                if (access != null && DateTime.UtcNow < expires) return;
                if (string.IsNullOrEmpty(refresh)) throw new CloudException("Session expirée, reconnecte-toi.", 401);
                Apply(await Send(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token", new { refresh_token = refresh }, false));
            }
            finally { gate.Release(); }
        }

        static async Task EnsureFresh()
        {
            if (access != null && DateTime.UtcNow < expires) return;
            await RefreshNow();
        }

        // ---------- HTTP ----------
        static void Check()
        {
            if (Url.Contains("XXXXXXXX") || WorkerUrl.Contains("XXXXXXXX") || AnonKey.StartsWith("COLLE_ICI"))
                throw new CloudException("Serveur non configuré : renseigne Url, AnonKey et WorkerUrl dans Cloud.cs.");
        }

        static async Task<JsonElement> Send(HttpMethod m, string path, object body, bool auth, Dictionary<string, string> hdr = null, int timeoutSec = 25)
        {
            Check();
            if (auth) await EnsureFresh();
            using var req = new HttpRequestMessage(m, Url + path);
            req.Headers.Add("apikey", AnonKey);
            if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (hdr != null) foreach (var kv in hdr) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            HttpResponseMessage r;
            try { r = await http.SendAsync(req, cts.Token); }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            { throw new CloudException("Impossible de joindre le serveur. Vérifie ta connexion.", 0, true); }
            using (r)
            {
                var txt = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode) throw new CloudException(Nice(txt), (int)r.StatusCode);
                if (string.IsNullOrWhiteSpace(txt)) return default;
                try { using var doc = JsonDocument.Parse(txt); return doc.RootElement.Clone(); } catch { return default; }
            }
        }

        static string Nice(string txt)
        {
            string code = "", msg = "";
            try
            {
                using var d = JsonDocument.Parse(txt); var e = d.RootElement;
                if (e.TryGetProperty("error_code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                foreach (var k in new[] { "msg", "message", "error_description", "error" })
                    if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) { msg = v.GetString(); break; }
            }
            catch { }
            switch (code)
            {
                case "invalid_credentials": return "Email ou mot de passe incorrect.";
                case "user_already_exists": case "email_exists": return "Un compte existe déjà avec cet email.";
                case "weak_password": return "Mot de passe trop faible : mets-en un plus long ou plus varié.";
                case "otp_expired": return "Code invalide ou expiré. Demande un nouveau code.";
                case "email_not_confirmed": return "Confirme d'abord ton email (lien reçu par mail).";
                case "same_password": return "Le nouveau mot de passe doit être différent de l'ancien.";
                case "over_email_send_rate_limit": case "over_request_rate_limit": return "Trop de tentatives, réessaie dans quelques minutes.";
                case "signup_disabled": return "Les inscriptions sont désactivées.";
                case "validation_failed": return "Adresse email invalide.";
            }
            if (msg.Contains("expired or is invalid", StringComparison.OrdinalIgnoreCase)) return "Code invalide ou expiré. Demande un nouveau code.";
            return string.IsNullOrWhiteSpace(msg) ? "Erreur serveur." : msg;
        }

        // ---------- comptes ----------
        /// <returns>true = connecté tout de suite ; false = confirmation par email nécessaire</returns>
        public static async Task<bool> SignUp(string email, string username, string password)
        {
            var j = await Send(HttpMethod.Post, "/auth/v1/signup", new { email, password, data = new { username } }, false);
            if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("access_token", out _)) { Apply(j); return true; }
            return false;
        }

        public static async Task SignIn(string email, string password) =>
            Apply(await Send(HttpMethod.Post, "/auth/v1/token?grant_type=password", new { email, password }, false));

        /// <summary>Envoie un code par email (modèle d'email "Reset Password" de Supabase, à modifier : voir les instructions).</summary>
        public static Task SendReset(string email) => Send(HttpMethod.Post, "/auth/v1/recover", new { email }, false);

        /// <summary>Valide le code reçu : ouvre une session (à enchaîner avec SetPassword).</summary>
        public static async Task VerifyRecovery(string email, string code) =>
            Apply(await Send(HttpMethod.Post, "/auth/v1/verify", new { type = "recovery", email, token = code }, false));

        public static Task SetPassword(string password) => Send(HttpMethod.Put, "/auth/v1/user", new { password }, true);

        public static async Task SignOut()
        {
            try { if (access != null) await Send(HttpMethod.Post, "/auth/v1/logout", null, true, null, 8); } catch { }
            Clear();
        }

        // ---------- clips ----------
        // Métadonnées (nom, jeu, durée, favori) : table Supabase. Vidéos : Cloudflare R2, via des URL signées par le Worker.
        static async Task<string> Sign(string op, string name, long size = 0)
        {
            Check(); await EnsureFresh();
            using var req = new HttpRequestMessage(HttpMethod.Post, WorkerUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            req.Content = new StringContent(JsonSerializer.Serialize(new { op, name, size }), Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            HttpResponseMessage r;
            try { r = await http.SendAsync(req, cts.Token); }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            { throw new CloudException("Impossible de joindre le serveur. Vérifie ta connexion.", 0, true); }
            using (r)
            {
                var txt = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode) throw new CloudException(Nice(txt), (int)r.StatusCode);
                using var d = JsonDocument.Parse(txt);
                return d.RootElement.GetProperty("url").GetString();
            }
        }

        public static async Task<List<RemoteClip>> ListClips()
        {
            var j = await Send(HttpMethod.Get, "/rest/v1/clips?select=name,game,dur,fav,size&order=created_at.desc&limit=5000", null, true);
            if (j.ValueKind != JsonValueKind.Array) return new List<RemoteClip>();
            return JsonSerializer.Deserialize<List<RemoteClip>>(j.GetRawText()) ?? new List<RemoteClip>();
        }

        public static async Task UploadClip(string path, string name, string game, double dur, bool fav, CancellationToken ct = default)
        {
            var len = new FileInfo(path).Length;
            var url = await Sign("put", name, len);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true))
            using (var req = new HttpRequestMessage(HttpMethod.Put, url))
            {
                var c = new StreamContent(fs, 1 << 20);
                c.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                c.Headers.ContentLength = len;
                req.Content = c;
                HttpResponseMessage r;
                try { r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                { throw new CloudException("Connexion perdue pendant l'envoi.", 0, true); }
                using (r)
                    if (!r.IsSuccessStatusCode) throw new CloudException($"Envoi refusé par le stockage (code {(int)r.StatusCode}).", (int)r.StatusCode);
            }
            await Send(HttpMethod.Post, "/rest/v1/clips", new { user_id = UserId, name, game, dur, fav, size = len }, true,
                new Dictionary<string, string> { ["Prefer"] = "resolution=merge-duplicates,return=minimal" });
        }

        public static async Task DownloadClip(string name, string dest, CancellationToken ct = default)
        {
            var url = await Sign("get", name);
            var tmp = dest + ".part";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                HttpResponseMessage r;
                try { r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                { throw new CloudException("Connexion perdue pendant le téléchargement.", 0, true); }
                using (r)
                {
                    if (!r.IsSuccessStatusCode) throw new CloudException($"Téléchargement refusé (code {(int)r.StatusCode}).", (int)r.StatusCode);
                    try
                    {
                        using var inp = await r.Content.ReadAsStreamAsync(ct);
                        using var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                        await inp.CopyToAsync(o, 1 << 20, ct);
                    }
                    catch (IOException) { throw new CloudException("Connexion perdue pendant le téléchargement.", 0, true); }
                }
                File.Move(tmp, dest, true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        public static async Task DeleteClip(string name)
        {
            var url = await Sign("delete", name);
            using (var req = new HttpRequestMessage(HttpMethod.Delete, url))
            {
                HttpResponseMessage r;
                try { r = await http.SendAsync(req); }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                { throw new CloudException("Impossible de joindre le serveur.", 0, true); }
                using (r)
                    if (!r.IsSuccessStatusCode && (int)r.StatusCode != 404) throw new CloudException($"Suppression refusée (code {(int)r.StatusCode}).", (int)r.StatusCode);
            }
            await Send(HttpMethod.Delete, $"/rest/v1/clips?user_id=eq.{UserId}&name=eq.{Uri.EscapeDataString(name)}", null, true);
        }

        public static Task SetFav(string name, bool fav) =>
            Send(HttpMethod.Patch, $"/rest/v1/clips?user_id=eq.{UserId}&name=eq.{Uri.EscapeDataString(name)}", new { fav }, true);
    }
}
