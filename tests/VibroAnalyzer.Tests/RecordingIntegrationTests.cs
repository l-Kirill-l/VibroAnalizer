using VibroAnalyzer.Audio;
using VibroAnalyzer.Core.Engine;
using VibroAnalyzer.Core.Playback;
using VibroAnalyzer.Core.Recording;
using VibroAnalyzer.Core.Session;

namespace VibroAnalyzer.Tests;

public sealed class RecordingIntegrationTests
{
    [Fact]
    public void EngineRecordsSessionAndPlaybackLoadsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibroanalyzer-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var context = SessionContext.FromArgs(new[] { "--session-dir", root });
            using var engine = new AudioEngine(AudioBackendFactory.Create("Демо"), context);

            var inputDevice = engine.Devices.First(device => device.Id == "sim-input");
            var outputDevice = engine.Devices.First(device => device.Id == "sim-output");

            engine.AddInput(inputDevice, 0, "Записываемый вход");
            engine.AddOutput(outputDevice, 0, "Записываемый выход");
            engine.Start();

            Assert.True(
                SpinWait.SpinUntil(
                    () => engine.Inputs[0].RawBuffer.Count > 4800,
                    TimeSpan.FromSeconds(2)),
                "Демо-вход не успел заполниться.");

            engine.StartRecording("integration", includeOutputs: true);
            Assert.True(engine.IsRecording);

            Thread.Sleep(600);

            var recordingDirectory = engine.StopRecording();
            engine.Stop();

            Assert.NotNull(recordingDirectory);
            Assert.True(System.IO.Directory.Exists(recordingDirectory));
            Assert.True(File.Exists(Path.Combine(recordingDirectory, "manifest.json")));

            var manifest = SessionRecorderManifestReader.Read(recordingDirectory);
            Assert.True(manifest.SampleRate > 0);
            Assert.NotEmpty(manifest.Buses);
            Assert.Contains(manifest.Buses, bus => bus.Kind == "input");
            Assert.Contains(manifest.Buses, bus => bus.Kind == "output");

            using var playback = PlaybackSession.Load(recordingDirectory);
            Assert.NotEmpty(playback.Buses);
            Assert.True(playback.Duration > TimeSpan.Zero);

            playback.Seek(TimeSpan.FromMilliseconds(Math.Min(200, playback.Duration.TotalMilliseconds / 2)));
            playback.Play();
            Thread.Sleep(120);
            playback.Pause();

            Assert.True(playback.Buses[0].RawBuffer.Count > 0);
        }
        finally
        {
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, true);
            }
        }
    }

    private static class SessionRecorderManifestReader
    {
        public static RecordingManifest Read(string directory)
        {
            var path = Path.Combine(directory, "manifest.json");
            var json = File.ReadAllText(path);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<RecordingManifest>(
                json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });

            return manifest ?? throw new InvalidDataException("manifest.json пуст.");
        }
    }
}
