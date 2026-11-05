namespace Cdx;

// Une fenêtre de la barre des tâches : sa poignée, le programme qui la possède (chemin complet), son titre et son état.
sealed record TaskWin(IntPtr H, string Exe, string Title, bool Active, bool Min);

// Le regroupement des fenêtres par application : un bouton par programme, la liste de ses fenêtres au clic.
static class TaskGrouping
{
    public sealed record Group(string Key, List<TaskWin> Windows)
    {
        public bool Many => Windows.Count > 1;
        public bool Active => Windows.Any(w => w.Active);
        public bool Min => Windows.All(w => w.Min);
        // La fenêtre que représente le bouton (glisser-déposer, épinglage) : l'active, sinon la première.
        public TaskWin Representative => Windows.FirstOrDefault(w => w.Active) ?? Windows[0];
    }

    // Les fenêtres des applis du Store partagent toutes ApplicationFrameHost.exe (Paramètres, Calculatrice…) et celles dont le programme
    // est inconnu (processus élevé) n'ont pas de chemin : ni les unes ni les autres ne sont regroupées.
    public static bool Groupable(string exe) => !string.IsNullOrEmpty(exe) && !exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    public static string KeyOf(TaskWin w) => Groupable(w.Exe) ? w.Exe.ToLowerInvariant() : "h" + w.H.ToInt64();

    // `windows` arrive dans l'ordre d'apparition des fenêtres ; un groupe prend la place de sa première fenêtre.
    public static List<Group> Build(IEnumerable<TaskWin> windows)
    {
        var groups = new List<Group>(); var index = new Dictionary<string, Group>();
        foreach (var w in windows)
        {
            string key = KeyOf(w);
            if (!index.TryGetValue(key, out var g)) { g = new Group(key, new List<TaskWin>()); index[key] = g; groups.Add(g); }
            g.Windows.Add(w);
        }
        return groups;
    }
}
