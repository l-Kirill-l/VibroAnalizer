using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VibroAnalizer.Core.Models
{
    public class VibroData
    {
        /// <summary>
        /// Массив значений амплитуды
        /// </summary>
        public double[] Samples { get; set; }

        /// <summary>
        /// частота дискретизации (количество отсчетов в секунду)
        /// </summary>
        public int SamplesRate { get; set; }

        /// <summary>
        /// время получения информации (время получения отсчетов)
        /// </summary>
        public DateTime TimeGetSamples { get; set; }

        public VibroData() 
        { 
            Samples = new double[0];
            SamplesRate = 44100;
            TimeGetSamples = DateTime.Now;
        }

        public VibroData(double[] samples)
        {
            Samples = samples ?? new double[0];
            SamplesRate = 44100;
            TimeGetSamples = DateTime.Now;
        }

        public VibroData(double[] samples, int samplesRate)
        {
            Samples = samples ?? new double[0];
            SamplesRate = samplesRate;
            TimeGetSamples = DateTime.Now;
        }

        public VibroData(double[] samples, int samplesRate, DateTime time)
        {
            Samples = samples ?? new double[0];
            SamplesRate = samplesRate;
            TimeGetSamples = time;
        }


    }
}
