#!/usr/bin/env python3
"""
Suíte Completa de Testes Unitários e de Integração — Motos em Aberto (Motocar)
Valida:
1. Geração de payloads dinâmicos de 90 dias (3 meses) e sanitização de JSON.
2. Comunicação real com a API da MicroWork Cloud (Financeiro e Base).
3. Alimentação, limpeza atômica (truncate/reload) e estrutura WAL do banco SQLite.
4. Regras de negócio:
   - Extração da sigla da loja pelo vendedor (Padrão 'SIGLA - NOME').
   - Mapeamento De/Para (Região e Estado/UF) e flag 'PENDENTE DE/PARA'.
   - Cálculo de Dias no Chão (dias_chegada) e Dia Emissão (dias_emissao).
   - Concatenação de NF+pedido (documento/proposta) e limpeza de ' - ' no Documento.
   - Higienização de datas inválidas '31/12/1899' na expedição.
   - Regra de Status: 'Verificar' se e somente se dias_emissao >= 10 E dias_chegada >= 10.
5. Endpoints HTTP REST e contrato JSON com o frontend (web/index.html).
"""

import os
import sys
import json
import sqlite3
import tempfile
import threading
import unittest
import urllib.request
import urllib.parse
from datetime import datetime, timedelta
from http.server import HTTPServer

BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
sys.path.insert(0, BASE_DIR)

from scripts.sync_database import (
    clean_json_newlines,
    get_token,
    get_payload,
    fetch_api,
    init_db,
    sync_de_para,
    populate_financeiro,
    populate_base,
    populate_consolidado,
    DB_PATH
)
from server import MotocarHandler, parse_semver


class TestMotosEmAbertoSystem(unittest.TestCase):

    # =========================================================================
    # 1. TESTES DE PAYLOAD, CREDENCIAL E CONEXÃO COM A API MICROWORK
    # =========================================================================
    def test_01_credencial_e_sanitizacao_json(self):
        """Verifica se o token existe e se quebras de linha dentro de strings JSON são limpas."""
        token = get_token()
        self.assertTrue(len(token) > 20, "O token da API deve existir e não estar vazio.")

        raw = '{"chave": "linha1\nlinha2", "numero": 10}'
        cleaned = clean_json_newlines(raw)
        parsed = json.loads(cleaned)
        self.assertEqual(parsed["chave"], "linha1 linha2")
        self.assertEqual(parsed["numero"], 10)

    def test_02_janela_dinamica_90_dias_payload(self):
        """Verifica se a regra de puxar sempre de hoje até 3 meses (90 dias) atrás está sendo injetada."""
        hoje = datetime.now()
        dt_ini = (hoje - timedelta(days=90)).strftime("%Y-%m-%d")
        dt_fim = hoje.strftime("%Y-%m-%d")

        payload_fin = get_payload('motos_em_aberto_financeiro', days_back=90)
        filtros_fin = payload_fin.get('filtros', '')
        self.assertIn(f"Datademovimentacaoinicial={dt_ini};", filtros_fin)
        self.assertIn(f"Datademovimentacaofinal={dt_fim};", filtros_fin)

        payload_base = get_payload('motos_em_aberto_base', days_back=90)
        filtros_base = payload_base.get('filtros', '')
        self.assertIn(f"Periododamovimentacaoinicial={dt_ini} 00:00;", filtros_base)
        self.assertIn(f"Periododamovimentacaofinal={dt_fim} 23:59;", filtros_base)

    def test_03_api_microwork_financeiro_ao_vivo(self):
        """Verifica em tempo real se a API Financeiro na MicroWork responde e retorna os campos obrigatórios."""
        token = get_token()
        payload = get_payload('motos_em_aberto_financeiro', days_back=90)
        records = fetch_api(payload, token)

        self.assertIsInstance(records, list)
        self.assertGreater(len(records), 100, "A API Financeiro deve retornar registros de motos em aberto.")

        sample = records[0]
        campos_esperados = [
            'empresa', 'emissao', 'vencimento', 'pessoa',
            'recebido', 'aberto', 'vendedor', 'documentoparcela',
            'numeroproposta', 'descricao_veiculo_tipo_venda'
        ]
        for campo in campos_esperados:
            self.assertIn(campo, sample, f"Campo '{campo}' ausente no retorno da API Financeiro.")

    # =========================================================================
    # 2. TESTES DE RECARGA ATÔMICA E REGRAS DE NEGÓCIO EM BANCO ISOLADO
    # =========================================================================
    def test_04_recarga_atomica_truncamento_e_regras_de_negocio(self):
        """Testa se a tabela anterior é apagada antes da recarga e se todas as regras são aplicadas."""
        with tempfile.TemporaryDirectory() as tmpdir:
            temp_db = os.path.join(tmpdir, "test_motocar.db")
            conn = sqlite3.connect(temp_db)
            try:
                init_db(conn)
                sync_de_para(conn)

                hoje = datetime.now()
                dt_15_dias_atras = (hoje - timedelta(days=15)).strftime("%Y-%m-%d")
                dt_5_dias_atras = (hoje - timedelta(days=5)).strftime("%Y-%m-%d")

                # Primeira carga simulada (deve ser apagada pela segunda carga!)
                lixo_inicial = [{
                    'empresa': 'EMPRESA ANTIGA',
                    'emissao': '2025-01-01',
                    'vencimento': '2025-02-01',
                    'dataemissao': '2025-01-01',
                    'especiedodocumento': 'NFE',
                    'pessoa': 'CLIENTE ANTIGO QUE DEVE SER APAGADO',
                    'recebido': 0.0,
                    'aberto': 99999.0,
                    'vendedor': 'SNS - VENDEDOR ANTIGO',
                    'documentoparcela': '11111/1',
                    'numeroproposta': 11111,
                    'descricao_veiculo_tipo_venda': 'VENDA'
                }]
                populate_financeiro(conn, lixo_inicial)
                populate_base(conn, [])
                populate_consolidado(conn)

                cur = conn.cursor()
                cur.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado WHERE pessoa = 'CLIENTE ANTIGO QUE DEVE SER APAGADO';")
                self.assertEqual(cur.fetchone()[0], 1)

                novos_fin = [
                    {
                        'empresa': 'MOTOCAR',
                        'emissao': f'{dt_15_dias_atras}T00:00:00',
                        'vencimento': f'{dt_5_dias_atras}T00:00:00',
                        'dataemissao': dt_15_dias_atras,
                        'especiedodocumento': 'NFE',
                        'pessoa': 'CLIENTE VERIFICAR 1',
                        'recebido': 500.0,
                        'aberto': 15000.0,
                        'vendedor': 'SNS - JOAO DA SILVA',
                        'documentoparcela': '144678/1 - NFE',
                        'numeroproposta': 116587,
                        'descricao_veiculo_tipo_venda': 'VENDA DIRETA'
                    },
                    {
                        'empresa': 'MOTOCAR',
                        'emissao': f'{dt_15_dias_atras}T00:00:00',
                        'vencimento': f'{dt_5_dias_atras}T00:00:00',
                        'dataemissao': dt_15_dias_atras,
                        'especiedodocumento': 'NFE',
                        'pessoa': 'CLIENTE NAO VERIFICAR 2',
                        'recebido': 0.0,
                        'aberto': 20000.0,
                        'vendedor': 'ALC - MARIA SOUZA',
                        'documentoparcela': '222333/1',
                        'numeroproposta': 200002,
                        'descricao_veiculo_tipo_venda': 'FINANCIAMENTO'
                    },
                    {
                        'empresa': 'MOTOCAR',
                        'emissao': f'{dt_15_dias_atras}T00:00:00',
                        'vencimento': f'{dt_5_dias_atras}T00:00:00',
                        'dataemissao': dt_15_dias_atras,
                        'especiedodocumento': 'NFE',
                        'pessoa': 'CLIENTE LOJA PENDENTE 3',
                        'recebido': 0.0,
                        'aberto': 18000.0,
                        'vendedor': 'LOJANOVA99 - CARLOS TESTE',
                        'documentoparcela': '333444/1',
                        'numeroproposta': 300003,
                        'descricao_veiculo_tipo_venda': 'CONSORCIO'
                    },
                    {
                        'empresa': 'MOTOCAR - MGA',
                        'emissao': f'{dt_5_dias_atras}T00:00:00',
                        'vencimento': f'{dt_5_dias_atras}T00:00:00',
                        'dataemissao': dt_5_dias_atras,
                        'especiedodocumento': 'NFE',
                        'pessoa': 'CLIENTE SEM HIFEN NO VENDEDOR 4',
                        'recebido': 1000.0,
                        'aberto': 12000.0,
                        'vendedor': 'VENDEDOR DIRETO SEM SIGLA',
                        'documentoparcela': '5104/1',
                        'numeroproposta': 11058,
                        'descricao_veiculo_tipo_venda': 'VENDA DIRETA'
                    }
                ]

                novos_base = [
                    {
                        'chassi': '9C2KC0001',
                        'pessoa': 'CLIENTE VERIFICAR 1',
                        'vendedor': 'SNS - JOAO DA SILVA',
                        'datachegada': f'{dt_15_dias_atras}T08:00:00',
                        'docfiscal': '144678',
                        'proposta': 116587,
                        'quantidade': 1,
                        'empresa': 'MOTOCAR',
                        'datahoraexpedicao': '2026-11-03T10:30:00'
                    },
                    {
                        'chassi': '9C2KC0002',
                        'pessoa': 'CLIENTE NAO VERIFICAR 2',
                        'vendedor': 'ALC - MARIA SOUZA',
                        'datachegada': f'{dt_5_dias_atras}T08:00:00',
                        'docfiscal': '222333',
                        'proposta': 200002,
                        'quantidade': 1,
                        'empresa': 'MOTOCAR',
                        'datahoraexpedicao': '1899-12-31T00:00:00'
                    },
                    {
                        # Mesma proposta (200002) em OUTRA filial / outra NF: NÃO pode vazar data de expedição!
                        'chassi': '9C2KC9999',
                        'pessoa': 'CLIENTE OUTRA FILIAL MESMA PROPOSTA',
                        'vendedor': 'MGA - OUTRO VENDEDOR',
                        'datachegada': f'{dt_15_dias_atras}T08:00:00',
                        'docfiscal': 'NF-E 999999/1',
                        'proposta': 200002,
                        'quantidade': 1,
                        'empresa': 'PR-MGA',
                        'datahoraexpedicao': '2026-11-05T14:20:00'
                    }
                ]

                populate_financeiro(conn, novos_fin)
                populate_base(conn, novos_base)
                populate_consolidado(conn)

                conn.row_factory = sqlite3.Row
                cur = conn.cursor()

                # 1. Garante que a tabela anterior foi apagada
                cur.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado WHERE pessoa = 'CLIENTE ANTIGO QUE DEVE SER APAGADO';")
                self.assertEqual(cur.fetchone()[0], 0, "A carga anterior deve ser integralmente apagada antes da nova!")

                cur.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado;")
                self.assertEqual(cur.fetchone()[0], 4, "Devem existir exatamente os 4 novos registros.")

                # 2. Valida Caso 1 (SNS -> Verificar, documento limpo, NF+pedido concatenado)
                cur.execute("SELECT * FROM motos_em_aberto_consolidado WHERE numeroproposta = 116587;")
                r1 = dict(cur.fetchone())
                self.assertEqual(r1['loja_vendedor'], 'SNS')
                self.assertEqual(r1['estado'], 'RJ')
                self.assertEqual(r1['dias_emissao'], 15)
                self.assertEqual(r1['dias_chegada'], 15)
                self.assertEqual(r1['documento'], '144678/1')
                self.assertEqual(r1['nf_pedido'], '144678/116587')
                self.assertEqual(r1['dataexpedicao'], '03/11/2026')
                self.assertEqual(r1['status'], 'Verificar')

                # 3. Valida Caso 2 (ALC -> 15 dias emissão, mas apenas 5 dias chegada -> 'Não Verificar', data 1899 -> None)
                cur.execute("SELECT * FROM motos_em_aberto_consolidado WHERE numeroproposta = 200002;")
                r2 = dict(cur.fetchone())
                self.assertEqual(r2['loja_vendedor'], 'ALC')
                self.assertEqual(r2['dias_emissao'], 15)
                self.assertEqual(r2['dias_chegada'], 5)
                self.assertIsNone(r2['dataexpedicao'], "Datas 1899 devem ser convertidas para NULL.")
                self.assertEqual(r2['status'], 'Não Verificar')

                # 4. Valida Caso 3 (Loja não mapeada -> PENDENTE DE/PARA)
                cur.execute("SELECT * FROM motos_em_aberto_consolidado WHERE numeroproposta = 300003;")
                r3 = dict(cur.fetchone())
                self.assertEqual(r3['loja_vendedor'], 'LOJANOVA99')
                self.assertEqual(r3['regiao'], 'PENDENTE DE/PARA')
                self.assertEqual(r3['estado'], 'PENDENTE')
                self.assertIsNone(r3['dias_chegada'])
                self.assertEqual(r3['status'], 'Não Verificar')

                # 5. Valida Caso 4 (Fallback para sigla na coluna empresa quando vendedor não tem '-')
                cur.execute("SELECT * FROM motos_em_aberto_consolidado WHERE numeroproposta = 11058;")
                r4 = dict(cur.fetchone())
                self.assertEqual(r4['loja_vendedor'], 'MGA')
                self.assertEqual(r4['estado'], 'PR')
                self.assertEqual(r4['nf_pedido'], '5104/11058')
            finally:
                conn.close()

    # =========================================================================
    # 3. AUDITORIA DO BANCO DE PRODUÇÃO (database/motocar.db)
    # =========================================================================
    def test_05_auditoria_banco_producao(self):
        """Audita 100% dos registros atuais em database/motocar.db."""
        self.assertTrue(os.path.exists(DB_PATH), "O banco database/motocar.db deve existir.")
        conn = sqlite3.connect(DB_PATH)
        cur = conn.cursor()

        # Reexecuta populate_consolidado para garantir que a higienização mais recente está aplicada
        populate_consolidado(conn)

        cur.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado;")
        total = cur.fetchone()[0]
        self.assertGreater(total, 1000, "O banco de produção deve conter os registros reais consolidados.")

        # Verifica se existe alguma linha violando a regra de 'Verificar'
        cur.execute("""
            SELECT COUNT(*) FROM motos_em_aberto_consolidado
            WHERE (dias_emissao >= 10 AND dias_chegada IS NOT NULL AND dias_chegada >= 10 AND status != 'Verificar')
               OR ((dias_emissao < 10 OR dias_chegada IS NULL OR dias_chegada < 10) AND status != 'Não Verificar');
        """)
        violacoes_status = cur.fetchone()[0]
        self.assertEqual(violacoes_status, 0, "Nenhuma linha pode violar a regra de validação 'Verificar'.")

        # Verifica se existe alguma data 31/12/1899
        cur.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado WHERE dataexpedicao = '31/12/1899';")
        datas_1899 = cur.fetchone()[0]
        self.assertEqual(datas_1899, 0, "Não deve existir nenhuma data 31/12/1899 na tabela consolidada.")

        # Verifica De/Para carregado
        cur.execute("SELECT COUNT(*) FROM de_para_lojas;")
        total_lojas = cur.fetchone()[0]
        self.assertGreaterEqual(total_lojas, 100, "Deve haver pelo menos 100 lojas mapeadas no De/Para.")

        conn.close()

    # =========================================================================
    # 4. TESTES DE INTEGRAÇÃO DOS ENDPOINTS HTTP REST (server.py)
    # =========================================================================
    def test_06_servidor_http_endpoints_e_crud_depara(self):
        """Inicia o servidor HTTP em uma porta efêmera e testa todas as rotas consumidas pelo frontend."""
        httpd = HTTPServer(('127.0.0.1', 0), MotocarHandler)
        port = httpd.server_port
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()

        base_url = f"http://127.0.0.1:{port}"
        try:
            # 1. GET /
            with urllib.request.urlopen(f"{base_url}/") as resp:
                self.assertEqual(resp.status, 200)
                html = resp.read().decode('utf-8')
                self.assertIn("Motos em Aberto", html)

            # 2. GET /api/filtros
            with urllib.request.urlopen(f"{base_url}/api/filtros") as resp:
                self.assertEqual(resp.status, 200)
                filtros = json.loads(resp.read().decode('utf-8'))
                self.assertIn("lojas", filtros)
                self.assertIn("regioes", filtros)
                self.assertIn("estados", filtros)
                self.assertIn("tipos_venda", filtros)
                self.assertIn("ultima_atualizacao", filtros)
                self.assertTrue(len(filtros["lojas"]) > 0)

            # 3. GET /api/dados (com filtro status=Verificar e ordenação dias_chao_desc)
            with urllib.request.urlopen(f"{base_url}/api/dados?status=Verificar&ordem=dias_chao_desc") as resp:
                self.assertEqual(resp.status, 200)
                dados = json.loads(resp.read().decode('utf-8'))
                self.assertIn("totais", dados)
                self.assertIn("linhas", dados)
                self.assertGreater(len(dados["linhas"]), 0)
                for row in dados["linhas"][:20]:
                    self.assertEqual(row["status"], "Verificar")
                    self.assertGreaterEqual(row["dia_emissao"], 10)
                    self.assertGreaterEqual(row["dias_no_chao"], 10)

            # 4. POST, GET e DELETE /api/depara (Teste de CRUD sem deixar sujeira)
            req_post = urllib.request.Request(
                f"{base_url}/api/depara",
                data=json.dumps({"loja": "TSTPY", "regiao": "GRANDE RIO - RJ", "estado": "RJ"}).encode('utf-8'),
                headers={'Content-Type': 'application/json'},
                method='POST'
            )
            with urllib.request.urlopen(req_post) as resp:
                self.assertEqual(resp.status, 200)
                res_post = json.loads(resp.read().decode('utf-8'))
                self.assertTrue(res_post.get("sucesso"))

            with urllib.request.urlopen(f"{base_url}/api/depara") as resp:
                depara = json.loads(resp.read().decode('utf-8'))
                siglas = [x["loja"] for x in depara["lojas"]]
                self.assertIn("TSTPY", siglas)

            req_del = urllib.request.Request(
                f"{base_url}/api/depara?loja=TSTPY",
                method='DELETE'
            )
            with urllib.request.urlopen(req_del) as resp:
                self.assertEqual(resp.status, 200)
                res_del = json.loads(resp.read().decode('utf-8'))
                self.assertTrue(res_del.get("sucesso"))

            # 5. GET /api/versao
            with urllib.request.urlopen(f"{base_url}/api/versao") as resp:
                self.assertEqual(resp.status, 200)
                versao = json.loads(resp.read().decode('utf-8'))
                self.assertIn("versao_atual", versao)
                self.assertIn("tem_atualizacao", versao)

            # 6. Comparador de SemVer
            self.assertGreater(parse_semver("v1.0.1"), parse_semver("1.0.0"))
            self.assertEqual(parse_semver("v1.0.0"), parse_semver("1.0.0"))

        finally:
            httpd.shutdown()
            httpd.server_close()


if __name__ == '__main__':
    unittest.main(verbosity=2)

