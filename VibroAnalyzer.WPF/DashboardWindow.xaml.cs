using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VibroAnalyzer.WPF
{
    public partial class DashboardWindow : Window
    {
        private MainWindow _window1;
        private MainWindow _window2;
        private MainWindow _window3;
        private MainWindow _window4;

        public DashboardWindow()
        {
            InitializeComponent();
        }

        private int GetSampleRate() => cmbSampleRate.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int sr) ? sr : 44100;
        private int GetBufferSize() => cmbBufferSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int bs) ? bs : 16384;
        private string GetTheme() => (cmbTheme.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Светлая";
        private double GetSeekStep() => double.TryParse(txtSeekStep.Text?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double step) && step > 0 ? step : 1.0;

        private void BtnOpenWindow4_Click(object sender, RoutedEventArgs e)
        {
            if (_window4 == null || !IsWindowOpen(_window4))
            {
                _window4 = new MainWindow(4, "Окно №4: Сырые данные (Composite)", GetSampleRate(), GetBufferSize(), GetTheme(), GetSeekStep());
                _window4.Owner = this;
                _window4.Closed += (s, args) => _window4 = null;
                _window4.Show();
                txtDashboardStatus.Text = "Открыто Окно №4 (Сырые данные)";
            }
            else { _window4.Activate(); }
        }

        private void BtnOpenWindow1_Click(object sender, RoutedEventArgs e)
        {
            if (_window1 == null || !IsWindowOpen(_window1))
            {
                int channelIndex = cmbChannel1.SelectedIndex + 1;
                _window1 = new MainWindow(1, $"Окно №1: Магнитный датчик (Канал {channelIndex})", GetSampleRate(), GetBufferSize(), GetTheme(), GetSeekStep(),
                                          chkOsc1.IsChecked == true, chkSpec1.IsChecked == true, chkWaterfall1.IsChecked == true);
                _window1.Owner = this;
                _window1.Closed += (s, args) => _window1 = null;
                _window1.Show();
                txtDashboardStatus.Text = $"Открыто Окно №1 (Канал {channelIndex})";
            }
            else { _window1.Activate(); }
        }

        private void BtnOpenWindow2_Click(object sender, RoutedEventArgs e)
        {
            if (_window2 == null || !IsWindowOpen(_window2))
            {
                int channelIndex = cmbChannel2.SelectedIndex + 1;
                _window2 = new MainWindow(2, $"Окно №2: Емкостной датчик (Канал {channelIndex})", GetSampleRate(), GetBufferSize(), GetTheme(), GetSeekStep(),
                                          chkOsc2.IsChecked == true, chkSpec2.IsChecked == true, chkWaterfall2.IsChecked == true);
                _window2.Owner = this;
                _window2.Closed += (s, args) => _window2 = null;
                _window2.Show();
                txtDashboardStatus.Text = $"Открыто Окно №2 (Канал {channelIndex})";
            }
            else { _window2.Activate(); }
        }

        private void BtnOpenWindow3_Click(object sender, RoutedEventArgs e)
        {
            if (_window3 == null || !IsWindowOpen(_window3))
            {
                int channelIndex = cmbChannel3.SelectedIndex + 1;
                _window3 = new MainWindow(3, $"Окно №3: Несущая и демодуляция (Канал {channelIndex})", GetSampleRate(), GetBufferSize(), GetTheme(), GetSeekStep());
                _window3.Owner = this;
                _window3.Closed += (s, args) => _window3 = null;
                _window3.Show();
                txtDashboardStatus.Text = $"Открыто Окно №3 (Канал {channelIndex})";
            }
            else { _window3.Activate(); }
        }

        private bool IsWindowOpen(Window window) => Application.Current.Windows.Cast<Window>().Any(w => w == window);
    }
}