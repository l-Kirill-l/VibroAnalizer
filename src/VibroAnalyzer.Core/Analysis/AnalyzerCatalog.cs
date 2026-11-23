using VibroAnalyzer.Core.Analysis.Acoustics;
using VibroAnalyzer.Core.Analysis.Diagnostics;
using VibroAnalyzer.Core.Analysis.Electrical;
using VibroAnalyzer.Core.Analysis.General;
using VibroAnalyzer.Core.Analysis.Medicine;
using VibroAnalyzer.Core.Analysis.Music;
using VibroAnalyzer.Core.Analysis.Seismology;
using VibroAnalyzer.Core.Analysis.Vibration;

namespace VibroAnalyzer.Core.Analysis;

/// <summary>Описание анализатора для каталога.</summary>
public sealed record AnalyzerDescriptor(
    string Title,
    string Category,
    string Description,
    Func<AnalyzerBase> Factory);

/// <summary>
/// Реестр всех доступных анализаторов, сгруппированных по категориям.
/// </summary>
public static class AnalyzerCatalog
{
    private static readonly Lazy<IReadOnlyList<AnalyzerDescriptor>> LazyAll = new(CreateAll);

    public static IReadOnlyList<AnalyzerDescriptor> All => LazyAll.Value;

    public static IReadOnlyDictionary<string, string> CategoryDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Общие измерения"] = "Базовые измерения уровня, статистики, распределения и корреляции сигнала.",
            ["Акустика"] = "Уровень звука, октавные полосы, реверберация и спектральные признаки.",
            ["Электрика"] = "Гармоники, THD и параметры сетевого напряжения или тока.",
            ["Музыка"] = "Тюнер, темп, ноты и гармонический состав музыкального сигнала.",
            ["Диагностика"] = "Кепстр и признаки периодических дефектов машин и механизмов.",
            ["Вибрация и механика"] = "Огибающая, подшипники, обороты, порядки и балансировка.",
            ["Сейсмология"] = "Триггеры STA/LTA, энергия, интенсивность и спектры реакции.",
            ["Медицина"] = "Пульс, дыхание, ритмы ЭЭГ и параметры ЭМГ.",
        };

    private static IReadOnlyList<AnalyzerDescriptor> CreateAll()
    {
        var factories = new Func<AnalyzerBase>[]
        {
            () => new LevelStatisticsAnalyzer(),
            () => new HistogramAnalyzer(),
            () => new WelchPsdAnalyzer(),
            () => new ZeroCrossingAnalyzer(),
            () => new NoiseFloorAnalyzer(),
            () => new AutocorrelationAnalyzer(),
            () => new TrendAnalyzer(),
            () => new BandLevelAnalyzer(),
            () => new OctaveBandAnalyzer(),
            () => new SoundLevelAnalyzer(),
            () => new LeqAnalyzer(),
            () => new Rt60Analyzer(),
            () => new SpectralFeaturesAnalyzer(),
            () => new HarmonicAnalyzer(),
            () => new MainsAnalyzer(),
            () => new TunerAnalyzer(),
            () => new TempoAnalyzer(),
            () => new CepstrumAnalyzer(),
            () => new EnvelopeAnalyzer(),
            () => new EnvelopeSpectrumAnalyzer(),
            () => new ShockPulseAnalyzer(),
            () => new TachometerAnalyzer(),
            () => new BearingAnalyzer(),
            () => new OrderAnalyzer(),
            () => new BalanceAnalyzer(),
            () => new StaLtaAnalyzer(),
            () => new SeismicEnergyAnalyzer(),
            () => new ResponseSpectrumAnalyzer(),
            () => new PulseAnalyzer(),
            () => new RespirationAnalyzer(),
            () => new EegBandAnalyzer(),
            () => new EmgAnalyzer(),
        };

        var result = new List<AnalyzerDescriptor>(factories.Length);

        foreach (var factory in factories)
        {
            var analyzer = factory();
            result.Add(new AnalyzerDescriptor(
                analyzer.Title,
                analyzer.Category,
                analyzer.Description,
                factory));
        }

        return result;
    }
}
