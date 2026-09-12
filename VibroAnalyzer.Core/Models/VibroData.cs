using System;

namespace VibroAnalyzer.Core.Models
{
    public class VibroData
    {
        public double[] Samples { get; set; }
        public int SampleRate { get; set; }
        public DateTime TimeGetSamples { get; set; }

        public VibroData()
        {
            Samples = new double[0];
            SampleRate = 44100;
            TimeGetSamples = DateTime.Now;
        }

        public VibroData(double[] samples)
        {
            Samples = samples ?? new double[0];
            SampleRate = 44100;
            TimeGetSamples = DateTime.Now;
        }

        // ИСПРАВЛЕНИЕ: параметр sampleRate (с маленькой буквы)
        public VibroData(double[] samples, int sampleRate)
        {
            Samples = samples ?? new double[0];
            SampleRate = sampleRate; // Теперь присваиваем значение параметра свойству
            TimeGetSamples = DateTime.Now;
        }

        // ИСПРАВЛЕНИЕ: параметр sampleRate (с маленькой буквы)
        public VibroData(double[] samples, int sampleRate, DateTime time)
        {
            Samples = samples ?? new double[0];
            SampleRate = sampleRate; // Теперь присваиваем значение параметра свойству
            TimeGetSamples = time;
        }
    }
}