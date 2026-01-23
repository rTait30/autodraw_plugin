using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace autodraw_plugin.Services
{
public static class ApiService
{
    public static string BaseUrl { get; } = "http://127.0.0.1:5001/copelands/api";

    private static readonly HttpClient client = new HttpClient();

    public static async Task<HttpResponseMessage> Get(string endpoint)
    {
        string url = $"{BaseUrl}{endpoint}";

        HttpResponseMessage response = await client.GetAsync(url);

        // Throws if not 200-299 (nice for POC)
        response.EnsureSuccessStatusCode();

        return response;
    }

    public static async Task<HttpResponseMessage> Post(string endpoint, StringContent content)
    {
        string url = $"{BaseUrl}{endpoint}";

        HttpResponseMessage response = await client.PostAsync(url, content);

        response.EnsureSuccessStatusCode();

        return response;
    }
}
}