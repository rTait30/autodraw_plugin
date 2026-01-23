using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Models.Auth;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace autodraw_plugin.Services;

public class AuthService : IDisposable
{
    bool loggedIn = false;
    public string AuthToken { get; private set; }
    public string CurrentUser { get; private set; }
    public string Role { get; private set; }
    public bool IsVerified { get; private set; }

    public bool IsLoggedIn => !string.IsNullOrEmpty(AuthToken);

    public void SetSession(string token, string user, string role, bool isVerified)
    {
        loggedIn = true;
        AuthToken = token;
        CurrentUser = user;
        Role = role;
        IsVerified = isVerified;
    }

    public async Task Login(string username, string password)
    {
        var loginData = new LoginRequest
        {
            username = username,
            password = password
        };

        string json = JsonConvert.SerializeObject(loginData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            // Using ApiService from the same namespace
            // endpoint should be relative if ApiService prepends BaseUrl
            HttpResponseMessage response = await ApiService.Post("/login", content);
            
            // TODO: Parse response and update session

            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\nLogin response: {response}");

            string responseString = await response.Content.ReadAsStringAsync();
            LoginResponse data = JsonConvert.DeserializeObject<LoginResponse>(responseString);

            SetSession(data.access_token, data.username, data.role, data.verified);

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            Database db = doc.Database;
            // Lock the document before making changes
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                MText mtext = new MText();
                mtext.TextHeight = 1000;
                mtext.Contents = $"Hello {data.username}, {data.role}";
                mtext.Location = new Point3d(0, 2000, 0);

                btr.AppendEntity(mtext);
                tr.AddNewlyCreatedDBObject(mtext, true);

                tr.Commit();
            }
        }
        catch (Exception ex)
        {
            // Handle error
            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\nLogin error: {ex.Message}");
        }
    }

    public async Task Logout()
    {
        // Clear session data
        loggedIn = false;
        AuthToken = null;
        CurrentUser = null;
        Role = null;
        IsVerified = false;
        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\nLogged out successfully.");
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}
