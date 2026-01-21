using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ApiService;
using Autodesk.AutoCAD.Internal;
using Newtonsoft.Json;

public static class ServerSession
{
    public static string AuthToken { get; private set; }
    public static string CurrentUser { get; private set; }

    // TODO: Update to your real API URL
    

    public static void SetSession(string token, string user)
    {
        AuthToken = token;
        CurrentUser = user;
    }

    public static bool IsLoggedIn => !string.IsNullOrEmpty(AuthToken);

    public static void login(string username, string password)
    {
        /*
        string json = JsonConvert.SerializeObject({"usename": {});
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        string url = $"{PluginSession.BaseUrl}/login";
        HttpResponseMessage response = await ApiService.PostAsync(url, content);
        */
    }
}

namespace autodraw_plugin.Services
{
    internal class AuthService
    {
    }
}
