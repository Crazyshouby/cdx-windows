using System.Drawing;

namespace Kdx;

// La fenêtre de « Transferts » : sans cadre de Windows, la page dessine le sien (voir ExplorerForm).
class TransfersForm : HostForm
{
    protected override bool ToolWindow => false;

    public TransfersForm(TransfersApp s) : base(s, "transfers") { ShowInTaskbar = true; MinimumSize = new Size(560, 340); Text = "Transferts"; Icon = SystemIcons.Application; }
}
