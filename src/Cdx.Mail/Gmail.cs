using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cdx;

// Ce que l'API Gmail rend d'un message (format metadata ou full), lu en une forme simple pour la page ; et la fabrication d'un message à envoyer (RFC 2822).
class MailMsg
{
    public string Id = "", ThreadId = "", From = "", To = "", Cc = "", Subject = "", Snippet = "", MessageId = "", References = "";
    public long Date; public bool Unread, Starred; public List<string> Labels = new();
    public string? Html, Text;                                   // null : pas de corps de ce type
    public List<MailAttachment> Attachments = new();
    public object Summary() => new { id = Id, threadId = ThreadId, from = From, to = To, cc = Cc, subject = Subject, snippet = Snippet, date = Date, unread = Unread, starred = Starred, labels = Labels };
    public object Full() => new { id = Id, threadId = ThreadId, from = From, to = To, cc = Cc, subject = Subject, snippet = Snippet, date = Date, unread = Unread, starred = Starred, labels = Labels, html = Html, text = Text, messageId = MessageId, references = References, attachments = Attachments.Select(a => new { id = a.Id, name = a.Name, mime = a.Mime, size = a.Size }) };
}
class MailAttachment { public string Id = "", Name = "", Mime = ""; public long Size; public string? Data; }   // Data : base64url du contenu quand il est livré avec le message (petites pièces)

static class Gmail
{
    public const string Me = "/gmail/v1/users/me";
    static readonly Dictionary<string, string> SystemNames = new()
    {
        ["INBOX"] = "Boîte de réception", ["STARRED"] = "Favoris", ["IMPORTANT"] = "Importants", ["SENT"] = "Envoyés", ["DRAFT"] = "Brouillons", ["SPAM"] = "Spam", ["TRASH"] = "Corbeille",
    };
    public static readonly string[] SystemOrder = { "INBOX", "STARRED", "IMPORTANT", "SENT", "DRAFT", "SPAM", "TRASH" };
    public static string LabelName(string id, string name) => SystemNames.TryGetValue(id, out var n) ? n : name;
    public static bool Hidden(string id) => id is "UNREAD" or "CHAT" || id.StartsWith("CATEGORY_");

    static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string Header(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty("headers", out var hs) || hs.ValueKind != JsonValueKind.Array) return "";
        foreach (var h in hs.EnumerateArray()) if (string.Equals(S(h, "name"), name, StringComparison.OrdinalIgnoreCase)) return S(h, "value");
        return "";
    }

    public static MailMsg Parse(JsonElement m)
    {
        var x = new MailMsg { Id = S(m, "id"), ThreadId = S(m, "threadId"), Snippet = System.Net.WebUtility.HtmlDecode(S(m, "snippet")) };
        if (m.TryGetProperty("labelIds", out var ls) && ls.ValueKind == JsonValueKind.Array) foreach (var l in ls.EnumerateArray()) if (l.GetString() is { } lid) x.Labels.Add(lid);   // un libellé null (jamais vu chez Google) n'entre pas dans la liste
        x.Unread = x.Labels.Contains("UNREAD"); x.Starred = x.Labels.Contains("STARRED");
        if (m.TryGetProperty("internalDate", out var d) && long.TryParse(d.GetString(), out var ms)) x.Date = ms;
        if (m.TryGetProperty("payload", out var p))
        {
            x.From = Header(p, "From"); x.To = Header(p, "To"); x.Cc = Header(p, "Cc"); x.Subject = Header(p, "Subject"); x.MessageId = Header(p, "Message-ID"); x.References = Header(p, "References");
            Walk(p, x);
        }
        return x;
    }
    // Le corps : la première partie text/html et la première text/plain ; les pièces jointes : les parties avec un nom de fichier.
    static void Walk(JsonElement part, MailMsg x)
    {
        string mime = S(part, "mimeType"), name = S(part, "filename");
        var body = part.TryGetProperty("body", out var b) ? b : default;
        if (name.Length > 0 && body.ValueKind == JsonValueKind.Object)
        {
            x.Attachments.Add(new MailAttachment { Id = S(body, "attachmentId"), Name = name, Mime = mime, Size = body.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0, Data = S(body, "data") is { Length: > 0 } dd ? dd : null });
        }
        else if (body.ValueKind == JsonValueKind.Object && S(body, "data").Length > 0)
        {
            string text = DecodeText(S(body, "data"));
            if (mime.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) x.Html ??= text;
            else if (mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)) x.Text ??= text;
        }
        if (part.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array) foreach (var q in parts.EnumerateArray()) Walk(q, x);
    }

    public static byte[] B64UrlDecode(string s) { s = s.Replace('-', '+').Replace('_', '/'); switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; } return Convert.FromBase64String(s); }
    public static string DecodeText(string b64url) { try { return Encoding.UTF8.GetString(B64UrlDecode(b64url)); } catch { return ""; } }
    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Le texte d'un message pour citer (réponse, transfert) : le texte brut, sinon le HTML dépouillé de ses balises.
    public static string TextOf(MailMsg m)
    {
        if (!string.IsNullOrEmpty(m.Text)) return m.Text.Replace("\r\n", "\n");
        if (string.IsNullOrEmpty(m.Html)) return "";
        string t = Regex.Replace(m.Html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</tr>|</li>|</h\d>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", ""); t = System.Net.WebUtility.HtmlDecode(t);
        return Regex.Replace(t, @"\n{3,}", "\n\n").Trim();
    }
    public static string Quote(MailMsg m)
    {
        var d = DateTimeOffset.FromUnixTimeMilliseconds(m.Date).ToLocalTime();
        return $"Le {d:dd/MM/yyyy} à {d:HH:mm}, {m.From} a écrit :\n" + string.Join("\n", TextOf(m).Split('\n').Select(l => "> " + l));
    }

    // L'adresse seule d'un en-tête « Nom <adresse> », et la liste des adresses d'un en-tête à plusieurs.
    public static string AddressOf(string h) { var mm = Regex.Match(h ?? "", @"<([^>]+)>"); return (mm.Success ? mm.Groups[1].Value : (h ?? "")).Trim().ToLowerInvariant(); }
    public static IEnumerable<string> Addresses(string h) => (h ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);

    // Un message RFC 2822 (UTF-8, pièces jointes en multipart/mixed, en-têtes non ASCII en RFC 2047), prêt pour messages.send (média message/rfc822).
    public static byte[] BuildRaw(string from, string to, string cc, string bcc, string subject, string text, string inReplyTo, string references, IList<(string name, byte[] data, string mime)> files)
    {
        var sb = new StringBuilder();
        // Un saut de ligne dans une valeur ajouterait un en-tête (« Objet\r\nBcc: … ») : refusé, jamais nettoyé en silence.
        static void NoBreak(string k, string v) { if (v != null && v.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("saut de ligne refusé dans l'en-tête " + k); }
        void H(string k, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            NoBreak(k, v);
            sb.Append(k).Append(": ").Append(k == "Subject" ? EncodeHeader(v) : EncodeAddresses(v)).Append("\r\n");
        }
        H("From", from); H("To", to); H("Cc", cc); H("Bcc", bcc); H("Subject", subject);
        NoBreak("In-Reply-To", inReplyTo); NoBreak("References", references);
        if (!string.IsNullOrEmpty(inReplyTo)) { sb.Append("In-Reply-To: ").Append(inReplyTo).Append("\r\n"); sb.Append("References: ").Append((references + " " + inReplyTo).Trim()).Append("\r\n"); }
        sb.Append("Date: ").Append(DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");   // zzz donne +01:00 : ramené à +0100 (RFC 2822) plus bas
        sb.Append("MIME-Version: 1.0\r\n");
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""), Base64FormattingOptions.InsertLineBreaks);
        if (files == null || files.Count == 0)
        {
            sb.Append("Content-Type: text/plain; charset=\"UTF-8\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(body).Append("\r\n");
        }
        else
        {
            string bnd = "=_cdx_" + Guid.NewGuid().ToString("N");
            sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(bnd).Append("\"\r\n\r\n");
            sb.Append("--").Append(bnd).Append("\r\nContent-Type: text/plain; charset=\"UTF-8\"\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(body).Append("\r\n");
            foreach (var (name, data, mime) in files)
            {
                string fn = EncodeHeader((name ?? "").Replace("\r", "").Replace("\n", "")).Replace("\"", "");
                sb.Append("--").Append(bnd).Append("\r\nContent-Type: ").Append(string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime).Append("; name=\"").Append(fn).Append("\"\r\nContent-Disposition: attachment; filename=\"").Append(fn).Append("\"\r\nContent-Transfer-Encoding: base64\r\n\r\n")
                  .Append(Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks)).Append("\r\n");
            }
            sb.Append("--").Append(bnd).Append("--\r\n");
        }
        var s = sb.ToString();
        // la date : « +01:00 » → « +0100 »
        s = Regex.Replace(s, @"^(Date: .*)([+-]\d\d):(\d\d)\r\n", "$1$2$3\r\n", RegexOptions.Multiline);
        return Encoding.UTF8.GetBytes(s);
    }
    public static string EncodeHeader(string v) => v.All(c => c < 128) ? v : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(v)) + "?=";
    // Un en-tête d'adresses : seul le nom affiché est encodé (« =?UTF-8?B?…?= <adresse> ») ; encoder toute la valeur rendait l'adresse illisible
    // pour le serveur dès qu'un nom portait un accent.
    public static string EncodeAddresses(string h) => string.Join(", ", Addresses(h).Select(a =>
    {
        var m = Regex.Match(a, "^\"?(.*?)\"?\\s*<([^<>]+)>$");
        if (!m.Success) return a;
        string name = m.Groups[1].Value.Trim();
        return name.Length == 0 ? "<" + m.Groups[2].Value + ">" : (name.All(c => c < 128) ? "\"" + name.Replace("\"", "") + "\"" : EncodeHeader(name)) + " <" + m.Groups[2].Value + ">";
    }));
    public static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".txt" or ".md" => "text/plain", ".zip" => "application/zip",
        ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".csv" => "text/csv", ".json" => "application/json", _ => "application/octet-stream",
    };
}
