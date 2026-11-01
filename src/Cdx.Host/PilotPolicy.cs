using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cdx;

// Ce que Pilote décide tout seul d'un appel d'outil de Claude, avant de demander à Valentin (logique pure, essai `cdx-shell --selftest-pilote`).
// Trois niveaux (décision du 01/11/2026, « lecture libre, irréversible confirmé » par défaut) :
//   libre     lire, ouvrir, naviguer, écrire dans les dossiers de travail (Dev\, le coffre de Notes), les commandes sûres : sans demander ;
//             confirmer envoyer, supprimer, pousser, tout ce qui sort des dossiers de travail et toute commande inconnue
//   prudent   toute écriture passe par Autoriser / Refuser
//   auto      tout est permis, sauf ce qui est irréversible (toujours confirmé, quel que soit le niveau)
// Les outils en lecture seule sont aussi donnés à Claude Code par --allowedTools (AllowedTools) : il ne demande même pas.
static class PilotPolicy
{
    public enum Decision { Allow, Ask }
    public static readonly string[] Levels = { "libre", "prudent", "auto" };

    // Les outils intégrés et MCP qui ne changent rien : jamais de demande.
    public static readonly string[] ReadTools =
    {
        "Read", "Glob", "Grep", "WebSearch", "WebFetch", "LSP", "TodoWrite", "ToolSearch",
        "mcp__cdx-notes__vault_info", "mcp__cdx-notes__notes_list", "mcp__cdx-notes__notes_search", "mcp__cdx-notes__notes_read", "mcp__cdx-notes__notes_links", "mcp__cdx-notes__notes_tags", "mcp__cdx-notes__projects_list", "mcp__cdx-notes__tasks_list",
        "mcp__cdx-suite__suite_state", "mcp__cdx-suite__app_state", "mcp__cdx-suite__browser_tabs", "mcp__cdx-suite__browser_read", "mcp__cdx-suite__browser_find", "mcp__cdx-suite__browser_screenshot",
        "mcp__cdx-suite__mail_search", "mcp__cdx-suite__mail_read", "mcp__cdx-suite__agenda_events", "mcp__cdx-suite__agenda_tasks",
    };
    // Ce qui est permis sans demander au niveau « libre » (réversible, ou dans les dossiers de travail).
    static readonly string[] FreeTools =
    {
        "mcp__cdx-notes__notes_create", "mcp__cdx-notes__notes_write", "mcp__cdx-notes__notes_append", "mcp__cdx-notes__notes_edit", "mcp__cdx-notes__notes_set_property", "mcp__cdx-notes__notes_move", "mcp__cdx-notes__folders_create", "mcp__cdx-notes__projects_create",
        "mcp__cdx-suite__app_open", "mcp__cdx-suite__browser_open", "mcp__cdx-suite__browser_navigate", "mcp__cdx-suite__browser_click", "mcp__cdx-suite__browser_back", "mcp__cdx-suite__notify", "mcp__cdx-suite__project_folder",
    };
    // Toujours confirmé, à tous les niveaux : irréversible ou visible de l'extérieur.
    static readonly string[] AlwaysAsk = { "mcp__cdx-notes__notes_trash", "AskUserQuestion" };
    static readonly Regex AlwaysAskCommand = new(@"(^|[\s;&|(])(git\s+push|git\s+reset\s+--hard|git\s+clean|git\s+branch\s+-D|git\s+checkout\s+--\s|rm\s+-r|rm\s+-f|rmdir|del\s|erase\s|Remove-Item|rd\s|format\s|shutdown|Restart-Computer|Stop-Computer|taskkill|Stop-Process|reg\s+delete|diskpart)", RegexOptions.IgnoreCase);
    // Les commandes sûres (lecture, ou réversibles dans un dépôt) : chaque morceau d'une ligne (&&, ;, |) doit en être.
    static readonly Regex SafeCommand = new(@"^\s*(cd|ls|dir|pwd|cat|type|head|tail|wc|find|findstr|rg|grep|echo|where|which|tree|Get-ChildItem|Get-Content|Get-Location|Select-String|Test-Path|git\s+(status|log|diff|show|branch|remote|rev-parse|ls-files|init|add|commit|switch\s+-c|checkout\s+-b|stash\s+list|tag)|mkdir|md|New-Item\s+-ItemType\s+Directory|dotnet\s+(--version|build|test|new|restore|run)|node\s+(--version|-v)|npm\s+(--version|-v|install|i|ci|test|run|init)|npx\s+tsc|python\s+(--version|-V|-m\s+venv)|pip\s+(--version|list|install)|cargo\s+(--version|build|test|new|init|check)|gh\s+(repo\s+view|pr\s+list|pr\s+view|run\s+list|issue\s+list))(\s|$)", RegexOptions.IgnoreCase);

    public static Decision Decide(string tool, JsonElement input, IEnumerable<string> roots, string level)
    {
        level = Levels.Contains(level) ? level : "libre";
        if (AlwaysAsk.Contains(tool)) return Decision.Ask;
        if (tool is "Bash" or "PowerShell")
        {
            string cmd = Str(input, "command");
            if (AlwaysAskCommand.IsMatch(cmd)) return Decision.Ask;
            if (level == "auto") return Decision.Allow;
            return cmd.Length > 0 && cmd.Split(new[] { "&&", "||", ";", "|" }, StringSplitOptions.RemoveEmptyEntries).All(p => SafeCommand.IsMatch(p)) ? Decision.Allow : Decision.Ask;
        }
        if (tool == "mcp__cdx-suite__browser_fill") return Bool(input, "submit") ? Decision.Ask : level == "prudent" ? Decision.Ask : Decision.Allow;
        if (tool == "mcp__cdx-suite__app_command")
        {
            string c = Str(input, "command"); if (c.Contains("ml.send") || c.Contains("delete") || c.Contains("trash") || c.Contains("ml.act")) return Decision.Ask;
            return level == "auto" ? Decision.Allow : Decision.Ask;
        }
        if (ReadTools.Contains(tool)) return Decision.Allow;
        if (level == "auto") return Decision.Allow;
        if (tool is "Edit" or "Write" or "NotebookEdit" or "MultiEdit")
        {
            if (level == "prudent") return Decision.Ask;
            string p = Str(input, "file_path"); if (p.Length == 0) p = Str(input, "notebook_path");
            return Under(p, roots) ? Decision.Allow : Decision.Ask;
        }
        if (FreeTools.Contains(tool)) return level == "prudent" ? Decision.Ask : Decision.Allow;
        return Decision.Ask;
    }

    // Un chemin est sous l'un des dossiers de travail (comparaison insensible à la casse, chemins normalisés ; « .. » résolu).
    public static bool Under(string path, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf((char)0) >= 0) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); } catch (Exception) { return false; }
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string root;
            try { root = Path.GetFullPath(r).TrimEnd('\\', '/'); } catch (Exception) { continue; }
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Ce qu'on montre à Valentin pour décider : le nom lisible et le détail.
    public static (string title, string detail) Describe(string tool, JsonElement input)
    {
        string t = tool.StartsWith("mcp__cdx-suite__") ? "Suite · " + tool["mcp__cdx-suite__".Length..] : tool.StartsWith("mcp__cdx-notes__") ? "Notes · " + tool["mcp__cdx-notes__".Length..] : tool.StartsWith("mcp__") ? tool[5..] : tool;
        string d = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "Write" or "NotebookEdit" or "MultiEdit" => Str(input, "file_path").Length > 0 ? Str(input, "file_path") : Str(input, "notebook_path"),
            _ => input.ValueKind == JsonValueKind.Object ? string.Join("\n", input.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Object && p.Value.ValueKind != JsonValueKind.Array).Select(p => p.Name + " : " + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()))) : "",
        };
        if (d.Length > 1200) d = d[..1200] + "…";
        return (t, d);
    }

    static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    static bool Bool(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
}
