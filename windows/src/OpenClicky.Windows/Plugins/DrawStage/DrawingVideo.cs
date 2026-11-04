//
//  DrawingVideo.cs
//  OpenClicky (Windows; no Swift twin yet)
//
//  Writes a recorded drawing as an MP4 (H.264 video, AAC sound) with Media Foundation's sink
//  writer, which ships with Windows, through NAudio's bindings. The frames are the page's captures
//  (JPEGs, each with its time) cut to the drawing's box, at a steady 15 frames a second (a frame is
//  repeated until the next capture); the sound is each line's audio placed at the moment its line
//  started, mixed into one track.
//

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenClicky.Windows.Platform;

namespace OpenClicky.Windows.Plugins.DrawStage;

internal static class DrawingVideo
{
    public const int FramesPerSecond = 15;
    private const int SampleRate = 44_100;
    private const long TicksPerSecond = 10_000_000;

    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private const int MFVideoInterlace_Progressive = 2;

    /// <summary>Writes <paramref name="path"/>: <paramref name="frames"/> cut to <paramref name="crop"/> (scaled down to fit
    /// 1920 × 1080), <paramref name="length"/> seconds long, with <paramref name="voice"/> under it. <paramref name="written"/>
    /// hears how much of it is written (0 to 1).</summary>
    public static void Write(string path, IReadOnlyList<(double At, byte[] Jpeg)> frames, Rectangle crop,
        IReadOnlyList<(double At, byte[] Audio)> voice, double length, CancellationToken cancellationToken, Action<double>? written = null)
    {
        if (frames.Count == 0) throw new InvalidOperationException("no frames were captured");
        var scale = Math.Min(1, Math.Min(1920.0 / crop.Width, 1080.0 / crop.Height));
        var width = Math.Max(2, (int)(crop.Width * scale) & ~1);
        var height = Math.Max(2, (int)(crop.Height * scale) & ~1);

        MediaFoundationApi.Startup();
        var attributes = MediaFoundationApi.CreateAttributes(1);
        attributes.SetUINT32(MediaFoundationAttributes.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        if (File.Exists(path)) File.Delete(path);
        MediaFoundationInterop.MFCreateSinkWriterFromURL(path, null!, attributes, out var writer);
        var readers = new List<WaveStream>();
        try
        {
            var videoStream = AddVideoStream(writer, width, height);
            var sound = voice.Count > 0 ? MixedVoice(voice, length, readers) : null;
            var audioStream = sound is null ? -1 : AddAudioStream(writer, sound.WaveFormat);
            if (audioStream < 0) sound = null;
            writer.BeginWriting();

            using var canvas = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using var graphics = Graphics.FromImage(canvas);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var frameTicks = TicksPerSecond / FramesPerSecond;
            var frameCount = Math.Max(1, (int)Math.Ceiling(length * FramesPerSecond));
            var shown = -1;
            var latest = 0;
            long soundWritten = 0;
            var soundBuffer = new byte[sound?.WaveFormat.AverageBytesPerSecond / 4 ?? 0];
            for (var n = 0; n < frameCount; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The last capture taken by this frame's time.
                var time = n / (double)FramesPerSecond;
                while (latest + 1 < frames.Count && frames[latest + 1].At <= time) latest++;
                if (latest != shown)
                {
                    using var image = new Bitmap(new MemoryStream(frames[latest].Jpeg));
                    graphics.DrawImage(image, new Rectangle(0, 0, width, height), crop, GraphicsUnit.Pixel);
                    shown = latest;
                }
                WriteFrame(writer, videoStream, canvas, n * frameTicks, frameTicks);
                written?.Invoke((n + 1) / (double)frameCount);
                // The sound, up to the end of this frame.
                while (sound is not null && soundWritten < (n + 1) * frameTicks)
                {
                    var read = sound.Read(soundBuffer, 0, soundBuffer.Length);
                    if (read <= 0) { sound = null; break; }
                    var ticks = read * TicksPerSecond / sound.WaveFormat.AverageBytesPerSecond;
                    WriteBytes(writer, audioStream, soundBuffer, read, soundWritten, ticks);
                    soundWritten += ticks;
                }
            }
            writer.DoFinalize();
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
            Marshal.ReleaseComObject(writer);
            Marshal.ReleaseComObject(attributes);
        }
    }

    /// <summary>The voice's lines mixed into one 16-bit stereo track at 44.1 kHz, each starting at its time,
    /// <paramref name="length"/> seconds long.</summary>
    private static IWaveProvider MixedVoice(IReadOnlyList<(double At, byte[] Audio)> voice, double length, List<WaveStream> readers)
    {
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)) { ReadFully = true };
        foreach (var (at, audio) in voice)
        {
            WaveStream reader;
            try
            {
                reader = AudioPlayer.OpenReader(audio);
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or COMException or InvalidOperationException)
            {
                continue;
            }
            readers.Add(reader);
            var samples = reader.ToSampleProvider();
            if (samples.WaveFormat.Channels == 1) samples = samples.ToStereo();
            else if (samples.WaveFormat.Channels > 2) continue;
            if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);
            mixer.AddMixerInput(new OffsetSampleProvider(samples) { DelayBy = TimeSpan.FromSeconds(Math.Max(0, at)) });
        }
        return mixer.Take(TimeSpan.FromSeconds(length)).ToWaveProvider16();
    }

    private static int AddVideoStream(IMFSinkWriter writer, int width, int height)
    {
        var output = MediaFoundationApi.CreateMediaType();
        output.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
        output.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, MFVideoFormat_H264);
        output.SetUINT32(MediaFoundationAttributes.MF_MT_AVG_BITRATE, Math.Clamp(width * height * FramesPerSecond / 6, 1_500_000, 8_000_000));
        SetPicture(output, width, height);
        writer.AddStream(output, out var stream);
        var input = MediaFoundationApi.CreateMediaType();
        input.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Video);
        input.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        // Rows top to bottom, as a Bitmap holds them.
        input.SetUINT32(MF_MT_DEFAULT_STRIDE, width * 4);
        SetPicture(input, width, height);
        writer.SetInputMediaType(stream, input, null!);
        Marshal.ReleaseComObject(output);
        Marshal.ReleaseComObject(input);
        return stream;
    }

    private static void SetPicture(IMFMediaType type, int width, int height)
    {
        type.SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        type.SetUINT64(MF_MT_FRAME_SIZE, ((long)width << 32) | (uint)height);
        type.SetUINT64(MF_MT_FRAME_RATE, ((long)FramesPerSecond << 32) | 1);
        type.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, (1L << 32) | 1);
    }

    /// <summary>An AAC track for <paramref name="input"/> (PCM); -1 when this PC has no AAC encoder.</summary>
    private static int AddAudioStream(IMFSinkWriter writer, WaveFormat input)
    {
        var aac = MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_AAC, input, 128_000);
        if (aac is null) return -1;
        writer.AddStream(aac.MediaFoundationObject, out var stream);
        var pcm = MediaFoundationApi.CreateMediaTypeFromWaveFormat(input);
        writer.SetInputMediaType(stream, pcm, null!);
        Marshal.ReleaseComObject(pcm);
        return stream;
    }

    [ThreadStatic] private static byte[]? frameBytes;

    private static void WriteFrame(IMFSinkWriter writer, int stream, Bitmap canvas, long time, long duration)
    {
        var size = canvas.Width * canvas.Height * 4;
        var bits = canvas.LockBits(new Rectangle(0, 0, canvas.Width, canvas.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            // 32 bits a pixel: a row is exactly width × 4 bytes, so the rows are one block.
            if (frameBytes?.Length != size) frameBytes = new byte[size];
            Marshal.Copy(bits.Scan0, frameBytes, 0, size);
            WriteBytes(writer, stream, frameBytes, size, time, duration);
        }
        finally
        {
            canvas.UnlockBits(bits);
        }
    }

    private static void WriteBytes(IMFSinkWriter writer, int stream, byte[] data, int count, long time, long duration)
    {
        var buffer = MediaFoundationApi.CreateMemoryBuffer(count);
        buffer.Lock(out var target, out _, out _);
        try
        {
            Marshal.Copy(data, 0, target, count);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.SetCurrentLength(count);
        WriteSample(writer, stream, buffer, time, duration);
    }

    private static void WriteSample(IMFSinkWriter writer, int stream, IMFMediaBuffer buffer, long time, long duration)
    {
        var sample = MediaFoundationApi.CreateSample();
        try
        {
            sample.AddBuffer(buffer);
            sample.SetSampleTime(time);
            sample.SetSampleDuration(duration);
            writer.WriteSample(stream, sample);
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
    }
}
