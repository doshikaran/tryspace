using System;

namespace Tryscape.Application.Creation
{
    /// <summary>
    /// Converts captured microphone samples to the R0 upload format (S13.3): PCM WAV, mono, 16 kHz, 16-bit, at most
    /// 15 s. Pure and bounded: input is truncated to the maximum duration before resampling.
    /// </summary>
    public static class AudioPreparation
    {
        /// <summary>RMS below this (full scale = 1.0) is treated as silence and is not uploaded.</summary>
        public const double SilenceRms = 0.003;

        /// <summary>Recordings shorter than this are rejected as accidental taps.</summary>
        public const double MinSeconds = 0.4;

        /// <summary>Interleaved float samples (any channel count and rate) to 16 kHz mono 16-bit.</summary>
        public static AudioClipData Prepare(float[] interleaved, int channels, int sampleRate)
        {
            if (interleaved == null)
                throw new ArgumentNullException(nameof(interleaved));
            if (channels < 1 || channels > 8)
                throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 8000 || sampleRate > 192000)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            var frames = Math.Min(interleaved.Length / channels, sampleRate * AudioClipData.MaxSeconds);
            if (frames == 0)
                throw new ArgumentException("No audio frames", nameof(interleaved));

            // Downmix to mono.
            var mono = new double[frames];
            for (var f = 0; f < frames; f++)
            {
                double sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += interleaved[f * channels + c];
                mono[f] = sum / channels;
            }

            // Resample. For downsampling, each output sample averages the input span it covers (a box low-pass that
            // limits aliasing); for upsampling it interpolates linearly.
            var target = AudioClipData.SampleRateHz;
            var outFrames = (int)Math.Max(1, Math.Min((long)frames * target / sampleRate, target * AudioClipData.MaxSeconds));
            var output = new short[outFrames];
            var ratio = (double)sampleRate / target;
            for (var i = 0; i < outFrames; i++)
            {
                double value;
                if (ratio > 1)
                {
                    var start = Math.Min(frames - 1, (int)(i * ratio));
                    var end = Math.Min(frames, Math.Max(start + 1, (int)((i + 1) * ratio)));
                    double sum = 0;
                    for (var k = start; k < end; k++)
                        sum += mono[k];
                    value = sum / (end - start);
                }
                else
                {
                    var position = i * ratio;
                    var k = (int)position;
                    var next = Math.Min(k + 1, frames - 1);
                    value = mono[k] + (mono[next] - mono[k]) * (position - k);
                }
                var clamped = Math.Max(-1.0, Math.Min(1.0, value));
                output[i] = (short)Math.Round(clamped * short.MaxValue);
            }
            return new AudioClipData(output);
        }

        public static double Rms(AudioClipData clip)
        {
            double sum = 0;
            foreach (var s in clip.Samples)
            {
                var v = s / (double)short.MaxValue;
                sum += v * v;
            }
            return Math.Sqrt(sum / clip.Samples.Length);
        }

        /// <summary>RIFF/WAVE PCM: 44-byte header plus little-endian 16-bit samples.</summary>
        public static byte[] EncodeWav(AudioClipData clip)
        {
            if (clip == null)
                throw new ArgumentNullException(nameof(clip));
            var dataBytes = clip.Samples.Length * 2;
            var wav = new byte[44 + dataBytes];
            void Ascii(int at, string s)
            {
                for (var i = 0; i < 4; i++)
                    wav[at + i] = (byte)s[i];
            }
            void U32(int at, int v)
            {
                wav[at] = (byte)v;
                wav[at + 1] = (byte)(v >> 8);
                wav[at + 2] = (byte)(v >> 16);
                wav[at + 3] = (byte)(v >> 24);
            }
            void U16(int at, int v)
            {
                wav[at] = (byte)v;
                wav[at + 1] = (byte)(v >> 8);
            }
            Ascii(0, "RIFF");
            U32(4, 36 + dataBytes);
            Ascii(8, "WAVE");
            Ascii(12, "fmt ");
            U32(16, 16);
            U16(20, 1); // PCM
            U16(22, 1); // mono
            U32(24, AudioClipData.SampleRateHz);
            U32(28, AudioClipData.SampleRateHz * 2);
            U16(32, 2);
            U16(34, 16);
            Ascii(36, "data");
            U32(40, dataBytes);
            for (var i = 0; i < clip.Samples.Length; i++)
                U16(44 + i * 2, clip.Samples[i]);
            return wav;
        }
    }

    public enum MicrophonePermission
    {
        NotDetermined,
        Granted,
        Denied,
    }

    /// <summary>
    /// Platform microphone port. The Unity adapter wraps <c>UnityEngine.Microphone</c> and Android runtime permission;
    /// tests use fakes. Samples stay in memory and are never written to storage.
    /// </summary>
    public interface IMicrophone
    {
        /// <summary>A capture device exists.</summary>
        bool IsAvailable { get; }

        MicrophonePermission Permission { get; }

        /// <summary>Asks the platform; the callback runs on the main thread with the decision.</summary>
        void RequestPermission(Action<bool> onResult);

        /// <summary>Starts capture for at most <paramref name="maxSeconds"/>; false if the device could not start.</summary>
        bool Start(int maxSeconds);

        /// <summary>False if capture stopped unexpectedly (device removed, permission revoked).</summary>
        bool IsCapturing { get; }

        /// <summary>Stops capture and returns the interleaved samples recorded so far (empty if none).</summary>
        float[] StopAndRead(out int channels, out int sampleRate);

        /// <summary>Stops capture and drops the samples.</summary>
        void Discard();
    }

    public enum RecorderState
    {
        Idle,
        RequestingPermission,
        Recording,
        Failed,
    }

    public enum RecorderFailure
    {
        None,
        Unavailable,
        PermissionDenied,
        DeviceLost,
        TooShort,
        Silence,
        Interrupted,
    }

    /// <summary>
    /// One bounded recording (D03.1, S10.1): availability and permission check, then capture with elapsed time. Capture
    /// stops automatically at <see cref="AudioClipData.MaxSeconds"/>; Stop prepares the audio, Cancel or focus loss
    /// discards it. A completed recording is raised once through <see cref="Completed"/>; the recorder keeps no copy.
    /// </summary>
    public sealed class VoiceRecorder
    {
        readonly IMicrophone _microphone;
        int _attempt;

        public VoiceRecorder(IMicrophone microphone) =>
            _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));

        public RecorderState State { get; private set; } = RecorderState.Idle;
        public RecorderFailure Failure { get; private set; }
        public double ElapsedSeconds { get; private set; }
        public int MaxSeconds => AudioClipData.MaxSeconds;
        public bool StoppedAtLimit { get; private set; }

        public event Action Changed;
        public event Action<AudioClipData> Completed;

        public void Begin()
        {
            if (State == RecorderState.Recording || State == RecorderState.RequestingPermission)
                return;
            Failure = RecorderFailure.None;
            ElapsedSeconds = 0;
            StoppedAtLimit = false;
            if (!_microphone.IsAvailable)
            {
                Fail(RecorderFailure.Unavailable);
                return;
            }
            switch (_microphone.Permission)
            {
                case MicrophonePermission.Granted:
                    StartCapture();
                    return;
                case MicrophonePermission.Denied:
                    Fail(RecorderFailure.PermissionDenied);
                    return;
            }
            var attempt = ++_attempt;
            State = RecorderState.RequestingPermission;
            Raise();
            _microphone.RequestPermission(granted =>
            {
                if (attempt != _attempt || State != RecorderState.RequestingPermission)
                    return; // cancelled while the dialog was open
                if (granted)
                    StartCapture();
                else
                    Fail(RecorderFailure.PermissionDenied);
            });
        }

        /// <summary>Advances elapsed time; detects device loss; stops at the maximum duration.</summary>
        public void Tick(double deltaSeconds)
        {
            if (State != RecorderState.Recording)
                return;
            if (!_microphone.IsCapturing)
            {
                _microphone.Discard();
                Fail(RecorderFailure.DeviceLost);
                return;
            }
            ElapsedSeconds = Math.Min(MaxSeconds, ElapsedSeconds + Math.Max(0, deltaSeconds));
            if (ElapsedSeconds >= MaxSeconds)
            {
                StoppedAtLimit = true;
                Stop();
                return;
            }
            Raise();
        }

        public void Stop()
        {
            if (State != RecorderState.Recording)
                return;
            var samples = _microphone.StopAndRead(out var channels, out var rate);
            State = RecorderState.Idle;
            if (samples == null || channels < 1 || rate <= 0 || samples.Length / channels < rate * AudioPreparation.MinSeconds)
            {
                Fail(RecorderFailure.TooShort);
                return;
            }
            var clip = AudioPreparation.Prepare(samples, channels, rate);
            Array.Clear(samples, 0, samples.Length);
            if (AudioPreparation.Rms(clip) < AudioPreparation.SilenceRms)
            {
                Fail(RecorderFailure.Silence);
                return;
            }
            Raise();
            Completed?.Invoke(clip);
        }

        /// <summary>User cancel: stops capture and drops the audio.</summary>
        public void Cancel()
        {
            _attempt++;
            if (State == RecorderState.Recording)
                _microphone.Discard();
            State = RecorderState.Idle;
            Failure = RecorderFailure.None;
            Raise();
        }

        /// <summary>Focus loss (S05.3): stop immediately and drop the audio.</summary>
        public void Suspend()
        {
            _attempt++;
            if (State == RecorderState.Recording || State == RecorderState.RequestingPermission)
            {
                if (State == RecorderState.Recording)
                    _microphone.Discard();
                Fail(RecorderFailure.Interrupted);
            }
        }

        void StartCapture()
        {
            if (!_microphone.Start(MaxSeconds))
            {
                Fail(RecorderFailure.Unavailable);
                return;
            }
            State = RecorderState.Recording;
            ElapsedSeconds = 0;
            Raise();
        }

        void Fail(RecorderFailure failure)
        {
            Failure = failure;
            State = RecorderState.Failed;
            Raise();
        }

        void Raise() => Changed?.Invoke();
    }
}
