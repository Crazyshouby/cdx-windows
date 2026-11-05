using System.Drawing;

namespace Cdx;

// La fenêtre d'« Agenda » : sans cadre de Windows, la page dessine le sien (voir ExplorerForm).
class AgendaForm : HostForm
{
    protected override bool ToolWindow => false;

    public AgendaForm(AgendaApp s) : base(s, "agenda") { ShowInTaskbar = true; MinimumSize = new Size(760, 480); Text = "Agenda"; Icon = SystemIcons.Application; }
}
