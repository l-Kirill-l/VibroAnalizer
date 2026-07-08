using NAudio.CoreAudioApi;
using VibroAnalizer.Core.Models;
using NAudio.Wave;

namespace VibroAnalizer.Core.services
{
    public class AudioCapture:IDisposable
    {
        private WasapiCapture _capture;
        private double[] _buffer;
        private int _bufferPosition;
        private bool _isCapturing;
        private int _sampleRate;


        ///<summary>
        ///Событие, вызываемое при получении новых данных.
        ///</summary>
        public event Action<VibroData> DataAvailable;

        public AudioCapture(int SampleRate = 44100, int bufferSize = 44100)
        {
            _sampleRate = SampleRate;
            _buffer = new double[bufferSize];
            _bufferPosition = 0;
            _isCapturing = false;
        }

        ///<summary>
        ///начало захвата маудио
        /// </summary>
        public void Start()
        {
            if (_isCapturing)
            {
                return;
            }
            _capture = new WasapiCapture();

            _capture.WaveFormat = new WaveFormat(_sampleRate, 16, 1);

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
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
            }

            _isCapturing = false;
        }

        ///<summary>
        ///обработчик событий получения данных от naudio
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

            var vibroData = new VibroData(samples, _sampleRate);

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

        public double[] GeyLatestSamples(int count)
        {
            if (count > _buffer.Length)
            {
                count = _buffer.Length;
            }

            double[] latestSamples = new double[count];

            int StartPosition = _bufferPosition - count;

            if (StartPosition < 0)
            {
                StartPosition += _buffer.Length;
            }

            for (int i = 0; i < count; i++)
            {
                int bufferIndex = (StartPosition + i) % _buffer.Length;
                latestSamples[i] = _buffer[bufferIndex];
            }
            return latestSamples;
        }

        public void Dispose()
        {
            Stop();
            _capture?.Dispose();
        }
    }
}
