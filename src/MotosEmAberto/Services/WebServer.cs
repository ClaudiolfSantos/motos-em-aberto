using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MotosEmAberto.Services
{
    public class WebServer
    {
        private HttpListener? _listener;
        private bool _isRunning;
        private int _port = 5055;

        public int Start()
        {
            for (int p = 5055; p <= 5065; p++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                    listener.Start();
                    _listener = listener;
                    _port = p;
                    _isRunning = true;
                    break;
                }
                catch
                {
                    // Tenta a próxima porta se 5055 estiver ocupada
                }
            }

            if (_listener == null || !_listener.IsListening)
            {
                throw new InvalidOperationException("Não foi possível iniciar o servidor HTTP embutido nas portas 5055-5065.");
            }

            Task.Run(ListenLoop);
            return _port;
        }

        public void Stop()
        {
            _isRunning = false;
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }
        }

        private async Task ListenLoop()
        {
            while (_isRunning && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch
                {
                    if (!_isRunning) break;
                }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                // Suporte CORS
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string path = request.Url?.AbsolutePath ?? "/";
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // 1. Arquivos Estáticos: Logo
                if (path.StartsWith("/logo/"))
                {
                    string fileName = Uri.UnescapeDataString(path.Substring(6));
                    string filePath = Path.Combine(baseDir, "logo", fileName);
                    if (File.Exists(filePath))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(filePath);
                        response.ContentType = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/x-icon";
                        response.StatusCode = 200;
                        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        response.Close();
                        return;
                    }
                }

                // 2. Arquivos Estáticos: Frontend
                if (path == "/" || path == "/index.html")
                {
                    string indexPath = Path.Combine(baseDir, "web", "index.html");
                    if (File.Exists(indexPath))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(indexPath);
                        response.ContentType = "text/html; charset=utf-8";
                        response.StatusCode = 200;
                        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        response.Close();
                        return;
                    }
                }

                // 3. APIs
                if (request.HttpMethod == "GET")
                {
                    if (path == "/api/dados")
                    {
                        var q = request.QueryString;
                        var result = DatabaseService.GetDados(
                            q["loja"], q["regiao"], q["estado"], q["tipo_venda"],
                            q["status"], q["busca"], q["ordem"], q["dir"]
                        );
                        await SendJsonAsync(response, result);
                        return;
                    }
                    if (path == "/api/filtros")
                    {
                        var result = DatabaseService.GetFiltros();
                        await SendJsonAsync(response, result);
                        return;
                    }
                    if (path == "/api/versao")
                    {
                        var result = await UpdateService.CheckVersionAsync();
                        await SendJsonAsync(response, result);
                        return;
                    }
                    if (path == "/api/depara")
                    {
                        var result = DatabaseService.GetDePara();
                        await SendJsonAsync(response, result);
                        return;
                    }
                    if (path == "/api/sincronizar/status")
                    {
                        var result = SyncService.GetStatus();
                        await SendJsonAsync(response, result);
                        return;
                    }
                }
                else if (request.HttpMethod == "POST")
                {
                    if (path == "/api/sincronizar")
                    {
                        bool started = SyncService.StartSync();
                        if (started)
                        {
                            await SendJsonAsync(response, new { sucesso = true, mensagem = "Sincronização iniciada com sucesso." });
                        }
                        else
                        {
                            response.StatusCode = 409;
                            await SendJsonAsync(response, new { sucesso = false, mensagem = "A sincronização já está em andamento." });
                        }
                        return;
                    }
                    if (path == "/api/depara")
                    {
                        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                        string body = await reader.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        string loja = root.GetProperty("loja").GetString() ?? "";
                        string regiao = root.GetProperty("regiao").GetString() ?? "";
                        string estado = root.GetProperty("estado").GetString() ?? "";

                        DatabaseService.SaveDePara(loja, regiao, estado);
                        await SendJsonAsync(response, new { sucesso = true, mensagem = "Mapeamento De/Para salvo com sucesso!" });
                        return;
                    }
                }
                else if (request.HttpMethod == "DELETE")
                {
                    if (path == "/api/depara")
                    {
                        string loja = request.QueryString["loja"] ?? "";
                        if (!string.IsNullOrEmpty(loja))
                        {
                            DatabaseService.DeleteDePara(loja);
                            await SendJsonAsync(response, new { sucesso = true, mensagem = $"Loja {loja} removida com sucesso!" });
                        }
                        else
                        {
                            response.StatusCode = 400;
                            await SendJsonAsync(response, new { sucesso = false, mensagem = "Parâmetro loja não informado." });
                        }
                        return;
                    }
                }

                response.StatusCode = 404;
                response.Close();
            }
            catch (Exception ex)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    byte[] err = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { erro = ex.Message }));
                    await context.Response.OutputStream.WriteAsync(err, 0, err.Length);
                    context.Response.Close();
                }
                catch { }
            }
        }

        private static async Task SendJsonAsync(HttpListenerResponse response, object data)
        {
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = response.StatusCode == 0 ? 200 : response.StatusCode;
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            response.Close();
        }
    }
}
