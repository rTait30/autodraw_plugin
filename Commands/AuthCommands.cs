using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.AutoCAD.EditorInput;

namespace autodraw_plugin.Commands;

public class AuthCommands
{
    [CommandMethod("ADLOGIN")]
    public static async void ADLogin()
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
            await autodraw_plugin.autodraw.Auth.Login(userRes.StringResult, passRes.StringResult);

            ed.WriteMessage("\nLogin OK\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nLogin failed: {ex.Message}\n");
        }
    }
    [CommandMethod("ADLOGOUT")]
    public static async void ADLogout()
    {
        await autodraw_plugin.autodraw.Auth.Logout();
    }

    [CommandMethod("ADSTATUS")]
    public static void ADStatus()
    {
        var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (ed is null) return;
        var auth = autodraw_plugin.autodraw.Auth;
        if (auth.IsLoggedIn)
        {
            ed.WriteMessage($"\nLogged in as: {auth.CurrentUser} (Role: {auth.Role}, Verified: {auth.IsVerified})\n");
        }
        else
        {
            ed.WriteMessage("\nNot logged in.\n");
        }
    }
}