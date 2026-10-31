using System.Drawing;
using Microsoft.Web.WebView2.Core;

namespace Kdx;

// La fenêtre de « Notes » : sans cadre de Windows, la page dessine le sien (voir ReposForm).
// Les images du coffre sont servies sous https://vault.kdx.notes/ : seulement des images, seulement dans le coffre (Vault.ImagePath), rien d'autre.
class NotesForm : HostForm
{
    protected override bool ToolWindow => false;
    readonly NotesApp notes;

    // KDX_NO_FOCUS=1 (essais automatiques) : la fenêtre s'ouvre sans prendre le focus, pour que la frappe de celui qui travaille à côté n'y tombe pas.
    public static bool NoFocus => Environment.GetEnvironmentVariable("KDX_NO_FOCUS") == "1";
    protected override bool ShowWithoutActivation => NoFocus;

    public NotesForm(NotesApp s) : base(s, "notes") { notes = s; ShowInTaskbar = true; MinimumSize = new Size(820, 500); Text = "Notes"; Icon = SystemIcons.Application; }

    protected override void MapExtraHosts(CoreWebView2 c)
    {
        c.AddWebResourceRequestedFilter("https://vault.kdx.notes/*", CoreWebView2WebResourceContext.All);
        c.WebResourceRequested += (s, e) =>
        {
            try
            {
                string rel = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
                string full = notes.ImageFile(rel);
                if (full == null) { e.Response = c.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); return; }
                string ext = Path.GetExtension(full).ToLowerInvariant();
                string mime = ext switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "image/bmp" };
                e.Response = c.Environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(full)), 200, "OK", $"Content-Type: {mime}\r\nCache-Control: no-cache");
            }
            catch (Exception ex) { Host.Log("image : " + ex.Message); e.Response = c.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); }
        };
    }
}
