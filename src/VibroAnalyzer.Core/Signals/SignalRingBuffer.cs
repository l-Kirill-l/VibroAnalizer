namespace VibroAnalyzer.Core.Signals;

/// <summary>
/// Потокобезопасный кольцевой буфер блоков сигнала. Хранит последние
/// <see cref="CapacityFrames"/> кадров и отдаёт снимки по отдельным каналам.
/// </summary>
public sealed class SignalRingBuffer
{
    private readonly object _sync = new();
    private readonly float[] _samples;

    private int _writeIndex;
    private int _count;
    private long _lastSequence;
    private DateTimeOffset _lastTimestamp;
    private long _totalFramesWritten;

    /// <summary>
    /// Создаёт буфер с заданным числом каналов и ёмкостью в кадрах.
    /// </summary>
    /// <param name="channels">Число каналов, должно быть больше нуля.</param>
    /// <param name="capacityFrames">Ёмкость в кадрах, должна быть больше нуля.</param>
    public SignalRingBuffer(int channels, int capacityFrames)
    {
        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Число каналов должно быть больше нуля.");
        }

        if (capacityFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrames), "Ёмкость буфера должна быть больше нуля.");
        }

        if ((long)channels * capacityFrames > int.MaxValue)
        {
            throw new ArgumentException("Объём буфера превышает допустимый размер массива.", nameof(capacityFrames));
        }

        Channels = channels;
        CapacityFrames = capacityFrames;
        _samples = new float[channels * capacityFrames];
    }

    /// <summary>Число каналов буфера.</summary>
    public int Channels { get; }

    /// <summary>Максимальное число хранимых кадров.</summary>
    public int CapacityFrames { get; }

    /// <summary>Текущее число кадров в буфере (не больше <see cref="CapacityFrames"/>).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Номер последовательности последнего записанного блока (0, если записей не было).</summary>
    public long LastSequence
    {
        get
        {
            lock (_sync)
            {
                return _lastSequence;
            }
        }
    }

    /// <summary>Метка времени последнего записанного блока (по умолчанию, если записей не было).</summary>
    public DateTimeOffset LastTimestamp
    {
        get
        {
            lock (_sync)
            {
                return _lastTimestamp;
            }
        }
    }

    /// <summary>
    /// Общее число кадров, записанных в буфер с момента создания.
    /// Используется потоковыми потребителями для выборки только новых данных.
    /// </summary>
    public long TotalFramesWritten
    {
        get
        {
            lock (_sync)
            {
                return _totalFramesWritten;
            }
        }
    }

    /// <summary>
    /// Записывает блок в буфер. Проверяется только число каналов;
    /// при переполнении самые старые кадры вытесняются.
    /// </summary>
    public void Write(SignalBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (block.Channels != Channels)
        {
            throw new ArgumentException(
                $"Число каналов блока ({block.Channels}) не совпадает с числом каналов буфера ({Channels}).",
                nameof(block));
        }

        lock (_sync)
        {
            for (var frame = 0; frame < block.FrameCount; frame++)
            {
                var sourceOffset = frame * Channels;
                var destinationOffset = _writeIndex * Channels;
                for (var channel = 0; channel < Channels; channel++)
                {
                    _samples[destinationOffset + channel] = block.Samples[sourceOffset + channel];
                }

                _writeIndex++;
                if (_writeIndex == CapacityFrames)
                {
                    _writeIndex = 0;
                }

                if (_count < CapacityFrames)
                {
                    _count++;
                }
            }

            _lastSequence = block.Sequence;
            _lastTimestamp = block.Timestamp;
            _totalFramesWritten += block.FrameCount;
        }
    }

    /// <summary>
    /// Возвращает ровно <paramref name="frames"/> значений указанного канала.
    /// При нехватке данных массив дополняется нулями слева, поэтому старые
    /// отсчёты всегда находятся в начале результата. Если запрошено больше
    /// кадров, чем помещается в буфер, используются только доступные данные,
    /// а длина результата сохраняется.
    /// </summary>
    public double[] Snapshot(int frames, int channel)
    {
        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "Число кадров не может быть отрицательным.");
        }

        if ((uint)channel >= (uint)Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), "Канал вне диапазона буфера.");
        }

        var result = new double[frames];

        lock (_sync)
        {
            var available = Math.Min(Math.Min(frames, _count), CapacityFrames);
            if (available == 0)
            {
                return result;
            }

            var readIndex = _writeIndex - available;
            if (readIndex < 0)
            {
                readIndex += CapacityFrames;
            }

            var resultIndex = frames - available;
            for (var i = 0; i < available; i++)
            {
                result[resultIndex + i] = _samples[readIndex * Channels + channel];

                readIndex++;
                if (readIndex == CapacityFrames)
                {
                    readIndex = 0;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Очищает буфер и сбрасывает служебные сведения о последнем блоке.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_samples);
            _writeIndex = 0;
            _count = 0;
            _lastSequence = 0;
            _lastTimestamp = default;
            _totalFramesWritten = 0;
        }
    }
}
