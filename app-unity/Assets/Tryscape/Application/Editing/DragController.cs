using System;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;

namespace Tryscape.Application.Editing
{
    public enum DragState
    {
        Idle,
        Dragging,

        /// <summary>Owner hand lost for at least the freeze threshold; preview frozen, nothing commits.</summary>
        Frozen,
    }

    public enum DragOutcomeKind
    {
        /// <summary>Input ignored (no drag, or not the owning hand).</summary>
        Ignored,

        /// <summary>One move command committed a new revision.</summary>
        Committed,

        /// <summary>Released at an invalid or rejected placement; committed state unchanged.</summary>
        Rejected,

        /// <summary>Drag ended without a command (lost tracking, cancel, or released on the committed pose).</summary>
        Cancelled,
    }

    public readonly struct DragPreview
    {
        public DragPreview(Guid entityId, Pose pose, CommandErrorCode placementError, bool frozen)
        {
            EntityId = entityId;
            Pose = pose;
            PlacementError = placementError;
            Frozen = frozen;
        }

        public Guid EntityId { get; }

        /// <summary>Snapped model pose the release would submit.</summary>
        public Pose Pose { get; }

        public CommandErrorCode PlacementError { get; }
        public bool IsValid => PlacementError == CommandErrorCode.None;
        public bool Frozen { get; }
    }

    public readonly struct DragOutcome
    {
        public DragOutcome(DragOutcomeKind kind, CommandErrorCode error = CommandErrorCode.None, CommandResult result = null)
        {
            Kind = kind;
            Error = error;
            Result = result;
        }

        public DragOutcomeKind Kind { get; }
        public CommandErrorCode Error { get; }
        public CommandResult Result { get; }
        public override string ToString() => Error == CommandErrorCode.None ? Kind.ToString() : $"{Kind}({Error.ToWire()})";
    }

    /// <summary>
    /// Drag transaction for one editable blocker (S07.2). During a drag only a preview changes; release submits at
    /// most one move command against the revision captured at drag start. One hand owns the drag. If the owner's
    /// tracking is lost for 200 ms the preview freezes; after 1000 ms the drag is cancelled. Reacquisition never
    /// commits. Time is supplied by the caller (seconds, monotonic) so this class has no clock dependency.
    /// </summary>
    public sealed class DragController
    {
        public const double FreezeAfterSeconds = 0.2;
        public const double CancelAfterSeconds = 1.0;

        // Tolerates floating-point subtraction error at the exact thresholds (e.g. 10.2 - 10.0 < 0.2).
        const double TimeEpsilon = 1e-6;

        readonly EditingSession _session;
        Guid _entityId;
        int _owner;
        long _startRevision;
        Pose _committedPose;
        DragPreview _preview;
        double? _lostSince;

        public DragController(EditingSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public DragState State { get; private set; } = DragState.Idle;
        public Guid EntityId => State == DragState.Idle ? Guid.Empty : _entityId;
        public DragPreview Preview => _preview;

        public bool TryBegin(Guid entityId, int ownerId)
        {
            if (State != DragState.Idle)
                return false; // one drag at a time; a second hand cannot start another
            var entity = _session.Current.FindEntity(entityId);
            if (entity == null || !entity.Editable || entity.Role != EntityRole.Blocker)
                return false;
            _entityId = entityId;
            _owner = ownerId;
            _startRevision = _session.Current.Revision;
            _committedPose = entity.Pose;
            _preview = new DragPreview(entityId, entity.Pose, CommandErrorCode.None, false);
            _lostSince = null;
            State = DragState.Dragging;
            return true;
        }

        /// <summary>Updates the preview from an unsnapped model-space position (mm). Ignored unless dragging by owner.</summary>
        public DragPreview UpdatePosition(int ownerId, double xMm, double zMm)
        {
            if (State != DragState.Dragging || ownerId != _owner)
                return _preview;
            var entry = Entry();
            var pose = _committedPose.WithPosition(PlacementRules.Snap(xMm, entry.SnapMm), PlacementRules.Snap(zMm, entry.SnapMm));
            var error = PlacementRules.Check(_session.Current, _session.Catalog, _entityId, pose);
            _preview = new DragPreview(_entityId, pose, error, false);
            return _preview;
        }

        public void TrackingLost(int ownerId, double now)
        {
            if (State != DragState.Idle && ownerId == _owner && _lostSince == null)
                _lostSince = now;
        }

        public void TrackingRestored(int ownerId)
        {
            if (State == DragState.Idle || ownerId != _owner)
                return;
            _lostSince = null;
            if (State == DragState.Frozen)
            {
                State = DragState.Dragging; // resume previewing; nothing is committed on reacquisition
                _preview = new DragPreview(_entityId, _preview.Pose, _preview.PlacementError, false);
            }
        }

        /// <summary>Advance lost-tracking timers. Returns Cancelled once the cancel threshold passes.</summary>
        public DragOutcome Tick(double now)
        {
            if (State == DragState.Idle || _lostSince == null)
                return new DragOutcome(DragOutcomeKind.Ignored);
            var lostFor = now - _lostSince.Value;
            if (lostFor >= CancelAfterSeconds - TimeEpsilon)
                return End(new DragOutcome(DragOutcomeKind.Cancelled));
            if (lostFor >= FreezeAfterSeconds - TimeEpsilon && State == DragState.Dragging)
            {
                State = DragState.Frozen;
                _preview = new DragPreview(_entityId, _preview.Pose, _preview.PlacementError, true);
            }
            return new DragOutcome(DragOutcomeKind.Ignored);
        }

        public DragOutcome Release(int ownerId)
        {
            if (State == DragState.Idle || ownerId != _owner)
                return new DragOutcome(DragOutcomeKind.Ignored);
            if (State == DragState.Frozen || _lostSince != null)
                return End(new DragOutcome(DragOutcomeKind.Cancelled));
            if (!_preview.IsValid)
                return End(new DragOutcome(DragOutcomeKind.Rejected, _preview.PlacementError));
            if (_preview.Pose.Equals(_committedPose))
                return End(new DragOutcome(DragOutcomeKind.Cancelled));

            var result = _session.Move(_entityId, _preview.Pose.XMm, _preview.Pose.ZMm, _startRevision);
            return End(result.Accepted
                ? new DragOutcome(DragOutcomeKind.Committed, CommandErrorCode.None, result)
                : new DragOutcome(DragOutcomeKind.Rejected, result.ErrorCode, result));
        }

        /// <summary>Abort without committing (interactor cancel, app pause, focus loss).</summary>
        public DragOutcome Cancel() =>
            State == DragState.Idle ? new DragOutcome(DragOutcomeKind.Ignored) : End(new DragOutcome(DragOutcomeKind.Cancelled));

        DragOutcome End(DragOutcome outcome)
        {
            State = DragState.Idle;
            _lostSince = null;
            return outcome;
        }

        CatalogEntry Entry()
        {
            var entity = _session.Current.FindEntity(_entityId);
            if (entity == null || !_session.Catalog.TryGet(entity.CatalogId, out var entry))
                throw new InvalidOperationException("Dragged entity has no catalog entry");
            return entry;
        }
    }
}
