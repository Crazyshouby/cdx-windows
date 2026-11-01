using System.Drawing;

namespace Cdx;

// La fenêtre de « Pilote » : sans cadre de Windows, la page dessine le sien (voir ExplorerForm).
class PiloteForm : HostForm
{
    protected override bool ToolWindow => false;

    public PiloteForm(PiloteApp s) : base(s, "pilote") { ShowInTaskbar = true; MinimumSize = new Size(900, 520); Text = "Pilote"; Icon = SystemIcons.Application; }
}
