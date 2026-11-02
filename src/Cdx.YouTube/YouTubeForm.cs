using System.Drawing;

namespace Cdx;

// La fenêtre de YouTube : sans cadre de Windows, la page dessine le sien (voir ReposForm).
class YouTubeForm : HostForm
{
    protected override bool ToolWindow => false;

    public YouTubeForm(YouTubeApp a) : base(a, "youtube") { ShowInTaskbar = true; MinimumSize = new Size(820, 520); Text = "YouTube"; }
}
