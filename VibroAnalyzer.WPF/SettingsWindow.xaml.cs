using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace VibroAnalyzer.WPF
{
    public partial class SettingsWindow : Window
    {
        // Текущие настройки (передаются из MainWindow)
        public int SelectedSampleRate { get; set; }
        public int SelectedBufferSize { get; set; }
        public string SelectedTheme { get; set; }
        public double SelectedSeekStep { get; set; }

        public SettingsWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// ФИКС: конструктор с текущими значениями — сначала сохраняем настройки,
        /// и ТОЛЬКО ПОТОМ выставляем выделение в элементах управления.
        /// </summary>
        public SettingsWindow(int sampleRate, int bufferSize, string theme, double seekStep) : this()
        {
            SelectedSampleRate = sampleRate;
            SelectedBufferSize = bufferSize;
            SelectedTheme = theme;
            SelectedSeekStep = seekStep;
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            // Тема
            cmbTheme.SelectedIndex = SelectedTheme == "Тёмная" ? 1 : 0;

            // Частота дискретизации
            foreach (ComboBoxItem item in cmbSampleRate.Items)
            {
                if (item.Tag?.ToString() == SelectedSampleRate.ToString())
                {
                    cmbSampleRate.SelectedItem = item;
                    break;
                }
            }

            // Размер буфера
            foreach (ComboBoxItem item in cmbBufferSize.Items)
            {
                if (item.Tag?.ToString() == SelectedBufferSize.ToString())
                {
                    cmbBufferSize.SelectedItem = item;
                    break;
                }
            }

            // Шаг перемотки
            txtSeekStep.Text = SelectedSeekStep.ToString("F3", CultureInfo.InvariantCulture);
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            // Тема применяется всегда из комбобокса
            SelectedTheme = (cmbTheme.SelectedItem as ComboBoxItem)?.Content.ToString() ?? SelectedTheme;

            // Частоту/буфер меняем ТОЛЬКО если пользователь что-то выбрал
            if (cmbSampleRate.SelectedItem is ComboBoxItem srItem &&
                int.TryParse(srItem.Tag?.ToString(), out int sr))
            {
                SelectedSampleRate = sr;
            }
            if (cmbBufferSize.SelectedItem is ComboBoxItem bsItem &&
                int.TryParse(bsItem.Tag?.ToString(), out int bs))
            {
                SelectedBufferSize = bs;
            }

            // Шаг перемотки: принимаем только валидное значение, иначе оставляем прежнее
            string stepText = txtSeekStep.Text?.Trim();
            if (double.TryParse(stepText?.Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double step) &&
                step > 0 && step <= 3600)
            {
                SelectedSeekStep = step;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}