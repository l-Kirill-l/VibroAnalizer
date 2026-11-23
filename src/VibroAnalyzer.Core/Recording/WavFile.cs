using System.Buffers.Binary;
using System.Text;

namespace VibroAnalyzer.Core.Recording;

/// <summary>
/// Чтение и запись моно WAV-файлов с отсчётами IEEE float32.
/// При чтении поддерживаются float32 (format 3) и PCM16, а также
/// WAVE_FORMAT_EXTENSIBLE с этими подформатами.
/// </summary>
public static class WavFile
{
    private const int HeaderSize = 44;
    private const ushort FormatPcm = 1;
    private const ushort FormatIeeeFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>
    /// Записывает моно float32 WAV-файл.
    /// </summary>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        EnsureDirectory(path);

        var dataBytes = (long)samples.Length * sizeof(float);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        Span<byte> header = stackalloc byte[HeaderSize];
        WriteHeader(header, sampleRate, dataBytes);
        stream.Write(header);

        const int framesPerChunk = 4096;
        var buffer = new byte[framesPerChunk * sizeof(float)];
        var offset = 0;

        while (offset < samples.Length)
        {
            var frames = Math.Min(framesPerChunk, samples.Length - offset);

            for (var i = 0; i < frames; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    buffer.AsSpan(i * sizeof(float), sizeof(float)),
                    BitConverter.SingleToInt32Bits(samples[offset + i]));
            }

            stream.Write(buffer, 0, frames * sizeof(float));
            offset += frames;
        }
    }

    /// <summary>
    /// Читает WAV-файл и возвращает моно-отсчёты float.
    /// Многоканальные файлы усредняются по каналам.
    /// </summary>
    public static float[] Read(string path, out int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        return Read(stream, out sampleRate);
    }

    private static float[] Read(Stream stream, out int sampleRate)
    {
        sampleRate = 0;

        var fileLength = stream.Length;
        if (fileLength < 12)
        {
            throw new InvalidDataException("Файл слишком короткий для WAV.");
        }

        Span<byte> riff = stackalloc byte[12];
        stream.ReadExactly(riff);

        if (!riff[..4].SequenceEqual("RIFF"u8) || !riff[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Файл не является WAV (нет сигнатуры RIFF/WAVE).");
        }

        var formatFound = false;
        var dataFound = false;
        long dataOffset = 0;
        long dataSize = 0;

        ushort audioFormat = 0;
        ushort channels = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        var rate = 0;

        Span<byte> chunkHeader = stackalloc byte[8];

        while (stream.Position + chunkHeader.Length <= fileLength)
        {
            stream.ReadExactly(chunkHeader);

            var chunkId = Encoding.ASCII.GetString(chunkHeader[..4]);
            var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var chunkStart = stream.Position;
            var chunkSize = Math.Min((long)declaredSize, fileLength - chunkStart);
            if (chunkSize < 0)
            {
                chunkSize = 0;
            }

            if (chunkId == "fmt ")
            {
                var toRead = (int)Math.Min(chunkSize, 64);
                if (toRead >= 16)
                {
                    var fmt = new byte[toRead];
                    stream.ReadExactly(fmt, 0, toRead);

                    audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                    rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12, 2));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));

                    // WAVE_FORMAT_EXTENSIBLE: настоящий формат лежит в начале GUID подформата.
                    if (audioFormat == FormatExtensible && fmt.Length >= 26)
                    {
                        audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24, 2));
                    }

                    formatFound = true;
                }
            }
            else if (chunkId == "data" && !dataFound)
            {
                dataOffset = chunkStart;
                dataSize = chunkSize;
                dataFound = true;
            }

            // Переходим к следующему чанку с учётом выравнивания по чётности.
            var next = chunkStart + chunkSize;
            if ((declaredSize & 1) != 0)
            {
                next++;
            }

            if (next > fileLength)
            {
                next = fileLength;
            }

            stream.Seek(next, SeekOrigin.Begin);
        }

        if (!formatFound || channels == 0 || rate <= 0)
        {
            throw new InvalidDataException("В WAV отсутствует корректный чанк fmt.");
        }

        if (!dataFound)
        {
            throw new InvalidDataException("В WAV отсутствует чанк data.");
        }

        sampleRate = rate;

        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            throw new InvalidDataException("Некорректная разрядность отсчётов в WAV.");
        }

        var frameBytes = channels * bytesPerSample;
        var frameStride = blockAlign >= frameBytes ? blockAlign : frameBytes;

        if (audioFormat != FormatIeeeFloat && audioFormat != FormatPcm)
        {
            throw new NotSupportedException($"Формат WAV {audioFormat} не поддерживается.");
        }

        if (audioFormat == FormatIeeeFloat && bitsPerSample != 32)
        {
            throw new NotSupportedException($"IEEE float с разрядностью {bitsPerSample} бит не поддерживается.");
        }

        if (audioFormat == FormatPcm && bitsPerSample != 16)
        {
            throw new NotSupportedException($"PCM с разрядностью {bitsPerSample} бит не поддерживается.");
        }

        if (dataSize <= 0)
        {
            return Array.Empty<float>();
        }

        var framesLong = dataSize / frameStride;
        if (framesLong > int.MaxValue - 1)
        {
            throw new InvalidDataException("WAV слишком большой для загрузки в один массив.");
        }

        var framesTotal = (int)framesLong;
        if (framesTotal <= 0)
        {
            return Array.Empty<float>();
        }

        stream.Seek(dataOffset, SeekOrigin.Begin);

        var result = new float[framesTotal];
        var framesPerRead = Math.Max(1, 65536 / frameStride);
        var readBuffer = new byte[framesPerRead * frameStride];
        var frameIndex = 0;

        while (frameIndex < framesTotal)
        {
            var chunkFrames = Math.Min(framesPerRead, framesTotal - frameIndex);
            var bytesToRead = chunkFrames * frameStride;
            stream.ReadExactly(readBuffer, 0, bytesToRead);

            for (var frame = 0; frame < chunkFrames; frame++)
            {
                var frameOffset = frame * frameStride;
                var sum = 0f;

                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = frameOffset + channel * bytesPerSample;

                    if (audioFormat == FormatIeeeFloat)
                    {
                        var bits = BinaryPrimitives.ReadInt32LittleEndian(readBuffer.AsSpan(offset, 4));
                        sum += BitConverter.Int32BitsToSingle(bits);
                    }
                    else
                    {
                        var value = BinaryPrimitives.ReadInt16LittleEndian(readBuffer.AsSpan(offset, 2));
                        sum += value / 32768f;
                    }
                }

                result[frameIndex + frame] = sum / channels;
            }

            frameIndex += chunkFrames;
        }

        return result;
    }

    internal static void WriteHeader(Span<byte> header, int sampleRate, long dataBytes)
    {
        var clampedDataBytes = (uint)Math.Clamp(dataBytes, 0, uint.MaxValue - 36);
        var riffSize = (uint)(36 + clampedDataBytes);
        const ushort channels = 1;
        const ushort bitsPerSample = 32;
        const ushort blockAlign = channels * (bitsPerSample / 8);
        var byteRate = (uint)sampleRate * blockAlign;

        "RIFF"u8.CopyTo(header[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], riffSize);
        "WAVE"u8.CopyTo(header[8..12]);
        "fmt "u8.CopyTo(header[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], FormatIeeeFloat);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..32], byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], bitsPerSample);
        "data"u8.CopyTo(header[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], clampedDataBytes);
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

/// <summary>
/// Потоковая запись моно float32 WAV. Заголовок обновляется при Dispose.
/// </summary>
public sealed class WavWriter : IDisposable
{
    private const int HeaderSize = 44;

    private readonly object _sync = new();
    private readonly FileStream _stream;
    private readonly int _sampleRate;

    private long _framesWritten;
    private bool _disposed;

    public WavWriter(string path, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Частота дискретизации должна быть больше нуля.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _sampleRate = sampleRate;
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);

        try
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            WavFile.WriteHeader(header, sampleRate, 0);
            _stream.Write(header);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>Число записанных кадров.</summary>
    public long FramesWritten
    {
        get
        {
            lock (_sync)
            {
                return _framesWritten;
            }
        }
    }

    /// <summary>Дописывает отсчёты в файл.</summary>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var buffer = new byte[samples.Length * sizeof(float)];

            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    buffer.AsSpan(i * sizeof(float), sizeof(float)),
                    BitConverter.SingleToInt32Bits(samples[i]));
            }

            _stream.Write(buffer, 0, buffer.Length);
            _framesWritten += samples.Length;
        }
    }

    /// <summary>
    /// Финализирует файл: обновляет размеры RIFF и data, сбрасывает данные на диск.
    /// Повторный вызов безопасен.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _stream.Flush();

                Span<byte> header = stackalloc byte[HeaderSize];
                WavFile.WriteHeader(header, _sampleRate, _framesWritten * sizeof(float));
                _stream.Seek(0, SeekOrigin.Begin);
                _stream.Write(header);
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                _stream.Dispose();
            }
        }
    }
}
