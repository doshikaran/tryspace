using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tryscape.Application.Persistence;
using Tryscape.Application.Rehearsal;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Infrastructure.Persistence
{
    /// <summary>Minimal file-system port, so tests can inject interruptions at every write stage.</summary>
    public interface IStorage
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        byte[] Read(string path);

        /// <summary>Creates or truncates, writes everything, flushes to the device and closes.</summary>
        void WriteDurable(string path, byte[] bytes);

        /// <summary>Renames a file; the destination must not exist.</summary>
        void Move(string from, string to);

        void Delete(string path);
        void CreateDirectory(string path);
        void DeleteDirectory(string path);
        IEnumerable<string> Directories(string path);
        IEnumerable<string> Files(string path);
        long Length(string path);
    }

    /// <summary>
    /// System.IO implementation. It uses only operations Mono/IL2CPP provide on Android and desktop: write, flush to disk,
    /// close, delete, and move to a non-existent name. It does not rely on POSIX atomic-replace semantics; crash safety
    /// comes from the <see cref="ProjectStore"/> protocol plus recovery.
    /// </summary>
    public sealed class FileStorage : IStorage
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public byte[] Read(string path) => File.ReadAllBytes(path);

        public void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        public void Move(string from, string to) => File.Move(from, to);

        public void Delete(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        public IEnumerable<string> Directories(string path) =>
            Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();

        public IEnumerable<string> Files(string path) => Directory.Exists(path) ? Directory.GetFiles(path) : Array.Empty<string>();
        public long Length(string path) => new FileInfo(path).Length;
    }

    /// <summary>
    /// Local projects (S12). Layout under the root:
    /// <c>index.json</c>, <c>projects/&lt;uuid&gt;/project.json</c> and <c>projects/&lt;uuid&gt;/runs/&lt;uuid&gt;.json</c>.
    /// Names come only from validated GUIDs.
    /// <para>
    /// Write protocol for each file X:
    /// <list type="number">
    /// <item>write <c>X.tmp</c> and flush it to disk;</item>
    /// <item>read it back and verify the bytes and parsed integrity;</item>
    /// <item>move the old X to <c>X.bak</c>, replacing the previous backup;</item>
    /// <item>move <c>X.tmp</c> to X.</item>
    /// </list>
    /// An interruption at any point leaves a verified copy. Load tries X, then <c>X.tmp</c> (verified before the swap),
    /// then <c>X.bak</c>. An invalid primary is first preserved as <c>X.corrupt</c>.
    /// </para>
    /// <para>
    /// Limits: 20 projects, 20 runs per project, 1 MiB per snapshot and 100 MiB in total. Nothing is ever deleted to make
    /// room. A file from a newer major version is reported and never overwritten. Delete removes the project directory,
    /// including its backups, and its index entry.
    /// </para>
    /// </summary>
    public sealed class ProjectStore : IProjectRepository
    {
        public const int MaxProjects = 20;
        public const int MaxRunsPerProject = 20;
        public const long MaxTotalBytes = 100L * 1024 * 1024;

        readonly string _root;
        readonly IStorage _io;
        readonly Catalog _catalog;

        public ProjectStore(string root, Catalog catalog, IStorage storage = null)
        {
            if (string.IsNullOrEmpty(root))
                throw new ArgumentException("A storage root is required", nameof(root));
            _root = root;
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _io = storage ?? new FileStorage();
        }

        string ProjectsDir => Path.Combine(_root, "projects");
        string IndexPath => Path.Combine(_root, "index.json");
        string ProjectDir(Guid id) => Path.Combine(ProjectsDir, id.ToString("D"));
        string ProjectPath(Guid id) => Path.Combine(ProjectDir(id), "project.json");
        string RunsDir(Guid id) => Path.Combine(ProjectDir(id), "runs");
        string RunPath(Guid project, Guid run) => Path.Combine(RunsDir(project), run.ToString("D") + ".json");

        // ---------------------------------------------------------------- public API

        public StoreResult<IReadOnlyList<ProjectListing>> List()
        {
            try
            {
                var read = ReadRecovering(IndexPath, SnapshotJson.ReadIndex, out _);
                if (read != null && read.Ok)
                    return StoreResult<IReadOnlyList<ProjectListing>>.Ok(read.Value.OrderByDescending(p => p.UpdatedAt, StringComparer.Ordinal).ToList());
                // Missing or unreadable index: it is derived data, so rebuild it from the project files.
                var rebuilt = Scan();
                if (read != null || rebuilt.Count > 0) // a fresh device with no projects writes nothing
                {
                    _io.CreateDirectory(_root);
                    WriteAtomic(IndexPath, SnapshotJson.WriteIndex(rebuilt), b => SnapshotJson.ReadIndex(b).Ok);
                }
                return StoreResult<IReadOnlyList<ProjectListing>>.Ok(rebuilt.OrderByDescending(p => p.UpdatedAt, StringComparer.Ordinal).ToList(),
                    read != null, "index_rebuilt");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return StoreResult<IReadOnlyList<ProjectListing>>.Fail(StoreError.IoFailure, "index_io");
            }
        }

        public StoreResult<ProjectSnapshot> Load(Guid projectId)
        {
            try
            {
                if (!_io.DirectoryExists(ProjectDir(projectId)))
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.NotFound, "no_project");
                var read = ReadRecovering(ProjectPath(projectId), SnapshotJson.ReadProject, out var source);
                if (read == null)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.NotFound, "no_project_file");
                if (read.UnsupportedVersion)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.UnsupportedVersion, "project_newer_version");
                if (!read.Ok)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "project_" + read.Problem);
                var p = read.Value;
                if (p.ProjectId != projectId)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "project_id_mismatch");
                var compatibility = Compatible(p.ConfirmedScene) ?? Compatible(p.CurrentScene);
                if (compatibility != null)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.IncompatibleContent, compatibility);
                if (!SceneValidator.Validate(p.ConfirmedScene, _catalog).IsValid || !SceneValidator.Validate(p.CurrentScene, _catalog).IsValid)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "scene_invalid");
                if (p.CurrentScene.SceneId != p.ConfirmedScene.SceneId || p.CurrentScene.Revision < p.ConfirmedScene.Revision)
                    return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "baseline_mismatch");

                var runs = new Dictionary<Guid, RehearsalRun>();
                var recovered = source != "primary";
                foreach (var runId in p.SavedRunIds)
                {
                    var r = ReadRecovering(RunPath(projectId, runId), SnapshotJson.ReadRun, out var runSource);
                    if (r == null)
                        return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "run_missing");
                    if (r.UnsupportedVersion)
                        return StoreResult<ProjectSnapshot>.Fail(StoreError.UnsupportedVersion, "run_newer_version");
                    if (!r.Ok)
                        return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "run_" + r.Problem);
                    if (r.Value.ProjectId != projectId || r.Value.Run.Result.RunId != runId ||
                        r.Value.Run.Scene.SceneId != p.ConfirmedScene.SceneId)
                        return StoreResult<ProjectSnapshot>.Fail(StoreError.Corrupt, "run_reference");
                    recovered |= runSource != "primary";
                    runs[runId] = r.Value.Run;
                }
                RehearsalRun Pick(Guid? id) => id.HasValue ? runs[id.Value] : null;
                var snapshot = new ProjectSnapshot(p.ProjectId, p.Title, p.CreatedAt, p.UpdatedAt, p.ConfirmedScene, p.CurrentScene,
                    p.ZoomStep, Pick(p.AttemptA), Pick(p.AttemptB), p.YawDeg);
                var migrated = p.StoredSchemaVersion != ProjectSnapshot.SchemaVersion;
                return StoreResult<ProjectSnapshot>.Ok(snapshot, recovered,
                    recovered ? "recovered_from_copy" : migrated ? "migrated_from_" + p.StoredSchemaVersion.Replace('.', '_') : null);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return StoreResult<ProjectSnapshot>.Fail(StoreError.IoFailure, "project_io");
            }
        }

        public StoreResult<ProjectListing> Save(ProjectSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            try
            {
                var id = snapshot.ProjectId;
                if (snapshot.ConfirmedScene.ProjectId != id || snapshot.CurrentScene.ProjectId != id)
                    return StoreResult<ProjectListing>.Fail(StoreError.Corrupt, "project_reference");
                if (!SceneValidator.Validate(snapshot.CurrentScene, _catalog).IsValid ||
                    !SceneValidator.Validate(snapshot.ConfirmedScene, _catalog).IsValid)
                    return StoreResult<ProjectListing>.Fail(StoreError.Corrupt, "scene_invalid");
                var runs = snapshot.Runs.ToList();
                if (runs.Count > MaxRunsPerProject)
                    return StoreResult<ProjectListing>.Fail(StoreError.QuotaExceeded, "too_many_runs");
                if (runs.Any(r => r.Scene.ProjectId != id))
                    return StoreResult<ProjectListing>.Fail(StoreError.Corrupt, "run_reference");

                var exists = _io.DirectoryExists(ProjectDir(id));
                if (exists && _io.FileExists(ProjectPath(id)))
                {
                    var current = SnapshotJson.ReadProject(SafeRead(ProjectPath(id)));
                    if (current.UnsupportedVersion)
                        return StoreResult<ProjectListing>.Fail(StoreError.UnsupportedVersion, "protected_newer_version");
                }
                if (!exists && ProjectIds().Count >= MaxProjects)
                    return StoreResult<ProjectListing>.Fail(StoreError.QuotaExceeded, "too_many_projects");

                var runBytes = runs.ToDictionary(r => r.Result.RunId,
                    r => SnapshotJson.WriteRun(id, r == snapshot.AttemptA ? "Attempt A" : "Attempt B", snapshot.UpdatedAt, r));
                var projectBytes = SnapshotJson.WriteProject(snapshot, runs.Select(r => r.Result.RunId));
                if (projectBytes.Length > SnapshotJson.MaxBytes || runBytes.Values.Any(b => b.Length > SnapshotJson.MaxBytes))
                    return StoreResult<ProjectListing>.Fail(StoreError.TooLarge, "snapshot_over_1_mib");

                long incoming = projectBytes.Length + runBytes.Values.Sum(b => (long)b.Length);
                long replaced = exists ? DirectorySize(ProjectDir(id)) : 0;
                if (TotalBytes() - replaced + incoming * 2 > MaxTotalBytes) // x2: the backup copy kept alongside each file
                    return StoreResult<ProjectListing>.Fail(StoreError.QuotaExceeded, "storage_full");

                _io.CreateDirectory(RunsDir(id));
                foreach (var pair in runBytes)
                {
                    var path = RunPath(id, pair.Key);
                    if (_io.FileExists(path) && SnapshotJson.ReadRun(SafeRead(path)).Ok)
                        continue; // runs are immutable: an existing verified copy is already correct
                    WriteAtomic(path, pair.Value, b => SnapshotJson.ReadRun(b).Ok);
                }
                WriteAtomic(ProjectPath(id), projectBytes, b => SnapshotJson.ReadProject(b).Ok);

                // Only after the project points at the new run set: remove runs it no longer references.
                var keep = new HashSet<string>(runBytes.Keys.Select(k => k.ToString("D")), StringComparer.Ordinal);
                foreach (var file in _io.Files(RunsDir(id)))
                {
                    var name = Path.GetFileName(file);
                    var stem = name.Split('.')[0];
                    if (!keep.Contains(stem))
                        _io.Delete(file);
                }

                var listing = new ProjectListing(id, snapshot.Title, snapshot.UpdatedAt, runs.Count);
                UpdateIndex(list => list.Where(p => p.ProjectId != id).Append(listing));
                return StoreResult<ProjectListing>.Ok(listing);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return StoreResult<ProjectListing>.Fail(StoreError.IoFailure, "save_io");
            }
        }

        public StoreResult<bool> Delete(Guid projectId)
        {
            try
            {
                if (!_io.DirectoryExists(ProjectDir(projectId)))
                    return StoreResult<bool>.Fail(StoreError.NotFound, "no_project");
                _io.DeleteDirectory(ProjectDir(projectId)); // includes .bak, .tmp, .corrupt and runs
                UpdateIndex(list => list.Where(p => p.ProjectId != projectId));
                return StoreResult<bool>.Ok(true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return StoreResult<bool>.Fail(StoreError.IoFailure, "delete_io");
            }
        }

        // ---------------------------------------------------------------- internals

        string Compatible(SceneSpec scene)
        {
            if (scene.ScenarioPackId != _catalog.ScenarioPackId)
                return "unknown_scenario_pack";
            if (scene.CatalogVersion != _catalog.CatalogVersion)
                return "unsupported_catalog_version";
            return null;
        }

        byte[] SafeRead(string path)
        {
            try
            {
                return _io.Length(path) > SnapshotJson.MaxBytes ? null : _io.Read(path);
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads X, else X.tmp, else X.bak. Returns null if none exists. A newer-version primary is returned as is, never
        /// replaced by an older copy. An invalid primary is preserved as X.corrupt before any recovery.
        /// </summary>
        SnapshotRead<T> ReadRecovering<T>(string path, Func<byte[], SnapshotRead<T>> parse, out string source)
        {
            source = null;
            SnapshotRead<T> primary = null;
            if (_io.FileExists(path))
            {
                primary = parse(SafeRead(path));
                if (primary.Ok || primary.UnsupportedVersion)
                {
                    source = "primary";
                    return primary;
                }
                var corrupt = path + ".corrupt";
                var bytes = SafeRead(path);
                if (bytes != null)
                {
                    _io.Delete(corrupt);
                    _io.WriteDurable(corrupt, bytes);
                }
            }
            foreach (var (candidate, name) in new[] { (path + ".tmp", "tmp"), (path + ".bak", "backup") })
            {
                if (!_io.FileExists(candidate))
                    continue;
                var read = parse(SafeRead(candidate));
                if (read.Ok)
                {
                    source = name;
                    return read;
                }
            }
            return primary;
        }

        void WriteAtomic(string path, byte[] bytes, Func<byte[], bool> verify)
        {
            var tmp = path + ".tmp";
            var bak = path + ".bak";
            _io.Delete(tmp);
            _io.WriteDurable(tmp, bytes);
            var back = _io.Read(tmp);
            if (!back.SequenceEqual(bytes) || !verify(back))
                throw new IOException("staged write did not verify");
            if (_io.FileExists(path))
            {
                var existing = SafeRead(path);
                if (existing != null && verify(existing))
                {
                    // Only a verified copy becomes the backup, so a corrupt primary never replaces a good backup.
                    _io.Delete(bak);
                    _io.Move(path, bak);
                }
                else
                {
                    if (existing != null)
                    {
                        _io.Delete(path + ".corrupt"); // diagnostic copy preserved before the corrupt file is replaced
                        _io.WriteDurable(path + ".corrupt", existing);
                    }
                    _io.Delete(path);
                }
            }
            _io.Move(tmp, path);
        }

        void UpdateIndex(Func<IEnumerable<ProjectListing>, IEnumerable<ProjectListing>> change)
        {
            _io.CreateDirectory(_root);
            var read = ReadRecovering(IndexPath, SnapshotJson.ReadIndex, out _);
            var current = read != null && read.Ok ? read.Value : Scan();
            var next = change(current).ToList();
            WriteAtomic(IndexPath, SnapshotJson.WriteIndex(next), b => SnapshotJson.ReadIndex(b).Ok);
        }

        List<Guid> ProjectIds() =>
            _io.Directories(ProjectsDir)
                .Select(Path.GetFileName)
                .Select(n => Guid.TryParseExact(n, "D", out var g) && g.ToString("D") == n ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();

        List<ProjectListing> Scan()
        {
            var list = new List<ProjectListing>();
            foreach (var id in ProjectIds())
            {
                var read = ReadRecovering(ProjectPath(id), SnapshotJson.ReadProject, out _);
                if (read != null && read.Ok)
                    list.Add(new ProjectListing(id, read.Value.Title, read.Value.UpdatedAt, read.Value.SavedRunIds.Count));
                else if (read != null && read.UnsupportedVersion)
                    list.Add(new ProjectListing(id, "Saved by a newer app version", "1970-01-01T00:00:00Z", 0));
            }
            return list.Take(MaxProjects).ToList();
        }

        long DirectorySize(string dir)
        {
            long total = 0;
            foreach (var f in _io.Files(dir))
                total += _io.Length(f);
            foreach (var d in _io.Directories(dir))
                total += DirectorySize(d);
            return total;
        }

        long TotalBytes() => _io.DirectoryExists(_root) ? DirectorySize(_root) : 0;
    }
}
