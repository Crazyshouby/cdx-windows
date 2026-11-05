using System.Drawing;
using Microsoft.Web.WebView2.Core;

namespace Cdx;

// Une fenêtre de l'Explorateur CDX : sans cadre de Windows, la page dessine le sien (barre de titre, boutons, bords) et nous demande
// de la déplacer, de la redimensionner, de la réduire... par des messages « win.* ».
class ExplorerForm : HostForm
{
    protected override bool ToolWindow => false;

    // Chaque disque devient un hôte virtuel « x.drive.cdx.shell » : la page peut y afficher images, sons, vidéos et PDF dans l'aperçu.
    // DenyCors : seules les inclusions (img, video, audio, iframe) passent ; la page ne peut pas lire le contenu par fetch.
    protected override void MapExtraHosts(CoreWebView2 c) => MapDrives(c);

    public static void MapDrives(CoreWebView2 c)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady) c.SetVirtualHostNameToFolderMapping(char.ToLowerInvariant(d.Name[0]) + ".drive.cdx.shell", d.Name, CoreWebView2HostResourceAccessKind.DenyCors); }
            catch (Exception ex) { Host.Log("disque non mappé " + d.Name + " : " + ex.Message); }
        }
    }

    // Boutons latéraux de la souris : Windows les convertit en WM_APPCOMMAND (précédent = 1, suivant = 2). La page les lit aussi (boutons 3 et 4) ;
    // elle ignore le second des deux avis arrivés à moins de 250 ms.
    protected override void WndProc(ref Message m)
    {
        if (ShellMenu.Active && ShellMenu.Forward(ref m)) return;   // les sous-menus du menu de Windows (« Plus d'options ») ont besoin de ces messages
        if (m.Msg == 0x0319 /* WM_APPCOMMAND */)
        {
            int cmd = (int)(((long)m.LParam >> 16) & 0x0FFF);
            Host.Log($"WM_APPCOMMAND reçu par la fenêtre Explorateur : commande {cmd}");
            if (cmd == 1 || cmd == 2) { Send(new { type = "nav", dir = cmd == 1 ? "back" : "fwd" }); m.Result = (IntPtr)1; return; }
        }
        base.WndProc(ref m);
    }

    public ExplorerForm(ExplorerApp s) : base(s, "explorer") { ShowInTaskbar = true; MinimumSize = new Size(640, 400); Text = "Explorateur"; Icon = SystemIcons.Application; }
}
