using UnityEngine;

namespace Tryscape.Unity.Experience
{
    /// <summary>
    /// Presentation-only tabletop placement: zoom steps and recentre in front of the seated user (S11). Changes the
    /// root transform only, never model geometry (AC20).
    /// </summary>
    public sealed class TabletopController : MonoBehaviour
    {
        static readonly float[] Scales = { 0.06f, 0.08f, 0.1f, 0.12f, 0.15f };
        const int DefaultIndex = 2;

        [SerializeField] Vector3 _offsetFromHead = new Vector3(0f, -0.45f, 0.55f);

        int _index = DefaultIndex;

        public float CurrentScale => Scales[_index];

        /// <summary>Saved presentation setting (S12): zoom step 0-4. It never affects model geometry.</summary>
        public int ZoomStep => _index;

        int _yawDeg;

        /// <summary>Saved presentation setting (S11): tabletop orientation in quarter turns. Never model geometry.</summary>
        public int YawDeg => _yawDeg;

        /// <summary>Turns the whole tabletop by a quarter turn about its own vertical axis (presentation only).</summary>
        public void RotateView(int direction) => SetYaw(_yawDeg + (direction > 0 ? 90 : -90));

        public void SetYaw(int yawDeg)
        {
            _yawDeg = ((yawDeg % 360) + 360) % 360;
            transform.localRotation = Quaternion.Euler(0f, _yawDeg, 0f);
        }

        public void SetZoomStep(int step)
        {
            _index = Mathf.Clamp(step, 0, Scales.Length - 1);
            transform.localScale = Vector3.one * Scales[_index];
        }

        void Awake()
        {
            transform.localScale = Vector3.one * Scales[_index];
        }

        public void Zoom(int direction)
        {
            _index = Mathf.Clamp(_index + (direction > 0 ? 1 : -1), 0, Scales.Length - 1);
            transform.localScale = Vector3.one * Scales[_index];
        }

        public void Recenter()
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (head == null)
                return;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            forward.Normalize();
            transform.position = head.position + forward * _offsetFromHead.z + Vector3.up * _offsetFromHead.y;
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }
    }
}
