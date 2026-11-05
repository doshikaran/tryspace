using System;
using Tryscape.Core.Commands;
using Tryscape.Core.Model;

namespace Tryscape.Application.Editing
{
    public sealed class RandomIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    /// <summary>
    /// Use-case facade over <see cref="SceneSession"/>: issues command envelopes with fresh IDs against the current
    /// revision. Presentation reads <see cref="Current"/> and listens to <see cref="Changed"/>; it never mutates state.
    /// </summary>
    public sealed class EditingSession
    {
        readonly SceneSession _session;
        readonly IIdGenerator _ids;

        public EditingSession(SceneSession session, IIdGenerator ids)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _session.Changed += (scene, result) => Changed?.Invoke(scene, result);
        }

        public SceneSpec Current => _session.Current;
        public SceneSpec Confirmed => _session.Confirmed;
        public Catalog Catalog => _session.Catalog;
        public bool CanUndo => _session.CanUndo;
        public bool CanRedo => _session.CanRedo;

        /// <summary>Accepted state changes only.</summary>
        public event Action<SceneSpec, CommandResult> Changed;

        /// <summary>Rejected commands (for user feedback); state is unchanged.</summary>
        public event Action<SceneCommand, CommandResult> Rejected;

        public CommandResult Move(Guid entityId, int xMm, int zMm, long expectedRevision) =>
            Send(new MoveEntityCommand(entityId, xMm, zMm), expectedRevision);

        /// <summary>Next allowed quarter turn for the entity (catalog-constrained).</summary>
        public CommandResult RotateQuarterTurn(Guid entityId)
        {
            var entity = Current.FindEntity(entityId);
            var yaw = entity == null ? 0 : (entity.Pose.YawDeg + 90) % 360;
            return Send(new RotateEntityCommand(entityId, yaw), Current.Revision);
        }

        public CommandResult SelectRoute(string routeId) => Send(new SelectRouteCommand(routeId), Current.Revision);
        public CommandResult Undo() => Send(new UndoCommand(), Current.Revision);
        public CommandResult Redo() => Send(new RedoCommand(), Current.Revision);
        public CommandResult Reset() => Send(new ResetSceneCommand(), Current.Revision);

        CommandResult Send(SceneCommand command, long expectedRevision)
        {
            var result = _session.Execute(new CommandEnvelope(_ids.NewId(), Current.ProjectId, expectedRevision, command));
            if (!result.Accepted)
                Rejected?.Invoke(command, result);
            return result;
        }
    }
}
