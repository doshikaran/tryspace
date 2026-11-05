using System;
using Tryscape.Application.Creation;
using UnityEngine;

namespace Tryscape.Unity.Creation
{
    /// <summary>
    /// <see cref="IMicrophone"/> over <c>UnityEngine.Microphone</c> and the platform permission API. Capture uses the
    /// default device into a single non-looping in-memory clip (max + 1 s), so recording is bounded even if Stop is
    /// never called. The clip is destroyed after reading or discarding; nothing is written to storage.
    /// <para>
    /// Platform behaviour:
    /// <list type="bullet">
    /// <item>Android/Quest: RECORD_AUDIO runtime permission via <c>UnityEngine.Android.Permission</c>. A permanent denial
    /// is remembered for the session. Revocation shows up as capture stopping.</item>
    /// <item>Editor/desktop: the OS prompts on first capture (macOS asks for the Unity Editor); Unity reports authorisation
    /// as granted.</item>
    /// </list>
    /// Verified only through tests with fakes and in the Editor. Real device permission, revocation and capture
    /// quality are manual Quest checks.
    /// </para>
    /// </summary>
    public sealed class UnityMicrophone : IMicrophone
    {
        const int PreferredRate = AudioClipData.SampleRateHz;
        AudioClip _clip;
        int _rate;
        bool _deniedPermanently;

        public bool IsAvailable => Microphone.devices != null && Microphone.devices.Length > 0;

        public MicrophonePermission Permission
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                    return MicrophonePermission.Granted;
                return _deniedPermanently ? MicrophonePermission.Denied : MicrophonePermission.NotDetermined;
#else
                if (_deniedPermanently)
                    return MicrophonePermission.Denied;
                return UnityEngine.Application.HasUserAuthorization(UserAuthorization.Microphone)
                    ? MicrophonePermission.Granted
                    : MicrophonePermission.NotDetermined;
#endif
            }
        }

        public void RequestPermission(Action<bool> onResult)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => onResult(true);
            callbacks.PermissionDenied += _ => onResult(false);
            callbacks.PermissionDeniedAndDontAskAgain += _ =>
            {
                _deniedPermanently = true;
                onResult(false);
            };
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
#else
            var request = UnityEngine.Application.RequestUserAuthorization(UserAuthorization.Microphone);
            request.completed += _ =>
            {
                var granted = UnityEngine.Application.HasUserAuthorization(UserAuthorization.Microphone);
                if (!granted)
                    _deniedPermanently = true;
                onResult(granted);
            };
#endif
        }

        public bool Start(int maxSeconds)
        {
            Release();
            if (!IsAvailable)
                return false;
            Microphone.GetDeviceCaps(null, out var min, out var max);
            _rate = min == 0 && max == 0 ? PreferredRate : Mathf.Clamp(PreferredRate, min, max);
            _clip = Microphone.Start(null, false, maxSeconds + 1, _rate);
            if (_clip == null)
                return false;
            return true;
        }

        public bool IsCapturing => _clip != null && Microphone.IsRecording(null);

        public float[] StopAndRead(out int channels, out int sampleRate)
        {
            channels = 1;
            sampleRate = _rate;
            if (_clip == null)
                return Array.Empty<float>();
            var position = Microphone.GetPosition(null);
            var stillRecording = Microphone.IsRecording(null);
            Microphone.End(null);
            channels = _clip.channels;
            sampleRate = _clip.frequency;
            // A non-looping clip that reached its end reports position 0; then the whole clip is valid.
            var frames = position > 0 ? position : stillRecording ? 0 : _clip.samples;
            var data = new float[frames * channels];
            if (frames > 0)
                _clip.GetData(data, 0);
            Release();
            return data;
        }

        public void Discard()
        {
            if (Microphone.IsRecording(null))
                Microphone.End(null);
            Release();
        }

        void Release()
        {
            if (_clip != null)
            {
                UnityEngine.Object.Destroy(_clip);
                _clip = null;
            }
        }
    }
}
