#!/usr/bin/env bash
# ==============================================================================
# MOTOS EM ABERTO — MOTOCAR (Desktop Launcher macOS)
# ==============================================================================

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$DIR"

echo "======================================================"
echo "          🏍️  MOTOS EM ABERTO — MOTOCAR               "
echo "======================================================"
echo ""

# 1. Garante que o banco de dados inicial existe
if [ ! -f "database/motocar.db" ]; then
    echo "📦 Inicializando banco de dados pela primeira vez..."
    python3 scripts/sync_database.py
fi

# 2. Finaliza qualquer processo anterior na porta 5055
PID=$(lsof -ti:5055 2>/dev/null)
if [ ! -z "$PID" ]; then
    echo "🔄 Liberando porta 5055..."
    kill -9 $PID 2>/dev/null
fi

# 3. Inicia o servidor local
echo "🚀 Iniciando servidor Motos em Aberto..."
python3 server.py &
SERVER_PID=$!

# Aguarda 1 segundo para o servidor subir
sleep 1

# 4. Abre a interface como Aplicativo Desktop no macOS
URL="http://127.0.0.1:5055"
echo "🌐 Abrindo interface desktop em $URL ..."

if [ -d "/Applications/Google Chrome.app" ]; then
    # Abre no Modo App do Chrome (janela dedicada sem abas, sensação 100% nativa)
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --app="$URL" &
else
    # Fallback para o navegador padrão do macOS
    open "$URL"
fi

echo ""
echo "✔ Sistema rodando com sucesso!"
echo "Pressione [Ctrl+C] nesta janela para encerrar o aplicativo."
echo "======================================================"

# Aguarda encerramento pelo usuário
trap "echo ''; echo '🛑 Encerrando Motos em Aberto...'; kill -9 $SERVER_PID 2>/dev/null; exit 0" INT TERM
wait $SERVER_PID

