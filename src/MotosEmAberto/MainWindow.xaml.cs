using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MotosEmAberto
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Configura pasta de perfil do WebView2 no AppData (garante permissão de escrita mesmo em Program Files)
                string localAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Motocar", "MotosEmAberto", "WebView2Data"
                );
                Directory.CreateDirectory(localAppData);

                var env = await CoreWebView2Environment.CreateAsync(null, localAppData);
                await webView.EnsureCoreWebView2Async(env);

                // Configurações do WebView2
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = true;

                // Navega para a interface local
                webView.Source = new Uri(App.ServerUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Erro ao carregar componente WebView2:\n{ex.Message}\n\nPor favor, certifique-se de que o Microsoft Edge WebView2 Runtime está instalado no Windows.",
                    "Motos em Aberto — Erro Visual",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }
}

