using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Models.Auth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace autodraw_plugin.Services;

/// <summary>
/// The access token lives ten minutes and only in memory. The refresh token is
/// the login: TokenStore keeps it encrypted so a restart does not need another
/// ADLOGIN, and every refresh spends it for a new one. The server ends the
/// login after 14 idle days, 60 days after ADLOGIN, on ADLOGOUT, or when the
/// password changes.
/// </summary>
public class AuthService : IDisposable
{
    public string AuthToken { get; private set; }
    public string RefreshToken { get; private set; }
    public string CurrentUser { get; private set; }
    public string Role { get; private set; }
    public bool IsVerified { get; private set; }

    DateTime accessExpiresUtc = DateTime.MinValue;
    readonly SemaphoreSlim refreshing = new SemaphoreSlim(1, 1);

    public bool IsLoggedIn => !string.IsNullOrEmpty(RefreshToken);

    /// <summary>
    /// Pick up the login saved by the last ADLOGIN. There is no access token
    /// yet; the first request refreshes one.
    /// </summary>
    public void Restore()
    {
        SavedSession saved = TokenStore.Load();
        if (saved == null) return;
        RefreshToken = saved.refresh_token;
        CurrentUser = saved.username;
        Role = saved.role;
        IsVerified = saved.verified;
    }

    void SetSession(LoginResponse data)
    {
        AuthToken = data.access_token;
        accessExpiresUtc = ExpiryOf(data.access_token);
        RefreshToken = data.refresh_token;
        CurrentUser = data.username;
        Role = data.role;
        IsVerified = data.verified;
        TokenStore.Save(new SavedSession
        {
            refresh_token = RefreshToken,
            username = CurrentUser,
            role = Role,
            verified = IsVerified,
        });
    }

    void ClearSession()
    {
        AuthToken = null;
        accessExpiresUtc = DateTime.MinValue;
        RefreshToken = null;
        CurrentUser = null;
        Role = null;
        IsVerified = false;
        TokenStore.Delete();
    }

    /// <summary>The access token's own expiry - the "exp" claim of the JWT's payload.</summary>
    static DateTime ExpiryOf(string jwt)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        long exp = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))["exp"].Value<long>();
        return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
    }

    /// <summary>
    /// Refresh before a request if the access token has lapsed or is about to,
    /// so a large drawing is not uploaded only to be refused.
    /// </summary>
    public async Task EnsureFresh()
    {
        if (IsLoggedIn && DateTime.UtcNow > accessExpiresUtc.AddMinutes(-1))
        {
            await Refresh(AuthToken);
        }
    }

    /// <summary>
    /// Spend the refresh token for a new pair. <paramref name="stale"/> is the
    /// access token the caller found wanting: if another request has refreshed
    /// since, that one is used rather than spending the token twice. False if
    /// there is still no usable access token.
    /// </summary>
    public async Task<bool> Refresh(string stale)
    {
        await refreshing.WaitAsync();
        try
        {
            if (AuthToken != stale) return AuthToken != null;
            if (!IsLoggedIn) return false;

            HttpResponseMessage response;
            try
            {
                response = await ApiService.SendAs(HttpMethod.Post, "/refresh", null, RefreshToken);
            }
            catch (HttpRequestException)
            {
                return false;       // server unreachable - keep the login for when it is back
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                ClearSession();
                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nSession expired - run ADLOGIN.");
                return false;
            }
            if (!response.IsSuccessStatusCode) return false;

            SetSession(JsonConvert.DeserializeObject<LoginResponse>(await response.Content.ReadAsStringAsync()));
            return true;
        }
        finally
        {
            refreshing.Release();
        }
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
            HttpResponseMessage response = await ApiService.SendAs(HttpMethod.Post, "/login", content, null);
            response.EnsureSuccessStatusCode();

            // TODO: Parse response and update session

            Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\nLogin response: {response}");

            string responseString = await response.Content.ReadAsStringAsync();
            LoginResponse data = JsonConvert.DeserializeObject<LoginResponse>(responseString);

            SetSession(data);

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            Database db = doc.Database;
            // Lock the document before making changes
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // On INFO so it is never submitted, and far enough out that it
                // stays clear of the drawing whatever size the job is.
                AutoDrawVisualizer.EnsureInfoLayer(tr, db);

                MText mtext = new MText();
                mtext.TextHeight = 1000;
                mtext.Contents = $"Hello {data.username}, {data.role}";
                mtext.Location = new Point3d(-60000, 5000, 0);
                mtext.Layer = AutoDrawVisualizer.InfoLayer;

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
        // Revoke the login on the server too, so the saved token is worthless
        // wherever a copy of it went. Logged out here whatever the server says.
        if (IsLoggedIn)
        {
            try
            {
                await ApiService.SendAs(HttpMethod.Post, "/logout", null, RefreshToken);
            }
            catch (HttpRequestException)
            {
            }
        }
        ClearSession();
        Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\nLogged out successfully.");
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}
