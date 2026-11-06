using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Application.Editing;
using Tryscape.Application.Persistence;
using Tryscape.Application.Rehearsal;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Canonical;
using Tryscape.Core.Commands;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Infrastructure.Persistence;

namespace Tryscape.Tests.EditMode.Core
{
    static class Journey
    {
        sealed class FixedClock : IUtcClock
        {
            public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>F01 created, rehearsed (A), console moved south (F02), rehearsed (B).</summary>
        public static (EditingSession editing, RehearsalSession rehearsal, AttemptBook book) AttemptsF01ThenF02()
        {
            Assert.That(SceneSession.TryCreate(Fixtures.F01, Fixtures.Catalog, out var core, out _), Is.True);
            var editing = new EditingSession(core, new SequenceIds());
            var rehearsal = new RehearsalSession(editing, new SequenceIds(), new FixedClock());
            var book = new AttemptBook();
            book.Keep(AttemptSlot.A, rehearsal.Rehearse());
            Assert.That(editing.Move(Fixtures.ConsoleId, 2500, 800, editing.Current.Revision).Accepted, Is.True);
            book.Keep(AttemptSlot.B, rehearsal.Rehearse());
            return (editing, rehearsal, book);
        }
    }

    public sealed class AttemptComparisonTests
    {
        [Test]
        public void AttemptAIsImmutableAfterEditsAndAttemptB()
        {
            var (editing, rehearsal, book) = Journey.AttemptsF01ThenF02();
            var aBytes = SceneCanonicalizer.CanonicalBytes(book.A.Run.Scene);
            var aEvidence = book.A.Run.Evidence.EvidenceHash();
            editing.Move(Fixtures.ConsoleId, 2500, 4000, editing.Current.Revision);
            editing.RotateQuarterTurn(Fixtures.ConsoleId);
            rehearsal.Rehearse();
            Assert.That(SceneCanonicalizer.CanonicalBytes(book.A.Run.Scene), Is.EqualTo(aBytes));
            Assert.That(book.A.Run.Evidence.EvidenceHash(), Is.EqualTo(aEvidence));
            Assert.That(book.A.Run.Scene.FindEntity(Fixtures.ConsoleId).Pose.ZMm, Is.EqualTo(2500));
            Assert.That(rehearsal.IsStale(book.A.Run), Is.True, "live result marking still applies");
        }

        [Test]
        public void ComparisonMatchesIndependentF01AndF02Expectations()
        {
            var (_, _, book) = Journey.AttemptsF01ThenF02();
            var report = AttemptComparison.Compare(book.A, book.B);
            Assert.That(report.Comparable, Is.True);
            // D06 F01: Direct blocked by the console at 11/120 of segment 0. D06 F02: Direct clear, 4300 mm.
            Assert.That(report.A.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            Assert.That(book.A.Run.Evidence.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120)));
            Assert.That(report.A.ObstructionLabel, Is.EqualTo("the console table"));
            Assert.That(report.B.Outcome, Is.EqualTo(EvaluationOutcome.ClearInModel));
            Assert.That(report.B.RouteLengthMm, Is.EqualTo(4300));
            Assert.That(report.A.RouteLengthMm, Is.EqualTo(4300));
            Assert.That(report.LengthDifferenceMm, Is.EqualTo(0));
            Assert.That(report.Changes, Is.EqualTo(new[] { "Console table moved from (2.50, 2.50) m to (2.50, 0.80) m." }));
            StringAssert.Contains("blocked in this model by the console table", report.Explanation);
            StringAssert.Contains("clear in this model after the console table was moved", report.Explanation);
            Assert.That(report.Preparation, Is.EqualTo("Before delivery, move the console table out of the direct approach, as in Attempt B."));
            foreach (var text in new[] { report.Explanation, report.Preparation })
                foreach (var banned in new[] { "%", "probab", "guarantee", "safe", "saving", "save you", "faster", "minutes" })
                    StringAssert.DoesNotContain(banned, text.ToLowerInvariant());
        }

        [Test]
        public void RouteChangeComparisonUsesF03LengthAndF04Obstruction()
        {
            Assert.That(SceneSession.TryCreate(Fixtures.F01, Fixtures.Catalog, out var core, out _), Is.True);
            var editing = new EditingSession(core, new SequenceIds());
            var rehearsal = new RehearsalSession(editing, new SequenceIds(), new SystemUtcClock());
            var book = new AttemptBook();
            book.Keep(AttemptSlot.A, rehearsal.Rehearse()); // F01 Direct blocked
            editing.SelectRoute(MovingDayPack.NorthRouteId);
            book.Keep(AttemptSlot.B, rehearsal.Rehearse()); // F03: Northern clear, 4700 mm
            var report = AttemptComparison.Compare(book.A, book.B);
            Assert.That(report.B.RouteLengthMm, Is.EqualTo(4700));
            Assert.That(report.LengthDifferenceMm, Is.EqualTo(400)); // D06.3 model calculation
            Assert.That(report.Changes, Is.EqualTo(new[] { "Route changed from Direct approach to Northern approach." }));
            Assert.That(report.Preparation, Is.EqualTo("Plan the delivery along the northern approach, as in Attempt B."));

            editing.Move(Fixtures.ConsoleId, 2500, 4000, editing.Current.Revision); // F04
            book.Keep(AttemptSlot.A, book.B.Run);
            book.Keep(AttemptSlot.B, rehearsal.Rehearse());
            var worse = AttemptComparison.Compare(book.A, book.B);
            Assert.That(worse.B.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            StringAssert.Contains("Attempt B is blocked in this model by the console table", worse.Explanation);
        }

        [Test]
        public void IncompatibleVersionsAndInvalidAttemptsAreNotCompared()
        {
            var (_, _, book) = Journey.AttemptsF01ThenF02();
            SavedAttempt WithEvaluator(SavedAttempt a, string evaluator, string catalog)
            {
                var e = a.Run.Evidence;
                var evidence = new EvaluationEvidence(e.SceneId, e.SceneRevision, e.SceneHash, e.RouteId, catalog, evaluator, e.Outcome,
                    e.RouteLengthMm, e.FirstViolation, e.Assumptions, e.ValidationErrors);
                return new SavedAttempt(a.Slot, new RehearsalRun(new EvaluationResult(a.Run.Result.RunId, a.Run.Result.CreatedAtUtc, evidence), a.Run.Scene));
            }
            var evaluatorMismatch = AttemptComparison.Compare(WithEvaluator(book.A, "0.9.0", "1.0.0"), book.B);
            Assert.That(evaluatorMismatch.Comparable, Is.False);
            Assert.That(evaluatorMismatch.IncompatibleCode, Is.EqualTo("evaluator_mismatch"));
            Assert.That(evaluatorMismatch.Explanation, Is.Null);
            Assert.That(evaluatorMismatch.Notes.Single(), Does.Contain("not re-evaluated"));
            Assert.That(AttemptComparison.Compare(WithEvaluator(book.A, "1.0.0", "0.9.0"), book.B).IncompatibleCode, Is.EqualTo("catalog_mismatch"));

            var invalidScene = Fixtures.F01;
            var invalid = new SavedAttempt(AttemptSlot.B, new RehearsalRun(new EvaluationResult(Guid.NewGuid(), "2026-11-06T09:00:00Z",
                RouteEvaluator.Evaluate(invalidScene, Fixtures.Catalog, "no_such_route")), invalidScene));
            Assert.That(AttemptComparison.Compare(book.A, invalid).IncompatibleCode, Is.EqualTo("not_viable"));
        }
    }

    /// <summary>File system that fails at a chosen operation (counted across all calls), to simulate interruptions.</summary>
    public sealed class FaultyStorage : IStorage
    {
        readonly FileStorage _real = new FileStorage();
        public int FailAt = -1;
        public int Operations;
        public bool TruncateInsteadOfThrow;

        void Step(string what)
        {
            if (Operations++ == FailAt)
                throw new IOException("simulated interruption at " + what);
        }

        public bool FileExists(string path) => _real.FileExists(path);
        public bool DirectoryExists(string path) => _real.DirectoryExists(path);
        public byte[] Read(string path) => _real.Read(path);

        public void WriteDurable(string path, byte[] bytes)
        {
            if (Operations == FailAt && TruncateInsteadOfThrow)
            {
                Operations++;
                _real.WriteDurable(path, bytes.Take(bytes.Length / 2).ToArray()); // power loss mid-write
                throw new IOException("simulated truncated write");
            }
            Step("write " + Path.GetFileName(path));
            _real.WriteDurable(path, bytes);
        }

        public void Move(string from, string to)
        {
            Step("move " + Path.GetFileName(from));
            _real.Move(from, to);
        }

        public void Delete(string path)
        {
            Step("delete " + Path.GetFileName(path));
            _real.Delete(path);
        }

        public void CreateDirectory(string path) => _real.CreateDirectory(path);
        public void DeleteDirectory(string path) => _real.DeleteDirectory(path);
        public IEnumerable<string> Directories(string path) => _real.Directories(path);
        public IEnumerable<string> Files(string path) => _real.Files(path);
        public long Length(string path) => _real.Length(path);
    }

    public sealed class ProjectStoreTests
    {
        string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "tryscape-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }

        ProjectStore Store(IStorage io = null) => new ProjectStore(_root, Fixtures.Catalog, io);

        static ProjectSnapshot Snapshot(EditingSession editing, AttemptBook book, string updated = "2026-11-06T09:00:00Z", int zoom = 3) =>
            new ProjectSnapshot(editing.Current.ProjectId, "Moving Day 6.0 x 5.0 m", "2026-11-06T08:00:00Z", updated,
                editing.Confirmed, editing.Current, zoom, book.A?.Run, book.B?.Run);

        string ProjectFile(Guid id) => Path.Combine(_root, "projects", id.ToString("D"), "project.json");

        [Test]
        public void SaveAndReopenWithANewStorePreservesCommittedStateAndAttribution()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            Assert.That(Store().Save(Snapshot(editing, book)).Succeeded, Is.True);

            var loaded = Store().Load(editing.Current.ProjectId); // fresh instance: nothing cached
            Assert.That(loaded.Succeeded, Is.True, loaded.Detail);
            Assert.That(loaded.Recovered, Is.False);
            var p = loaded.Value;
            Assert.That(SceneCanonicalizer.SceneHash(p.CurrentScene), Is.EqualTo(SceneCanonicalizer.SceneHash(editing.Current)));
            Assert.That(p.CurrentScene.Revision, Is.EqualTo(1));
            Assert.That(SceneCanonicalizer.SceneHash(p.ConfirmedScene), Is.EqualTo(SceneCanonicalizer.SceneHash(Fixtures.F01)));
            Assert.That(p.TabletopZoomStep, Is.EqualTo(3));
            foreach (var (saved, original) in new[] { (p.AttemptA, book.A.Run), (p.AttemptB, book.B.Run) })
            {
                Assert.That(saved.Result.RunId, Is.EqualTo(original.Result.RunId));
                Assert.That(saved.Result.CreatedAtUtc, Is.EqualTo(original.Result.CreatedAtUtc));
                Assert.That(saved.Evidence.EvidenceHash(), Is.EqualTo(original.Evidence.EvidenceHash()));
                Assert.That(saved.Evidence.SceneHash, Is.EqualTo(original.Evidence.SceneHash));
                Assert.That(saved.Evidence.SceneRevision, Is.EqualTo(original.Evidence.SceneRevision));
                Assert.That(saved.Evidence.EvaluatorVersion, Is.EqualTo(RouteEvaluator.Version));
                Assert.That(saved.Evidence.CatalogVersion, Is.EqualTo("1.0.0"));
            }
            Assert.That(SceneSession.TryRestore(p.ConfirmedScene, p.CurrentScene, Fixtures.Catalog, out var restored, out _), Is.True);
            Assert.That(restored.Current.Revision, Is.EqualTo(1));
            var listing = Store().List();
            Assert.That(listing.Value.Single().RunCount, Is.EqualTo(2));
        }

        [Test]
        public void PythonSerializedFixturesLoadAndHashIdentically()
        {
            // contracts/v1/fixtures/persistence is written by the second (Python) serializer in check_contracts.py.
            var a = SnapshotJson.ReadRun(File.ReadAllBytes(Fixtures.FixturePath("persistence/valid_run_attempt_a.json")));
            var b = SnapshotJson.ReadRun(File.ReadAllBytes(Fixtures.FixturePath("persistence/valid_run_attempt_b.json")));
            var project = SnapshotJson.ReadProject(File.ReadAllBytes(Fixtures.FixturePath("persistence/valid_project.json")));
            var index = SnapshotJson.ReadIndex(File.ReadAllBytes(Fixtures.FixturePath("persistence/valid_index.json")));
            Assert.That(a.Ok, Is.True, a.Problem);
            Assert.That(b.Ok, Is.True, b.Problem);
            Assert.That(project.Ok, Is.True, project.Problem);
            Assert.That(index.Ok, Is.True, index.Problem);
            // The C# evaluator reproduces the evidence the Python reference evaluator stored.
            Assert.That(RouteEvaluator.Evaluate(a.Value.Run.Scene, Fixtures.Catalog, "direct").EvidenceHash(),
                Is.EqualTo(a.Value.Run.Evidence.EvidenceHash()));
            Assert.That(a.Value.Run.Evidence.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
            Assert.That(b.Value.Run.Evidence.RouteLengthMm, Is.EqualTo(4300));
            var report = AttemptComparison.Compare(new SavedAttempt(AttemptSlot.A, a.Value.Run), new SavedAttempt(AttemptSlot.B, b.Value.Run));
            Assert.That(report.Comparable, Is.True);
            // And the C# writer produces the same bytes the Python serializer wrote.
            var rewritten = SnapshotJson.WriteRun(a.Value.ProjectId, a.Value.Label, a.Value.SavedAt, a.Value.Run);
            Assert.That(JToken.DeepEquals(JToken.Parse(Encoding.UTF8.GetString(rewritten)),
                JToken.Parse(File.ReadAllText(Fixtures.FixturePath("persistence/valid_run_attempt_a.json")))), Is.True);
        }

        [Test]
        public void TamperedNewerAndExtraFieldSnapshotsAreRejected()
        {
            var tampered = SnapshotJson.ReadRun(File.ReadAllBytes(Fixtures.FixturePath("persistence/tampered_run_attempt_a.json")));
            Assert.That(tampered.Problem, Is.EqualTo("content_hash_mismatch"));
            var newer = SnapshotJson.ReadProject(File.ReadAllBytes(Fixtures.FixturePath("persistence/invalid_project_newer_major.json")));
            Assert.That(newer.UnsupportedVersion, Is.True);
            var extra = SnapshotJson.ReadProject(File.ReadAllBytes(Fixtures.FixturePath("persistence/invalid_project_extra_field.json")));
            Assert.That(extra.Ok, Is.False);
        }

        [Test]
        public void InterruptionAtEveryWriteStageLeavesALoadableProject()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            var id = editing.Current.ProjectId;
            Assert.That(Store().Save(Snapshot(editing, book, "2026-11-06T09:00:00Z")).Succeeded, Is.True);

            // Count the operations a second save performs, then interrupt it at each one in turn.
            var counter = new FaultyStorage();
            Assert.That(Store(counter).Save(Snapshot(editing, book, "2026-11-06T10:00:00Z")).Succeeded, Is.True);
            var total = counter.Operations;
            Assert.That(total, Is.GreaterThan(5));
            for (var stage = 0; stage < total; stage++)
                foreach (var truncate in new[] { false, true })
                {
                    var faulty = new FaultyStorage { FailAt = stage, TruncateInsteadOfThrow = truncate };
                    var save = Store(faulty).Save(Snapshot(editing, book, $"2026-11-06T11:{stage:00}:00Z"));
                    Assert.That(save.Succeeded, Is.False, $"stage {stage} should have been interrupted");
                    Assert.That(save.Error, Is.EqualTo(StoreError.IoFailure));
                    var loaded = Store().Load(id);
                    Assert.That(loaded.Succeeded, Is.True, $"stage {stage} truncate={truncate}: {loaded.Detail}");
                    Assert.That(SceneCanonicalizer.SceneHash(loaded.Value.CurrentScene), Is.EqualTo(SceneCanonicalizer.SceneHash(editing.Current)));
                    Assert.That(loaded.Value.AttemptA.Result.RunId, Is.EqualTo(book.A.Run.Result.RunId));
                    Assert.That(Store().Save(Snapshot(editing, book, "2026-11-06T12:00:00Z")).Succeeded, Is.True, "store recovers for the next save");
                }
        }

        [Test]
        public void CorruptOrTruncatedPrimaryRecoversFromBackupAndIsPreserved()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            var id = editing.Current.ProjectId;
            Store().Save(Snapshot(editing, book, "2026-11-06T09:00:00Z"));
            Store().Save(Snapshot(editing, book, "2026-11-06T10:00:00Z")); // second save creates project.json.bak
            var primary = ProjectFile(id);
            var good = File.ReadAllBytes(primary);
            File.WriteAllBytes(primary, good.Take(good.Length / 3).ToArray());
            var loaded = Store().Load(id);
            Assert.That(loaded.Succeeded, Is.True, loaded.Detail);
            Assert.That(loaded.Recovered, Is.True);
            Assert.That(loaded.Value.UpdatedAt, Is.EqualTo("2026-11-06T09:00:00Z"), "the previous verified copy was used");
            Assert.That(File.Exists(primary + ".corrupt"), Is.True, "damaged bytes are preserved for diagnosis");

            File.WriteAllText(primary, "{\"schema_version\":\"1.0.0\"}");
            File.Delete(primary + ".bak");
            var lost = Store().Load(id);
            Assert.That(lost.Error, Is.EqualTo(StoreError.Corrupt));

            var flipped = Encoding.UTF8.GetString(good).Replace("\"x_mm\":2500,\"yaw_deg\":0,\"z_mm\":800", "\"x_mm\":2600,\"yaw_deg\":0,\"z_mm\":800");
            Assert.That(flipped, Is.Not.EqualTo(Encoding.UTF8.GetString(good)));
            File.WriteAllText(primary, flipped);
            Assert.That(Store().Load(id).Error, Is.EqualTo(StoreError.Corrupt), "a silently edited coordinate fails the content hash");
        }

        [Test]
        public void InvalidRunReferencesAndNewerVersionsNeverLoadOrGetOverwritten()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            var id = editing.Current.ProjectId;
            Store().Save(Snapshot(editing, book));
            var runs = Path.Combine(_root, "projects", id.ToString("D"), "runs");
            var runA = Path.Combine(runs, book.A.Run.Result.RunId.ToString("D") + ".json");
            var aBytes = File.ReadAllBytes(runA);
            File.Delete(runA);
            Assert.That(Store().Load(id).Detail, Is.EqualTo("run_missing"));

            File.WriteAllBytes(runA, File.ReadAllBytes(Path.Combine(runs, book.B.Run.Result.RunId.ToString("D") + ".json")));
            Assert.That(Store().Load(id).Error, Is.EqualTo(StoreError.Corrupt), "a run file under the wrong ID is rejected");
            File.WriteAllBytes(runA, aBytes);
            Assert.That(Store().Load(id).Succeeded, Is.True);

            var newer = File.ReadAllText(ProjectFile(id)).Replace("\"schema_version\":\"1.1.0\",\"serializer_version\"", "\"schema_version\":\"2.0.0\",\"serializer_version\"");
            File.WriteAllText(ProjectFile(id), newer);
            Assert.That(Store().Load(id).Error, Is.EqualTo(StoreError.UnsupportedVersion));
            var save = Store().Save(Snapshot(editing, book));
            Assert.That(save.Error, Is.EqualTo(StoreError.UnsupportedVersion));
            Assert.That(File.ReadAllText(ProjectFile(id)), Is.EqualTo(newer), "newer-version file is read-protected and not rewritten");
        }

        [Test]
        public void ProjectLimitIsEnforcedWithoutDeletingAndDeleteRemovesEverything()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            var store = Store();
            for (var i = 0; i < ProjectStore.MaxProjects; i++)
            {
                var scene = MovingDayTemplate.Assemble(MovingDayParameters.Example, new RandomIdGenerator());
                var s = new ProjectSnapshot(scene.ProjectId, "Project " + i, "2026-11-06T08:00:00Z", $"2026-11-06T09:{i:00}:00Z",
                    scene, scene, 2, null, null);
                Assert.That(store.Save(s).Succeeded, Is.True, "project " + i);
            }
            var full = store.Save(Snapshot(editing, book));
            Assert.That(full.Error, Is.EqualTo(StoreError.QuotaExceeded));
            Assert.That(full.Detail, Is.EqualTo("too_many_projects"));
            Assert.That(store.List().Value.Count, Is.EqualTo(ProjectStore.MaxProjects), "nothing was deleted to make room");

            var victim = store.List().Value.First();
            Assert.That(store.Delete(victim.ProjectId).Succeeded, Is.True);
            Assert.That(Directory.Exists(Path.Combine(_root, "projects", victim.ProjectId.ToString("D"))), Is.False, "backups go too");
            Assert.That(store.List().Value.Any(p => p.ProjectId == victim.ProjectId), Is.False);
            Assert.That(store.Load(victim.ProjectId).Error, Is.EqualTo(StoreError.NotFound));
            Assert.That(store.Save(Snapshot(editing, book)).Succeeded, Is.True, "room was made only by the user's delete");
        }

        [Test]
        public void OversizedFilesAndIncompatibleCatalogAreRejected()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            var id = editing.Current.ProjectId;
            Store().Save(Snapshot(editing, book));
            File.WriteAllBytes(ProjectFile(id), new byte[SnapshotJson.MaxBytes + 1]);
            File.Delete(ProjectFile(id) + ".bak");
            Assert.That(Store().Load(id).Error, Is.EqualTo(StoreError.Corrupt));

            var otherCatalog = new Catalog(MovingDayPack.PackId, "2.0.0", Fixtures.Catalog.Entries);
            Store().Save(Snapshot(editing, book));
            Assert.That(new ProjectStore(_root, otherCatalog).Load(id).Error, Is.EqualTo(StoreError.IncompatibleContent));
        }

        [Test]
        public void IndexIsRebuiltWhenMissingOrCorrupt()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            Store().Save(Snapshot(editing, book));
            File.WriteAllText(Path.Combine(_root, "index.json"), "not json");
            File.Delete(Path.Combine(_root, "index.json.bak"));
            var listed = Store().List();
            Assert.That(listed.Succeeded, Is.True);
            Assert.That(listed.Value.Single().ProjectId, Is.EqualTo(editing.Current.ProjectId));
        }

        [Test]
        public void NoAudioCredentialsOrTranscriptFieldsArePersisted()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            Store().Save(Snapshot(editing, book));
            foreach (var file in Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file).ToLowerInvariant();
                foreach (var banned in new[] { "audio", "wav", "token", "secret", "password", "session_token", "transcript" })
                    StringAssert.DoesNotContain(banned, text, file);
            }
        }

        [Test]
        public void PresentationZoomDoesNotChangeModelGeometryOrResults()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            Store().Save(Snapshot(editing, book, zoom: 0));
            var small = Store().Load(editing.Current.ProjectId).Value;
            Store().Save(Snapshot(editing, book, zoom: 4));
            var large = Store().Load(editing.Current.ProjectId).Value;
            Assert.That((small.TabletopZoomStep, large.TabletopZoomStep), Is.EqualTo((0, 4)));
            Assert.That(SceneCanonicalizer.SceneHash(small.CurrentScene), Is.EqualTo(SceneCanonicalizer.SceneHash(large.CurrentScene)));
            Assert.That(small.AttemptA.Evidence.EvidenceHash(), Is.EqualTo(large.AttemptA.Evidence.EvidenceHash()));
        }

        [Test]
        public void LegacyProjectMigratesExplicitlyIdempotentlyAndKeepsTheOriginal()
        {
            // Lay out the Python-written legacy 1.0.0 project plus its runs as a device would have stored them.
            var legacyBytes = File.ReadAllBytes(Fixtures.FixturePath("persistence/valid_project_v1_0_0_legacy.json"));
            var legacy = SnapshotJson.ReadProject(legacyBytes);
            Assert.That(legacy.Ok, Is.True, legacy.Problem);
            Assert.That(legacy.Value.StoredSchemaVersion, Is.EqualTo("1.0.0"));
            Assert.That(legacy.Value.YawDeg, Is.EqualTo(0), "migration default");
            var id = legacy.Value.ProjectId;
            var dir = Path.Combine(_root, "projects", id.ToString("D"));
            Directory.CreateDirectory(Path.Combine(dir, "runs"));
            File.WriteAllBytes(Path.Combine(dir, "project.json"), legacyBytes);
            foreach (var f in new[] { "valid_run_attempt_a.json", "valid_run_attempt_b.json" })
            {
                var run = SnapshotJson.ReadRun(File.ReadAllBytes(Fixtures.FixturePath("persistence/" + f))).Value.Run;
                File.Copy(Fixtures.FixturePath("persistence/" + f), Path.Combine(dir, "runs", run.Result.RunId.ToString("D") + ".json"));
            }

            var first = Store().Load(id);
            Assert.That(first.Succeeded, Is.True, first.Detail);
            Assert.That(first.Detail, Is.EqualTo("migrated_from_1_0_0"));
            Assert.That(File.ReadAllBytes(Path.Combine(dir, "project.json")), Is.EqualTo(legacyBytes), "reading never rewrites the original");

            // The migrated snapshot, written by C#, equals the Python serializer's 1.1.0 migration output.
            var written = SnapshotJson.WriteProject(first.Value, first.Value.Runs.Select(r => r.Result.RunId));
            Assert.That(JToken.DeepEquals(JToken.Parse(Encoding.UTF8.GetString(written)),
                JToken.Parse(File.ReadAllText(Fixtures.FixturePath("persistence/valid_project.json")))), Is.True);

            Assert.That(Store().Save(first.Value).Succeeded, Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(dir, "project.json.bak")), Is.EqualTo(legacyBytes), "original kept as the backup");
            var second = Store().Load(id);
            Assert.That(second.Detail, Is.Null, "now stored as 1.1.0: no second migration");
            Assert.That(SceneCanonicalizer.SceneHash(second.Value.CurrentScene), Is.EqualTo(SceneCanonicalizer.SceneHash(first.Value.CurrentScene)));
            Assert.That(second.Value.AttemptA.Evidence.EvidenceHash(), Is.EqualTo(first.Value.AttemptA.Evidence.EvidenceHash()));
            Assert.That(second.Value.AttemptA.Result.RunId, Is.EqualTo(first.Value.AttemptA.Result.RunId), "run attribution preserved");
            Assert.That(Store().Save(second.Value).Succeeded, Is.True);
            Assert.That(Store().Load(id).Value.TabletopYawDeg, Is.EqualTo(0), "migrating twice is idempotent");
        }

        [Test]
        public void TabletopOrientationIsSavedAsPresentationOnly()
        {
            var (editing, _, book) = Journey.AttemptsF01ThenF02();
            var s = new ProjectSnapshot(editing.Current.ProjectId, "Moving Day 6.0 x 5.0 m", "2026-11-06T08:00:00Z", "2026-11-06T09:00:00Z",
                editing.Confirmed, editing.Current, 2, book.A.Run, book.B.Run, 270);
            Store().Save(s);
            var loaded = Store().Load(editing.Current.ProjectId).Value;
            Assert.That(loaded.TabletopYawDeg, Is.EqualTo(270));
            Assert.That(SceneCanonicalizer.SceneHash(loaded.CurrentScene), Is.EqualTo(SceneCanonicalizer.SceneHash(editing.Current)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSnapshot(s.ProjectId, s.Title, s.CreatedAt, s.UpdatedAt,
                s.ConfirmedScene, s.CurrentScene, 2, null, null, 45));
        }

        [Test]
        public void RestoreRejectsMismatchedBaseline()
        {
            var other = MovingDayTemplate.Assemble(MovingDayParameters.Example, new RandomIdGenerator());
            Assert.That(SceneSession.TryRestore(Fixtures.F01, other, Fixtures.Catalog, out _, out var v), Is.False);
            Assert.That(v.Errors.Single().Code, Is.EqualTo("baseline_mismatch"));
        }
    }
}
