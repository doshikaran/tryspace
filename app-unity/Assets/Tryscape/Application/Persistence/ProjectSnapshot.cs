using System;
using System.Collections.Generic;
using Tryscape.Application.Rehearsal;
using Tryscape.Core.Model;

namespace Tryscape.Application.Persistence
{
    /// <summary>
    /// Local project snapshot (S12, contracts/v1/schemas/project_snapshot.schema.json). It holds committed state only:
    /// the confirmed creation baseline, the current committed scene, presentation settings and the retained attempts.
    /// Drag previews, undo history, playback progress, audio and credentials are never part of it.
    /// </summary>
    public sealed class ProjectSnapshot
    {
        public const string SchemaVersion = "1.0.0";
        public const int MaxZoomStep = 4;

        public ProjectSnapshot(Guid projectId, string title, string createdAt, string updatedAt, SceneSpec confirmedScene,
            SceneSpec currentScene, int tabletopZoomStep, RehearsalRun attemptA, RehearsalRun attemptB)
        {
            ProjectId = projectId;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            CreatedAt = createdAt ?? throw new ArgumentNullException(nameof(createdAt));
            UpdatedAt = updatedAt ?? throw new ArgumentNullException(nameof(updatedAt));
            ConfirmedScene = confirmedScene ?? throw new ArgumentNullException(nameof(confirmedScene));
            CurrentScene = currentScene ?? throw new ArgumentNullException(nameof(currentScene));
            if (tabletopZoomStep < 0 || tabletopZoomStep > MaxZoomStep)
                throw new ArgumentOutOfRangeException(nameof(tabletopZoomStep));
            TabletopZoomStep = tabletopZoomStep;
            AttemptA = attemptA;
            AttemptB = attemptB;
        }

        public Guid ProjectId { get; }
        public string Title { get; }
        public string CreatedAt { get; }
        public string UpdatedAt { get; }
        public SceneSpec ConfirmedScene { get; }
        public SceneSpec CurrentScene { get; }
        public int TabletopZoomStep { get; }
        public RehearsalRun AttemptA { get; }
        public RehearsalRun AttemptB { get; }

        public IEnumerable<RehearsalRun> Runs
        {
            get
            {
                if (AttemptA != null)
                    yield return AttemptA;
                if (AttemptB != null && (AttemptA == null || AttemptB.Result.RunId != AttemptA.Result.RunId))
                    yield return AttemptB;
            }
        }
    }

    public sealed class ProjectListing
    {
        public ProjectListing(Guid projectId, string title, string updatedAt, int runCount)
        {
            ProjectId = projectId;
            Title = title;
            UpdatedAt = updatedAt;
            RunCount = runCount;
        }

        public Guid ProjectId { get; }
        public string Title { get; }
        public string UpdatedAt { get; }
        public int RunCount { get; }
    }

    public enum StoreError
    {
        None,

        /// <summary>No such project (deleted, or never saved on this device).</summary>
        NotFound,

        /// <summary>Every copy failed validation (size, JSON shape, schema, hash, references).</summary>
        Corrupt,

        /// <summary>The file was written by a newer app version; it is left untouched and is not overwritten.</summary>
        UnsupportedVersion,

        /// <summary>The project uses a catalog or scenario version this app does not have.</summary>
        IncompatibleContent,

        /// <summary>20-project, 20-run, 1 MiB or 100 MiB limit reached; nothing was deleted to make room.</summary>
        QuotaExceeded,

        /// <summary>A snapshot would exceed 1 MiB.</summary>
        TooLarge,

        /// <summary>The platform refused a read or write (permissions, disk full).</summary>
        IoFailure,
    }

    /// <summary>Typed outcome of a store operation; no exception text reaches the UI.</summary>
    public sealed class StoreResult<T>
    {
        StoreResult(T value, StoreError error, string detail, bool recovered)
        {
            Value = value;
            Error = error;
            Detail = detail;
            Recovered = recovered;
        }

        public T Value { get; }
        public StoreError Error { get; }

        /// <summary>Safe machine code describing the failure or recovery source (for example <c>backup</c>).</summary>
        public string Detail { get; }

        /// <summary>The primary file was invalid and an earlier verified copy was used instead.</summary>
        public bool Recovered { get; }

        public bool Succeeded => Error == StoreError.None;

        public static StoreResult<T> Ok(T value, bool recovered = false, string detail = null) =>
            new StoreResult<T>(value, StoreError.None, detail, recovered);

        public static StoreResult<T> Fail(StoreError error, string detail) => new StoreResult<T>(default, error, detail, false);
    }

    /// <summary>Local project storage port. The Unity build uses <c>Tryscape.Infrastructure.Persistence.ProjectStore</c>.</summary>
    public interface IProjectRepository
    {
        StoreResult<IReadOnlyList<ProjectListing>> List();
        StoreResult<ProjectSnapshot> Load(Guid projectId);
        StoreResult<ProjectListing> Save(ProjectSnapshot snapshot);
        StoreResult<bool> Delete(Guid projectId);
    }
}
