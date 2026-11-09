using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MotosEmAberto.Services
{
    public static class UpdateService
    {
        public const string CurrentVersion = "1.0.0";
        public const string GitHubRepo = "ClaudiolfSantos/motos-em-aberto";

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        static UpdateService()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "MotosEmAberto-Updater");
        }

        public static async Task<Dictionary<string, object?>> CheckVersionAsync()
        {
            string url = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrEmpty(token))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }

                var response = await _httpClient.SendAsync(req);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tag = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                    string cleanTag = tag.TrimStart('v', 'V');
                    string htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

                    string? msiUrl = null;
                    if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsElem.EnumerateArray())
                        {
                            string assetName = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                                assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                if (asset.TryGetProperty("browser_download_url", out var dl))
                                {
                                    msiUrl = dl.GetString();
                                    break;
                                }
                            }
                        }
                    }

                    bool hasUpdate = CompareSemVer(cleanTag, CurrentVersion) > 0;

                    return new Dictionary<string, object?>
                    {
                        ["versao_atual"] = CurrentVersion,
                        ["ultima_versao"] = cleanTag,
                        ["tem_atualizacao"] = hasUpdate,
                        ["url_release"] = htmlUrl,
                        ["url_msi"] = msiUrl,
                        ["notas"] = body,
                        ["mensagem"] = hasUpdate
                            ? $"Nova versão v{cleanTag} disponível para download!"
                            : $"Você está na versão mais recente (v{CurrentVersion})."
                    };
                }
            }
            catch
            {
                // Fallback silencioso em caso de sem rede
            }

            return new Dictionary<string, object?>
            {
                ["versao_atual"] = CurrentVersion,
                ["ultima_versao"] = CurrentVersion,
                ["tem_atualizacao"] = false,
                ["url_release"] = $"https://github.com/{GitHubRepo}/releases",
                ["url_msi"] = null,
                ["notas"] = "",
                ["mensagem"] = $"Você está na versão oficial v{CurrentVersion} (Mais recente)."
            };
        }

        private static int CompareSemVer(string v1, string v2)
        {
            var p1 = ParseParts(v1);
            var p2 = ParseParts(v2);

            for (int i = 0; i < Math.Max(p1.Count, p2.Count); i++)
            {
                int n1 = i < p1.Count ? p1[i] : 0;
                int n2 = i < p2.Count ? p2[i] : 0;
                if (n1 != n2) return n1.CompareTo(n2);
            }
            return 0;
        }

        private static List<int> ParseParts(string v)
        {
            var list = new List<int>();
            var parts = v.Split(new[] { '.', '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (int.TryParse(p, out int val))
                {
                    list.Add(val);
                }
            }
            return list;
        }
    }
}
