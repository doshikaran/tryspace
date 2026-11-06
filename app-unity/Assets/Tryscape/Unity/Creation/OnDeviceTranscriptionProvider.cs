using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tryscape.Application.Creation;
using UnityEngine;
using UnityEngine.Networking;

namespace Tryscape.Unity.Creation
{
    /// <summary>The bundled speech model (decision D12): file name, size and SHA-256 checked before every first load.</summary>
    public static class SpeechModel
    {
        public const string FileName = "ggml-base.en.bin";
        public const long SizeBytes = 147964211;
        public const string Sha256 = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002";
        public const string ProviderId = "whisper_cpp_base_en_on_device";
        public const string StreamingFolder = "asr";

        public static string StreamingPath => Path.Combine(UnityEngine.Application.streamingAssetsPath, StreamingFolder, FileName);
    }

    /// <summary>P/Invoke surface of native/whisper_shim/tryscape_whisper.c (ABI version 1).</summary>
    static class WhisperNative
    {
        const string Lib = "tryscape_whisper";
        public const int AbiVersion = 2;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tw_abi_version();

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr tw_load([MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int tw_transcribe(IntPtr handle, float[] samples, int sampleCount, int threads,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, byte[] output, int outputCapacity);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void tw_abort(IntPtr handle);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void tw_free(IntPtr handle);
    }

    /// <summary>
    /// On-device speech recognition (technical-spec S10.2): whisper.cpp in the APK, no network. The model is copied
    /// out of the APK and verified on first voice use, then loaded on a worker thread; transcription also runs on a
    /// worker thread, so rendering never waits. Cancellation aborts the native run. Audio exists only in memory.
    /// A missing, corrupt or unloadable model is a typed <c>asr_unavailable</c> failure, never a crash.
    /// </summary>
    public sealed class OnDeviceTranscriptionProvider : ITranscriptionProvider, IDisposable
    {
        const int OutputCapacity = 8192;

        /// <summary>
        /// Domain vocabulary for whisper's initial prompt (recognition bias only; the interpreter still decides). It names
        /// the Moving Day terms whisper otherwise mishears, such as "parked south".
        /// </summary>
        public const string VocabularyPrompt =
            "Moving Day room description. Room size in metres, for example six by five metres. Sofa delivery. " +
            "The console table is in the way, or parked south. Direct approach or northern approach.";

        readonly Func<CancellationToken, Task<string>> _locateModel;
        readonly int _threads;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        IntPtr _handle = IntPtr.Zero;
        volatile bool _disposed;
        volatile bool _releaseRequested;

        /// <param name="locateModel">Returns a verified local model path, or null. Defaults to <see cref="BundledModel"/>.</param>
        public OnDeviceTranscriptionProvider(Func<CancellationToken, Task<string>> locateModel = null, int threads = 4)
        {
            _locateModel = locateModel ?? BundledModel.LocateAsync;
            _threads = threads;
        }

        public bool Loaded => _handle != IntPtr.Zero;

        /// <summary>True when the native library is present and has the expected ABI (false in builds without it).</summary>
        public static bool NativeAvailable
        {
            get
            {
                try
                {
                    return WhisperNative.tw_abi_version() == WhisperNative.AbiVersion;
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
                {
                    return false;
                }
            }
        }

        public async Task<TranscriptionOutcome> TranscribeAsync(AudioClipData audio, CancellationToken cancellationToken)
        {
            if (audio == null)
                throw new ArgumentNullException(nameof(audio));
            if (_disposed)
                return TranscriptionOutcome.Failed(ProposalFailure.Unavailable, "asr_unavailable");
            var started = DateTime.UtcNow;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (!await EnsureLoadedAsync(cancellationToken).ConfigureAwait(true))
                    return TranscriptionOutcome.Failed(ProposalFailure.Unavailable, "asr_unavailable");
                cancellationToken.ThrowIfCancellationRequested();

                var samples = new float[audio.Samples.Length];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = audio.Samples[i] / 32768f;
                var handle = _handle;
                using (cancellationToken.Register(() => WhisperNative.tw_abort(handle)))
                {
                    var (code, text) = await Task.Run(() =>
                    {
                        var output = new byte[OutputCapacity];
                        var n = WhisperNative.tw_transcribe(handle, samples, samples.Length, _threads, VocabularyPrompt, output, output.Length);
                        return (n, n > 0 ? Encoding.UTF8.GetString(output, 0, n) : "");
                    }).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (code == -3)
                        throw new OperationCanceledException(cancellationToken);
                    if (code < 0)
                        return TranscriptionOutcome.Failed(ProposalFailure.JobFailed, "asr_failed", retryable: true);
                    return TranscriptionOutcome.Of(new TranscriptionResult(Clean(text), SpeechModel.ProviderId, "en",
                        DateTime.UtcNow - started, null));
                }
            }
            finally
            {
                if (_releaseRequested || _disposed)
                    FreeHandle(); // a Release() arrived while this run was in progress
                _gate.Release();
            }
        }

        /// <summary>Whisper marks non-speech as bracketed tags such as [BLANK_AUDIO]; those are not words.</summary>
        public static string Clean(string text)
        {
            var sb = new StringBuilder(text.Length);
            var depth = 0;
            foreach (var c in text)
            {
                if (c == '[' || c == '(') depth++;
                else if ((c == ']' || c == ')') && depth > 0) depth--;
                else if (depth == 0) sb.Append(c);
            }
            return string.Join(" ", sb.ToString().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        async Task<bool> EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_handle != IntPtr.Zero)
                return true;
            if (!NativeAvailable)
                return false;
            var path = await _locateModel(cancellationToken).ConfigureAwait(true);
            if (path == null)
                return false;
            var handle = await Task.Run(() => WhisperNative.tw_load(path)).ConfigureAwait(true);
            if (handle == IntPtr.Zero)
                return false;
            if (_disposed)
            {
                WhisperNative.tw_free(handle);
                return false;
            }
            _handle = handle;
            return true;
        }

        /// <summary>
        /// Frees the model (suspension, memory pressure, shutdown); it reloads on the next voice use. Never blocks: if a
        /// transcription is running it is aborted, and the model is freed as soon as that run returns.
        /// </summary>
        public void Release()
        {
            if (_handle == IntPtr.Zero)
                return;
            if (_gate.Wait(0))
            {
                try
                {
                    FreeHandle();
                }
                finally
                {
                    _gate.Release();
                }
                return;
            }
            _releaseRequested = true;
            WhisperNative.tw_abort(_handle);
        }

        void FreeHandle()
        {
            _releaseRequested = false;
            if (_handle == IntPtr.Zero)
                return;
            WhisperNative.tw_free(_handle);
            _handle = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Release();
        }
    }

    /// <summary>
    /// Locates the bundled model. In the Editor it is read in place from StreamingAssets. On Android, StreamingAssets
    /// lives inside the APK, so the model is copied once into app storage. Either way its size and SHA-256 must match
    /// <see cref="SpeechModel"/> before it is used; a mismatched copy is deleted.
    /// </summary>
    public static class BundledModel
    {
        static string _verifiedPath;

        public static async Task<string> LocateAsync(CancellationToken cancellationToken)
        {
            if (_verifiedPath != null && File.Exists(_verifiedPath))
                return _verifiedPath;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                var dir = Path.Combine(UnityEngine.Application.persistentDataPath, SpeechModel.StreamingFolder);
                Directory.CreateDirectory(dir);
                var local = Path.Combine(dir, SpeechModel.FileName);
                if (!await Task.Run(() => Verify(local)).ConfigureAwait(true))
                {
                    var tmp = local + ".tmp";
                    if (File.Exists(tmp)) File.Delete(tmp);
                    using (var request = UnityWebRequest.Get(SpeechModel.StreamingPath))
                    {
                        request.downloadHandler = new DownloadHandlerFile(tmp) { removeFileOnAbort = true };
                        var op = request.SendWebRequest();
                        while (!op.isDone)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                request.Abort();
                                cancellationToken.ThrowIfCancellationRequested();
                            }
                            await Task.Yield();
                        }
                        if (request.result != UnityWebRequest.Result.Success)
                            return null;
                    }
                    if (File.Exists(local)) File.Delete(local);
                    File.Move(tmp, local);
                    if (!await Task.Run(() => Verify(local)).ConfigureAwait(true))
                    {
                        File.Delete(local);
                        return null;
                    }
                }
                _verifiedPath = local;
#else
                var path = SpeechModel.StreamingPath;
                if (!await Task.Run(() => Verify(path)).ConfigureAwait(true))
                    return null;
                _verifiedPath = path;
#endif
                return _verifiedPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tryscape] Speech model unavailable: {e.GetType().Name}");
                return null;
            }
        }

        /// <summary>Size and SHA-256 check; false for a missing, truncated or altered file.</summary>
        public static bool Verify(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != SpeechModel.SizeBytes)
                return false;
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var hex = new StringBuilder(64);
                foreach (var b in hash)
                    hex.Append(b.ToString("x2"));
                return hex.ToString() == SpeechModel.Sha256;
            }
        }
    }
}
