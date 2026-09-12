using System;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics;

namespace VibroAnalyzer.Core.Services
{
    public class SignalProcessor
    {
        public void ApplyHannWindow(double[] samples)
        {
            int n = samples.Length;
            for (int i = 0; i < n; i++)
            {
                double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
                samples[i] *= window;
            }
        }

        public double[] CalculateFFT(double[] samples)
        {
            double[] data = new double[samples.Length];
            Array.Copy(samples, data, samples.Length);
            ApplyHannWindow(data);

            Complex[] complex = new Complex[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                complex[i] = new Complex(data[i], 0);
            }

            Fourier.Forward(complex, FourierOptions.Matlab);

            double[] magnitude = new double[complex.Length];
            for (int i = 0; i < complex.Length; i++)
            {
                magnitude[i] = complex[i].Magnitude;
            }

            // НОРМАЛИЗАЦИЯ
            for (int i = 0; i < magnitude.Length; i++)
            {
                magnitude[i] = 2.0 * magnitude[i] / data.Length;
            }

            int halfLength = magnitude.Length / 2;
            double[] result = new double[halfLength];
            Array.Copy(magnitude, result, halfLength);
            return result;
        }

        public double[] GetFrequencies(int sampleCount, int sampleRate)
        {
            double[] frequencies = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                frequencies[i] = (double)i * sampleRate / (2 * sampleCount);
            }
            return frequencies;
        }

        public double FindPeakFrequency(double[] spectrum, double[] frequencies)
        {
            if (spectrum == null || frequencies == null || spectrum.Length == 0 || frequencies.Length == 0)
            {
                return 0;
            }

            int peakIndex = 0;
            double maxMagnitude = 0;

            // Ищем максимум начиная с i=1 (пропускаем DC компоненту)
            for (int i = 1; i < spectrum.Length; i++)
            {
                if (spectrum[i] > maxMagnitude)
                {
                    maxMagnitude = spectrum[i];
                    peakIndex = i;
                }
            }

            if (peakIndex >= frequencies.Length)
            {
                peakIndex = frequencies.Length - 1;
            }

            return frequencies[peakIndex];
        }

        public double CalculateRMS(double[] samples)
        {
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return Math.Sqrt(sum / samples.Length);
        }

        /// <summary>
        /// Возвращает частоты topN самых сильных пиков (DC-компонента пропускается).
        /// ИСПРАВЛЕНО: раньше было Array.Copy(double[] -> int[]) — падение в рантайме.
        /// Теперь сортируем ИНДЕКСЫ по убыванию амплитуды.
        /// </summary>
        public double[] FindTopPeaks(double[] spectrum, double[] frequencies, int topN = 5)
        {
            if (spectrum == null || frequencies == null ||
                spectrum.Length == 0 || frequencies.Length == 0)
            {
                return new double[0];
            }

            // Массив индексов 0..N-1
            int[] indices = new int[spectrum.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = i;
            }

            // Сортируем индексы по убыванию амплитуды спектра
            Array.Sort(indices, (a, b) => spectrum[b].CompareTo(spectrum[a]));

            int count = Math.Min(topN, frequencies.Length);
            double[] result = new double[count];
            int resultIndex = 0;

            for (int i = 0; i < indices.Length && resultIndex < count; i++)
            {
                if (indices[i] > 0) // пропускаем DC-компоненту
                {
                    result[resultIndex] = frequencies[indices[i]];
                    resultIndex++;
                }
            }

            // Если пиков нашлось меньше, чем topN — обрезаем массив
            if (resultIndex < count)
            {
                Array.Resize(ref result, resultIndex);
            }

            return result;
        }
    }
}