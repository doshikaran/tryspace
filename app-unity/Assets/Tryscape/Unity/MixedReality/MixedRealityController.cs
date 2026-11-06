using System;
using System.Collections.Generic;
using Tryscape.Application.Placement;
using Tryscape.Unity.Experience;
using UnityEngine;

namespace Tryscape.Unity.MixedReality
{
    public enum PlacementMode
    {
        /// <summary>Floating at seated height in front of the user (no room data, or chosen by the user).</summary>
        Manual,

        /// <summary>On a real horizontal surface from the headset's scene model.</summary>
        Surface,
    }

    /// <summary>
    /// Passthrough and real-surface placement (technical-spec S11, AC33/AC34). Passthrough is the default background;
    /// the virtual environment is its accessible alternative. Placement asks the scene model for horizontal surfaces,
    /// ranks them with <see cref="SurfacePlacement"/> and moves only the tabletop root. Every failure falls back to
    /// manual placement with a plain explanation; the rehearsal never waits on a room scan.
    /// </summary>
    public sealed class MixedRealityController : MonoBehaviour
    {
        [SerializeField] OVRPassthroughLayer _passthroughLayer;
        [SerializeField] Camera _camera;
        [SerializeField] GameObject _virtualEnvironment;
        [SerializeField] TabletopController _tabletop;
        [SerializeField] Color _virtualBackground = new Color(0.18f, 0.19f, 0.22f, 1f);

        ISurfaceSource _source = new MrukSurfaceSource();
        IReadOnlyList<SurfaceCandidate> _ranked = Array.Empty<SurfaceCandidate>();
        int _surfaceIndex = -1;
        int _scanId;
        bool _passthroughOn = true;

        public event Action Changed;

        public bool PassthroughOn => _passthroughOn;
        public PlacementMode Placement { get; private set; } = PlacementMode.Manual;
        public SurfaceScanStatus? LastScanStatus { get; private set; }
        public bool Scanning { get; private set; }
        public int SurfaceCount => _ranked.Count;
        public SurfaceCandidate? CurrentSurface =>
            Placement == PlacementMode.Surface && _surfaceIndex >= 0 && _surfaceIndex < _ranked.Count ? _ranked[_surfaceIndex] : (SurfaceCandidate?)null;

        public void Configure(OVRPassthroughLayer passthroughLayer, Camera camera, GameObject virtualEnvironment,
            TabletopController tabletop)
        {
            _passthroughLayer = passthroughLayer;
            _camera = camera;
            _virtualEnvironment = virtualEnvironment;
            _tabletop = tabletop;
        }

        /// <summary>Test seam: replaces the MR Utility Kit adapter with a fixed room.</summary>
        public void UseSurfaceSource(ISurfaceSource source) => _source = source ?? throw new ArgumentNullException(nameof(source));

        /// <summary>Passthrough on (real room visible) or off (neutral virtual background). Presentation only.</summary>
        public void SetPassthrough(bool on)
        {
            _passthroughOn = on;
            var manager = OVRManager.instance;
            if (manager != null)
                manager.isInsightPassthroughEnabled = on;
            if (_passthroughLayer != null)
                _passthroughLayer.enabled = on;
            if (_camera != null)
            {
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = on ? Color.clear : _virtualBackground;
            }
            if (_virtualEnvironment != null)
                _virtualEnvironment.SetActive(!on);
            Changed?.Invoke();
        }

        /// <summary>
        /// Loads room data and places the tabletop on the nearest suitable surface. A newer scan, or a manual placement
        /// made while waiting, makes this scan's result obsolete; it is then discarded.
        /// </summary>
        public async void PlaceOnSurface(bool allowSpaceSetup = false)
        {
            var id = ++_scanId;
            Scanning = true;
            Changed?.Invoke();
            SurfaceScan scan;
            try
            {
                scan = await _source.ScanAsync(allowSpaceSetup);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tryscape] Surface scan failed: {e.GetType().Name}");
                scan = SurfaceScan.Failed(SurfaceScanStatus.Unavailable);
            }
            if (this == null || id != _scanId)
                return; // destroyed, superseded or cancelled by a manual placement
            Scanning = false;
            ApplyScan(scan);
        }

        void ApplyScan(SurfaceScan scan)
        {
            var head = Head();
            _ranked = scan.Status == SurfaceScanStatus.Found
                ? SurfacePlacement.Rank(scan.Candidates, scan.FloorY, head.x, head.z)
                : Array.Empty<SurfaceCandidate>();
            LastScanStatus = scan.Status == SurfaceScanStatus.Found && _ranked.Count == 0
                ? SurfaceScanStatus.NoSuitableSurface
                : scan.Status;
            if (_ranked.Count > 0)
            {
                PlaceOn(0);
            }
            else
            {
                _surfaceIndex = -1;
                Placement = PlacementMode.Manual;
                _tabletop?.Recenter();
                Changed?.Invoke();
            }
        }

        /// <summary>Moves to the next qualifying surface found by the last scan, wrapping around.</summary>
        public bool NextSurface()
        {
            if (_ranked.Count == 0)
                return false;
            PlaceOn((_surfaceIndex + 1) % _ranked.Count);
            return true;
        }

        /// <summary>Manual placement in front of the user; also cancels a scan in progress.</summary>
        public void PlaceManually()
        {
            _scanId++;
            Scanning = false;
            Placement = PlacementMode.Manual;
            _surfaceIndex = -1;
            _tabletop?.Recenter();
            Changed?.Invoke();
        }

        /// <summary>Re-places on the current surface (after a view reset); no-op when floating.</summary>
        public void ReapplySurface()
        {
            if (CurrentSurface != null)
                PlaceOn(_surfaceIndex);
        }

        void PlaceOn(int index)
        {
            _surfaceIndex = index;
            Placement = PlacementMode.Surface;
            var head = Head();
            var pose = SurfacePlacement.PoseOn(_ranked[index], head.x, head.z);
            _tabletop?.PlaceAt(new Vector3((float)pose.X, (float)pose.Y, (float)pose.Z), (float)pose.YawDeg);
            Changed?.Invoke();
        }

        /// <summary>One line for the Settings card.</summary>
        public string StatusText
        {
            get
            {
                var background = _passthroughOn ? "Passthrough: on (your room is visible)." : "Passthrough: off (virtual room).";
                string placement;
                if (Scanning)
                    placement = "Looking for a table or desk near you...";
                else if (CurrentSurface is SurfaceCandidate s)
                    placement = $"Placed on a {FriendlyLabel(s.Label)} ({_surfaceIndex + 1} of {_ranked.Count} surfaces). " +
                                "Placement is view only; it does not measure your room.";
                else if (LastScanStatus is SurfaceScanStatus status && status != SurfaceScanStatus.Found)
                    placement = SurfacePlacement.Describe(status);
                else
                    placement = "Floating in front of you.";
                return background + "\n" + placement;
            }
        }

        static string FriendlyLabel(string label) => label switch
        {
            "TABLE" => "table",
            "COUCH" => "couch",
            "STORAGE" => "cabinet",
            "BED" => "bed",
            _ => "surface",
        };

        Vector3 Head()
        {
            var camera = _camera != null ? _camera : Camera.main;
            return camera != null ? camera.transform.position : Vector3.zero;
        }
    }
}
