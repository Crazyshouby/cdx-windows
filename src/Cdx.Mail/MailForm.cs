using System.Drawing;

namespace Cdx;

// La fenêtre de « Courrier » : sans cadre de Windows, la page dessine le sien (voir ExplorerForm).
class MailForm : HostForm
{
    protected override bool ToolWindow => false;

    public MailForm(MailApp s) : base(s, "courrier") { ShowInTaskbar = true; MinimumSize = new Size(900, 520); Text = "Courrier"; Icon = SystemIcons.Application; }
}
