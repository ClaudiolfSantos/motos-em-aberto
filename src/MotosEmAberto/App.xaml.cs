using System;
using System.IO;
using System.Windows;
using MotosEmAberto.Services;

namespace MotosEmAberto
{
    public partial class App : Application
    {
        public static string ServerUrl { get; private set; } = "http://127.0.0.1:5055";
        private WebServer? _webServer;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            try
            {
                // 1. Inicializa o banco de dados no AppData local do usuário
                DatabaseService.EnsureDatabaseInitialized();

                // 2. Inicia o servidor HTTP embutido
                _webServer = new WebServer();
                int port = _webServer.Start();
                ServerUrl = $"http://127.0.0.1:{port}/";

                // 3. Abre a janela principal com WebView2
                var mainWindow = new MainWindow();
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Falha ao inicializar o Motos em Aberto:\n{ex.Message}\n\nDetalhes: {ex.StackTrace}",
                    "Motos em Aberto — Erro de Inicialização",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                Shutdown(-1);
            }
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            _webServer?.Stop();
        }
    }
}
