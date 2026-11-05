using System.Drawing;

namespace Cdx;

// La fenêtre de « Dépôts » : sans cadre de Windows, la page dessine le sien (voir ExplorerForm).
class ReposForm : HostForm
{
    protected override bool ToolWindow => false;

    public ReposForm(ReposApp s) : base(s, "repos") { ShowInTaskbar = true; MinimumSize = new Size(760, 460); Text = "Dépôts"; Icon = SystemIcons.Application; }
}
