#!/usr/bin/env python3
"""
Servidor Local do Motos em Aberto (Motocar)
Fornece API REST, gerenciamento do De/Para com CRUD,
sincronização assíncrona com barra de progresso em tempo real e interface desktop.
"""

import os
import sys
import json
import sqlite3
import urllib.parse
import threading
from datetime import datetime
from http.server import HTTPServer, BaseHTTPRequestHandler

PORT = 5055
APP_VERSION = "1.0.0"
GITHUB_REPO = "ClaudiolfSantos/motos-em-aberto"
BASE_DIR = os.path.abspath(os.path.dirname(__file__))
DB_PATH = os.path.join(BASE_DIR, 'database', 'motocar.db')
WEB_DIR = os.path.join(BASE_DIR, 'web')
LOGO_DIR = os.path.join(BASE_DIR, 'logo')

# Importa funções do motor de sincronização
sys.path.append(BASE_DIR)
from scripts.sync_database import (
    get_token, init_db, get_payload, fetch_api, 
    sync_de_para, populate_financeiro, populate_base, populate_consolidado
)

# Estado global da sincronização em segundo plano
sync_state = {
    "rodando": False,
    "etapa": "",
    "progresso": 0,
    "total_fin": 0,
    "total_base": 0,
    "erro": None,
    "concluido_em": None
}

def worker_sincronizacao():
    global sync_state
    sync_state["rodando"] = True
    sync_state["erro"] = None
    sync_state["progresso"] = 10
    sync_state["etapa"] = "1/4: Conectando ao banco SQLite e carregando De-Para..."
    
    try:
        conn = sqlite3.connect(DB_PATH)
        init_db(conn)
        sync_de_para(conn)
        
        # Etapa 2: Financeiro
        sync_state["progresso"] = 30
        sync_state["etapa"] = "2/4: Consultando API de Financeiro na MicroWork (3 meses)..."
        token = get_token()
        payload_fin = get_payload('motos_em_aberto_financeiro', days_back=90)
        records_fin = fetch_api(payload_fin, token)
        populate_financeiro(conn, records_fin)
        sync_state["total_fin"] = len(records_fin)
        
        # Etapa 3: Veículos / Base (12.000+ linhas)
        sync_state["progresso"] = 60
        sync_state["etapa"] = "3/4: Consultando API de Veículos (12.000+ linhas) - Processando dados, por favor aguarde..."
        payload_base = get_payload('motos_em_aberto_base', days_back=90)
        records_base = fetch_api(payload_base, token)
        populate_base(conn, records_base)
        sync_state["total_base"] = len(records_base)
        
        # Etapa 4: Consolidação e regras
        sync_state["progresso"] = 90
        sync_state["etapa"] = "4/4: Cruzando pedidos, calculando dias no chão e regras de validação..."
        populate_consolidado(conn)
        conn.close()
        
        sync_state["progresso"] = 100
        sync_state["etapa"] = "Sincronização concluída com sucesso!"
        sync_state["concluido_em"] = datetime.now().strftime("%d/%m/%Y %H:%M")
        
    except Exception as e:
        sync_state["erro"] = str(e)
        sync_state["etapa"] = f"Erro na sincronização: {str(e)}"
    finally:
        sync_state["rodando"] = False

def parse_semver(v_str):
    try:
        clean = v_str.lstrip('v').split('-')[0]
        return tuple(int(x) for x in clean.split('.') if x.isdigit())
    except:
        return (0, 0, 0)

def verificar_versao_github():
    url = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"
    req = urllib.request.Request(url, headers={'User-Agent': 'MotosEmAberto-Updater'})
    try:
        with urllib.request.urlopen(req, timeout=5) as r:
            data = json.loads(r.read().decode('utf-8'))
            tag = data.get('tag_name', '').lstrip('v')
            
            # Localiza o instalador .msi nos assets
            msi_url = None
            for asset in data.get('assets', []):
                nome = asset.get('name', '').lower()
                if nome.endswith('.msi') or nome.endswith('.exe'):
                    msi_url = asset.get('browser_download_url')
                    break
            
            tem_update = parse_semver(tag) > parse_semver(APP_VERSION)
            return {
                'versao_atual': APP_VERSION,
                'ultima_versao': tag,
                'tem_atualizacao': tem_update,
                'url_release': data.get('html_url'),
                'url_msi': msi_url,
                'notas': data.get('body', ''),
                'mensagem': f'Nova versão v{tag} disponível para download!' if tem_update else f'Você está na versão mais recente (v{APP_VERSION}).'
            }
    except Exception as e:
        return {
            'versao_atual': APP_VERSION,
            'ultima_versao': APP_VERSION,
            'tem_atualizacao': False,
            'url_release': f"https://github.com/{GITHUB_REPO}/releases",
            'url_msi': None,
            'notas': '',
            'mensagem': f'Você está na versão oficial v{APP_VERSION} (Mais recente).'
        }

class MotocarHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass

    def get_db(self):
        conn = sqlite3.connect(DB_PATH)
        conn.row_factory = sqlite3.Row
        return conn

    def send_json(self, data, status=200):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.end_headers()
        self.wfile.write(json.dumps(data, ensure_ascii=False).encode('utf-8'))

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path
        query = urllib.parse.parse_qs(parsed.query)

        # 1. API - Dados com Filtros
        if path == '/api/dados':
            self.handle_api_dados(query)
            return

        # 2. API - Filtros Disponíveis
        elif path == '/api/filtros':
            self.handle_api_filtros()
            return

        # 3. API - Status da Sincronização em tempo real
        elif path == '/api/sincronizar/status':
            self.send_json(sync_state)
            return

        # 4. API - Verificação de Versão em Tempo Real
        elif path == '/api/versao':
            res = verificar_versao_github()
            self.send_json(res)
            return

        # 5. API - Listagem completa do De/Para e Lojas Pendentes
        elif path == '/api/depara':
            self.handle_get_depara()
            return

        # 5. Logo e arquivos estáticos
        elif path.startswith('/logo/'):
            filename = urllib.parse.unquote(path[6:])
            file_path = os.path.join(LOGO_DIR, filename)
            if os.path.exists(file_path):
                self.send_response(200)
                if filename.endswith('.png'):
                    self.send_header('Content-Type', 'image/png')
                self.end_headers()
                with open(file_path, 'rb') as f:
                    self.wfile.write(f.read())
            else:
                self.send_error(404, 'Logo nao encontrada')
            return

        # 6. Frontend Web (index.html)
        elif path == '/' or path == '/index.html':
            index_path = os.path.join(WEB_DIR, 'index.html')
            if os.path.exists(index_path):
                self.send_response(200)
                self.send_header('Content-Type', 'text/html; charset=utf-8')
                self.end_headers()
                with open(index_path, 'rb') as f:
                    self.wfile.write(f.read())
            else:
                self.send_error(404, 'index.html nao encontrado')
            return

        else:
            self.send_error(404, 'Rota nao encontrada')

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path

        # Disparar Sincronização
        if path == '/api/sincronizar':
            global sync_state
            if sync_state["rodando"]:
                self.send_json({'sucesso': False, 'mensagem': 'A sincronização já está em andamento.'}, status=409)
                return
            
            t = threading.Thread(target=worker_sincronizacao, daemon=True)
            t.start()
            self.send_json({'sucesso': True, 'mensagem': 'Sincronização iniciada com sucesso.'})
            return

        # Salvar / Atualizar Loja no De/Para
        elif path == '/api/depara':
            content_length = int(self.headers.get('Content-Length', 0))
            body = self.rfile.read(content_length).decode('utf-8')
            try:
                payload = json.loads(body)
                loja = payload.get('loja', '').strip().upper()
                regiao = payload.get('regiao', '').strip()
                estado = payload.get('estado', '').strip().upper()

                if not loja or not regiao or not estado:
                    self.send_json({'sucesso': False, 'mensagem': 'Loja, Região e Estado são obrigatórios.'}, status=400)
                    return

                conn = self.get_db()
                cursor = conn.cursor()
                cursor.execute("""
                    INSERT INTO de_para_lojas (loja, regiao, estado) 
                    VALUES (?, ?, ?)
                    ON CONFLICT(loja) DO UPDATE SET regiao = excluded.regiao, estado = excluded.estado;
                """, (loja, regiao, estado))

                # Atualiza também na tabela consolidada
                cursor.execute("""
                    UPDATE motos_em_aberto_consolidado
                    SET regiao = ?, estado = ?
                    WHERE UPPER(loja_vendedor) = ?;
                """, (regiao, estado, loja))

                conn.commit()
                conn.close()

                self.send_json({'sucesso': True, 'mensagem': f'Loja {loja} salva com sucesso no De/Para!'})
            except Exception as e:
                self.send_json({'sucesso': False, 'mensagem': str(e)}, status=500)
            return

        else:
            self.send_error(404, 'Endpoint nao encontrado')

    def do_DELETE(self):
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path == '/api/depara':
            query = urllib.parse.parse_qs(parsed.query)
            loja = query.get('loja', [''])[0].strip().upper()
            if not loja:
                self.send_json({'sucesso': False, 'mensagem': 'Parâmetro loja obrigatório.'}, status=400)
                return

            try:
                conn = self.get_db()
                cursor = conn.cursor()
                cursor.execute("DELETE FROM de_para_lojas WHERE UPPER(loja) = ?;", (loja,))

                # Marca as motos dessa loja como PENDENTE DE/PARA
                cursor.execute("""
                    UPDATE motos_em_aberto_consolidado
                    SET regiao = 'PENDENTE DE/PARA', estado = 'PENDENTE'
                    WHERE UPPER(loja_vendedor) = ?;
                """, (loja,))

                conn.commit()
                conn.close()
                self.send_json({'sucesso': True, 'mensagem': f'Loja {loja} removida do De/Para.'})
            except Exception as e:
                self.send_json({'sucesso': False, 'mensagem': str(e)}, status=500)
        else:
            self.send_error(404, 'Endpoint nao encontrado')

    def handle_get_depara(self):
        conn = self.get_db()
        cursor = conn.cursor()

        # 1. Lojas cadastradas no De/Para
        cursor.execute("SELECT loja, regiao, estado FROM de_para_lojas ORDER BY loja ASC;")
        lojas = [dict(r) for r in cursor.fetchall()]

        # 2. Regiões e Estados disponíveis para seleção
        cursor.execute("SELECT regiao, estado FROM de_para_regioes ORDER BY regiao ASC;")
        regioes = [dict(r) for r in cursor.fetchall()]

        # 3. Lojas não localizadas (que existem na consolidada mas estão PENDENTE DE/PARA)
        cursor.execute("""
            SELECT 
                loja_vendedor as loja,
                empresa,
                vendedor as exemplo_vendedor,
                COUNT(*) as total_motos,
                ROUND(SUM(aberto), 2) as total_aberto
            FROM motos_em_aberto_consolidado
            WHERE regiao = 'PENDENTE DE/PARA'
            GROUP BY loja_vendedor
            ORDER BY total_motos DESC;
        """)
        pendentes = [dict(r) for r in cursor.fetchall()]

        conn.close()

        self.send_json({
            'lojas': lojas,
            'regioes': regioes,
            'pendentes': pendentes
        })

    def handle_api_dados(self, q):
        conn = self.get_db()
        cursor = conn.cursor()

        loja = q.get('loja', [''])[0].strip()
        regiao = q.get('regiao', [''])[0].strip()
        estado = q.get('estado', [''])[0].strip()
        tipo_venda = q.get('tipo_venda', [''])[0].strip()
        status = q.get('status', [''])[0].strip()
        busca = q.get('busca', [''])[0].strip()
        ordem = q.get('ordem', ['aberto_desc'])[0].strip()

        where_clauses = ["1=1"]
        params = []

        if loja and loja != 'Todos':
            where_clauses.append("loja_vendedor = ?")
            params.append(loja)

        if regiao and regiao != 'Todos':
            where_clauses.append("regiao = ?")
            params.append(regiao)

        if estado and estado != 'Todos':
            where_clauses.append("estado = ?")
            params.append(estado)

        if tipo_venda and tipo_venda != 'Todos':
            where_clauses.append("descricao_veiculo_tipo_venda = ?")
            params.append(tipo_venda)

        if status and status != 'Todos':
            where_clauses.append("status = ?")
            params.append(status)

        if busca:
            where_clauses.append("""
                (pessoa LIKE ? OR 
                 vendedor LIKE ? OR 
                 documento LIKE ? OR 
                 nf_pedido LIKE ? OR 
                 numeroproposta LIKE ?)
            """)
            term = f"%{busca}%"
            params.extend([term, term, term, term, term])

        where_str = " AND ".join(where_clauses)

        # Totais e KPIs do conjunto filtrado
        total_sql = f"""
            SELECT 
                COUNT(*) as total_motos,
                COALESCE(SUM(aberto), 0) as total_aberto,
                COALESCE(SUM(recebido), 0) as total_recebido,
                COALESCE(SUM(CASE WHEN status = 'Verificar' THEN 1 ELSE 0 END), 0) as total_verificar,
                strftime('%d/%m/%Y %H:%M', MAX(sincronizado_em)) as ultima_atualizacao
            FROM motos_em_aberto_consolidado
            WHERE {where_str};
        """
        cursor.execute(total_sql, params)
        totais = dict(cursor.fetchone())

        # Ordenação
        order_by = "aberto DESC"
        if ordem == 'dias_chao_desc':
            order_by = "dias_chegada DESC NULLS LAST"
        elif ordem == 'dias_emissao_desc':
            order_by = "dias_emissao DESC"
        elif ordem == 'pessoa_asc':
            order_by = "pessoa ASC"
        elif ordem == 'loja_asc':
            order_by = "loja_vendedor ASC"

        # Registros
        dados_sql = f"""
            SELECT 
                id,
                loja_vendedor as loja,
                vendedor,
                pessoa,
                dias_chegada as dias_no_chao,
                dias_emissao as dia_emissao,
                aberto as valor_em_aberto,
                recebido as valor_recebido,
                documento,
                dataexpedicao as data_expedicao,
                nf_pedido,
                regiao,
                estado,
                descricao_veiculo_tipo_venda as tipo_venda,
                vencimento,
                emissao,
                status
            FROM motos_em_aberto_consolidado
            WHERE {where_str}
            ORDER BY {order_by};
        """
        cursor.execute(dados_sql, params)
        rows = [dict(r) for r in cursor.fetchall()]
        conn.close()

        self.send_json({
            'totais': totais,
            'linhas': rows
        })

    def handle_api_filtros(self):
        conn = self.get_db()
        cursor = conn.cursor()

        cursor.execute("SELECT DISTINCT loja_vendedor FROM motos_em_aberto_consolidado WHERE loja_vendedor IS NOT NULL AND loja_vendedor != '' ORDER BY loja_vendedor;")
        lojas = [r[0] for r in cursor.fetchall()]

        cursor.execute("SELECT DISTINCT regiao FROM motos_em_aberto_consolidado WHERE regiao IS NOT NULL AND regiao != '' ORDER BY regiao;")
        regioes = [r[0] for r in cursor.fetchall()]

        cursor.execute("SELECT DISTINCT estado FROM motos_em_aberto_consolidado WHERE estado IS NOT NULL AND estado != '' ORDER BY estado;")
        estados = [r[0] for r in cursor.fetchall()]

        cursor.execute("SELECT DISTINCT descricao_veiculo_tipo_venda FROM motos_em_aberto_consolidado WHERE descricao_veiculo_tipo_venda IS NOT NULL AND descricao_veiculo_tipo_venda != '' ORDER BY descricao_veiculo_tipo_venda;")
        tipos_venda = [r[0] for r in cursor.fetchall()]

        cursor.execute("SELECT strftime('%d/%m/%Y %H:%M', MAX(sincronizado_em)) FROM motos_em_aberto_consolidado;")
        last_sync = cursor.fetchone()[0]

        conn.close()

        self.send_json({
            'lojas': lojas,
            'regioes': regioes,
            'estados': estados,
            'tipos_venda': tipos_venda,
            'ultima_atualizacao': last_sync or ''
        })

def run_server():
    server = HTTPServer(('127.0.0.1', PORT), MotocarHandler)
    print(f"🚀 Servidor Motos em Aberto rodando em http://127.0.0.1:{PORT}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nServidor encerrado.")

if __name__ == '__main__':
    run_server()
