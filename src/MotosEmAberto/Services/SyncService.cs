using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace MotosEmAberto.Services
{
    public static class SyncService
    {
        private const string ApiUrl = "https://microworkcloud.com.br/api/integracao/terceiro";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public static bool IsRunning { get; private set; }
        public static string CurrentStage { get; private set; } = "";
        public static int Progress { get; private set; } = 0;
        public static int TotalFinanceiro { get; private set; } = 0;
        public static int TotalBase { get; private set; } = 0;
        public static string? Error { get; private set; } = null;
        public static string? CompletedAt { get; private set; } = null;

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        public static Dictionary<string, object?> GetStatus()
        {
            return new Dictionary<string, object?>
            {
                ["rodando"] = IsRunning,
                ["etapa"] = CurrentStage,
                ["progresso"] = Progress,
                ["total_fin"] = TotalFinanceiro,
                ["total_base"] = TotalBase,
                ["erro"] = Error,
                ["concluido_em"] = CompletedAt
            };
        }

        public static bool StartSync()
        {
            if (IsRunning) return false;

            Task.Run(async () =>
            {
                IsRunning = true;
                Error = null;
                Progress = 10;
                CurrentStage = "1/4: Conectando ao banco SQLite...";

                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string credPath = Path.Combine(baseDir, "API", "credencial");
                    if (!File.Exists(credPath))
                    {
                        throw new FileNotFoundException("Arquivo de credencial não encontrado em: " + credPath);
                    }
                    string token = File.ReadAllText(credPath, Encoding.UTF8).Trim();

                    // Etapa 2: Financeiro
                    Progress = 30;
                    CurrentStage = "2/4: Consultando API de Financeiro na MicroWork (3 meses)...";
                    string finPayload = PreparePayload(Path.Combine(baseDir, "API", "motos_em_aberto_financeiro"), 90, isFinanceiro: true);
                    var finRecords = await FetchApiAsync(finPayload, token);
                    TotalFinanceiro = finRecords.Count;
                    SaveFinanceiro(finRecords);

                    // Etapa 3: Veículos / Base
                    Progress = 60;
                    CurrentStage = "3/4: Consultando API de Veículos (12.000+ linhas) - Processando dados, por favor aguarde...";
                    string basePayload = PreparePayload(Path.Combine(baseDir, "API", "motos_em_aberto_base"), 90, isFinanceiro: false);
                    var baseRecords = await FetchApiAsync(basePayload, token);
                    TotalBase = baseRecords.Count;
                    SaveBase(baseRecords);

                    // Etapa 4: Consolidação
                    Progress = 90;
                    CurrentStage = "4/4: Cruzando pedidos, calculando dias no chão e regras de validação...";
                    Consolidate();

                    Progress = 100;
                    CurrentStage = "Sincronização concluída com sucesso!";
                    CompletedAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                    CurrentStage = "Erro na sincronização: " + ex.Message;
                }
                finally
                {
                    IsRunning = false;
                }
            });

            return true;
        }

        public static string PreparePayload(string filePath, int daysBack, bool isFinanceiro)
        {
            string content = File.ReadAllText(filePath, Encoding.UTF8);
            int start = content.IndexOf('{');
            int end = content.LastIndexOf('}');
            if (start < 0 || end < 0) throw new InvalidDataException("JSON inválido no arquivo de API.");

            string jsonStr = content.Substring(start, end - start + 1);
            
            // Limpeza de quebras literais
            var sb = new StringBuilder();
            bool inQuote = false;
            foreach (char c in jsonStr)
            {
                if (c == '"') inQuote = !inQuote;
                if (c == '\n' && inQuote) sb.Append(' ');
                else sb.Append(c);
            }
            jsonStr = sb.ToString();

            DateTime hoje = DateTime.Today;
            DateTime dataIni = hoje.AddDays(-daysBack);
            string dtFimStr = hoje.ToString("yyyy-MM-dd");
            string dtIniStr = dataIni.ToString("yyyy-MM-dd");

            if (isFinanceiro)
            {
                jsonStr = Regex.Replace(jsonStr, @"Datademovimentacaoinicial=[^;]+;", $"Datademovimentacaoinicial={dtIniStr};");
                jsonStr = Regex.Replace(jsonStr, @"Datademovimentacaofinal=[^;]+;", $"Datademovimentacaofinal={dtFimStr};");
            }
            else
            {
                jsonStr = Regex.Replace(jsonStr, @"Periododamovimentacaoinicial=[^;]+;", $"Periododamovimentacaoinicial={dtIniStr} 00:00;");
                jsonStr = Regex.Replace(jsonStr, @"Periododamovimentacaofinal=[^;]+;", $"Periododamovimentacaofinal={dtFimStr} 23:59;");
            }

            return jsonStr;
        }

        public static async Task<List<JsonElement>> FetchApiAsync(string payloadJson, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var list = new List<JsonElement>();

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    list.Add(item.Clone());
                }
            }
            return list;
        }

        public static void SaveFinanceiro(List<JsonElement> records)
        {
            using var conn = DatabaseService.GetConnection();
            using var trans = conn.BeginTransaction();

            using (var cmdDel = conn.CreateCommand())
            {
                cmdDel.Transaction = trans;
                cmdDel.CommandText = "DELETE FROM motos_em_aberto_financeiro;";
                cmdDel.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    INSERT INTO motos_em_aberto_financeiro (
                        empresa, emissao, vencimento, dataemissao, especiedodocumento,
                        pessoa, recebido, aberto, vendedor, documentoparcela,
                        numeroproposta, descricao_veiculo_tipo_venda, sincronizado_em
                    ) VALUES (
                        @empresa, @emissao, @vencimento, @dataemissao, @especie,
                        @pessoa, @recebido, @aberto, @vendedor, @documento,
                        @proposta, @tipo_venda, datetime('now', 'localtime')
                    );
                ";

                var pEmpresa = cmd.Parameters.Add("@empresa", SqliteType.Text);
                var pEmissao = cmd.Parameters.Add("@emissao", SqliteType.Text);
                var pVencimento = cmd.Parameters.Add("@vencimento", SqliteType.Text);
                var pDataemissao = cmd.Parameters.Add("@dataemissao", SqliteType.Text);
                var pEspecie = cmd.Parameters.Add("@especie", SqliteType.Text);
                var pPessoa = cmd.Parameters.Add("@pessoa", SqliteType.Text);
                var pRecebido = cmd.Parameters.Add("@recebido", SqliteType.Real);
                var pAberto = cmd.Parameters.Add("@aberto", SqliteType.Real);
                var pVendedor = cmd.Parameters.Add("@vendedor", SqliteType.Text);
                var pDocumento = cmd.Parameters.Add("@documento", SqliteType.Text);
                var pProposta = cmd.Parameters.Add("@proposta", SqliteType.Integer);
                var pTipoVenda = cmd.Parameters.Add("@tipo_venda", SqliteType.Text);

                foreach (var r in records)
                {
                    pEmpresa.Value = GetProp(r, "empresa");
                    pEmissao.Value = GetProp(r, "emissao");
                    pVencimento.Value = GetProp(r, "vencimento");
                    pDataemissao.Value = GetProp(r, "dataemissao");
                    pEspecie.Value = GetProp(r, "especiedodocumento");
                    pPessoa.Value = GetProp(r, "pessoa");
                    pRecebido.Value = ParseDouble(GetProp(r, "recebido"));
                    pAberto.Value = ParseDouble(GetProp(r, "aberto"));
                    pVendedor.Value = GetProp(r, "vendedor");
                    pDocumento.Value = GetProp(r, "documentoparcela");
                    pProposta.Value = ParseLong(GetProp(r, "numeroproposta"));
                    pTipoVenda.Value = GetProp(r, "descricao_veiculo_tipo_venda");

                    cmd.ExecuteNonQuery();
                }
            }

            trans.Commit();
        }

        public static void SaveBase(List<JsonElement> records)
        {
            using var conn = DatabaseService.GetConnection();
            using var trans = conn.BeginTransaction();

            using (var cmdDel = conn.CreateCommand())
            {
                cmdDel.Transaction = trans;
                cmdDel.CommandText = "DELETE FROM motos_em_aberto_base;";
                cmdDel.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    INSERT INTO motos_em_aberto_base (
                        chassi, pessoa, vendedor, datachegada, docfiscal,
                        proposta, quantidade, empresa, datahoraexpedicao, sincronizado_em
                    ) VALUES (
                        @chassi, @pessoa, @vendedor, @datachegada, @docfiscal,
                        @proposta, @quantidade, @empresa, @datahoraexpedicao, datetime('now', 'localtime')
                    );
                ";

                var pChassi = cmd.Parameters.Add("@chassi", SqliteType.Text);
                var pPessoa = cmd.Parameters.Add("@pessoa", SqliteType.Text);
                var pVendedor = cmd.Parameters.Add("@vendedor", SqliteType.Text);
                var pDatachegada = cmd.Parameters.Add("@datachegada", SqliteType.Text);
                var pDocfiscal = cmd.Parameters.Add("@docfiscal", SqliteType.Text);
                var pProposta = cmd.Parameters.Add("@proposta", SqliteType.Integer);
                var pQuantidade = cmd.Parameters.Add("@quantidade", SqliteType.Real);
                var pEmpresa = cmd.Parameters.Add("@empresa", SqliteType.Text);
                var pDataexp = cmd.Parameters.Add("@datahoraexpedicao", SqliteType.Text);

                foreach (var r in records)
                {
                    pChassi.Value = GetProp(r, "chassi");
                    pPessoa.Value = GetProp(r, "pessoa");
                    pVendedor.Value = GetProp(r, "vendedor");
                    pDatachegada.Value = GetProp(r, "datachegada");
                    pDocfiscal.Value = GetProp(r, "docfiscal");
                    pProposta.Value = ParseLong(GetProp(r, "proposta"));
                    pQuantidade.Value = ParseDouble(GetProp(r, "quantidade"));
                    pEmpresa.Value = GetProp(r, "empresa");
                    pDataexp.Value = GetProp(r, "datahoraexpedicao");

                    cmd.ExecuteNonQuery();
                }
            }

            trans.Commit();
        }

        public static void Consolidate()
        {
            using var conn = DatabaseService.GetConnection();
            using var trans = conn.BeginTransaction();

            using (var cmdDel = conn.CreateCommand())
            {
                cmdDel.Transaction = trans;
                cmdDel.CommandText = "DELETE FROM motos_em_aberto_consolidado;";
                cmdDel.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    INSERT INTO motos_em_aberto_consolidado (
                        empresa, loja_vendedor, regiao, estado, nome_vendedor, vendedor,
                        pessoa, dias_chegada, dias_emissao, aberto, recebido,
                        documento, dataexpedicao, nf_pedido,
                        documentoparcela, numeroproposta, descricao_veiculo_tipo_venda,
                        vencimento, emissao, info_dias_emissao,
                        datachegada, info_dias_chegada, status,
                        especiedodocumento, sincronizado_em
                    )
                    SELECT 
                        f.empresa,
                        t.loja_vendedor,
                        COALESCE(dp.regiao, 'PENDENTE DE/PARA') as regiao,
                        COALESCE(dp.estado, 'PENDENTE') as estado,
                        
                        CASE 
                            WHEN f.vendedor LIKE '%-%' THEN TRIM(SUBSTR(f.vendedor, INSTR(f.vendedor, '-') + 1))
                            ELSE TRIM(f.vendedor)
                        END as nome_vendedor,
                        f.vendedor,
                        f.pessoa,
                        
                        -- Dias no chão (dias desde a chegada)
                        CASE 
                            WHEN b.datachegada IS NOT NULL AND b.datachegada != '' 
                            THEN CAST(julianday(date('now', 'localtime')) - julianday(date(b.datachegada)) AS INTEGER)
                            ELSE NULL 
                        END as dias_chegada,
                        
                        -- Dia emissão (dias desde a emissão)
                        CAST(julianday(date('now', 'localtime')) - julianday(date(f.emissao)) AS INTEGER) as dias_emissao,
                        
                        f.aberto,
                        f.recebido,
                        
                        -- Documento (NF base antes do ' - ')
                        CASE 
                            WHEN INSTR(f.documentoparcela, ' - ') > 0 
                            THEN SUBSTR(f.documentoparcela, 1, INSTR(f.documentoparcela, ' - ') - 1)
                            ELSE f.documentoparcela 
                        END as documento,
                        
                        -- Data Expedição
                        CASE 
                            WHEN b.datahoraexpedicao IS NOT NULL AND b.datahoraexpedicao != '' AND b.datahoraexpedicao NOT LIKE '1899%'
                            THEN strftime('%d/%m/%Y', b.datahoraexpedicao)
                            ELSE NULL 
                        END as dataexpedicao,
                        
                        -- NF+pedido (ex: 144678/116587)
                        CASE 
                            WHEN INSTR(f.documentoparcela, '/') > 0 
                            THEN SUBSTR(f.documentoparcela, 1, INSTR(f.documentoparcela, '/') - 1) || '/' || f.numeroproposta
                            ELSE f.documentoparcela || '/' || f.numeroproposta
                        END as nf_pedido,
                        
                        f.documentoparcela,
                        f.numeroproposta,
                        f.descricao_veiculo_tipo_venda,
                        strftime('%d/%m/%Y', f.vencimento) as vencimento,
                        strftime('%d/%m/%Y', f.emissao) as emissao,
                        CAST(julianday(date('now', 'localtime')) - julianday(date(f.emissao)) AS INTEGER) || ' dias (' || strftime('%d/%m/%Y', f.emissao) || ')' as info_dias_emissao,
                        
                        CASE 
                            WHEN b.datachegada IS NOT NULL AND b.datachegada != '' 
                            THEN strftime('%d/%m/%Y', b.datachegada)
                            ELSE NULL 
                        END as datachegada,
                        
                        CASE 
                            WHEN b.datachegada IS NOT NULL AND b.datachegada != '' 
                            THEN CAST(julianday(date('now', 'localtime')) - julianday(date(b.datachegada)) AS INTEGER) || ' dias (' || strftime('%d/%m/%Y', b.datachegada) || ')'
                            ELSE 'Aguardando Chegada'
                        END as info_dias_chegada,
                        
                        -- Regra de Validação
                        CASE 
                            WHEN (CAST(julianday(date('now', 'localtime')) - julianday(date(f.emissao)) AS INTEGER) >= 10)
                             AND (b.datachegada IS NOT NULL AND b.datachegada != '' AND CAST(julianday(date('now', 'localtime')) - julianday(date(b.datachegada)) AS INTEGER) >= 10)
                            THEN 'Verificar'
                            ELSE 'Não Verificar'
                        END as status,
                        
                        f.especiedodocumento,
                        datetime('now', 'localtime')
                    FROM motos_em_aberto_financeiro f
                    CROSS JOIN (
                        SELECT 
                            f_inner.id as fid,
                            CASE 
                                WHEN f_inner.vendedor LIKE '%-%' THEN TRIM(SUBSTR(f_inner.vendedor, 1, INSTR(f_inner.vendedor, '-') - 1))
                                WHEN f_inner.empresa LIKE '%-%' THEN TRIM(SUBSTR(f_inner.empresa, INSTR(f_inner.empresa, '-') + 1))
                                ELSE 'OUTROS'
                            END as loja_vendedor
                        FROM motos_em_aberto_financeiro f_inner
                    ) t ON f.id = t.fid
                    LEFT JOIN (
                        SELECT 
                            proposta, 
                            MAX(datachegada) as datachegada,
                            MAX(datahoraexpedicao) as datahoraexpedicao
                        FROM motos_em_aberto_base
                        GROUP BY proposta
                    ) b ON f.numeroproposta = b.proposta
                    LEFT JOIN de_para_lojas dp ON UPPER(t.loja_vendedor) = dp.loja;
                ";
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }

        public static string GetProp(JsonElement elem, string prop)
        {
            if (elem.TryGetProperty(prop, out var v))
            {
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString() ?? "",
                    JsonValueKind.Number => v.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => ""
                };
            }
            return "";
        }

        public static double ParseDouble(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return 0.0;
            str = str.Replace("R$", "").Trim();
            if (double.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                return val;
            if (double.TryParse(str, System.Globalization.NumberStyles.Any, new System.Globalization.CultureInfo("pt-BR"), out var valPt))
                return valPt;
            return 0.0;
        }

        public static long ParseLong(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return 0;
            if (long.TryParse(str, out var val)) return val;
            if (double.TryParse(str, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dval))
                return (long)dval;
            return 0;
        }
    }
}
