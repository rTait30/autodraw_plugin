using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace ApiService { 
    internal static class ApiService
    {
        public static string BaseUrl { get; } = "http://127.0.0.1:5001/copelands/api";

        private static readonly HttpClient client = new HttpClient();

        public static async Task<string> GetAsync(string endpoint)
        {
            string url = $"{BaseUrl}{endpoint}";

            HttpResponseMessage response = await client.GetAsync(url);

            // Throws if not 200-299 (nice for POC)
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        public static async Task<string> PostAsync(string endpoint, StringContent content)
        {
            string url = $"{BaseUrl}{endpoint}";

            HttpResponseMessage response = await client.PostAsync(url, content);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }
    }
}
