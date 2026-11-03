using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cdx;

// Une seule instance de chaque programme (verrou « Local\Cdx.<nom> ») : la seconde passe sa demande à la première par le bus (open-path, open-url...)
// puis s'arrête.
static class SingleInstance
{
    // True : ce processus est le premier (le verrou est rendu dans `mutex`, à garder jusqu'à la fin).
    public static bool Acquire(string name, [NotNullWhen(true)] out Mutex? mutex)
    {
        // CDX_PIPE_PREFIX (essais isolés) sépare aussi le verrou : un essai ne se heurte pas au vrai programme.
        mutex = new Mutex(true, "Local\\Cdx." + (Environment.GetEnvironmentVariable("CDX_PIPE_PREFIX") ?? "") + name, out bool created);
        if (created) return true;
        mutex.Dispose(); mutex = null;
        return false;
    }

    // Passe la demande à la première instance, qui démarre peut-être encore : on réessaie pendant 5 s.
    public static void Relay(string app, string type, object data)
    {
        for (int i = 0; i < 25; i++) { if (Bus.Send(app, type, data, app)) return; Thread.Sleep(200); }
        Host.Log("bus : la première instance de " + app + " ne répond pas");
    }
}
