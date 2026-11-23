using System.Text.Json;
using VibroAnalyzer.Core.Playback;
using VibroAnalyzer.Core.Recording;
using VibroAnalyzer.Core.Routing;
using VibroAnalyzer.Core.Signals;

namespace VibroAnalyzer.Tests;

public sealed class RecordingPlaybackTests
{
    [Fact]
    public void WavFileRoundTripsFloat32()
    {
        var root = NewTempDirectory();

        try
        {
            const int sampleRate = 48000;
            var path = Path.Combine(root, "tone.wav");
            var samples = GenerateSamples(1000, 0.5, sampleRate, 4096);

            WavFile.Write(path, samples, sampleRate);

            var loaded = WavFile.Read(path, out var rate);

            Assert.Equal(sampleRate, rate);
            Assert.Equal(samples.Length, loaded.Length);

            for (var i = 0; i < samples.Length; i++)
            {
                Assert.Equal(samples[i], loaded[i]);
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void SessionRecorderWritesManifestAndWavFiles()
    {
        const int sampleRate = 48000;
        var root = NewTempDirectory();
        InputBus? bus = null;
        SessionRecorder? recorder = null;

        try
        {
            bus = new InputBus("Вход", "test", 0, sampleRate);

            // Данные до старта записи не должны попасть в файл.
            bus.RawBuffer.Write(MakeBlock(1000, 0.3, sampleRate, 4800));

            recorder = new SessionRecorder(
                root,
                sampleRate,
                new[] { RecordingTarget.ForBus(bus) });

            recorder.Start();
            Assert.True(recorder.IsRecording);

            for (var i = 0; i < 3; i++)
            {
                bus.RawBuffer.Write(MakeBlock(1000, 0.3, sampleRate, 4800));
            }

            Thread.Sleep(200);

            var stoppedDirectory = recorder.Stop();

            Assert.Equal(root, stoppedDirectory);
            Assert.False(recorder.IsRecording);

            var manifestPath = Path.Combine(root, "manifest.json");
            Assert.True(File.Exists(manifestPath));

            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var rootElement = document.RootElement;

            Assert.Equal("vibroanalyzer.recording.v1", rootElement.GetProperty("schema").GetString());
            Assert.Equal(sampleRate, rootElement.GetProperty("sampleRate").GetInt32());

            var duration = rootElement.GetProperty("durationSeconds").GetDouble();
            Assert.InRange(duration, 0.29, 0.31);

            var buses = rootElement.GetProperty("buses");
            Assert.Equal(1, buses.GetArrayLength());

            var busElement = buses[0];
            Assert.Equal("input", busElement.GetProperty("kind").GetString());

            var rawName = busElement.GetProperty("rawFile").GetString();
            var demodName = busElement.GetProperty("demodFile").GetString();

            Assert.Equal("bus-01-raw.wav", rawName);
            Assert.Equal("bus-01-demod.wav", demodName);

            var rawPath = Path.Combine(root, rawName!);
            var demodPath = Path.Combine(root, demodName!);

            Assert.True(File.Exists(rawPath));
            Assert.True(File.Exists(demodPath));

            var raw = WavFile.Read(rawPath, out var rawRate);
            Assert.Equal(sampleRate, rawRate);
            Assert.Equal(3 * 4800, raw.Length);

            // Повторный Stop идемпотентен.
            Assert.Equal(root, recorder.Stop());
        }
        finally
        {
            recorder?.Dispose();
            bus?.Dispose();
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void PlaybackSessionLoadsSeeksAndPlaysRecording()
    {
        const int sampleRate = 8000;
        var root = NewTempDirectory();
        InputBus? bus = null;
        SessionRecorder? recorder = null;
        PlaybackSession? session = null;

        try
        {
            bus = new InputBus("Вход", "test", 0, sampleRate);
            recorder = new SessionRecorder(
                root,
                sampleRate,
                new[] { RecordingTarget.ForBus(bus) });

            recorder.Start();

            for (var i = 0; i < 10; i++)
            {
                bus.RawBuffer.Write(MakeBlock(440, 0.4, sampleRate, 800));
            }

            Thread.Sleep(200);
            recorder.Stop();
            recorder.Dispose();
            recorder = null;

            bus.Dispose();
            bus = null;

            session = PlaybackSession.Load(root);

            Assert.Single(session.Buses);
            Assert.Equal(sampleRate, session.SampleRate);
            Assert.InRange(session.Duration.TotalSeconds, 0.9, 1.1);
            Assert.Equal(TimeSpan.Zero, session.Position);
            Assert.False(session.IsPlaying);

            var playbackBus = session.Buses[0];
            Assert.Equal("Вход", playbackBus.Name);
            Assert.Equal("input", playbackBus.Kind);
            Assert.False(playbackBus.IsVirtual);

            session.Seek(TimeSpan.FromSeconds(0.2));

            Assert.InRange(session.Position.TotalSeconds, 0.19, 0.21);
            Assert.True(playbackBus.RawBuffer.Count > 0);

            session.Play();
            Thread.Sleep(150);

            Assert.True(session.IsPlaying);
            Assert.True(session.Position > TimeSpan.FromSeconds(0.2));

            var snapshot = playbackBus.RawBuffer.Snapshot(1024, 0);
            Assert.Contains(snapshot, value => Math.Abs(value) > 1e-6);

            session.Stop();

            Assert.False(session.IsPlaying);
            Assert.Equal(TimeSpan.Zero, session.Position);
            Assert.Equal(0, playbackBus.RawBuffer.Count);
            Assert.Equal(0, playbackBus.DemodBuffer.Count);
        }
        finally
        {
            session?.Dispose();
            recorder?.Dispose();
            bus?.Dispose();
            DeleteDirectory(root);
        }
    }

    private static float[] GenerateSamples(double frequency, double amplitude, int sampleRate, int frames)
    {
        var samples = new float[frames];

        for (var i = 0; i < frames; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        }

        return samples;
    }

    private static SignalBlock MakeBlock(double frequency, double amplitude, int sampleRate, int frames)
    {
        return new SignalBlock(GenerateSamples(frequency, amplitude, sampleRate, frames), 1, sampleRate);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "vibroanalyzer-tests",
            Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
            {
                System.IO.Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
