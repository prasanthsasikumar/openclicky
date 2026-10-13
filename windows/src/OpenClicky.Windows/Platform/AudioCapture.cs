using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using OpenClicky.Core.Platform;

namespace OpenClicky.Windows.Platform;

/// <summary>
/// WASAPI shared-mode capture (NAudio WasapiCapture, event-driven) turned into mono float at the
/// requested rate with NAudio's WDL resampler. Frames are delivered on WASAPI's capture thread.
/// For Realtime the stream is opened with AudioCategory_Communications (IAudioClient2::SetClientProperties)
/// so Windows applies its communications processing — echo cancellation where the driver or Windows
/// Voice Clarity provides it; when that fails the plain stream is used.
/// </summary>
public sealed class AudioCapture : IAudioCapture
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int DeviceInUseHResult = unchecked((int)0x8889000A);
    private const int DeviceInvalidatedHResult = unchecked((int)0x88890004);

    private readonly object stateLock = new();
    private WasapiCapture? capture;
    private MMDevice? device;
    private SampleConverter? converter;
    private float smoothedLevel;
    private bool stopRequested;

    public event Action<AudioFrame>? FramesAvailable;
    public event Action<Exception?>? CaptureStopped;

    /// <summary>Lock-free, so a frame handler may ask while Stop() waits for the capture thread.</summary>
    public bool IsCapturing => Volatile.Read(ref capture)?.CaptureState == CaptureState.Capturing;

    public float CurrentLevel => Volatile.Read(ref smoothedLevel);

    public Task<AudioCaptureStartResult> StartAsync(AudioCaptureOptions options, CancellationToken cancellationToken = default) =>
        // WASAPI objects are created on an MTA thread-pool thread, never the UI thread.
        Task.Run(() => Start(options), cancellationToken);

    private AudioCaptureStartResult Start(AudioCaptureOptions options)
    {
        lock (stateLock)
        {
            StopLocked(raiseStopped: false);
            stopRequested = false;
            using var enumerator = new MMDeviceEnumerator();
            var wantsEchoCancellation = options.PreferEchoCancellation;
            device = ResolveDevice(enumerator, options.DeviceId, wantsEchoCancellation)
                ?? throw new AudioCaptureException(AudioCaptureFailure.NoDevice, "no microphone is connected");

            var notes = new List<string> { $"microphone {MicrophoneDevices.SafeName(device)}" };
            var echoCancellationApplied = false;
            try
            {
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: Math.Max(10, options.BufferMilliseconds));
                if (wantsEchoCancellation)
                {
                    echoCancellationApplied = TrySetCommunicationsCategory(capture, out var categoryNote);
                    notes.Add(categoryNote);
                }
                converter = new SampleConverter(capture.WaveFormat, options.SampleRate, options.Format == AudioSampleFormat.Pcm16);
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                try
                {
                    capture.StartRecording();
                }
                catch when (echoCancellationApplied)
                {
                    // The communications stream would not start: fall back to a plain one, as the Mac
                    // falls back from voice processing.
                    DisposeCaptureLocked();
                    capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: Math.Max(10, options.BufferMilliseconds));
                    converter = new SampleConverter(capture.WaveFormat, options.SampleRate, options.Format == AudioSampleFormat.Pcm16);
                    capture.DataAvailable += OnDataAvailable;
                    capture.RecordingStopped += OnRecordingStopped;
                    capture.StartRecording();
                    echoCancellationApplied = false;
                    notes.Add("communications stream unavailable, plain capture");
                }
            }
            catch (Exception exception) when (exception is not AudioCaptureException)
            {
                DisposeCaptureLocked();
                throw Classify(exception);
            }

            notes.Add($"{capture.WaveFormat.SampleRate} Hz × {capture.WaveFormat.Channels} ch → {options.SampleRate} Hz mono");
            return new AudioCaptureStartResult(string.Join(", ", notes), device.ID, MicrophoneDevices.SafeName(device), wantsEchoCancellation, echoCancellationApplied);
        }
    }

    public void Stop()
    {
        lock (stateLock) StopLocked(raiseStopped: true);
    }

    private void StopLocked(bool raiseStopped)
    {
        if (capture is null) return;
        stopRequested = true;
        try
        {
            capture.StopRecording();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
        }
        DisposeCaptureLocked();
        Volatile.Write(ref smoothedLevel, 0);
        if (raiseStopped) CaptureStopped?.Invoke(null);
    }

    private void DisposeCaptureLocked()
    {
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }
        capture = null;
        converter = null;
    }

    public void Dispose()
    {
        Stop();
        lock (stateLock)
        {
            device?.Dispose();
            device = null;
        }
    }

    private static MMDevice? ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId, bool preferCommunications)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var chosen = enumerator.GetDevice(deviceId);
                if (chosen.State == DeviceState.Active) return chosen;
                chosen.Dispose();
            }
            catch (COMException)
            {
            }
        }
        var role = preferCommunications ? Role.Communications : Role.Console;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role)) return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
        return enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : null;
    }

    /// <summary>
    /// Sets AudioCategory_Communications on the capture's IAudioClient before it is initialised.
    /// NAudio 2.2.1 keeps that client private, so it is reached by reflection (pinned package version)
    /// and asked for IAudioClient2 through our own full-vtable declaration.
    /// </summary>
    private static bool TrySetCommunicationsCategory(WasapiCapture wasapiCapture, out string note)
    {
        try
        {
            var audioClient = typeof(WasapiCapture).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(wasapiCapture);
            var audioClientInterface = audioClient?.GetType().GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(audioClient);
            if (audioClientInterface is not IAudioClient2Native audioClient2)
            {
                note = "echo cancellation unavailable (no IAudioClient2)";
                return false;
            }
            var properties = new AudioClientPropertiesNative
            {
                cbSize = (uint)Marshal.SizeOf<AudioClientPropertiesNative>(),
                bIsOffload = 0,
                eCategory = 4, // AudioCategory_Communications
                Options = 0,
            };
            var hresult = audioClient2.SetClientProperties(ref properties);
            note = hresult == 0 ? "communications stream (echo cancellation where available)" : $"echo cancellation unavailable (0x{hresult:X8})";
            return hresult == 0;
        }
        catch (Exception exception) when (exception is InvalidCastException or COMException or FieldAccessException)
        {
            note = $"echo cancellation unavailable ({exception.GetType().Name})";
            return false;
        }
    }

    private static AudioCaptureException Classify(Exception exception)
    {
        var hresult = exception.HResult;
        if (hresult == AccessDeniedHResult || exception is UnauthorizedAccessException)
        {
            return new AudioCaptureException(AudioCaptureFailure.AccessDenied, "windows is not letting openclicky use the microphone", exception);
        }
        if (hresult == DeviceInUseHResult)
        {
            return new AudioCaptureException(AudioCaptureFailure.DeviceInUse, "another app has the microphone to itself", exception);
        }
        if (hresult == DeviceInvalidatedHResult)
        {
            return new AudioCaptureException(AudioCaptureFailure.NoDevice, "the microphone was disconnected", exception);
        }
        return new AudioCaptureException(AudioCaptureFailure.Other, $"the microphone could not start ({exception.Message})", exception);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        // WASAPI's capture thread: no UI state here, only the conversion and the handlers.
        var currentConverter = converter;
        if (currentConverter is null || eventArgs.BytesRecorded == 0) return;
        var samples = currentConverter.Convert(eventArgs.Buffer, eventArgs.BytesRecorded);
        if (samples.Length == 0) return;

        double summedSquares = 0;
        foreach (var sample in samples) summedSquares += sample * sample;
        var rootMeanSquare = (float)Math.Sqrt(summedSquares / samples.Length);
        var level = Math.Clamp(rootMeanSquare * 10.2f, 0, 1);
        Volatile.Write(ref smoothedLevel, Math.Max(level, Volatile.Read(ref smoothedLevel) * 0.72f));

        byte[]? pcm16 = null;
        if (currentConverter.WantsPcm16)
        {
            pcm16 = new byte[samples.Length * 2];
            for (var index = 0; index < samples.Length; index++)
            {
                var value = (short)(Math.Clamp(samples[index], -1f, 1f) * short.MaxValue);
                pcm16[index * 2] = (byte)value;
                pcm16[index * 2 + 1] = (byte)(value >> 8);
            }
        }
        try
        {
            FramesAvailable?.Invoke(new AudioFrame(samples, pcm16, currentConverter.OutputRate, level));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"audio frame handler failed: {exception}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (stopRequested) return;
        CaptureStopped?.Invoke(eventArgs.Exception is null ? null : Classify(eventArgs.Exception));
    }

    /// <summary>Device format (float / 16 / 24 / 32-bit int, any channel count) → mono float at the output rate.</summary>
    private sealed class SampleConverter
    {
        private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

        private readonly WaveFormat inputFormat;
        private readonly bool isFloat;
        private readonly WdlResampler? resampler;

        public SampleConverter(WaveFormat inputFormat, int outputRate, bool wantsPcm16)
        {
            this.inputFormat = inputFormat;
            WantsPcm16 = wantsPcm16;
            OutputRate = outputRate;
            isFloat = inputFormat.Encoding == WaveFormatEncoding.IeeeFloat
                || (inputFormat is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);
            if (inputFormat.SampleRate != outputRate)
            {
                resampler = new WdlResampler();
                resampler.SetMode(true, 2, false);
                resampler.SetFilterParms();
                resampler.SetFeedMode(true);
                resampler.SetRates(inputFormat.SampleRate, outputRate);
            }
        }

        public int OutputRate { get; }

        public bool WantsPcm16 { get; }

        public float[] Convert(byte[] buffer, int byteCount)
        {
            var channels = Math.Max(1, inputFormat.Channels);
            var bytesPerSample = inputFormat.BitsPerSample / 8;
            var frameCount = byteCount / (bytesPerSample * channels);
            var mono = new float[frameCount];
            for (var frame = 0; frame < frameCount; frame++)
            {
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = (frame * channels + channel) * bytesPerSample;
                    sum += ReadSample(buffer, offset, bytesPerSample);
                }
                mono[frame] = sum / channels;
            }
            if (resampler is null) return mono;

            var inputNeeded = resampler.ResamplePrepare(frameCount, 1, out var inputBuffer, out var inputOffset);
            Array.Copy(mono, 0, inputBuffer, inputOffset, Math.Min(inputNeeded, frameCount));
            var maximumOutput = (int)Math.Ceiling(frameCount * (OutputRate / (double)inputFormat.SampleRate)) + 16;
            var output = new float[maximumOutput];
            var produced = resampler.ResampleOut(output, 0, Math.Min(inputNeeded, frameCount), maximumOutput, 1);
            if (produced == output.Length) return output;
            Array.Resize(ref output, produced);
            return output;
        }

        private float ReadSample(byte[] buffer, int offset, int bytesPerSample)
        {
            if (isFloat && bytesPerSample == 4) return BitConverter.ToSingle(buffer, offset);
            return bytesPerSample switch
            {
                2 => BitConverter.ToInt16(buffer, offset) / 32768f,
                3 => ((buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16))) / 8388608f,
                4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                1 => (buffer[offset] - 128) / 128f,
                _ => 0,
            };
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientPropertiesNative
{
    public uint cbSize;
    public int bIsOffload;
    public int eCategory;
    public int Options;
}

/// <summary>IAudioClient2 with the whole vtable spelled out (IAudioClient's 12 slots first) so SetClientProperties lands in the right slot.</summary>
[ComImport]
[Guid("726778CD-F60A-4eda-82DE-E47610CD78AA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient2Native
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferSize);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out int currentPadding);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint eventHandle);
    [PreserveSig] int GetService(ref Guid interfaceId, out nint service);
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(ref AudioClientPropertiesNative properties);
    [PreserveSig] int GetBufferSizeLimits(nint format, int eventDriven, out long minimumBufferDuration, out long maximumBufferDuration);
}
