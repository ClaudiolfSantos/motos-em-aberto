# 🏍️ Motos em Aberto — Motocar
### Sistema Corporativo de Auditoria e Acompanhamento de Motos Faturadas e em Trânsito

Aplicativo oficial para monitoramento, cruzamento relacional e auditoria em tempo real das operações de faturamento e expedição da rede **Motocar**, integrando diretamente as APIs da **MicroWork Cloud** com janela contínua de 3 meses.

---

## 🚀 Principais Funcionalidades

- **Réplica Aprimorada do Power BI**: Interface corporativa moderna desenvolvida com Tailwind CSS, com visual idêntico ao modelo Power BI, cards de métricas (KPIs), busca universal e tabela de auditoria completa.
- **Validação Automática de Status**:
  - Classificação automática com badge **`Verificar`** quando **`dias_emissao >= 10`** e **`dias_chegada >= 10`**.
- **Gestão De/Para Dinâmica**:
  - Mapeamento completo de 105 lojas para Regiões e Estados (UF).
  - Modal administrativo embutido para criar, editar e excluir mapeamentos, com alerta visual de lojas pendentes de mapeamento.
- **Janela Dinâmica de 3 Meses**:
  - Parâmetros de data calculados dinamicamente (`Hoje - 90 dias` até `Hoje`).
  - Limpeza atômica do banco SQLite e recarga dos dados mais recentes a cada sincronização.
- **Controle de Versão em Tempo Real**:
  - Botão de checagem direta com a API do GitHub Releases no canto superior direito.
  - Alerta visual e download direto do instalador `.msi` quando uma nova versão for publicada.
- **Multiplataforma**:
  - **macOS**: Execução instantânea via script `./iniciar.sh`.
  - **Windows**: Aplicativo desktop nativo (.NET 8 WPF WebView2) com instalador oficial Microsoft (`.msi`).

---

## 🛠️ Stack Tecnológica

| Componente | Tecnologia | Finalidade |
| :--- | :--- | :--- |
| **Windows Desktop** | C# 12 (.NET 8) / WPF WebView2 | Executável autônomo (Self-Contained win-x64) com servidor HTTP e SQLite embutidos. |
| **macOS Desktop** | Python 3 + Browser App Mode | Servidor HTTP local e inicialização da interface via `./iniciar.sh`. |
| **Banco de Dados** | SQLite 3 (Modo WAL) | Armazenamento local com cache de alta concorrência e joins relacionais. |
| **Instalador Oficial** | WiX Toolset v5 (MSI) | Pacote oficial `.msi` com atalhos de Desktop e Menu Iniciar. |
| **Automação CI/CD** | GitHub Actions | Compilação em `windows-latest`, Smoke Test obrigatório (`msiexec`) e Release automático. |

---

## 🖥️ Como Executar no macOS

1. Abra o terminal na raiz do projeto:
```bash
cd "/caminho/para/motos em aberto"
```

2. Execute o inicializador:
```bash
./iniciar.sh
```
O script verifica o banco local, inicia o serviço na porta `5055` e abre a janela do aplicativo no Google Chrome em modo App (ou no navegador padrão).

---

## 🪟 Instalação no Windows

Baixe a versão mais recente diretamente na aba [Releases](https://github.com/ClaudiolfSantos/motos-em-aberto/releases) do repositório:

1. Baixe o arquivo **`Setup-MotosEmAberto-v1.0.0.msi`**.
2. Dê duplo clique para instalar (ou instale silenciosamente via terminal com `msiexec /i Setup-MotosEmAberto-v1.0.0.msi /qn`).
3. O atalho **"Motos em Aberto"** será criado na sua Área de Trabalho e no Menu Iniciar.

---

## 🔄 Pipeline de Integração Contínua (GitHub Actions)

A cada push na branch `main` ou publicação de tag `v*`, o fluxo de trabalho automatizado:
1. Compila o projeto em **.NET 8** no modo **Self-Contained** para arquitetura `win-x64`.
2. Empacota o instalador oficial `.msi` utilizando o **WiX Toolset**.
3. Realiza o **Smoke Test**:
   - Instalação silenciosa do `.msi` (`msiexec /i ... /qn`).
   - Verificação da integridade dos arquivos em `C:\Program Files\Motocar\Motos em Aberto`.
   - Inicialização de teste do executável.
   - Desinstalação silenciosa (`msiexec /x ... /qn`).
4. Publica a versão no **GitHub Releases** com os artefatos assinados com hash SHA-256.

---

## 🏢 Desenvolvido para
**Motocar — Motos em Aberto**  
Copyright © 2026 Motocar. Todos os direitos reservados.
