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

            if (!File.Exists(DatabasePath))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string seedDb = Path.Combine(baseDir, "database", "motocar.db");
                if (File.Exists(seedDb))
                {
                    File.Copy(seedDb, DatabasePath, true);
                }
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
            string atualizadoEm = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

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
                cmd.CommandText = "SELECT DISTINCT tipo_venda FROM motos_em_aberto_consolidado WHERE tipo_venda IS NOT NULL AND tipo_venda != '' ORDER BY tipo_venda ASC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) tiposVenda.Add(reader.GetString(0));
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT max(sincronizado_em) FROM motos_em_aberto_financeiro;";
                var val = cmd.ExecuteScalar();
                if (val != null && val != DBNull.Value && !string.IsNullOrEmpty(val.ToString()))
                {
                    if (DateTime.TryParse(val.ToString(), out var dt))
                    {
                        atualizadoEm = dt.ToString("dd/MM/yyyy HH:mm");
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["sucesso"] = true,
                ["lojas"] = lojas,
                ["regioes"] = regioes,
                ["estados"] = estados,
                ["tipos_venda"] = tiposVenda,
                ["atualizado_em"] = atualizadoEm
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
                parametros.Add(new SqliteParameter("@loja", loja));
            }

            if (!string.IsNullOrEmpty(regiao) && regiao != "Todos")
            {
                condicoes.Add("regiao = @regiao");
                parametros.Add(new SqliteParameter("@regiao", regiao));
            }

            if (!string.IsNullOrEmpty(estado) && estado != "Todos")
            {
                condicoes.Add("estado = @estado");
                parametros.Add(new SqliteParameter("@estado", estado));
            }

            if (!string.IsNullOrEmpty(tipoVenda) && tipoVenda != "Todos")
            {
                condicoes.Add("tipo_venda = @tipoVenda");
                parametros.Add(new SqliteParameter("@tipoVenda", tipoVenda));
            }

            if (status == "Verificar")
            {
                condicoes.Add("status = 'Verificar'");
            }
            else if (status == "Não Verificar")
            {
                condicoes.Add("status = 'Não Verificar'");
            }

            if (!string.IsNullOrEmpty(busca))
            {
                condicoes.Add("(pessoa LIKE @busca OR vendedor LIKE @busca OR documento LIKE @busca OR nf_pedido LIKE @busca OR loja_vendedor LIKE @busca)");
                parametros.Add(new SqliteParameter("@busca", $"%{busca}%"));
            }

            string whereClause = string.Join(" AND ", condicoes);

            // Validação de ordenação
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
                ["data_expedicao"] = "data_expedicao",
                ["nf_pedido"] = "nf_pedido",
                ["status"] = "status",
                ["regiao"] = "regiao",
                ["estado"] = "estado"
            };

            string colunaSql = "dias_emissao";
            if (!string.IsNullOrEmpty(ordem) && mapaColunas.TryGetValue(ordem, out var col))
            {
                colunaSql = col;
            }
            string direcaoSql = (dir?.ToUpperInvariant() == "ASC") ? "ASC" : "DESC";

            using var conn = GetConnection();

            // Totais
            long totalLinhas = 0;
            double totalAberto = 0;
            double totalRecebido = 0;
            long aVerificar = 0;

            using (var cmdTotais = conn.CreateCommand())
            {
                cmdTotais.CommandText = $@"
                    SELECT 
                        count(*),
                        coalesce(sum(aberto), 0),
                        coalesce(sum(recebido), 0),
                        count(case when status = 'Verificar' then 1 end)
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
                    totalLinhas = reader.GetInt64(0);
                    totalAberto = reader.GetDouble(1);
                    totalRecebido = reader.GetDouble(2);
                    aVerificar = reader.GetInt64(3);
                }
            }

            // Registros
            var registros = new List<Dictionary<string, object?>>();
            using (var cmdDados = conn.CreateCommand())
            {
                cmdDados.CommandText = $@"
                    SELECT 
                        loja_vendedor AS loja,
                        vendedor,
                        pessoa,
                        dias_chegada AS dias_no_chao,
                        dias_emissao AS dia_emissao,
                        aberto AS valor_em_aberto,
                        recebido AS valor_recebido,
                        documento,
                        data_expedicao,
                        nf_pedido,
                        status,
                        regiao,
                        estado,
                        tipo_venda
                    FROM motos_em_aberto_consolidado
                    WHERE {whereClause}
                    ORDER BY {colunaSql} {direcaoSql};
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
                        ["loja"] = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        ["vendedor"] = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        ["pessoa"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        ["dias_no_chao"] = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                        ["dia_emissao"] = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                        ["valor_em_aberto"] = reader.IsDBNull(5) ? 0.0 : reader.GetDouble(5),
                        ["valor_recebido"] = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6),
                        ["documento"] = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        ["data_expedicao"] = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        ["nf_pedido"] = reader.IsDBNull(9) ? "" : reader.GetString(9),
                        ["status"] = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        ["regiao"] = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        ["estado"] = reader.IsDBNull(12) ? "" : reader.GetString(12),
                        ["tipo_venda"] = reader.IsDBNull(13) ? "" : reader.GetString(13)
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["sucesso"] = true,
                ["total_linhas"] = totalLinhas,
                ["totais"] = new Dictionary<string, object>
                {
                    ["linhas"] = totalLinhas,
                    ["aberto"] = Math.Round(totalAberto, 2),
                    ["recebido"] = Math.Round(totalRecebido, 2),
                    ["a_verificar"] = aVerificar
                },
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
                        (SELECT count(*) FROM motos_em_aberto_consolidado c WHERE c.loja_vendedor = l.loja) AS total_motos
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
                cmd.CommandText = "SELECT loja_identificada, total_motos, exemplo_vendedor FROM v_lojas_pendentes_depara;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    pendentes.Add(new Dictionary<string, object>
                    {
                        ["loja"] = reader.GetString(0),
                        ["total_motos"] = reader.GetInt64(1),
                        ["exemplo_vendedor"] = reader.IsDBNull(2) ? "" : reader.GetString(2)
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
                    WHERE loja_vendedor = @loja;
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
                cmd.CommandText = "DELETE FROM de_para_lojas WHERE loja = @loja;";
                cmd.Parameters.AddWithValue("@loja", loja);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = @"
                    UPDATE motos_em_aberto_consolidado
                    SET regiao = 'PENDENTE DE/PARA', estado = '??'
                    WHERE loja_vendedor = @loja;
                ";
                cmd.Parameters.AddWithValue("@loja", loja);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
    }
}
