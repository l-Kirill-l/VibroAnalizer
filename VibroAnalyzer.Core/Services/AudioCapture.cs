using NAudio.CoreAudioApi;
using VibroAnalyzer.Core.Models;
using NAudio.Wave;

namespace VibroAnalyzer.Core.Services
{
    public class AudioCapture : IDisposable
    {
        private WasapiCapture _capture;
        private double[] _buffer;
        private int _bufferPosition;
        private bool _isCapturing;
        private int _sampleRate;
        private int _actualSampleRate;

        /// <summary>
        /// Событие, вызываемое при получении новых данных.
        /// </summary>
        public event Action<VibroData> DataAvailable;

        public AudioCapture(int SampleRate = 44100, int bufferSize = 44100)
        {
            _sampleRate = SampleRate;
            _actualSampleRate = SampleRate;
            _buffer = new double[bufferSize];
            _bufferPosition = 0;
            _isCapturing = false;
        }

        /// <summary>
        /// Реальная частота захвата (устройство может отличаться от запрошенной).
        /// </summary>
        public int ActualSampleRate => _actualSampleRate;

        /// <summary>
        /// Начало захвата аудио. Если запрошенный формат не поддерживается —
        /// фолбэк на дефолтный формат устройства.
        /// </summary>
        public void Start()
        {
            if (_isCapturing)
            {
                return;
            }

            try
            {
                _capture = new WasapiCapture();
                _capture.WaveFormat = new WaveFormat(_sampleRate, 16, 1);
                _capture.DataAvailable += OnDataAvailable;
                _capture.StartRecording();
            }
            catch
            {
                // Запрошенный формат не поддерживается — устройство по умолчанию
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnDataAvailable;
                    _capture.Dispose();
                    _capture = null;
                }
                _capture = new WasapiCapture();
                _capture.DataAvailable += OnDataAvailable;
                _capture.StartRecording();
            }

            _actualSampleRate = _capture.WaveFormat.SampleRate;
            _isCapturing = true;
        }

        public void Stop()
        {
            if (!_isCapturing)
            {
                return;
            }
            _capture?.StopRecording();
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.Dispose();
                _capture = null;
            }
            _isCapturing = false;
        }

        /// <summary>
        /// Обработчик получения данных от NAudio.
        /// </summary>
        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            int bytesPerSample = 2;
            int sampleCount = e.BytesRecorded / bytesPerSample;
            double[] samples = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short sampleValue = BitConverter.ToInt16(e.Buffer, i * bytesPerSample);
                samples[i] = sampleValue / 32768.0;
            }
            AddToBuffer(samples);
            var vibroData = new VibroData(samples, _actualSampleRate);
            DataAvailable?.Invoke(vibroData);
        }

        private void AddToBuffer(double[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                _buffer[_bufferPosition] = samples[i];
                _bufferPosition++;
                if (_bufferPosition >= _buffer.Length)
                {
                    _bufferPosition = 0;
                }
            }
        }

        public double[] GetLatestSamples(int count)
        {
            if (count > _buffer.Length)
            {
                count = _buffer.Length;
            }
            double[] latestSamples = new double[count];
            int startPosition = _bufferPosition - count;
            if (startPosition < 0)
            {
                startPosition += _buffer.Length;
            }
            for (int i = 0; i < count; i++)
            {
                int bufferIndex = (startPosition + i) % _buffer.Length;
                latestSamples[i] = _buffer[bufferIndex];
            }
            return latestSamples;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}