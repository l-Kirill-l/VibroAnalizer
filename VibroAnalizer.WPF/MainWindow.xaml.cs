using System;
using System.Windows;
using VibroAnalizer.Core.services;
using VibroAnalizer.Core.Models;

namespace VibrationAnalyzer.WPF
{
    /// <summary>
    /// Главное окно приложения
    /// </summary>
    public partial class MainWindow : Window
    {
        // Поле для хранения объекта захвата аудио
        private AudioCapture _audioCapture;

        /// <summary>
        /// Конструктор окна
        /// </summary>
        public MainWindow()
        {
            // Инициализируем компоненты из XAML (кнопки, тексты и т.д.)
            InitializeComponent();

            // Создаем объект захвата аудио
            _audioCapture = new AudioCapture();

            // Подписываемся на событие получения данных
            _audioCapture.DataAvailable += OnDataAvailable;
        }

        /// <summary>
        /// Обработчик события получения данных от AudioCapture
        /// </summary>
        private void OnDataAvailable(VibroData data)
        {
            // Вычисляем уровень сигнала (среднее абсолютное значение)
            double level = 0;
            foreach (var sample in data.Samples)
            {
                level += Math.Abs(sample);
            }
            level /= data.Samples.Length;

            // Выводим в заголовок окна (для теста)
            Dispatcher.Invoke(() =>
            {
                this.Title = $"Виброанализатор | Уровень: {level:F4}";
            });
        }

        /// <summary>
        /// Обработчик события загрузки окна
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Автоматически начинаем захват при запуске
            _audioCapture.Start();
        }

        /// <summary>
        /// Обработчик события закрытия окна
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Освобождаем ресурсы
            _audioCapture?.Dispose();
        }
    }
}