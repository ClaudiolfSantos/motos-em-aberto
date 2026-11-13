using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MotosEmAberto.Services
{
    public static class SelfTestRunner
    {
        public static int RunAllTests()
        {
            var logBuilder = new StringBuilder();

            void Log(string msg)
            {
                Console.WriteLine(msg);
                logBuilder.AppendLine(msg);
            }

            void LogErr(string msg)
            {
                Console.Error.WriteLine(msg);
                logBuilder.AppendLine("ERROR: " + msg);
            }

            void SaveLog()
            {
                try
                {
                    string logPath = Path.Combine(Environment.CurrentDirectory, "self-test-report.log");
                    File.WriteAllText(logPath, logBuilder.ToString(), Encoding.UTF8);
                }
                catch { }
            }

            Log("=================================================================");
            Log("🧪 INICIANDO SUÍTE COMPLETA DE TESTES UNITÁRIOS E INTEGRAÇÃO (C#)");
            Log("=================================================================");

            int passed = 0;
            int failed = 0;

            void Assert(bool condition, string testName, string? details = null)
            {
                if (condition)
                {
                    passed++;
                    Log($"  [PASS] {testName}");
                }
                else
                {
                    failed++;
                    LogErr($"  [FAIL] {testName}" + (details != null ? $" -> {details}" : ""));
                }
            }

            try
            {
                // -----------------------------------------------------------------
                // 1. TESTE DE INICIALIZAÇÃO XAML / BAML (MainWindow)
                // -----------------------------------------------------------------
                Log("\n[1/5] Testando compilação BAML/XAML e instanciação de MainWindow...");
                try
                {
                    var window = new MainWindow();
                    Assert(window != null, "Instanciação de MainWindow sem XamlParseException");
                    Assert(window!.Title.Contains("Motos em Aberto"), "Título da janela principal configurado corretamente");
                    window.Close();
                }
                catch (Exception ex)
                {
                    Assert(false, "Instanciação de MainWindow sem XamlParseException", ex.ToString());
                }

                // -----------------------------------------------------------------
                // 2. TESTE DE BANCO DE DADOS E CONSOLIDAÇÃO (DatabaseService & SyncService)
                // -----------------------------------------------------------------
                Log("\n[2/5] Testando inicialização do SQLite, consolidação e regras de negócio...");
                DatabaseService.EnsureDatabaseInitialized();
                Assert(File.Exists(DatabaseService.DatabasePath), "Banco de dados motocar.db inicializado no AppData");

                // Executa Consolidate() para validar a query SQL do C#
                SyncService.Consolidate();
                Assert(true, "SyncService.Consolidate() executou sem erros SQL");

                using (var conn = DatabaseService.GetConnection())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT 
                            COUNT(*),
                            SUM(CASE WHEN loja_vendedor IS NULL OR loja_vendedor = '' THEN 1 ELSE 0 END),
                            SUM(CASE WHEN dataexpedicao = '31/12/1899' THEN 1 ELSE 0 END),
                            SUM(CASE 
                                WHEN (dias_emissao >= 10 AND dias_chegada IS NOT NULL AND dias_chegada >= 10 AND status != 'Verificar')
                                  OR ((dias_emissao < 10 OR dias_chegada IS NULL OR dias_chegada < 10) AND status != 'Não Verificar')
                                THEN 1 ELSE 0 END),
                            SUM(CASE WHEN nf_pedido IS NULL OR INSTR(nf_pedido, '/') = 0 THEN 1 ELSE 0 END),
                            SUM(CASE WHEN dataexpedicao IS NOT NULL AND NOT EXISTS (
                                SELECT 1 FROM motos_em_aberto_base b
                                WHERE b.empresa = motos_em_aberto_consolidado.empresa
                                  AND (CASE WHEN INSTR(TRIM(REPLACE(b.docfiscal, 'NF-E', '')), '/') > 0 THEN SUBSTR(TRIM(REPLACE(b.docfiscal, 'NF-E', '')), 1, INSTR(TRIM(REPLACE(b.docfiscal, 'NF-E', '')), '/') - 1) || '/' || b.proposta ELSE TRIM(REPLACE(b.docfiscal, 'NF-E', '')) || '/' || b.proposta END) = motos_em_aberto_consolidado.nf_pedido
                                  AND b.datahoraexpedicao IS NOT NULL AND TRIM(b.datahoraexpedicao) != '' AND b.datahoraexpedicao NOT LIKE '1899%'
                            ) THEN 1 ELSE 0 END)
                        FROM motos_em_aberto_consolidado;
                    ";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        long totalRows = reader.GetInt64(0);
                        long emptyLojas = reader.GetInt64(1);
                        long invalidDates1899 = reader.GetInt64(2);
                        long brokenStatusRule = reader.GetInt64(3);
                        long brokenNfPedido = reader.GetInt64(4);
                        long phantomExpedicao = reader.GetInt64(5);

                        Assert(totalRows > 0, $"Tabela consolidada populada ({totalRows} registros)");
                        Assert(emptyLojas == 0, "Regra de extração de sigla da loja (0 lojas vazias)");
                        Assert(invalidDates1899 == 0, "Regra de higienização de data 31/12/1899 (0 ocorrências)");
                        Assert(brokenStatusRule == 0, "Regra de validação 'Verificar' (dias_emissao >= 10 E dias_chegada >= 10) 100% consistente");
                        Assert(brokenNfPedido == 0, "Regra de formatação NF+pedido (documento/proposta) 100% consistente");
                        Assert(phantomExpedicao == 0, "Integridade de Data Expedição por (empresa + NF+pedido) (0 datas fantasmas de outras filiais)");
                    }
                }

                // -----------------------------------------------------------------
                // 3. TESTE DE PARSING JSON E PAYLOAD DE 90 DIAS (SyncService)
                // -----------------------------------------------------------------
                Log("\n[3/5] Testando geração de payload de 90 dias e parsing de tokens JSON...");
                string sampleJson = @"{
                    ""empresa"": ""MOTOCAR"",
                    ""aberto"": 18500.75,
                    ""recebido"": 0,
                    ""numeroproposta"": 127492,
                    ""datahoraexpedicao"": null,
                    ""ativo"": true
                }";
                using (var doc = JsonDocument.Parse(sampleJson))
                {
                    var root = doc.RootElement;
                    Assert(SyncService.GetProp(root, "empresa") == "MOTOCAR", "GetProp lê token String corretamente");
                    Assert(SyncService.ParseDouble(SyncService.GetProp(root, "aberto")) == 18500.75, "GetProp + ParseDouble lê token Number decimal sem exceção");
                    Assert(SyncService.ParseLong(SyncService.GetProp(root, "numeroproposta")) == 127492L, "GetProp + ParseLong lê token Number inteiro sem exceção");
                    Assert(SyncService.GetProp(root, "datahoraexpedicao") == "", "GetProp lê token Null sem exceção");
                    Assert(SyncService.GetProp(root, "inexistente") == "", "GetProp lida com chave ausente sem exceção");
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string finPath = Path.Combine(baseDir, "API", "motos_em_aberto_financeiro");
                string basePath = Path.Combine(baseDir, "API", "motos_em_aberto_base");

                if (File.Exists(finPath) && File.Exists(basePath))
                {
                    string finPayload = SyncService.PreparePayload(finPath, 90, isFinanceiro: true);
                    string basePayload = SyncService.PreparePayload(basePath, 90, isFinanceiro: false);

                    string expectedEnd = DateTime.Today.ToString("yyyy-MM-dd");
                    string expectedStart = DateTime.Today.AddDays(-90).ToString("yyyy-MM-dd");

                    Assert(finPayload.Contains($"Datademovimentacaoinicial={expectedStart};"), $"Payload Financeiro contém data inicial de 90 dias atrás ({expectedStart})");
                    Assert(finPayload.Contains($"Datademovimentacaofinal={expectedEnd};"), $"Payload Financeiro contém data final de hoje ({expectedEnd})");
                    Assert(basePayload.Contains($"Periododamovimentacaoinicial={expectedStart} 00:00;"), $"Payload Base contém período inicial de 90 dias atrás ({expectedStart} 00:00)");
                    Assert(basePayload.Contains($"Periododamovimentacaofinal={expectedEnd} 23:59;"), $"Payload Base contém período final de hoje ({expectedEnd} 23:59)");
                }
                else
                {
                    Assert(false, "Arquivos de configuração de API presentes no diretório de instalação");
                }

                // -----------------------------------------------------------------
                // 4. TESTE DE CONTRATO JSON E ROTAS HTTP REST (WebServer)
                // -----------------------------------------------------------------
                Log("\n[4/5] Testando servidor HTTP embutido e contrato JSON com o frontend...");
                var server = new WebServer();
                int port = server.Start();
                string baseUrl = $"http://127.0.0.1:{port}";

                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    // 4.1 GET /
                    var htmlResp = http.GetAsync($"{baseUrl}/").GetAwaiter().GetResult();
                    string htmlText = htmlResp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Assert(htmlResp.IsSuccessStatusCode && htmlText.Contains("Motos em Aberto"), "GET / retorna index.html com sucesso");

                    // 4.2 GET /api/filtros
                    var filtrosJson = http.GetStringAsync($"{baseUrl}/api/filtros").GetAwaiter().GetResult();
                    using (var fDoc = JsonDocument.Parse(filtrosJson))
                    {
                        var fRoot = fDoc.RootElement;
                        Assert(fRoot.TryGetProperty("lojas", out var l) && l.GetArrayLength() > 0, "GET /api/filtros retorna lista de lojas");
                        Assert(fRoot.TryGetProperty("regioes", out var r) && r.GetArrayLength() > 0, "GET /api/filtros retorna lista de regiões");
                        Assert(fRoot.TryGetProperty("estados", out var e) && e.GetArrayLength() > 0, "GET /api/filtros retorna lista de estados");
                        Assert(fRoot.TryGetProperty("ultima_atualizacao", out var u) && !string.IsNullOrEmpty(u.GetString()), "GET /api/filtros retorna 'ultima_atualizacao' exigido pelo frontend");
                    }

                    // 4.3 GET /api/dados
                    var dadosJson = http.GetStringAsync($"{baseUrl}/api/dados?ordem=dias_chao_desc&status=Verificar").GetAwaiter().GetResult();
                    using (var dDoc = JsonDocument.Parse(dadosJson))
                    {
                        var dRoot = dDoc.RootElement;
                        Assert(dRoot.TryGetProperty("linhas", out var linhas) && linhas.ValueKind == JsonValueKind.Array && linhas.GetArrayLength() > 0,
                            "GET /api/dados retorna array 'linhas' exigido pelo frontend");
                        Assert(dRoot.TryGetProperty("totais", out var totais) &&
                               totais.TryGetProperty("total_motos", out _) &&
                               totais.TryGetProperty("total_aberto", out _) &&
                               totais.TryGetProperty("total_recebido", out _) &&
                               totais.TryGetProperty("total_verificar", out _),
                            "GET /api/dados retorna objeto 'totais' com chaves exatas do frontend");
                    }

                    // 4.4 GET, POST, DELETE /api/depara (Teste de CRUD completo)
                    var postBody = new StringContent(
                        JsonSerializer.Serialize(new { loja = "TST99", regiao = "GRANDE RIO - RJ", estado = "RJ" }),
                        Encoding.UTF8, "application/json"
                    );
                    var postResp = http.PostAsync($"{baseUrl}/api/depara", postBody).GetAwaiter().GetResult();
                    Assert(postResp.IsSuccessStatusCode, "POST /api/depara adiciona/atualiza loja com sucesso");

                    var deparaJson = http.GetStringAsync($"{baseUrl}/api/depara").GetAwaiter().GetResult();
                    using (var dpDoc = JsonDocument.Parse(deparaJson))
                    {
                        var dpRoot = dpDoc.RootElement;
                        Assert(dpRoot.TryGetProperty("lojas", out var dpLojas) && dpLojas.GetArrayLength() >= 100, "GET /api/depara retorna lojas mapeadas");
                        Assert(dpRoot.TryGetProperty("pendentes", out var dpPend) && dpPend.ValueKind == JsonValueKind.Array, "GET /api/depara retorna 'pendentes' sem erro SQL");
                    }

                    var delResp = http.DeleteAsync($"{baseUrl}/api/depara?loja=TST99").GetAwaiter().GetResult();
                    Assert(delResp.IsSuccessStatusCode, "DELETE /api/depara remove loja de teste com sucesso");

                    // 4.5 GET /api/versao
                    var verJson = http.GetStringAsync($"{baseUrl}/api/versao").GetAwaiter().GetResult();
                    using (var vDoc = JsonDocument.Parse(verJson))
                    {
                        var vRoot = vDoc.RootElement;
                        Assert(vRoot.TryGetProperty("versao_atual", out _) && vRoot.TryGetProperty("tem_atualizacao", out _),
                            "GET /api/versao retorna status de controle de versão");
                    }
                }

                server.Stop();

                // -----------------------------------------------------------------
                // 5. RESUMO FINAL
                // -----------------------------------------------------------------
                Log("\n=================================================================");
                Log($"📊 RESULTADO DOS TESTES: {passed} aprovados | {failed} falharam");
                Log("=================================================================");

                SaveLog();
                return failed == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                LogErr("Erro fatal durante execução da suíte de testes: " + ex);
                SaveLog();
                return 2;
            }
        }
    }
}
