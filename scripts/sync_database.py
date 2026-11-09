#!/usr/bin/env python3
"""
Sincronizador e Inicializador do Banco de Dados SQLite (Motocar)
Cria as tabelas motos_em_aberto_financeiro e motos_em_aberto_base e insere os registros das APIs.
"""

import os
import json
import sqlite3
import urllib.request
import urllib.error
import zipfile
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta

DB_PATH = os.path.join(os.path.dirname(__file__), '..', 'database', 'motocar.db')
API_URL = 'https://microworkcloud.com.br/api/integracao/terceiro'
USER_AGENT = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'

def clean_json_newlines(s: str) -> str:
    """Limpa quebras de linha literais dentro de strings JSON."""
    in_quote = False
    res = []
    for c in s:
        if c == '"':
            in_quote = not in_quote
            res.append(c)
        elif c == '\n' and in_quote:
            res.append(' ')
        else:
            res.append(c)
    return ''.join(res)

def get_token() -> str:
    cred_path = os.path.join(os.path.dirname(__file__), '..', 'API', 'credencial')
    with open(cred_path, 'r', encoding='utf-8') as f:
        return f.read().strip()

def get_payload(file_name: str, days_back: int = 90) -> dict:
    """
    Carrega o payload base e injeta dinamicamente o intervalo de datas:
    Hoje até 'days_back' dias para trás (padrão: 90 dias / 3 meses).
    """
    file_path = os.path.join(os.path.dirname(__file__), '..', 'API', file_name)
    with open(file_path, 'r', encoding='utf-8') as f:
        text = f.read()
    json_str = text[text.find('{'):text.rfind('}') + 1]
    cleaned = clean_json_newlines(json_str)
    payload = json.loads(cleaned)

    # Cálculo dinâmico das datas
    hoje = datetime.now()
    data_ini = hoje - timedelta(days=days_back)
    dt_fim_str = hoje.strftime("%Y-%m-%d")
    dt_ini_str = data_ini.strftime("%Y-%m-%d")

    filtros = payload.get('filtros', '')

    # 1. Ajuste para o endpoint Financeiro
    filtros = re.sub(r'Datademovimentacaoinicial=[^;]+;', f'Datademovimentacaoinicial={dt_ini_str};', filtros)
    filtros = re.sub(r'Datademovimentacaofinal=[^;]+;', f'Datademovimentacaofinal={dt_fim_str};', filtros)

    # 2. Ajuste para o endpoint de Veículos/Base
    filtros = re.sub(r'Periododamovimentacaoinicial=[^;]+;', f'Periododamovimentacaoinicial={dt_ini_str} 00:00;', filtros)
    filtros = re.sub(r'Periododamovimentacaofinal=[^;]+;', f'Periododamovimentacaofinal={dt_fim_str} 23:59;', filtros)

    payload['filtros'] = filtros
    print(f"📅 Janela de busca configurada para {file_name}: {dt_ini_str} até {dt_fim_str} ({days_back} dias / 3 meses)")
    return payload

def fetch_api(payload: dict, token: str) -> list:
    req = urllib.request.Request(
        API_URL,
        data=json.dumps(payload).encode('utf-8'),
        headers={
            'Content-Type': 'application/json',
            'Authorization': f'Bearer {token}',
            'User-Agent': USER_AGENT
        }
    )
    with urllib.request.urlopen(req, timeout=120) as resp:
        data = resp.read().decode('utf-8')
        return json.loads(data)

def init_db(conn: sqlite3.Connection):
    cursor = conn.cursor()
    # Ativa o modo WAL (Write-Ahead Logging) para resiliência e alta concorrência
    cursor.execute("PRAGMA journal_mode=WAL;")
    cursor.execute("PRAGMA synchronous=NORMAL;")
    
    # 1. Tabela Financeiro
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS motos_em_aberto_financeiro (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            empresa TEXT,
            emissao TEXT,
            vencimento TEXT,
            dataemissao TEXT,
            especiedodocumento TEXT,
            pessoa TEXT,
            recebido REAL,
            aberto REAL,
            vendedor TEXT,
            documentoparcela TEXT,
            numeroproposta INTEGER,
            descricao_veiculo_tipo_venda TEXT,
            sincronizado_em TEXT DEFAULT (datetime('now', 'localtime'))
        );
    """)
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_fin_proposta ON motos_em_aberto_financeiro (numeroproposta);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_fin_empresa ON motos_em_aberto_financeiro (empresa);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_fin_vencimento ON motos_em_aberto_financeiro (vencimento);")

    # 2. Tabela Base Veículo
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS motos_em_aberto_base (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            chassi TEXT,
            pessoa TEXT,
            vendedor TEXT,
            datachegada TEXT,
            docfiscal TEXT,
            proposta INTEGER,
            quantidade REAL,
            empresa TEXT,
            datahoraexpedicao TEXT,
            sincronizado_em TEXT DEFAULT (datetime('now', 'localtime'))
        );
    """)
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_base_proposta ON motos_em_aberto_base (proposta);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_base_chassi ON motos_em_aberto_base (chassi);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_base_empresa ON motos_em_aberto_base (empresa);")

    # 3. Tabela Consolidada (Cruzamento Financeiro + Veículo + Região + Estado + Esboço Power BI)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS motos_em_aberto_consolidado (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            empresa TEXT,
            loja_vendedor TEXT,
            regiao TEXT,
            estado TEXT,
            nome_vendedor TEXT,
            vendedor TEXT,
            pessoa TEXT,
            dias_chegada INTEGER,          -- 'Dias no chão'
            dias_emissao INTEGER,          -- 'Dia emissão'
            aberto REAL,                   -- 'Valor em Aberto'
            recebido REAL,                 -- 'Valor Recebido'
            documento TEXT,                -- 'Documento' (ex: 144678/1)
            dataexpedicao TEXT,            -- 'Data Expedição' (DD/MM/AAAA)
            nf_pedido TEXT,                -- 'NF+pedido' (ex: 144678/116587)
            documentoparcela TEXT,
            numeroproposta INTEGER,
            descricao_veiculo_tipo_venda TEXT,
            vencimento TEXT,
            emissao TEXT,
            info_dias_emissao TEXT,
            datachegada TEXT,
            info_dias_chegada TEXT,
            status TEXT,                   -- 'Verificar' / 'Não Verificar'
            especiedodocumento TEXT,
            sincronizado_em TEXT DEFAULT (datetime('now', 'localtime'))
        );
    """)
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_cons_regiao ON motos_em_aberto_consolidado (regiao);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_cons_estado ON motos_em_aberto_consolidado (estado);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_cons_loja ON motos_em_aberto_consolidado (loja_vendedor);")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_cons_status ON motos_em_aberto_consolidado (status);")

    # 4. Tabelas de De-Para (Lojas e Regiões)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS de_para_lojas (
            loja TEXT PRIMARY KEY,
            regiao TEXT,
            estado TEXT
        );
    """)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS de_para_regioes (
            regiao TEXT PRIMARY KEY,
            estado TEXT
        );
    """)

    # 5. View de Lojas Pendentes de Mapeamento no De-Para
    cursor.execute("""
        CREATE VIEW IF NOT EXISTS v_lojas_pendentes_depara AS
        SELECT 
            loja_vendedor as loja_nao_mapeada,
            empresa as filial_origem,
            vendedor as exemplo_vendedor,
            COUNT(*) as total_motos_afetadas,
            ROUND(SUM(aberto), 2) as valor_total_afetado
        FROM motos_em_aberto_consolidado
        WHERE regiao = 'PENDENTE DE/PARA'
        GROUP BY loja_vendedor
        ORDER BY total_motos_afetadas DESC;
    """)

    # Garante a exclusão da antiga tabela avulsa de validação se ainda existir
    cursor.execute("DROP TABLE IF EXISTS motos_em_aberto_validacao;")

    conn.commit()

def populate_financeiro(conn: sqlite3.Connection, records: list):
    cursor = conn.cursor()
    # Limpa antes de popular ou atualiza
    cursor.execute("DELETE FROM motos_em_aberto_financeiro;")
    
    insert_sql = """
        INSERT INTO motos_em_aberto_financeiro (
            empresa, emissao, vencimento, dataemissao, especiedodocumento,
            pessoa, recebido, aberto, vendedor, documentoparcela,
            numeroproposta, descricao_veiculo_tipo_venda, sincronizado_em
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """
    now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    batch = []
    for r in records:
        batch.append((
            r.get('empresa'),
            r.get('emissao'),
            r.get('vencimento'),
            r.get('dataemissao'),
            r.get('especiedodocumento'),
            r.get('pessoa'),
            r.get('recebido'),
            r.get('aberto'),
            r.get('vendedor'),
            r.get('documentoparcela'),
            r.get('numeroproposta'),
            r.get('descricao_veiculo_tipo_venda'),
            now_str
        ))
    cursor.executemany(insert_sql, batch)
    conn.commit()
    print(f"✔ Tabela 'motos_em_aberto_financeiro': {len(batch)} registros inseridos.")

def populate_base(conn: sqlite3.Connection, records: list):
    cursor = conn.cursor()
    cursor.execute("DELETE FROM motos_em_aberto_base;")
    
    insert_sql = """
        INSERT INTO motos_em_aberto_base (
            chassi, pessoa, vendedor, datachegada, docfiscal,
            proposta, quantidade, empresa, datahoraexpedicao, sincronizado_em
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """
    now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    batch = []
    for r in records:
        batch.append((
            r.get('chassi'),
            r.get('pessoa'),
            r.get('vendedor'),
            r.get('datachegada'),
            r.get('docfiscal'),
            r.get('proposta'),
            r.get('quantidade'),
            r.get('empresa'),
            r.get('datahoraexpedicao'),
            now_str
        ))
    cursor.executemany(insert_sql, batch)
    conn.commit()
    print(f"✔ Tabela 'motos_em_aberto_base': {len(batch)} registros inseridos.")

def sync_de_para(conn: sqlite3.Connection):
    """Lê a planilha de:para.xlsx e popula as tabelas de referência de_para_lojas e de_para_regioes."""
    xlsx_path = os.path.join(os.path.dirname(__file__), '..', 'de:para.xlsx')
    if not os.path.exists(xlsx_path):
        print(f"⚠ Planilha {xlsx_path} não encontrada. Pulando sincronização do de-para.")
        return

    zf = zipfile.ZipFile(xlsx_path)
    shared_strings = []
    if 'xl/sharedStrings.xml' in zf.namelist():
        tree = ET.fromstring(zf.read('xl/sharedStrings.xml'))
        for si in tree.findall('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}si'):
            t = si.find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}t')
            if t is not None:
                shared_strings.append(t.text)
            else:
                r_texts = [r.find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}t').text 
                           for r in si.findall('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}r')
                           if r.find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}t') is not None]
                shared_strings.append(''.join(r_texts))

    def read_sheet(sheet_name):
        tree = ET.fromstring(zf.read(sheet_name))
        rows = tree.findall('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}sheetData/{http://schemas.openxmlformats.org/spreadsheetml/2006/main}row')
        data = []
        for row in rows:
            row_vals = []
            for c in row.findall('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}c'):
                t_attr = c.get('t')
                v = c.find('{http://schemas.openxmlformats.org/spreadsheetml/2006/main}v')
                val = v.text if v is not None else ''
                if t_attr == 's' and val.isdigit():
                    val = shared_strings[int(val)]
                row_vals.append(val.strip() if val else '')
            if row_vals:
                data.append(row_vals)
        return data

    sheet1 = read_sheet('xl/worksheets/sheet1.xml')
    sheet2 = read_sheet('xl/worksheets/sheet2.xml')

    cursor = conn.cursor()
    # 1. Regiões -> Estados (Sheet 2)
    regiao_estado = {}
    for r in sheet2[1:]:
        if len(r) >= 2:
            regiao, uf = r[0].strip(), r[1].strip()
            regiao_estado[regiao] = uf
            cursor.execute("INSERT OR REPLACE INTO de_para_regioes (regiao, estado) VALUES (?, ?);", (regiao, uf))

    # 2. Lojas -> Regiões (Sheet 1)
    inserted = set()
    for r in sheet1[1:]:
        if len(r) >= 2:
            loja = r[0].strip().upper()
            regiao = r[1].strip()
            uf = regiao_estado.get(regiao)
            if not uf and ' - ' in regiao:
                uf = regiao.split(' - ')[-1].strip()
            if loja and loja not in inserted:
                cursor.execute("INSERT OR REPLACE INTO de_para_lojas (loja, regiao, estado) VALUES (?, ?, ?);", (loja, regiao, uf))
                inserted.add(loja)

    # Inclusão de siglas complementares identificadas na operação
    cursor.execute("INSERT OR REPLACE INTO de_para_lojas (loja, regiao, estado) VALUES ('NVL', 'PARANAVAI - PR', 'PR');")
    cursor.execute("INSERT OR REPLACE INTO de_para_lojas (loja, regiao, estado) VALUES ('ITA', 'OCEÂNICA - RJ', 'RJ');")
    conn.commit()

    cursor.execute("SELECT COUNT(*) FROM de_para_lojas;")
    print(f"✔ De-para carregado: {cursor.fetchone()[0]} lojas e {len(regiao_estado)} regiões cadastradas.")

def populate_consolidado(conn: sqlite3.Connection):
    cursor = conn.cursor()
    cursor.execute("DELETE FROM motos_em_aberto_consolidado;")
    cursor.execute("""
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
                WHEN b.datahoraexpedicao IS NOT NULL AND b.datahoraexpedicao != '' 
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
    """)
    conn.commit()
    cursor.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado;")
    count = cursor.fetchone()[0]
    cursor.execute("SELECT COUNT(*) FROM motos_em_aberto_consolidado WHERE status = 'Verificar';")
    verificar_count = cursor.fetchone()[0]
    cursor.execute("SELECT COUNT(DISTINCT loja_vendedor) FROM motos_em_aberto_consolidado WHERE regiao = 'PENDENTE DE/PARA';")
    pendentes_count = cursor.fetchone()[0]
    if pendentes_count > 0:
        print(f"✔ Tabela 'motos_em_aberto_consolidado': {count} registros consolidados ({verificar_count} com status 'Verificar', ⚠ {pendentes_count} lojas com 'PENDENTE DE/PARA').")
    else:
        print(f"✔ Tabela 'motos_em_aberto_consolidado': {count} registros consolidados ({verificar_count} com status 'Verificar', 100% das lojas mapeadas no De-Para).")

def main():
    print(f"Iniciando criação e sincronização do banco SQLite: {DB_PATH}")
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    
    token = get_token()
    print("✔ Token recuperado de API/credencial.")
    
    conn = sqlite3.connect(DB_PATH)
    init_db(conn)
    print("✔ Tabelas e índices criados no SQLite com modo WAL ativado.")
    
    # 1. Carregar De-Para de Lojas, Regiões e Estados
    print("Carregando mapa De-Para da planilha...")
    sync_de_para(conn)
    
    # 2. API Financeiro
    print("Buscando dados da API Financeiro (motos_em_aberto_financeiro)...")
    payload_fin = get_payload('motos_em_aberto_financeiro')
    records_fin = fetch_api(payload_fin, token)
    populate_financeiro(conn, records_fin)
    
    # 3. API Base
    print("Buscando dados da API Base (motos_em_aberto_base)...")
    payload_base = get_payload('motos_em_aberto_base')
    records_base = fetch_api(payload_base, token)
    populate_base(conn, records_base)
    
    # 4. Consolidação Financeiro + Data de Chegada + Classificação de Loja, Região e Estado
    print("Consolidando dados com classificação e validações...")
    populate_consolidado(conn)
    
    conn.close()
    print("🎉 Sincronização e população concluídas com sucesso!")

if __name__ == '__main__':
    main()

