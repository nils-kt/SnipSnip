using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace SnipSnip
{
    public record UploadResult(string Url, string? DeleteUrl);

    internal static class Uploader
    {
        static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SnipSnip/1.0");
            return c;
        }

        public static string Name(UploadProvider p) => p switch
        {
            UploadProvider.Imgur => "Imgur",
            UploadProvider.Catbox => "catbox.moe",
            _ => p.ToString()
        };

        public static Task<UploadResult> UploadAsync(byte[] png, UploadProvider provider, string fileName) =>
            provider switch
            {
                UploadProvider.Catbox => UploadCatboxAsync(png, fileName),
                _ => UploadImgurAsync(png, fileName)
            };

        static async Task<UploadResult> UploadImgurAsync(byte[] png, string fileName)
        {
            string id = App.Settings.ImgurClientId.Trim();
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("Keine Imgur-Client-ID hinterlegt.");

            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(file, "image", fileName);
            content.Add(new StringContent("file"), "type");
            content.Add(new StringContent("SnipSnip"), "title");

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.imgur.com/3/image") { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", id);

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { throw new Exception($"Imgur antwortete mit HTTP {(int)resp.StatusCode}."); }

            using (doc)
            {
                var root = doc.RootElement;
                bool ok = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                if (!ok || !root.TryGetProperty("data", out var data))
                {
                    string msg = $"HTTP {(int)resp.StatusCode}";
                    if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("error", out var e))
                        msg = e.ValueKind == JsonValueKind.String ? e.GetString()! :
                              e.TryGetProperty("message", out var m) ? m.ToString() : e.ToString();
                    throw new Exception("Imgur: " + msg);
                }
                string link = data.GetProperty("link").GetString()!;
                string? del = data.TryGetProperty("deletehash", out var dh) ? dh.GetString() : null;
                return new UploadResult(link, del != null ? $"https://imgur.com/delete/{del}" : null);
            }
        }

        static async Task<UploadResult> UploadCatboxAsync(byte[] png, string fileName)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent("fileupload"), "reqtype");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(file, "fileToUpload", fileName);

            using var resp = await Http.PostAsync("https://catbox.moe/user/api.php", content).ConfigureAwait(false);
            string body = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
            if (!resp.IsSuccessStatusCode || !body.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"catbox.moe: {(body.Length > 0 && body.Length < 200 ? body : "HTTP " + (int)resp.StatusCode)}");
            return new UploadResult(body, null);
        }
    }
}
