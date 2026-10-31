using Kdx.Coffre;

namespace Kdx.NotesMcp;

// kdx-notes-mcp.exe            le serveur MCP (stdio), à déclarer à Claude
// kdx-notes-mcp.exe --selftest les essais du moteur du coffre et du serveur, sur un dossier jetable (jamais le vrai coffre) ; résultat dans selftest-notes.txt
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
        return Server.Serve(new Server(() => VaultConfig.Open(true)));
    }
}


