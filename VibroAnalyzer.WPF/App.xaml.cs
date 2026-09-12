using System.Windows;

namespace VibroAnalyzer.WPF
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Создаем и показываем Главный экран (Dashboard) в качестве основного окна
            var dashboard = new DashboardWindow();
            dashboard.Show();
        }
    }
}