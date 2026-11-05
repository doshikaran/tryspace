using System;
using System.Collections.Generic;
using System.Linq;

namespace Tryscape.Core.Commands
{
    /// <summary>Typed command failures; wire names match contracts/v1 command.schema.json.</summary>
    public enum CommandErrorCode
    {
        None,
        RevisionConflict,
        ProjectMismatch,
        UnknownEntity,
        EntityNotEditable,
        OutOfRoom,
        OverlapsPayloadStart,
        OverlapsBlocker,
        UnsupportedYaw,
        NotSnapped,
        UnknownRoute,
        NothingToUndo,
        NothingToRedo,
        InvalidScene,
        UnsupportedCommand,
    }

    public static class CommandErrorCodes
    {
        static readonly Dictionary<CommandErrorCode, string> Wire = new Dictionary<CommandErrorCode, string>
        {
            { CommandErrorCode.RevisionConflict, "revision_conflict" },
            { CommandErrorCode.ProjectMismatch, "project_mismatch" },
            { CommandErrorCode.UnknownEntity, "unknown_entity" },
            { CommandErrorCode.EntityNotEditable, "entity_not_editable" },
            { CommandErrorCode.OutOfRoom, "out_of_room" },
            { CommandErrorCode.OverlapsPayloadStart, "overlaps_payload_start" },
            { CommandErrorCode.OverlapsBlocker, "overlaps_blocker" },
            { CommandErrorCode.UnsupportedYaw, "unsupported_yaw" },
            { CommandErrorCode.NotSnapped, "not_snapped" },
            { CommandErrorCode.UnknownRoute, "unknown_route" },
            { CommandErrorCode.NothingToUndo, "nothing_to_undo" },
            { CommandErrorCode.NothingToRedo, "nothing_to_redo" },
            { CommandErrorCode.InvalidScene, "invalid_scene" },
            { CommandErrorCode.UnsupportedCommand, "unsupported_command" },
        };

        /// <summary>snake_case wire name, or null for <see cref="CommandErrorCode.None"/>.</summary>
        public static string ToWire(this CommandErrorCode code) => code == CommandErrorCode.None ? null : Wire[code];

        public static IEnumerable<string> AllWireNames => Wire.Values;
    }

    public abstract class SceneCommand
    {
        public abstract string WireType { get; }
    }

    public sealed class MoveEntityCommand : SceneCommand
    {
        public MoveEntityCommand(Guid entityId, int xMm, int zMm)
        {
            EntityId = entityId;
            XMm = xMm;
            ZMm = zMm;
        }

        public Guid EntityId { get; }
        public int XMm { get; }
        public int ZMm { get; }
        public override string WireType => "move_entity";
    }

    public sealed class RotateEntityCommand : SceneCommand
    {
        public RotateEntityCommand(Guid entityId, int yawDeg)
        {
            EntityId = entityId;
            YawDeg = yawDeg;
        }

        public Guid EntityId { get; }
        public int YawDeg { get; }
        public override string WireType => "rotate_entity";
    }

    public sealed class SelectRouteCommand : SceneCommand
    {
        public SelectRouteCommand(string routeId)
        {
            RouteId = routeId ?? throw new ArgumentNullException(nameof(routeId));
        }

        public string RouteId { get; }
        public override string WireType => "select_route";
    }

    public sealed class UndoCommand : SceneCommand
    {
        public override string WireType => "undo";
    }

    public sealed class RedoCommand : SceneCommand
    {
        public override string WireType => "redo";
    }

    public sealed class ResetSceneCommand : SceneCommand
    {
        public override string WireType => "reset_scene";
    }

    /// <summary>Command envelope (S07.1). Timestamps are never used for ordering, so none are carried here.</summary>
    public sealed class CommandEnvelope
    {
        public CommandEnvelope(Guid commandId, Guid projectId, long expectedRevision, SceneCommand command)
        {
            CommandId = commandId;
            ProjectId = projectId;
            ExpectedRevision = expectedRevision;
            Command = command ?? throw new ArgumentNullException(nameof(command));
        }

        public Guid CommandId { get; }
        public Guid ProjectId { get; }
        public long ExpectedRevision { get; }
        public SceneCommand Command { get; }
    }

    public sealed class CommandResult
    {
        CommandResult(Guid commandId, bool accepted, long revision, CommandErrorCode error, IEnumerable<Guid> changed)
        {
            CommandId = commandId;
            Accepted = accepted;
            Revision = revision;
            ErrorCode = error;
            ChangedEntityIds = changed.ToArray();
        }

        public Guid CommandId { get; }
        public bool Accepted { get; }

        /// <summary>Revision after the command; unchanged when rejected.</summary>
        public long Revision { get; }

        public CommandErrorCode ErrorCode { get; }
        public IReadOnlyList<Guid> ChangedEntityIds { get; }

        public static CommandResult Accept(Guid commandId, long revision, IEnumerable<Guid> changed) =>
            new CommandResult(commandId, true, revision, CommandErrorCode.None, changed);

        public static CommandResult Reject(Guid commandId, long revision, CommandErrorCode error)
        {
            if (error == CommandErrorCode.None)
                throw new ArgumentException("A rejection needs an error code", nameof(error));
            return new CommandResult(commandId, false, revision, error, Array.Empty<Guid>());
        }
    }
}
