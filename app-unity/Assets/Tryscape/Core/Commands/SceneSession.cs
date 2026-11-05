using System;
using System.Collections.Generic;
using System.Linq;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Core.Commands
{
    /// <summary>
    /// Authoritative editing state for one project (S07). Commands are applied sequentially and atomically against an
    /// expected revision; a rejection changes nothing. Every accepted state change, including undo, redo and reset,
    /// produces a new revision (revisions never decrease). Not thread-safe: call from the application thread only.
    /// </summary>
    public sealed class SceneSession
    {
        public const int UndoLimit = 50;
        public const int CommandCacheSize = 256;

        readonly Catalog _catalog;
        readonly LinkedList<SceneSpec> _undo = new LinkedList<SceneSpec>();
        readonly Stack<SceneSpec> _redo = new Stack<SceneSpec>();
        readonly Dictionary<Guid, CommandResult> _resultsById = new Dictionary<Guid, CommandResult>();
        readonly Queue<Guid> _resultOrder = new Queue<Guid>();

        SceneSession(SceneSpec confirmed, Catalog catalog)
        {
            Confirmed = confirmed;
            Current = confirmed;
            _catalog = catalog;
        }

        /// <summary>The confirmed creation snapshot that reset restores.</summary>
        public SceneSpec Confirmed { get; }

        public SceneSpec Current { get; private set; }
        public Catalog Catalog => _catalog;
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>Raised after an accepted state change with the new immutable scene.</summary>
        public event Action<SceneSpec, CommandResult> Changed;

        /// <summary>Starts a session from a confirmed scene, or returns the validation failure.</summary>
        public static bool TryCreate(SceneSpec confirmed, Catalog catalog, out SceneSession session, out ValidationResult validation)
        {
            if (confirmed == null)
                throw new ArgumentNullException(nameof(confirmed));
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));
            validation = SceneValidator.Validate(confirmed, catalog);
            session = validation.IsValid ? new SceneSession(confirmed, catalog) : null;
            return session != null;
        }

        public CommandResult Execute(CommandEnvelope envelope)
        {
            if (envelope == null)
                throw new ArgumentNullException(nameof(envelope));
            if (_resultsById.TryGetValue(envelope.CommandId, out var previous))
                return previous; // duplicate delivery: original response, no second mutation

            var result = Apply(envelope);
            Remember(envelope.CommandId, result);
            if (result.Accepted)
                Changed?.Invoke(Current, result);
            return result;
        }

        CommandResult Apply(CommandEnvelope envelope)
        {
            var id = envelope.CommandId;
            var revision = Current.Revision;
            if (envelope.ProjectId != Current.ProjectId)
                return CommandResult.Reject(id, revision, CommandErrorCode.ProjectMismatch);
            if (envelope.ExpectedRevision != revision)
                return CommandResult.Reject(id, revision, CommandErrorCode.RevisionConflict);

            switch (envelope.Command)
            {
                case MoveEntityCommand move:
                {
                    var entity = Current.FindEntity(move.EntityId);
                    if (entity == null)
                        return CommandResult.Reject(id, revision, CommandErrorCode.UnknownEntity);
                    return ApplyPose(id, entity.Id, entity.Pose.WithPosition(move.XMm, move.ZMm));
                }
                case RotateEntityCommand rotate:
                {
                    var entity = Current.FindEntity(rotate.EntityId);
                    if (entity == null)
                        return CommandResult.Reject(id, revision, CommandErrorCode.UnknownEntity);
                    return ApplyPose(id, entity.Id, entity.Pose.WithYaw(rotate.YawDeg));
                }
                case SelectRouteCommand select:
                    if (Current.FindRoute(select.RouteId) == null)
                        return CommandResult.Reject(id, revision, CommandErrorCode.UnknownRoute);
                    return Commit(id, Current.WithActiveRoute(select.RouteId), recordUndo: true);
                case UndoCommand _:
                {
                    if (_undo.Count == 0)
                        return CommandResult.Reject(id, revision, CommandErrorCode.NothingToUndo);
                    var before = _undo.Last.Value;
                    _undo.RemoveLast();
                    _redo.Push(Current);
                    return Replace(id, before);
                }
                case RedoCommand _:
                {
                    if (_redo.Count == 0)
                        return CommandResult.Reject(id, revision, CommandErrorCode.NothingToRedo);
                    var after = _redo.Pop();
                    PushUndo(Current);
                    return Replace(id, after);
                }
                case ResetSceneCommand _:
                {
                    _undo.Clear();
                    _redo.Clear();
                    return Replace(id, Confirmed);
                }
                default:
                    return CommandResult.Reject(id, revision, CommandErrorCode.UnsupportedCommand);
            }
        }

        CommandResult ApplyPose(Guid commandId, Guid entityId, Pose pose)
        {
            var error = PlacementRules.Check(Current, _catalog, entityId, pose);
            if (error != CommandErrorCode.None)
                return CommandResult.Reject(commandId, Current.Revision, error);
            return Commit(commandId, Current.WithEntityPose(entityId, pose), recordUndo: true);
        }

        /// <summary>Applies another snapshot's content as a new revision (undo/redo/reset).</summary>
        CommandResult Replace(Guid commandId, SceneSpec contentSource)
        {
            var next = Current.WithContentOf(contentSource, checked(Current.Revision + 1));
            var changed = ChangedEntities(Current, next);
            Current = next;
            return CommandResult.Accept(commandId, next.Revision, changed);
        }

        CommandResult Commit(Guid commandId, SceneSpec candidate, bool recordUndo)
        {
            // Defence in depth: the committed scene must remain semantically valid.
            if (!SceneValidator.Validate(candidate, _catalog).IsValid)
                return CommandResult.Reject(commandId, Current.Revision, CommandErrorCode.InvalidScene);
            var next = candidate.WithRevision(checked(Current.Revision + 1));
            var changed = ChangedEntities(Current, next);
            if (recordUndo)
            {
                PushUndo(Current);
                _redo.Clear(); // a new edit after undo discards redo history
            }
            Current = next;
            return CommandResult.Accept(commandId, next.Revision, changed);
        }

        void PushUndo(SceneSpec snapshot)
        {
            _undo.AddLast(snapshot);
            while (_undo.Count > UndoLimit)
                _undo.RemoveFirst();
        }

        void Remember(Guid commandId, CommandResult result)
        {
            _resultsById[commandId] = result;
            _resultOrder.Enqueue(commandId);
            while (_resultOrder.Count > CommandCacheSize)
                _resultsById.Remove(_resultOrder.Dequeue());
        }

        static IEnumerable<Guid> ChangedEntities(SceneSpec before, SceneSpec after) =>
            after.Entities
                .Where(e => !e.Equals(before.FindEntity(e.Id)))
                .Select(e => e.Id)
                .ToArray();
    }
}
