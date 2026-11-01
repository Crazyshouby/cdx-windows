using System.Drawing;

namespace Cdx;

// Aimantation des fenêtres (charte : tokens.json « snap_distance » 10 px d'attraction, « snap_gap » 2 px d'écart entre deux fenêtres, 0 au bord de l'écran).
// Logique pure, les mêmes règles que KIRC (src-tauri/src/app.rs : snap_rect, snap_size) : une fenêtre glissée ou redimensionnée colle aux bords des autres
// fenêtres de la suite qui lui font face, et à ceux de la zone de travail de son écran. Pixels d'écran (l'hôte met Dist et Gap à l'échelle de l'écran).
static class Snap
{
    public const int Dist = 10, Gap = 2;

    // Déplacement : `r` est le rectangle proposé pour la fenêtre glissée ; renvoie sa position une fois aimantée.
    public static Point Move(Rectangle r, IEnumerable<Rectangle> others, Rectangle? area, int dist, int gap)
    {
        int? bx = null, by = null; int dx = int.MaxValue, dy = int.MaxValue;   // bord le plus proche trouvé sur chaque axe : (coordonnée, distance)
        void Near(ref int? best, ref int bd, int at, int cand) { int d = Math.Abs(cand - at); if (d <= dist && d < bd) { best = cand; bd = d; } }
        foreach (var o in others)
        {
            // seules les fenêtres qui font face sur l'autre axe comptent
            if (r.Y < o.Bottom + dist && r.Bottom > o.Y - dist)
            {
                Near(ref bx, ref dx, r.X, o.Right + gap);       // notre bord gauche contre leur bord droit
                Near(ref bx, ref dx, r.X, o.X - r.Width - gap); // notre bord droit contre leur bord gauche
                Near(ref bx, ref dx, r.X, o.X);                 // bords gauches alignés
                Near(ref bx, ref dx, r.X, o.Right - r.Width);   // bords droits alignés
            }
            if (r.X < o.Right + dist && r.Right > o.X - dist)
            {
                Near(ref by, ref dy, r.Y, o.Bottom + gap);
                Near(ref by, ref dy, r.Y, o.Y - r.Height - gap);
                Near(ref by, ref dy, r.Y, o.Y);
                Near(ref by, ref dy, r.Y, o.Bottom - r.Height);
            }
        }
        if (area is Rectangle a)
        {
            Near(ref bx, ref dx, r.X, a.X); Near(ref bx, ref dx, r.X, a.Right - r.Width);
            Near(ref by, ref dy, r.Y, a.Y); Near(ref by, ref dy, r.Y, a.Bottom - r.Height);
        }
        return new Point(bx ?? r.X, by ?? r.Y);
    }

    // Redimensionnement : `r` est le rectangle proposé, `edge` le bord tiré (WMSZ_* : 1 gauche, 2 droite, 3 haut, 4 haut-gauche, 5 haut-droite, 6 bas,
    // 7 bas-gauche, 8 bas-droite). Seuls les bords qui bougent s'aimantent (contre le bord opposé d'une autre fenêtre à `gap` près, ou alignés avec
    // ses bords) ; le rectangle rendu ne descend jamais sous `minW` x `minH`.
    public static Rectangle Size(Rectangle r, int edge, IEnumerable<Rectangle> others, Rectangle? area, int dist, int gap, int minW = 1, int minH = 1)
    {
        bool left = edge is 1 or 4 or 7, right = edge is 2 or 5 or 8, top = edge is 3 or 4 or 5, bottom = edge is 6 or 7 or 8;
        int? bl = null, br = null, bt = null, bb = null; int dl = int.MaxValue, dr = int.MaxValue, dt = int.MaxValue, db = int.MaxValue;
        void Near(ref int? best, ref int bd, int at, int cand) { int d = Math.Abs(cand - at); if (d <= dist && d < bd) { best = cand; bd = d; } }
        foreach (var o in others)
        {
            if (r.Y < o.Bottom + dist && r.Bottom > o.Y - dist)
            {
                if (left) { Near(ref bl, ref dl, r.X, o.Right + gap); Near(ref bl, ref dl, r.X, o.X); Near(ref bl, ref dl, r.X, o.Right); }
                if (right) { Near(ref br, ref dr, r.Right, o.X - gap); Near(ref br, ref dr, r.Right, o.Right); Near(ref br, ref dr, r.Right, o.X); }
            }
            if (r.X < o.Right + dist && r.Right > o.X - dist)
            {
                if (top) { Near(ref bt, ref dt, r.Y, o.Bottom + gap); Near(ref bt, ref dt, r.Y, o.Y); Near(ref bt, ref dt, r.Y, o.Bottom); }
                if (bottom) { Near(ref bb, ref db, r.Bottom, o.Y - gap); Near(ref bb, ref db, r.Bottom, o.Bottom); Near(ref bb, ref db, r.Bottom, o.Y); }
            }
        }
        if (area is Rectangle a)
        {
            if (left) Near(ref bl, ref dl, r.X, a.X); if (right) Near(ref br, ref dr, r.Right, a.Right);
            if (top) Near(ref bt, ref dt, r.Y, a.Y); if (bottom) Near(ref bb, ref db, r.Bottom, a.Bottom);
        }
        int l = bl ?? r.X, t = bt ?? r.Y, rr = br ?? r.Right, b = bb ?? r.Bottom;
        if (rr - l < minW) { if (left) l = rr - minW; else rr = l + minW; }
        if (b - t < minH) { if (top) t = b - minH; else b = t + minH; }
        return Rectangle.FromLTRB(l, t, rr, b);
    }
}
