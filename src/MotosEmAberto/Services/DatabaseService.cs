using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MotosEmAberto.Services
{
    public static class DatabaseService
    {
        public static string AppDataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Motocar", "MotosEmAberto"
        );

        public static string DatabasePath => Path.Combine(AppDataDir, "motocar.db");

        public static void EnsureDatabaseInitialized()
        {
            Directory.CreateDirectory(AppDataDir);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string seedDb = Path.Combine(baseDir, "database", "motocar.db");

            if (!File.Exists(DatabasePath))
            {
                if (File.Exists(seedDb))
                {
                    File.Copy(seedDb, DatabasePath, true);
                    try
                    {
                        File.SetAttributes(DatabasePath, FileAttributes.Normal);
                    }
                    catch { }
                }
            }
            else if (File.Exists(seedDb) && File.GetLastWriteTimeUtc(seedDb) > File.GetLastWriteTimeUtc(DatabasePath))
            {
                // Preserva mapeamentos De/Para customizados antes de atualizar a base seed mais recente
                var customDePara = new List<(string Loja, string Regiao, string Estado)>();
                try
                {
                    using var oldConn = GetConnection();
                    using var cmd = oldConn.CreateCommand();
                    cmd.CommandText = "SELECT loja, regiao, estado FROM de_para_lojas;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        customDePara.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                    }
                }
                catch { }

                try
                {
                    SqliteConnection.ClearAllPools();
                    string walPath = DatabasePath + "-wal";
                    string shmPath = DatabasePath + "-shm";
                    if (File.Exists(walPath)) File.Delete(walPath);
                    if (File.Exists(shmPath)) File.Delete(shmPath);
                    File.Copy(seedDb, DatabasePath, true);
                    File.SetAttributes(DatabasePath, FileAttributes.Normal);

                    if (customDePara.Count > 0)
                    {
                        using var newConn = GetConnection();
                        using var trans = newConn.BeginTransaction();
                        using var cmd = newConn.CreateCommand();
                        cmd.Transaction = trans;
                        cmd.CommandText = "INSERT OR REPLACE INTO de_para_lojas (loja, regiao, estado) VALUES (@l, @r, @e);";
                        var pL = cmd.Parameters.Add("@l", SqliteType.Text);
                        var pR = cmd.Parameters.Add("@r", SqliteType.Text);
                        var pE = cmd.Parameters.Add("@e", SqliteType.Text);
                        foreach (var item in customDePara)
                        {
                            pL.Value = item.Loja;
                            pR.Value = item.Regiao;
                            pE.Value = item.Estado;
                            cmd.ExecuteNonQuery();
                        }
                        trans.Commit();
                    }
                }
                catch { }
            }

            // Re-consolida sempre na inicialização para garantir regras de cruzamento (empresa + NF+pedido) e datas atualizadas
            if (File.Exists(DatabasePath))
            {
                try
                {
                    SyncService.Consolidate();
                }
                catch { }
            }
        }

        public static SqliteConnection GetConnection()
        {
            var conn = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;");
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        public static Dictionary<string, object> GetFiltros()
        {
            var lojas = new List<string>();
            var regioes = new List<string>();
            var estados = new List<string>();
            var tiposVenda = new List<string>();
            string ultimaAtualizacao = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

            using var conn = GetConnection();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT loja_vendedor FROM motos_em_aberto_consolidado WHERE loja_vendedor IS NOT NULL AND loja_vendedor != '' ORDER BY loja_vendedor ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) lojas.Add(reader.GetString(0));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT regiao FROM motos_em_aberto_consolidado WHERE regiao IS NOT NULL AND regiao != '' ORDER BY regiao ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) regioes.Add(reader.GetString(0));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT estado FROM motos_em_aberto_consolidado WHERE estado IS NOT NULL AND estado != '' ORDER BY estado ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) estados.Add(reader.GetString(0));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT descricao_veiculo_tipo_venda FROM motos_em_aberto_consolidado WHERE descricao_veiculo_tipo_venda IS NOT NULL AND descricao_veiculo_tipo_venda != '' ORDER BY descricao_veiculo_tipo_venda ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) tiposVenda.Add(reader.GetString(0));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT strftime('%d/%m/%Y %H:%M', MAX(sincronizado_em)) FROM motos_em_aberto_consolidado;";
                var val = cmd.ExecuteScalar();
                if (val != null && val != DBNull.Value && !string.IsNullOrEmpty(val.ToString()))
                {
                    ultimaAtualizacao = val.ToString()!;
                }
            }

            return new Dictionary<string, object>
            {
                ["sucesso"] = true,
                ["lojas"] = lojas,
                ["regioes"] = regioes,
                ["estados"] = estados,
                ["tipos_venda"] = tiposVenda,
                ["ultima_atualizacao"] = ultimaAtualizacao,
                ["atualizado_em"] = ultimaAtualizacao
            };
        }

        public static Dictionary<string, object> GetDados(
            string? loja, string? regiao, string? estado, string? tipoVenda,
            string? status, string? busca, string? ordem, string? dir)
        {
            var condicoes = new List<string> { "1=1" };
            var parametros = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(loja) && loja != "Todos")
            {
                condicoes.Add("loja_vendedor = @loja");
                parametros.Add(new SqliteParameter("@loja", loja.Trim()));
            }

            if (!string.IsNullOrEmpty(regiao) && regiao != "Todos")
            {
                condicoes.Add("regiao = @regiao");
                parametros.Add(new SqliteParameter("@regiao", regiao.Trim()));
            }

            if (!string.IsNullOrEmpty(estado) && estado != "Todos")
            {
                condicoes.Add("estado = @estado");
                parametros.Add(new SqliteParameter("@estado", estado.Trim()));
            }

            if (!string.IsNullOrEmpty(tipoVenda) && tipoVenda != "Todos")
            {
                condicoes.Add("descricao_veiculo_tipo_venda = @tipoVenda");
                parametros.Add(new SqliteParameter("@tipoVenda", tipoVenda.Trim()));
            }

            if (!string.IsNullOrEmpty(status) && status != "Todos")
            {
                condicoes.Add("status = @status");
                parametros.Add(new SqliteParameter("@status", status.Trim()));
            }

            if (!string.IsNullOrEmpty(busca))
            {
                condicoes.Add("(pessoa LIKE @busca OR vendedor LIKE @busca OR documento LIKE @busca OR nf_pedido LIKE @busca OR loja_vendedor LIKE @busca OR CAST(numeroproposta AS TEXT) LIKE @busca)");
                parametros.Add(new SqliteParameter("@busca", $"%{busca.Trim()}%"));
            }

            string whereClause = string.Join(" AND ", condicoes);

            // Ordenação idêntica ao server.py + suporte a colunas diretas
            string orderBySql = "aberto DESC";
            string ordemClean = (ordem ?? "aberto_desc").Trim().ToLowerInvariant();
            if (ordemClean == "aberto_desc")
                orderBySql = "aberto DESC";
            else if (ordemClean == "aberto_asc")
                orderBySql = "aberto ASC";
            else if (ordemClean == "dias_chao_desc")
                orderBySql = "CASE WHEN dias_chegada IS NULL THEN 1 ELSE 0 END ASC, dias_chegada DESC";
            else if (ordemClean == "dias_chao_asc")
                orderBySql = "CASE WHEN dias_chegada IS NULL THEN 1 ELSE 0 END ASC, dias_chegada ASC";
            else if (ordemClean == "dias_emissao_desc")
                orderBySql = "dias_emissao DESC";
            else if (ordemClean == "dias_emissao_asc")
                orderBySql = "dias_emissao ASC";
            else if (ordemClean == "pessoa_asc")
                orderBySql = "pessoa ASC";
            else if (ordemClean == "pessoa_desc")
                orderBySql = "pessoa DESC";
            else if (ordemClean == "loja_asc")
                orderBySql = "loja_vendedor ASC";
            else if (ordemClean == "loja_desc")
                orderBySql = "loja_vendedor DESC";
            else if (ordemClean == "data_expedicao_desc")
                orderBySql = "CASE WHEN dataexpedicao IS NULL OR dataexpedicao = '' THEN 1 ELSE 0 END ASC, substr(dataexpedicao,7,4)||substr(dataexpedicao,4,2)||substr(dataexpedicao,1,2) DESC";
            else if (ordemClean == "data_expedicao_asc")
                orderBySql = "CASE WHEN dataexpedicao IS NULL OR dataexpedicao = '' THEN 1 ELSE 0 END ASC, substr(dataexpedicao,7,4)||substr(dataexpedicao,4,2)||substr(dataexpedicao,1,2) ASC";
            else
            {
                var mapaColunas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["loja"] = "loja_vendedor",
                    ["vendedor"] = "vendedor",
                    ["pessoa"] = "pessoa",
                    ["dias_no_chao"] = "dias_chegada",
                    ["dia_emissao"] = "dias_emissao",
                    ["valor_em_aberto"] = "aberto",
                    ["valor_recebido"] = "recebido",
                    ["documento"] = "documento",
                    ["data_expedicao"] = "dataexpedicao",
                    ["nf_pedido"] = "nf_pedido",
                    ["status"] = "status",
                    ["regiao"] = "regiao",
                    ["estado"] = "estado"
                };
                if (mapaColunas.TryGetValue(ordemClean, out var col))
                {
                    string direcaoSql = (dir?.ToUpperInvariant() == "ASC") ? "ASC" : "DESC";
                    orderBySql = $"{col} {direcaoSql}";
                }
            }

            using var conn = GetConnection();

            // Totais
            long totalMotos = 0;
            double totalAberto = 0;
            double totalRecebido = 0;
            long totalVerificar = 0;
            string ultimaAtualizacao = "";

            using (var cmdTotais = conn.CreateCommand())
            {
                cmdTotais.CommandText = $@"
                    SELECT 
                        COUNT(*) as total_motos,
                        COALESCE(SUM(aberto), 0) as total_aberto,
                        COALESCE(SUM(recebido), 0) as total_recebido,
                        COALESCE(SUM(CASE WHEN status = 'Verificar' THEN 1 ELSE 0 END), 0) as total_verificar,
                        COALESCE(strftime('%d/%m/%Y %H:%M', MAX(sincronizado_em)), '') as ultima_atualizacao
                    FROM motos_em_aberto_consolidado
                    WHERE {whereClause};
                ";
                foreach (var p in parametros)
                {
                    cmdTotais.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                }

                using var reader = cmdTotais.ExecuteReader();
                if (reader.Read())
                {
                    totalMotos = reader.GetInt64(0);
                    totalAberto = reader.GetDouble(1);
                    totalRecebido = reader.GetDouble(2);
                    totalVerificar = reader.GetInt64(3);
                    ultimaAtualizacao = reader.IsDBNull(4) ? "" : reader.GetString(4);
                }
            }

            // Registros
            var registros = new List<Dictionary<string, object?>>();
            using (var cmdDados = conn.CreateCommand())
            {
                cmdDados.CommandText = $@"
                    SELECT 
                        id,
                        loja_vendedor AS loja,
                        vendedor,
                        pessoa,
                        dias_chegada AS dias_no_chao,
                        dias_emissao AS dia_emissao,
                        aberto AS valor_em_aberto,
                        recebido AS valor_recebido,
                        documento,
                        dataexpedicao AS data_expedicao,
                        nf_pedido,
                        regiao,
                        estado,
                        descricao_veiculo_tipo_venda AS tipo_venda,
                        vencimento,
                        emissao,
                        status
                    FROM motos_em_aberto_consolidado
                    WHERE {whereClause}
                    ORDER BY {orderBySql};
                ";
                foreach (var p in parametros)
                {
                    cmdDados.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                }

                using var reader = cmdDados.ExecuteReader();
                while (reader.Read())
                {
                    registros.Add(new Dictionary<string, object?>
                    {
                        ["id"] = reader.IsDBNull(0) ? 0L : reader.GetInt64(0),
                        ["loja"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ["vendedor"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        ["pessoa"] = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        ["dias_no_chao"] = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                        ["dia_emissao"] = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5),
                        ["valor_em_aberto"] = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6),
                        ["valor_recebido"] = reader.IsDBNull(7) ? 0.0 : reader.GetDouble(7),
                        ["documento"] = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        ["data_expedicao"] = reader.IsDBNull(9) ? "" : reader.GetString(9),
                        ["nf_pedido"] = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        ["regiao"] = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        ["estado"] = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        ["tipo_venda"] = reader.IsDBNull(13) ? "" : reader.GetString(13),
                        ["vencimento"] = reader.IsDBNull(14) ? "" : reader.GetString(14),
                        ["emissao"] = reader.IsDBNull(15) ? "" : reader.GetString(15),
                        ["status"] = reader.IsDBNull(16) ? "" : reader.GetString(16)
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["sucesso"] = true,
                ["total_linhas"] = totalMotos,
                ["totais"] = new Dictionary<string, object>
                {
                    ["total_motos"] = totalMotos,
                    ["total_aberto"] = Math.Round(totalAberto, 2),
                    ["total_recebido"] = Math.Round(totalRecebido, 2),
                    ["total_verificar"] = totalVerificar,
                    ["ultima_atualizacao"] = ultimaAtualizacao,
                    // Chaves legadas para retrocompatibilidade
                    ["linhas"] = totalMotos,
                    ["aberto"] = Math.Round(totalAberto, 2),
                    ["recebido"] = Math.Round(totalRecebido, 2),
                    ["a_verificar"] = totalVerificar
                },
                ["linhas"] = registros,
                ["dados"] = registros
            };
        }

        public static Dictionary<string, object> GetDePara()
        {
            var lojas = new List<Dictionary<string, object>>();
            var regioes = new List<Dictionary<string, object>>();
            var pendentes = new List<Dictionary<string, object>>();

            using var conn = GetConnection();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT 
                        l.loja,
                        l.regiao,
                        l.estado,
                        (SELECT count(*) FROM motos_em_aberto_consolidado c WHERE UPPER(c.loja_vendedor) = UPPER(l.loja)) AS total_motos
                    FROM de_para_lojas l
                    ORDER BY l.loja ASC;
                ";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    lojas.Add(new Dictionary<string, object>
                    {
                        ["loja"] = reader.GetString(0),
                        ["regiao"] = reader.GetString(1),
                        ["estado"] = reader.GetString(2),
                        ["total_motos"] = reader.GetInt64(3)
                    });
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT regiao, estado FROM de_para_regioes ORDER BY regiao ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    regioes.Add(new Dictionary<string, object>
                    {
                        ["regiao"] = reader.GetString(0),
                        ["estado"] = reader.GetString(1)
                    });
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT 
                        loja_vendedor as loja,
                        empresa,
                        vendedor as exemplo_vendedor,
                        COUNT(*) as total_motos,
                        ROUND(COALESCE(SUM(aberto), 0), 2) as total_aberto
                    FROM motos_em_aberto_consolidado
                    WHERE regiao = 'PENDENTE DE/PARA'
                    GROUP BY loja_vendedor
                    ORDER BY total_motos DESC;
                ";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string exemploVendedor = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    pendentes.Add(new Dictionary<string, object>
                    {
                        ["loja"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ["empresa"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ["exemplo_vendedor"] = exemploVendedor,
                        ["vendedor"] = exemploVendedor,
                        ["total_motos"] = reader.GetInt64(3),
                        ["total_aberto"] = reader.GetDouble(4)
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["sucesso"] = true,
                ["total_lojas"] = lojas.Count,
                ["total_pendentes"] = pendentes.Count,
                ["lojas"] = lojas,
                ["regioes"] = regioes,
                ["pendentes"] = pendentes
            };
        }

        public static void SaveDePara(string loja, string regiao, string estado)
        {
            loja = loja.Trim().ToUpperInvariant();
            regiao = regiao.Trim();
            estado = estado.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(loja) || string.IsNullOrEmpty(regiao) || string.IsNullOrEmpty(estado))
            {
                throw new ArgumentException("Loja, Região e Estado são obrigatórios.");
            }

            using var conn = GetConnection();
            using var trans = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    INSERT INTO de_para_lojas (loja, regiao, estado)
                    VALUES (@loja, @regiao, @estado)
                    ON CONFLICT(loja) DO UPDATE SET regiao = @regiao, estado = @estado;
                ";
                cmd.Parameters.AddWithValue("@loja", loja);
                cmd.Parameters.AddWithValue("@regiao", regiao);
                cmd.Parameters.AddWithValue("@estado", estado);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    UPDATE motos_em_aberto_consolidado
                    SET regiao = @regiao, estado = @estado
                    WHERE UPPER(loja_vendedor) = @loja;
                ";
                cmd.Parameters.AddWithValue("@loja", loja);
                cmd.Parameters.AddWithValue("@regiao", regiao);
                cmd.Parameters.AddWithValue("@estado", estado);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }

        public static void DeleteDePara(string loja)
        {
            loja = loja.Trim().ToUpperInvariant();

            using var conn = GetConnection();
            using var trans = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = "DELETE FROM de_para_lojas WHERE UPPER(loja) = @loja;";
                cmd.Parameters.AddWithValue("@loja", loja);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    UPDATE motos_em_aberto_consolidado
                    SET regiao = 'PENDENTE DE/PARA', estado = 'PENDENTE'
                    WHERE UPPER(loja_vendedor) = @loja;
                ";
                cmd.Parameters.AddWithValue("@loja", loja);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
    }
}
