using System.Text;

namespace Cdx;

// La mémoire de Pilote (décisions du 01/11/2026) : des notes du coffre, dossier Pilote/, que Claude lit au début de chaque conversation (injectées dans sa
// consigne) et met à jour lui-même avec les outils de Notes : Profil.md (Valentin, ses habitudes), Décisions.md (ce qui a été tranché), Projets.md (l'état
// et les prochaines actions, en complément des fiches), Journal.md (une entrée datée par échange : demandé, fait, en suspens). Tout est visible et
// corrigeable dans Notes ; rien n'est caché dans un dossier de Claude Code (sa mémoire automatique est coupée pour Pilote).
static class PilotMemory
{
    public const string Folder = "Pilote";
    public static readonly (string name, string template)[] Notes =
    {
        ("Profil.md", "# Profil\n\nCe que Pilote sait de Valentin : qui il est, comment il travaille, ce qu'il préfère. Une ligne par fait, courte.\n\n- Il parle français ; réponses courtes.\n"),
        ("Décisions.md", "# Décisions\n\nCe qui a été tranché avec Pilote, pour ne pas y revenir. Une ligne par décision, datée.\n\n"),
        ("Projets.md", "# Projets\n\nL'état des projets vu par Pilote et les prochaines actions, en complément des fiches de Projets/. Une section par projet.\n\n"),
        ("Journal.md", "# Journal\n\nUne entrée par échange avec Pilote : « ## AAAA-MM-JJ HH:MM — titre », puis Demandé / Fait / En suspens.\n\n"),
    };
    const int MaxNote = 6000, MaxJournal = 4000;

    // Crée le dossier et les notes qui manquent (jamais réécrites). Rien si le coffre n'existe pas.
    public static void Ensure(string vault)
    {
        try
        {
            if (string.IsNullOrEmpty(vault) || !Directory.Exists(vault)) return;
            string dir = Path.Combine(vault, Folder); Directory.CreateDirectory(dir);
            foreach (var (name, template) in Notes) { string f = Path.Combine(dir, name); if (!File.Exists(f)) File.WriteAllText(f, template, new UTF8Encoding(false)); }
        }
        catch (Exception ex) { Host.Log("mémoire : " + ex.Message); }
    }

    // Le bloc de consigne : les notes bornées (le début des trois premières, la fin du journal) et la règle d'écriture.
    public static string Prompt(string vault)
    {
        var sb = new StringBuilder();
        sb.Append(" TA MÉMOIRE : les notes du dossier Pilote/ du coffre, ci-dessous. Quand tu apprends quelque chose de durable (sur Valentin : Pilote/Profil.md ; une décision prise : Pilote/Décisions.md ; l'état ou la prochaine action d'un projet : Pilote/Projets.md), écris-le aussitôt dans la bonne note avec notes_append ou notes_edit, en une ligne courte, datée si utile ; ne recopie pas ce qui est déjà dans une fiche de projet, et retire ce qui est devenu faux. ");
        sb.Append("À la fin de chaque échange où tu as fait quelque chose (pas pour une simple réponse), ajoute à Pilote/Journal.md une entrée « ## AAAA-MM-JJ HH:MM — titre court » suivie de trois lignes : Demandé, Fait, En suspens. Garde ces notes courtes : elles sont relues à chaque conversation.");
        string dir = Path.Combine(vault ?? "", Folder);
        if (!Directory.Exists(dir)) return sb.ToString();
        foreach (var (name, _) in Notes)
        {
            string f = Path.Combine(dir, name); if (!File.Exists(f)) continue;
            string t; try { t = File.ReadAllText(f, Encoding.UTF8).Replace("\r\n", "\n").Trim(); } catch (Exception) { continue; }
            if (name == "Journal.md") { if (t.Length > MaxJournal) t = "…\n" + t[^MaxJournal..]; }   // la fin : les entrées récentes
            else if (t.Length > MaxNote) t = t[..MaxNote] + "\n… (note tronquée : la raccourcir)";
            sb.Append("\n\n=== Pilote/").Append(name).Append(" ===\n").Append(t);
        }
        return sb.ToString();
    }
}
