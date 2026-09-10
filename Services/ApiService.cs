using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace autodraw_plugin.Services
{
public static class ApiService
{
    // The Flask API. Point this at the backend directly - the Vite dev server
    // only proxies /api while `npm run dev` happens to be running.
    public static string BaseUrl { get; set; } = "http://localhost:5001/api";

    private static readonly HttpClient client = new HttpClient();

    private static string Url(string endpoint) =>
        $"{BaseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";

    /// <summary>
    /// Every automation route is behind role_required, so the bearer token has
    /// to travel with the request or the server answers 401.
    /// </summary>
    private static HttpRequestMessage Build(HttpMethod method, string endpoint, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, Url(endpoint));
        if (content != null) request.Content = content;

        string token = autodraw.Auth?.AuthToken;
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    public static async Task<HttpResponseMessage> Get(string endpoint)
    {
        HttpResponseMessage response = await client.SendAsync(Build(HttpMethod.Get, endpoint));
        response.EnsureSuccessStatusCode();
        return response;
    }

    public static async Task<HttpResponseMessage> Post(string endpoint, HttpContent content)
    {
        HttpResponseMessage response = await client.SendAsync(Build(HttpMethod.Post, endpoint, content));
        response.EnsureSuccessStatusCode();
        return response;
    }

    /// <summary>Form-only POST that does not throw on a 4xx, so gate messages can be read.</summary>
    public static async Task<HttpResponseMessage> PostForm(string endpoint, IDictionary<string, string> fields)
    {
        var form = new MultipartFormDataContent();
        if (fields != null)
        {
            foreach (var pair in fields)
            {
                if (!string.IsNullOrEmpty(pair.Value)) form.Add(new StringContent(pair.Value), pair.Key);
            }
        }
        return await client.SendAsync(Build(HttpMethod.Post, endpoint, form));
    }

    /// <summary>
    /// POST a DXF plus form fields, and hand back the response whatever its
    /// status. The gates the loop stops at - "this step is manual", "panel
    /// exceeds roll width" - arrive as 400 with a message worth reading, so
    /// this must not throw on them.
    /// </summary>
    public static async Task<HttpResponseMessage> PostDxf(
        string endpoint, string dxfPath, IDictionary<string, string> fields)
    {
        var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(File.ReadAllBytes(dxfPath));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "dxf", Path.GetFileName(dxfPath));

        if (fields != null)
        {
            foreach (var pair in fields)
            {
                if (!string.IsNullOrEmpty(pair.Value)) form.Add(new StringContent(pair.Value), pair.Key);
            }
        }

        return await client.SendAsync(Build(HttpMethod.Post, endpoint, form));
    }
}
}
