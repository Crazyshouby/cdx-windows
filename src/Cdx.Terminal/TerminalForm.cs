using System.Drawing;

namespace Cdx;

// La fenêtre du terminal : sans cadre de Windows, la page dessine le sien (voir ReposForm).
class TerminalForm : HostForm
{
    protected override bool ToolWindow => false;

    public TerminalForm(TerminalApp s) : base(s, "terminal") { ShowInTaskbar = true; MinimumSize = new Size(480, 280); Text = "Terminal"; Icon = SystemIcons.Application; }
}
