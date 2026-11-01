using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cdx;

// La brique Google de la suite : un compte Google relié une fois (OAuth 2.0, application de bureau, PKCE, redirection sur 127.0.0.1), un jeton gardé chiffré pour
// l'utilisateur Windows (%APPDATA%\CDX\google-token.bin), rafraîchi tout seul ; les programmes (Agenda, demain le courrier) appellent les API avec ApiAsync.
//   - L'identifiant client vient de %APPDATA%\CDX\google-client.json : le fichier « client_secret_….json » téléchargé de la console Google Cloud (jamais commité).
//   - CDX_GOOGLE_API remplace https://www.googleapis.com (essais contre un faux serveur) ; auth_uri et token_uri sont ceux du fichier client.
//   - Chaque programme demande les portées (scopes) dont il a besoin ; une nouvelle portée refait passer par la page de consentement (en gardant les acquises).
static class GoogleAuth
{
    public static string ClientFile => Path.Combine(Suite.Dir, "google-client.json");
    public static string TokenFile => Path.Combine(Suite.Dir, "google-token.bin");
    public static string ApiBase => Environment.GetEnvironmentVariable("CDX_GOOGLE_API") is { Length: > 0 } a ? a.TrimEnd('/') : "https://www.googleapis.com";
    public const string ScopeCalendar = "https://www.googleapis.com/auth/calendar", ScopeTasks = "https://www.googleapis.com/auth/tasks", ScopeGmail = "https://www.googleapis.com/auth/gmail.modify";
    static readonly string[] BaseScopes = { "openid", "email" };

    sealed class Client { public string Id, Secret, AuthUri = "https://accounts.google.com/o/oauth2/v2/auth", TokenUri = "https://oauth2.googleapis.com/token"; }
    sealed class Token { public string Access { get; set; } public string Refresh { get; set; } public long Expires { get; set; } public string Scope { get; set; } = ""; public string Email { get; set; } = ""; }

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static Token token = LoadToken();
    static readonly object gate = new();
    public static string PendingUrl { get; private set; }        // l'adresse de consentement en attente (essais)
    // CDX_GOOGLE_NO_BROWSER=1 (essais) : la page de consentement n'est pas ouverte, l'essai la lit par le canal de test (google-url) et joue le navigateur.
    public static Func<string, bool> OpenUrl = url => Environment.GetEnvironmentVariable("CDX_GOOGLE_NO_BROWSER") == "1" || Bus.Send("browser", "open-url", new { url }, launch: true) || Start(url);
    static bool Start(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; } catch { return false; } }

    public static bool HasClient => File.Exists(ClientFile);
    public static bool IsConnected => token?.Refresh is { Length: > 0 };
    public static string Email => token?.Email ?? "";
    public static bool HasScope(string s) => IsConnected && (token.Scope ?? "").Split(' ').Contains(s);

    static Client LoadClient()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ClientFile));
        var r = doc.RootElement; var i = r.TryGetProperty("installed", out var ins) ? ins : r.TryGetProperty("web", out var web) ? web : r;
        var c = new Client { Id = i.GetProperty("client_id").GetString(), Secret = i.TryGetProperty("client_secret", out var s) ? s.GetString() : "" };
        if (i.TryGetProperty("auth_uri", out var au)) c.AuthUri = au.GetString();
        if (i.TryGetProperty("token_uri", out var tu)) c.TokenUri = tu.GetString();
        if (string.IsNullOrEmpty(c.Id)) throw new InvalidOperationException("client_id absent de google-client.json");
        return c;
    }
    static Token LoadToken()
    {
        try { if (!File.Exists(TokenFile)) return null; return JsonSerializer.Deserialize<Token>(Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(TokenFile)))); }
        catch (Exception ex) { Host.Log("jeton Google illisible : " + ex.Message); return null; }
    }
    static void SaveToken()
    {
        try
        {
            if (token == null) { File.Delete(TokenFile); return; }
            Directory.CreateDirectory(Suite.Dir); File.WriteAllBytes(TokenFile, Dpapi.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token)), "CDX Google"));
        }
        catch (Exception ex) { Host.Log("jeton Google : " + ex.Message); }
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Relie le compte (ou étend les portées) : ouvre la page de consentement dans le navigateur de la suite, attend le retour sur 127.0.0.1, échange le code.
    // Rend null, ou le message d'erreur.
    public static async Task<string> ConnectAsync(string[] scopes, CancellationToken ct = default)
    {
        Client c; try { c = LoadClient(); } catch (Exception ex) { return "Identifiant client Google introuvable (" + ClientFile + ") : " + ex.Message; }
        var want = BaseScopes.Concat(scopes).Concat(IsConnected ? token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>()).Distinct().ToArray();
        string verifier = B64Url(RandomNumberGenerator.GetBytes(48)), challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), state = B64Url(RandomNumberGenerator.GetBytes(16));
        int port = FreePort(); string redirect = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener(); listener.Prefixes.Add(redirect);
        try { listener.Start(); } catch (Exception ex) { return "Port local indisponible : " + ex.Message; }
        var q = new Dictionary<string, string>
        {
            ["client_id"] = c.Id, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = string.Join(' ', want), ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["state"] = state, ["access_type"] = "offline", ["prompt"] = "consent", ["include_granted_scopes"] = "true",
        };
        string url = c.AuthUri + (c.AuthUri.Contains('?') ? "&" : "?") + string.Join('&', q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        PendingUrl = url; Host.Log("Google : page de consentement ouverte (port " + port + ")");
        if (!OpenUrl(url)) Host.Log("Google : impossible d'ouvrir le navigateur ; adresse : " + url);
        string code = null, err = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromMinutes(5));
            while (code == null && err == null)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                var qs = ctx.Request.QueryString;
                if (qs["state"] != state) { Answer(ctx, "Demande inattendue", "Cette page ne correspond pas à la connexion en cours.", false); continue; }
                if (qs["error"] != null) { err = qs["error"]; Answer(ctx, "Connexion refusée", "Google a répondu : " + err + ". Tu peux fermer cet onglet.", false); break; }
                code = qs["code"]; Answer(ctx, "Compte relié", "La suite CDX est reliée à ton compte Google. Tu peux fermer cet onglet.", true);
            }
        }
        catch (OperationCanceledException) { return "Délai dépassé : la page de consentement n'a pas répondu en cinq minutes."; }
        catch (Exception ex) { return "Attente du retour de Google : " + ex.Message; }
        finally { PendingUrl = null; try { listener.Stop(); } catch { } }
        if (err != null) return "Google a refusé : " + err;
        try
        {
            var t = await TokenRequestAsync(c, new Dictionary<string, string> { ["code"] = code, ["client_id"] = c.Id, ["client_secret"] = c.Secret, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier }, ct);
            lock (gate) { token = t; }
            try { using var me = await ApiAsync(HttpMethod.Get, "/oauth2/v3/userinfo", null, ct); token.Email = me.RootElement.TryGetProperty("email", out var e) ? e.GetString() : ""; } catch (Exception ex) { Host.Log("Google : adresse du compte non lue : " + ex.Message); }
            SaveToken(); Host.Log("Google : compte relié (" + token.Email + "), portées : " + token.Scope);
            return null;
        }
        catch (Exception ex) { return "Échange du code : " + ex.Message; }
    }

    static int FreePort() { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    static void Answer(HttpListenerContext ctx, string title, string text, bool ok)
    {
        try
        {
            string html = "<!doctype html><html lang=\"fr\"><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title>" +
                "<body style=\"margin:0;min-height:100vh;display:grid;place-items:center;background:#333;color:#fff;font-family:'Segoe UI',sans-serif\"><div style=\"background:#444;border:1px solid #000;box-shadow:inset 1px 1px 0 #666,inset -1px -1px 0 #222;padding:24px 32px;max-width:460px\">" +
                "<div style=\"font-size:20px;letter-spacing:2px;color:" + (ok ? "#8f8" : "#f88") + "\">" + WebUtility.HtmlEncode(title) + "</div><p style=\"margin:12px 0 0\">" + WebUtility.HtmlEncode(text) + "</p></div></body></html>";
            var b = Encoding.UTF8.GetBytes(html); ctx.Response.ContentType = "text/html; charset=utf-8"; ctx.Response.ContentLength64 = b.Length; ctx.Response.OutputStream.Write(b); ctx.Response.Close();
        }
        catch { }
    }

    static async Task<Token> TokenRequestAsync(Client c, Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await http.PostAsync(c.TokenUri, new FormUrlEncodedContent(form), ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body); var r = doc.RootElement;
        if (!res.IsSuccessStatusCode || r.TryGetProperty("error", out _)) throw new InvalidOperationException(r.TryGetProperty("error_description", out var d) ? d.GetString() : r.TryGetProperty("error", out var e) ? e.GetString() : "HTTP " + (int)res.StatusCode);
        var t = new Token
        {
            Access = r.GetProperty("access_token").GetString(), Refresh = r.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : token?.Refresh,
            Expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (r.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 3600), Scope = r.TryGetProperty("scope", out var sc) ? sc.GetString() : token?.Scope ?? "", Email = token?.Email ?? "",
        };
        return t;
    }

    // Un jeton d'accès valable (rafraîchi s'il expire dans la minute). Lève si le compte n'est pas relié.
    static readonly SemaphoreSlim refreshing = new(1, 1);   // deux appels concurrents ne rafraîchissent qu'une fois
    public static async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Compte Google non relié.");
        if (token.Expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 60) return token.Access;
        await refreshing.WaitAsync(ct);
        try
        {
            if (!IsConnected) throw new InvalidOperationException("Compte Google non relié.");
            if (token.Expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 60) return token.Access;
            var c = LoadClient();
            var t = await TokenRequestAsync(c, new Dictionary<string, string> { ["refresh_token"] = token.Refresh, ["client_id"] = c.Id, ["client_secret"] = c.Secret, ["grant_type"] = "refresh_token" }, ct);
            lock (gate) { token = t; } SaveToken();
            return token.Access;
        }
        finally { refreshing.Release(); }
    }

    // Oublie le compte (le jeton est révoqué chez Google quand on parle au vrai Google).
    public static async Task DisconnectAsync()
    {
        var t = token; lock (gate) { token = null; } SaveToken();
        if (t?.Refresh != null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CDX_GOOGLE_API")))   // le vrai Google seulement (pas un faux serveur d'essai)
            try { await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = t.Refresh })); } catch { }
        Host.Log("Google : compte oublié");
    }

    // Un appel d'API (chemin sous ApiBase, par exemple /calendar/v3/users/me/calendarList) avec le jeton ; 401 → rafraîchi une fois. Lève GoogleApiException avec le message de Google.
    public static async Task<JsonDocument> ApiAsync(HttpMethod method, string path, object body = null, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            string tok = await AccessTokenAsync(ct);
            using var req = new HttpRequestMessage(method, path.StartsWith("http") ? path : ApiBase + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, ct);
            string text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { lock (gate) { if (token != null) token.Expires = 0; } continue; }
            if (!res.IsSuccessStatusCode)
            {
                string msg = "HTTP " + (int)res.StatusCode;
                try { using var d = JsonDocument.Parse(text); if (d.RootElement.TryGetProperty("error", out var e)) msg = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString(); } catch { }
                throw new GoogleApiException(msg, (int)res.StatusCode);
            }
            return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        }
    }
}

class GoogleApiException : Exception { public int Status; public GoogleApiException(string m, int status) : base(m) { Status = status; } }
