using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.AutoCAD.EditorInput;

internal static class AuthCommandsNew
{
    [CommandMethod("ADLOGIN2")]
    public static async void Login()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null) return;

        try
        {
            PromptStringOptions userOpts = new PromptStringOptions("\nEnter Username: ");
            userOpts.AllowSpaces = false;
            PromptResult userRes = ed.GetString(userOpts);
            if (userRes.Status != PromptStatus.OK) return;

            PromptStringOptions passOpts = new PromptStringOptions("\nEnter Password: ");
            passOpts.AllowSpaces = true;
            PromptResult passRes = ed.GetString(passOpts);
            if (passRes.Status != PromptStatus.OK) return;

            ed.WriteMessage("\nConnecting...");


        
            // hardcoded for now (POC) or read from env vars
            await ApiService.PostAsync("admin", "password");

            ed.WriteMessage("\n[COPE] Login OK\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\n[COPE] Login failed: {ex.Message}\n");
        }
    }
}