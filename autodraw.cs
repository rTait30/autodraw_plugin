using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

using Autodesk.AutoCAD.ApplicationServices;

using autodraw_plugin.Commands;
using autodraw_plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

[assembly: ExtensionApplication(typeof(autodraw_plugin.autodraw))]

namespace autodraw_plugin;

public class autodraw : IExtensionApplication
{
    // specific static instance to access services globally if not using full DI
    public static AuthService Auth { get; private set; }
    public static AutoDrawService AutoDraw { get; private set; }

    public void Initialize()
    {

        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        ed.WriteMessage($"\n> autodraw plugin loaded successfully.");
        // 1. Initialize Services
        Auth = new AuthService();
        AutoDraw = new AutoDrawService();
    }

    public void Terminate()
    {
        // Cleanup resources, close streams, or save config on exit
        Auth?.Dispose();
    }
}
