using System;
using Oculus.Interaction.Input;
using UnityEngine;

namespace Tryscape.Unity.MixedReality
{
    /// <summary>Which pose drives look-and-pinch selection (technical-spec S11, AC35).</summary>
    public enum GazeSource
    {
        /// <summary>Eye tracking where the headset has it and the user allowed it; otherwise hand rays are the pointer.</summary>
        Eyes,

        /// <summary>Head direction stands in for eye gaze, so look-and-pinch also works on headsets without eye tracking.</summary>
        Head,
    }

    /// <summary>
    /// Owns the Interaction SDK gaze source built into the scene (EyeGaze -> GazeConecaster -> GazeInteractor; the
    /// canvas and console carry gaze interactables with ray fallback). Eye-tracking permission is requested only on
    /// headsets that support eye tracking. Gaze hover only highlights; a pinch selects, and dwell never commits.
    /// </summary>
    public sealed class GazeController : MonoBehaviour
    {
        const string HeadGazeKey = "tryscape.gaze.head";

        [SerializeField] EyeGaze _eyeGaze;

        public event Action Changed;

        public GazeSource Source { get; private set; } = GazeSource.Eyes;
        public bool Wired => _eyeGaze != null;

        public void Configure(EyeGaze eyeGaze) => _eyeGaze = eyeGaze;

        void Start()
        {
            SetSource(SafePrefs.GetBool(HeadGazeKey) ? GazeSource.Head : GazeSource.Eyes, persist: false);
            if (EyeTrackingSupported && !EyeTrackingPermitted)
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.EyeTracking });
        }

        public static bool EyeTrackingSupported
        {
            get
            {
                try
                {
                    return OVRPlugin.eyeTrackingSupported;
                }
                catch (Exception)
                {
                    return false; // no runtime (Editor without XR, batch tests)
                }
            }
        }

        public static bool EyeTrackingPermitted =>
            OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.EyeTracking);

        /// <summary>True while gaze, not the hand ray, is the active pointer for gaze-enabled targets.</summary>
        public bool GazeActive => _eyeGaze != null && _eyeGaze.isActiveAndEnabled && _eyeGaze.Active;

        public void Toggle() => SetSource(Source == GazeSource.Eyes ? GazeSource.Head : GazeSource.Eyes, persist: true);

        public void SetSource(GazeSource source, bool persist = true)
        {
            Source = source;
            if (_eyeGaze != null)
                _eyeGaze.EmulateGazeWithCameraPose = source == GazeSource.Head;
            if (persist)
                SafePrefs.SetBool(HeadGazeKey, source == GazeSource.Head);
            Changed?.Invoke();
        }

        public string StatusText
        {
            get
            {
                if (!Wired)
                    return "Gaze: not available in this build.";
                if (Source == GazeSource.Head)
                    return "Gaze: head. Look at a button or the console and pinch to select it.";
                if (EyeTrackingSupported && EyeTrackingPermitted)
                    return "Gaze: eyes. Look at a button or the console and pinch to select it.";
                return EyeTrackingSupported
                    ? "Gaze: eye tracking not allowed, so the hand ray is the pointer. Switch to head gaze to look and pinch."
                    : "Gaze: this headset has no eye tracking, so the hand ray is the pointer. Switch to head gaze to look and pinch.";
            }
        }
    }
}
