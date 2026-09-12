using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VibroAnalyzer.WPF
{
    public partial class DashboardWindow : Window
    {
        private readonly List<MainWindow> _windows1 = new List<MainWindow>();
        private readonly List<MainWindow> _windows2 = new List<MainWindow>();
        private readonly List<MainWindow> _windows3 = new List<MainWindow>();
        private readonly List<MainWindow> _windows4 = new List<MainWindow>();

        private int _counter1 = 0;
        private int _counter2 = 0;
        private int _counter3 = 0;
        private int _counter4 = 0;

        public DashboardWindow()
        {
            InitializeComponent();
        }

        private int GetSampleRate() => cmbSampleRate.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int sr) ? sr : 44100;
        private int GetBufferSize() => cmbBufferSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int bs) ? bs : 16384;
        private string GetTheme() => (cmbTheme.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Светлая";
        private double GetSeekStep() => double.TryParse(txtSeekStep.Text?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double step) && step > 0 ? step : 1.0;

        private void BtnOpenWindow1_Click(object sender, RoutedEventArgs e)
        {
            _counter1++;
            string channelName = (cmbChannel1.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Канал 1";
            var win = new MainWindow(
                windowId: 1,
                windowTitle: $"Окно №1 #{_counter1}: Магнитный датчик ({channelName})",
                sampleRate: GetSampleRate(),
                bufferSize: GetBufferSize(),
                theme: GetTheme(),
                seekStep: GetSeekStep(),
                showOsc: chkOsc1.IsChecked == true,
                showSpec: chkSpec1.IsChecked == true,
                showWaterfall: chkWaterfall1.IsChecked == true,
                showEnv: chkEnv1.IsChecked == true,
                showPsd: chkPsd1.IsChecked == true,
                showAcf: chkAcf1.IsChecked == true,
                showOctave: chkOctave1.IsChecked == true,
                showCepstrum: chkCepstrum1.IsChecked == true,
                showCwt: chkCwt1.IsChecked == true,
                showPhase: chkPhase1.IsChecked == true
            );
            win.Owner = this;
            win.Closed += (s, args) => _windows1.Remove(win);
            _windows1.Add(win);
            win.Show();
            txtDashboardStatus.Text = $"Открыт экземпляр Окна №1 #{_counter1} ({channelName}). Всего активно: {_windows1.Count}";
        }

        private void BtnOpenWindow2_Click(object sender, RoutedEventArgs e)
        {
            _counter2++;
            string channelName = (cmbChannel2.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Канал 2";
            var win = new MainWindow(
                windowId: 2,
                windowTitle: $"Окно №2 #{_counter2}: Емкостной датчик ({channelName})",
                sampleRate: GetSampleRate(),
                bufferSize: GetBufferSize(),
                theme: GetTheme(),
                seekStep: GetSeekStep(),
                showOsc: chkOsc2.IsChecked == true,
                showSpec: chkSpec2.IsChecked == true,
                showWaterfall: chkWaterfall2.IsChecked == true,
                showEnv: chkEnv2.IsChecked == true,
                showPsd: chkPsd2.IsChecked == true,
                showAcf: chkAcf2.IsChecked == true,
                showOctave: chkOctave2.IsChecked == true,
                showCepstrum: chkCepstrum2.IsChecked == true,
                showCwt: chkCwt2.IsChecked == true,
                showPhase: chkPhase2.IsChecked == true
            );
            win.Owner = this;
            win.Closed += (s, args) => _windows2.Remove(win);
            _windows2.Add(win);
            win.Show();
            txtDashboardStatus.Text = $"Открыт экземпляр Окна №2 #{_counter2} ({channelName}). Всего активно: {_windows2.Count}";
        }

        private void BtnOpenWindow3_Click(object sender, RoutedEventArgs e)
        {
            _counter3++;
            string channelName = (cmbChannel3.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Встроенный микрофон";
            var win = new MainWindow(
                windowId: 3,
                windowTitle: $"Окно №3 #{_counter3}: Сырые данные Composite ({channelName})",
                sampleRate: GetSampleRate(),
                bufferSize: GetBufferSize(),
                theme: GetTheme(),
                seekStep: GetSeekStep(),
                showOsc: chkOsc3.IsChecked == true,
                showSpec: chkSpec3.IsChecked == true,
                showWaterfall: chkWaterfall3.IsChecked == true,
                showEnv: chkEnv3.IsChecked == true,
                showPsd: chkPsd3.IsChecked == true,
                showAcf: chkAcf3.IsChecked == true,
                showOctave: chkOctave3.IsChecked == true,
                showCepstrum: chkCepstrum3.IsChecked == true,
                showCwt: chkCwt3.IsChecked == true,
                showPhase: chkPhase3.IsChecked == true
            );
            win.Owner = this;
            win.Closed += (s, args) => _windows3.Remove(win);
            _windows3.Add(win);
            win.Show();
            txtDashboardStatus.Text = $"Открыт экземпляр Окна №3 #{_counter3} ({channelName}). Всего активно: {_windows3.Count}";
        }

        private void BtnOpenWindow4_Click(object sender, RoutedEventArgs e)
        {
            _counter4++;
            string channelName = (cmbChannel4.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Канал 3";
            var win = new MainWindow(
                windowId: 4,
                windowTitle: $"Окно №4 #{_counter4}: Демодуляция несущей ({channelName})",
                sampleRate: GetSampleRate(),
                bufferSize: GetBufferSize(),
                theme: GetTheme(),
                seekStep: GetSeekStep(),
                showOsc: chkOsc4.IsChecked == true,
                showSpec: chkSpec4.IsChecked == true,
                showWaterfall: chkWaterfall4.IsChecked == true,
                showEnv: chkEnv4.IsChecked == true,
                showPsd: chkPsd4.IsChecked == true,
                showAcf: chkAcf4.IsChecked == true,
                showOctave: chkOctave4.IsChecked == true,
                showCepstrum: chkCepstrum4.IsChecked == true,
                showCwt: chkCwt4.IsChecked == true,
                showPhase: chkPhase4.IsChecked == true
            );
            win.Owner = this;
            win.Closed += (s, args) => _windows4.Remove(win);
            _windows4.Add(win);
            win.Show();
            txtDashboardStatus.Text = $"Открыт экземпляр Окна №4 #{_counter4} ({channelName}). Всего активно: {_windows4.Count}";
        }

        // ===== Групповое закрытие окон соответствующего раздела =====

        private void BtnCloseAllWindows1_Click(object sender, RoutedEventArgs e)
        {
            var list = _windows1.ToList();
            foreach (var w in list) w.Close();
            _windows1.Clear();
            txtDashboardStatus.Text = "Все экземпляры Окна №1 закрыты.";
        }

        private void BtnCloseAllWindows2_Click(object sender, RoutedEventArgs e)
        {
            var list = _windows2.ToList();
            foreach (var w in list) w.Close();
            _windows2.Clear();
            txtDashboardStatus.Text = "Все экземпляры Окна №2 закрыты.";
        }

        private void BtnCloseAllWindows3_Click(object sender, RoutedEventArgs e)
        {
            var list = _windows3.ToList();
            foreach (var w in list) w.Close();
            _windows3.Clear();
            txtDashboardStatus.Text = "Все экземпляры Окна №3 закрыты.";
        }

        private void BtnCloseAllWindows4_Click(object sender, RoutedEventArgs e)
        {
            var list = _windows4.ToList();
            foreach (var w in list) w.Close();
            _windows4.Clear();
            txtDashboardStatus.Text = "Все экземпляры Окна №4 закрыты.";
        }
    }
}