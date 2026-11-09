# 🛠️ Stack Tecnológica, Regras e Arquitetura do Projeto
### Motos em Aberto — Motocar (Desktop Multiplataforma & Windows Oficial)

Este documento detalha todas as **linguagens, tecnologias, regras obrigatórias de desenvolvimento, layout da interface e validações** do aplicativo oficial **Motos em Aberto**.

---

## 🎨 Interface Gráfica Oficial: "Motos em Aberto"
A interface replica fielmente o modelo do Power BI com melhorias visuais e corporativas:

### 1. Cabeçalho Superior e Filtros:
* **Filtros Rápidos:** `Loja`, `Região`, `Estado` (UF) e `Tipo de Venda`.
* **Identidade Visual:** Logotipo oficial da Motocar (`logo/Motocar vermelha.png`) e ícone do sistema.
* **Status de Atualização:** Exibição do rótulo `ATUALIZADO` com data e hora da última sincronização.
* **Botão "Sincronizar":** Dispara a recarga dos últimos 3 meses em segundo plano com feedback visual.
* **Busca e Exportação:** Campo de busca rápida (cliente, vendedor, NF, pedido) e exportador para CSV/Excel.

### 2. Cards de KPIs (Métricas Rápidas):
* **Motos no Filtro:** Quantidade dinâmica de motos selecionadas.
* **Valor em Aberto:** Soma total do saldo devedor pendente (R$).
* **Valor Recebido:** Soma dos valores já pagos/recebidos (R$).
* **A Verificar:** Alerta em destaque com o total de casos em que `dias_emissao >= 10` e `dias_chegada >= 10`.

### 3. Tabela Central (Grid de Dados — Padrão Oficial):
1. **Loja** (Sigla da loja do vendedor)
2. **Vendedor** (Nome completo e sigla)
3. **Pessoa** (Nome do cliente com código)
4. **Dias no chão** (Contagem de dias desde a chegada da moto no pátio)
5. **Dia emissão** (Contagem de dias corridos desde a emissão da venda)
6. **Valor em Aberto** (Formatado em R$)
7. **Valor Recebido** (Formatado em R$)
8. **Documento** (Número da Nota Fiscal e série)
9. **Data Expedição** (Data abreviada sem horário, sem o erro 31/12/1899)
10. **NF+pedido** (Concatenação oficial ex: `144678/116587`)
11. **Status** (Badge de validação: `Verificar` ou `OK`)

### 4. Rodapé Fixo (Linha de Totais):
* Exibição em tempo real do total de linhas filtradas, soma total do Valor em Aberto e Valor Recebido.

---

## 📋 Resumo da Stack Tecnológica

| Componente | Tecnologia / Linguagem | Papel no Projeto |
| :--- | :--- | :--- |
| **Lógica e Motor** | **C# 12 (.NET 8)** | Núcleo da aplicação, consumo das APIs e regras de negócio. |
| **Interface Visual (UI)** | **XAML (Avalonia UI / .NET 8)** | Interface gráfica moderna, compatível com execução local no **macOS** e compilação nativa para **Windows**. |
| **Armazenamento** | **SQL (SQLite 3 - Modo WAL)** | Banco de dados local para cache de 3 meses e cruzamento relacional (*JOIN*). |
| **Comunicação Web** | **JSON** | Formato de payloads e respostas das APIs MicroWork. |
| **Instalador Oficial** | **WiX Toolset (MSI - Microsoft Installer)** | Criação do pacote oficial de instalação do Windows (`.msi`). |
| **Automação & CI/CD** | **GitHub Actions (YAML)** | Pipeline em nuvem em runner Windows para compilação, testes e releases. |

---

## ⚙️ Tecnologias e Linguagens

### 1. 🔷 C# 12 (.NET 8)
* **Motor de Sincronização:** Consumo assíncrono (`async/await`) em background das APIs MicroWork, sem congelar a interface.
* **Cálculo Dinâmico de Datas:** Geração automática do intervalo de datas (*Hoje até 3 meses*) nos formatos exigidos por cada endpoint.
* **Resiliência e Políticas de Retry (Polly):** Tentativas inteligentes de reconexão caso a API da MicroWork oscile.
* **Criptografia de Credenciais:** Proteção segura do token JWT Bearer no disco.
* **Compilação "Self-Contained":** Gera um executável autônomo para Windows com todo o runtime .NET embutido (o cliente final não precisa ter nada instalado).

### 2. 🎨 XAML com Avalonia UI (.NET 8)
* **Compatibilidade macOS + Windows:** Permite que a interface gráfica execute e seja testada localmente no **macOS via `./iniciar.sh`**, e seja compilada para Windows com visual nativo *Fluent Design (Windows 11)*.
* **Identidade Visual:** Aplicação da paleta de cores corporativa, ícones e logotipo oficial da Motocar.
* **Arquitetura MVVM:** Separação estrita entre a camada visual e o motor de dados.

### 3. 🗄️ SQL com SQLite 3 (Modo WAL)
* **Cache Local Inteligente:** Armazenamento das 84 filiais e histórico de 3 meses para abertura instantânea do aplicativo.
* **Cruzamento das 2 APIs (JOIN):** União dos dados de *Contas a Receber (Financeiro)* com *Venda de Veículos (Base)* por chaves comuns.
* **Modo WAL (Write-Ahead Logging):** Blindagem contra corrupção e eliminação de erros de concorrência (*Database Locked*).

### 4. 📦 WiX Toolset (.wxs) — Instalador Oficial Windows (MSI)
* **Instalador Oficial Microsoft (.msi):** Gera o pacote nativo **`.msi` (Microsoft Installer Package)**, gerenciado diretamente pelo motor oficial do sistema operacional (`msiexec.exe`).
* **Padrão Corporativo Windows:** Suporte total a instalação por máquina em `Program Files`, registro oficial no banco de dados do Windows Installer, e compatibilidade com políticas de grupo (GPO/SCCM/Intune).
* **Funções Nativas do Windows:** Suporte automático e integrado aos botões oficiais de **Instalar**, **Reparar (Repair)** e **Desinstalar** no menu de Configurações e Painel de Controle do Windows.
* **Preparado para Auto-Update:** O pacote `.msi` utiliza controle oficial de versão de produto (`UpgradeCode` e `ProductCode`) que permite ao instalador atualizar versões antigas de forma atômica e segura.

---

## 🚨 REGRAS OBRIGATÓRIAS DO PROJETO

As regras a seguir devem ser rigorosamente seguidas durante todo o ciclo de desenvolvimento:

### Regra 1: Execução Local no macOS com `./iniciar.sh`
* O projeto deve conter na raiz um script executável `iniciar.sh`.
* Ao executar `./iniciar.sh` no macOS, o sistema deve restaurar dependências, inicializar o banco de dados local SQLite e subir a interface gráfica completa para visualização e testes em tempo real.
* Nenhum código específico de Windows deve impedir a execução do ambiente de desenvolvimento no Mac.

### Regra 2: Pipeline de CI/CD no GitHub Actions (Runner Windows)
* Toda compilação do executável e a geração do instalador oficial **`.msi`** ocorrerá obrigatoriamente no GitHub Actions utilizando uma máquina virtual `windows-latest`.
* O pipeline compilará a aplicação em arquitetura `win-x64` no modo *Self-Contained* e empacotará o produto final como pacote oficial do Windows Installer via **WiX Toolset**.

### Regra 3: Teste Automatizado de Instalação (Smoke Test com msiexec no GitHub)
* **Critério de Validação:** Antes de qualquer release ser gerada, o GitHub Actions executará um teste real de instalação do pacote `.msi` no Windows:
  1. O pacote `.msi` gerado será instalado via comando oficial do Windows: `msiexec /i Setup-Motocar-MotosEmAberto.msi /qn /norestart /log install.log`.
  2. O workflow verificará o código de saída de sucesso do Windows Installer (`%ERRORLEVEL% == 0`).
  3. O workflow verificará se os arquivos foram gravados com sucesso em `C:\Program Files\Motocar\MotosEmAberto`.
  4. O executável instalado será iniciado no runner para validar se sobe sem erros de inicialização (*Smoke Test*).
  5. Será testada também a desinstalação silenciosa oficial (`msiexec /x ... /qn`).
  6. Se qualquer etapa falhar, o build é reprovado e a release é cancelada.

### Regra 4: Distribuição Automática via GitHub Releases
* Ao publicar uma nova versão (tag), o GitHub Actions fará o upload automático dos seguintes artefatos:
  - O Instalador oficial **`Setup-Motocar-MotosEmAberto.msi`**.
  - O arquivo de manifesto de versão com hash SHA-256 para integridade e verificação do atualizador automático.

### Regra 5: Critério de Conclusão ("Definition of Done")
* Uma funcionalidade ou versão **SÓ** poderá ser dada como concluída quando:
  1. For validada localmente no macOS via `./iniciar.sh` com resposta positiva das APIs.
  2. Passar com status verde no GitHub Actions em todos os testes de compilação, instalação e inicialização no Windows.

---

> 📌 **Status do Projeto:** Fase de Planejamento e Arquitetura. Nenhuma alteração de código ou arquivos de compilação foram iniciados.
