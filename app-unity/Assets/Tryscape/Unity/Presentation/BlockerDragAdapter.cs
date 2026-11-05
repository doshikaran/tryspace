using System;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using Tryscape.Application.Editing;
using UnityEngine;

namespace Tryscape.Unity.Presentation
{
    /// <summary>
    /// Translates Interaction SDK pointer events on an editable blocker into the pure <see cref="DragController"/>.
    /// The Grabbable moves only this view (the preview); release submits at most one command; the view is then reset
    /// from the committed model. Height and yaw are pinned so the preview stays a floor translation (S07.2).
    /// </summary>
    [RequireComponent(typeof(EntityView))]
    public sealed class BlockerDragAdapter : MonoBehaviour
    {
        [SerializeField, Interface(typeof(IPointable))] UnityEngine.Object _pointable;

        IPointable _source;
        EntityView _view;
        IDragHost _host;
        IHand _ownerHand;
        int _owner;
        bool _dragging;
        Quaternion _pinnedRotation;

        public void Configure(IPointable pointable) => _pointable = pointable as UnityEngine.Object;

        public void Attach(IDragHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        void Awake()
        {
            _view = GetComponent<EntityView>();
            _source = _pointable as IPointable;
        }

        void OnEnable()
        {
            if (_source != null)
                _source.WhenPointerEventRaised += HandlePointerEvent;
        }

        void OnDisable()
        {
            if (_source != null)
                _source.WhenPointerEventRaised -= HandlePointerEvent;
            if (_dragging)
                Finish(_host?.Drag.Cancel() ?? default); // never leave a drag open across disable/pause
        }

        /// <summary>Entry point for Interaction SDK events; public so PlayMode tests can drive it without XR.</summary>
        public void HandlePointerEvent(PointerEvent evt)
        {
            if (_host == null || _host.Drag == null)
                return;
            switch (evt.Type)
            {
                case PointerEventType.Select:
                    if (!_dragging && _host.Drag.TryBegin(_view.EntityId, evt.Identifier))
                    {
                        _dragging = true;
                        _owner = evt.Identifier;
                        _ownerHand = FindHand(evt.Identifier);
                        _pinnedRotation = transform.localRotation;
                        _host.OnDragStarted(_view);
                        UpdatePreview();
                    }
                    break;
                case PointerEventType.Move:
                    // Grabbable raises Move to listeners *before* it moves the transform, so the preview is computed
                    // in LateUpdate from the transform the user actually sees, never from a stale position.
                    break;
                case PointerEventType.Unselect:
                    if (_dragging && evt.Identifier == _owner)
                    {
                        // Grabbable force-moves to the final pointer pose before raising Unselect: the transform is
                        // current here, so recompute before releasing to commit exactly where the object was dropped.
                        PinToFloor();
                        UpdatePreview();
                        Finish(_host.Drag.Release(_owner));
                    }
                    break;
                case PointerEventType.Cancel:
                    if (_dragging && evt.Identifier == _owner)
                        Finish(_host.Drag.Cancel());
                    break;
            }
        }

        void Update()
        {
            if (!_dragging || _host == null)
                return;
            if (_ownerHand != null)
            {
                if (_ownerHand.IsTrackedDataValid)
                    _host.Drag.TrackingRestored(_owner);
                else
                    _host.Drag.TrackingLost(_owner, Time.realtimeSinceStartupAsDouble);
            }
            var tick = _host.Drag.Tick(Time.realtimeSinceStartupAsDouble);
            if (tick.Kind == DragOutcomeKind.Cancelled)
                Finish(tick);
            else if (_host.Drag.State == DragState.Frozen)
                _host.OnDragPreview(_view, _host.Drag.Preview); // show the paused state; position updates resume on reacquisition
        }

        void LateUpdate()
        {
            if (!_dragging || _host.Drag.State == DragState.Frozen)
                return;
            PinToFloor();
            UpdatePreview();
        }

        /// <summary>Keep the preview on the floor plane and at its committed quarter-turn yaw.</summary>
        void PinToFloor()
        {
            var p = transform.localPosition;
            transform.localPosition = new Vector3(p.x, 0f, p.z);
            transform.localRotation = _pinnedRotation;
        }

        void UpdatePreview()
        {
            var local = transform.localPosition;
            var (xMm, zMm) = ModelSpaceMapping.ToModel(_host.Room, local.x, local.z);
            var preview = _host.Drag.UpdatePosition(_owner, xMm, zMm);
            _host.OnDragPreview(_view, preview);
        }

        void Finish(DragOutcome outcome)
        {
            _dragging = false;
            _ownerHand = null;
            _host?.OnDragFinished(_view, outcome);
        }

        static IHand FindHand(int interactorId)
        {
            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
            {
                if (!(behaviour is IInteractorView interactor) || interactor.Identifier != interactorId)
                    continue;
                if (behaviour is HandGrabInteractor handGrab)
                    return handGrab.Hand;
                if (behaviour is DistanceHandGrabInteractor distance)
                    return distance.Hand;
                return behaviour.GetComponentInParent<Hand>();
            }
            return null; // controller or unknown interactor: rely on the SDK's Cancel event
        }
    }

    /// <summary>What the drag adapter needs from the application composition root.</summary>
    public interface IDragHost
    {
        DragController Drag { get; }
        Tryscape.Core.Model.RoomSpec Room { get; }
        void OnDragStarted(EntityView view);
        void OnDragPreview(EntityView view, DragPreview preview);
        void OnDragFinished(EntityView view, DragOutcome outcome);
    }
}
