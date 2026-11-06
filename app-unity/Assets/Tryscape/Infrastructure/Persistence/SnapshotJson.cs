using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Tryscape.Application.Persistence;
using Tryscape.Application.Rehearsal;
using Tryscape.Core.Canonical;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;
using Tryscape.Infrastructure.Json;

namespace Tryscape.Infrastructure.Persistence
{
    /// <summary>A run snapshot read from disk: the run, its label and the project it belongs to.</summary>
    public sealed class StoredRun
    {
        public StoredRun(Guid projectId, string label, string savedAt, RehearsalRun run)
        {
            ProjectId = projectId;
            Label = label;
            SavedAt = savedAt;
            Run = run;
        }

        public Guid ProjectId { get; }
        public string Label { get; }
        public string SavedAt { get; }
        public RehearsalRun Run { get; }
    }

    /// <summary>The project document as read, before its run references are resolved.</summary>
    public sealed class StoredProject
    {
        public Guid ProjectId;
        public string Title;
        public string CreatedAt;
        public string UpdatedAt;
        public SceneSpec ConfirmedScene;
        public SceneSpec CurrentScene;
        public int ZoomStep;
        public int YawDeg;

        /// <summary>Schema version the file had on disk ("1.0.0" means it was migrated in memory).</summary>
        public string StoredSchemaVersion;
        public Guid? AttemptA;
        public Guid? AttemptB;
        public IReadOnlyList<Guid> SavedRunIds;
    }

    public sealed class SnapshotRead<T>
    {
        SnapshotRead(T value, string problem, bool unsupportedVersion)
        {
            Value = value;
            Problem = problem;
            UnsupportedVersion = unsupportedVersion;
        }

        public T Value { get; }

        /// <summary>Null when valid; otherwise a safe code (for example <c>content_hash_mismatch</c>).</summary>
        public string Problem { get; }

        public bool UnsupportedVersion { get; }
        public bool Ok => Problem == null;

        public static SnapshotRead<T> Of(T value) => new SnapshotRead<T>(value, null, false);
        public static SnapshotRead<T> Bad(string problem, bool unsupported = false) => new SnapshotRead<T>(default, problem, unsupported);
    }

    /// <summary>
    /// Serializer for the S12 snapshot shapes (contracts/v1/schemas/project_snapshot.schema.json), serializer version
    /// <see cref="CanonicalJson.SerializerVersion"/>.
    /// <list type="bullet">
    /// <item>Writing emits S09.1 canonical bytes, so the stored bytes are exactly the bytes covered by
    /// <c>content_hash</c> plus that member.</item>
    /// <item>Reading is strict: bounded size, no duplicate keys, an exact field set, a supported major version, then a
    /// re-serialisation that must reproduce <c>content_hash</c>. For runs, it also checks the evidence hash, the scene
    /// hash and the scene/result identity.</item>
    /// </list>
    /// </summary>
    public static class SnapshotJson
    {
        public const int MaxBytes = 1024 * 1024;
        static readonly string[] ProjectKeys =
        {
            "schema_version", "serializer_version", "project_id", "title", "created_at", "updated_at", "confirmed_scene",
            "current_scene", "presentation", "attempts", "saved_run_ids", "content_hash",
        };
        static readonly string[] RunKeys =
        {
            "schema_version", "serializer_version", "run_id", "project_id", "label", "saved_at", "scene", "result",
            "evidence_hash", "content_hash",
        };
        static readonly string[] ResultKeys =
        {
            "schema_version", "run_id", "scene_id", "scene_revision", "scene_hash", "route_id", "catalog_version",
            "evaluator_version", "outcome", "route_length_mm", "first_violation", "assumptions", "validation_errors", "created_at",
        };

        // ---------------------------------------------------------------- write

        public static byte[] WriteProject(ProjectSnapshot p, IEnumerable<Guid> savedRunIds)
        {
            var ids = savedRunIds.ToList();
            CanonicalObject Body() => CanonicalJson.Object()
                .Set("schema_version", ProjectSnapshot.SchemaVersion)
                .Set("serializer_version", CanonicalJson.SerializerVersion)
                .Set("project_id", SceneCanonicalizer.Uuid(p.ProjectId))
                .Set("title", p.Title)
                .Set("created_at", p.CreatedAt)
                .Set("updated_at", p.UpdatedAt)
                .Set("confirmed_scene", SceneCanonicalizer.ToCanonical(p.ConfirmedScene, true))
                .Set("current_scene", SceneCanonicalizer.ToCanonical(p.CurrentScene, true))
                .Set("presentation", CanonicalJson.Object()
                    .Set("tabletop_zoom_step", p.TabletopZoomStep)
                    .Set("tabletop_yaw_deg", p.TabletopYawDeg))
                .Set("attempts", CanonicalJson.Object()
                    .Set("a", p.AttemptA == null ? null : SceneCanonicalizer.Uuid(p.AttemptA.Result.RunId))
                    .Set("b", p.AttemptB == null ? null : SceneCanonicalizer.Uuid(p.AttemptB.Result.RunId)))
                .Set("saved_run_ids", Ids(ids));
            return Seal(Body(), Body);
        }

        public static byte[] WriteRun(Guid projectId, string label, string savedAt, RehearsalRun run)
        {
            CanonicalObject Body() => CanonicalJson.Object()
                .Set("schema_version", "1.0.0")
                .Set("serializer_version", CanonicalJson.SerializerVersion)
                .Set("run_id", SceneCanonicalizer.Uuid(run.Result.RunId))
                .Set("project_id", SceneCanonicalizer.Uuid(projectId))
                .Set("label", label)
                .Set("saved_at", savedAt)
                .Set("scene", SceneCanonicalizer.ToCanonical(run.Scene, true))
                .Set("result", run.Result.ToWire())
                .Set("evidence_hash", run.Evidence.EvidenceHash());
            return Seal(Body(), Body);
        }

        public static byte[] WriteIndex(IEnumerable<ProjectListing> projects)
        {
            CanonicalObject Body()
            {
                var list = CanonicalJson.Array();
                foreach (var p in projects.OrderBy(p => SceneCanonicalizer.Uuid(p.ProjectId), StringComparer.Ordinal))
                    list.Add(CanonicalJson.Object()
                        .Set("project_id", SceneCanonicalizer.Uuid(p.ProjectId))
                        .Set("title", p.Title)
                        .Set("updated_at", p.UpdatedAt)
                        .Set("run_count", p.RunCount));
                return CanonicalJson.Object().Set("schema_version", "1.0.0").Set("projects", list);
            }
            return Seal(Body(), Body);
        }

        static CanonicalArray Ids(IEnumerable<Guid> ids)
        {
            var array = CanonicalJson.Array();
            foreach (var id in ids)
                array.Add(SceneCanonicalizer.Uuid(id));
            return array;
        }

        /// <summary>Hash the body, then emit the same body plus content_hash (a CanonicalObject cannot be re-keyed).</summary>
        static byte[] Seal(CanonicalObject body, Func<CanonicalObject> rebuild)
        {
            var hash = CanonicalJson.Sha256Hex(CanonicalJson.ToBytes(body));
            return CanonicalJson.ToBytes(rebuild().Set("content_hash", hash));
        }

        // ---------------------------------------------------------------- read

        static bool Major(JObject o, out bool unsupported)
        {
            unsupported = false;
            var v = o["schema_version"];
            if (v == null || v.Type != JTokenType.String)
                return false;
            var text = (string)v;
            if (text == "1.0.0")
                return true;
            unsupported = !text.StartsWith("1.", StringComparison.Ordinal);
            return false;
        }

        static bool Parse(byte[] bytes, out JObject o, out string problem)
        {
            o = null;
            problem = null;
            if (bytes == null || bytes.Length == 0)
            {
                problem = "empty";
                return false;
            }
            if (bytes.Length > MaxBytes)
            {
                problem = "too_large";
                return false;
            }
            string text;
            try
            {
                text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                problem = "invalid_utf8";
                return false;
            }
            if (!StrictJson.TryParse(text, MaxBytes, out var token, out var error) || !(token is JObject obj))
            {
                problem = error.Code ?? "not_an_object";
                return false;
            }
            o = obj;
            return true;
        }

        /// <summary>
        /// Reads a project snapshot. Version 1.1.0 is read directly. Version 1.0.0 is migrated by an explicit function: its
        /// own content hash is verified first, then <c>tabletop_yaw_deg = 0</c> is added in memory. The file on disk is not
        /// rewritten until the next verified save, which keeps the original as the backup. Any other 1.x version is
        /// rejected as malformed; 2.x and later is reported as unsupported and is read-protected.
        /// </summary>
        public static SnapshotRead<StoredProject> ReadProject(byte[] bytes)
        {
            if (!Parse(bytes, out var o, out var problem))
                return SnapshotRead<StoredProject>.Bad(problem);
            var version = o["schema_version"]?.Type == JTokenType.String ? (string)o["schema_version"] : null;
            if (version != ProjectSnapshot.SchemaVersion && version != ProjectSnapshot.LegacySchemaVersion)
            {
                var unsupported = version != null && !version.StartsWith("1.", StringComparison.Ordinal);
                return SnapshotRead<StoredProject>.Bad(unsupported ? "unsupported_version" : "bad_schema_version", unsupported);
            }
            var legacy = version == ProjectSnapshot.LegacySchemaVersion;
            var r = new ShapeReader();
            var p = r.Object(o, "", ProjectKeys);
            if (p == null)
                return SnapshotRead<StoredProject>.Bad("shape");
            var stored = new StoredProject
            {
                ProjectId = r.Uuid(p, "project_id", ""),
                Title = r.PlainText(p, "title", "", 1, 80),
                CreatedAt = r.Timestamp(p, "created_at", ""),
                UpdatedAt = r.Timestamp(p, "updated_at", ""),
                ConfirmedScene = SceneSpecJsonReader.ReadScene(p["confirmed_scene"], "confirmed_scene", r),
                CurrentScene = SceneSpecJsonReader.ReadScene(p["current_scene"], "current_scene", r),
            };
            r.Semver(p, "serializer_version", "");
            var pres = legacy
                ? r.Object(p["presentation"], "presentation", "tabletop_zoom_step")
                : r.Object(p["presentation"], "presentation", "tabletop_zoom_step", "tabletop_yaw_deg");
            stored.ZoomStep = pres == null ? 0 : r.Int(pres, "tabletop_zoom_step", "presentation", 0, ProjectSnapshot.MaxZoomStep);
            stored.YawDeg = pres == null || legacy ? 0 : r.IntEnum(pres, "tabletop_yaw_deg", "presentation", 0, 90, 180, 270);
            stored.StoredSchemaVersion = version;
            var att = r.Object(p["attempts"], "attempts", "a", "b");
            stored.AttemptA = att == null ? null : NullableUuid(att, "a", r);
            stored.AttemptB = att == null ? null : NullableUuid(att, "b", r);
            var ids = new List<Guid>();
            var arr = r.Array(p, "saved_run_ids", "", 0, 20);
            if (arr != null)
                for (var i = 0; i < arr.Count; i++)
                    ids.Add(r.Uuid(new JObject { ["id"] = arr[i] }, "id", $"saved_run_ids[{i}]"));
            stored.SavedRunIds = ids;
            var hash = r.String(p, "content_hash", "");
            if (r.HasErrors)
                return SnapshotRead<StoredProject>.Bad("shape");
            if (ids.Distinct().Count() != ids.Count)
                return SnapshotRead<StoredProject>.Bad("duplicate_run_ids");
            if ((stored.AttemptA.HasValue && !ids.Contains(stored.AttemptA.Value)) ||
                (stored.AttemptB.HasValue && !ids.Contains(stored.AttemptB.Value)))
                return SnapshotRead<StoredProject>.Bad("attempt_not_saved");

            // Integrity: re-serialise what was read; it must reproduce the stored hash exactly.
            var rebuilt = new ProjectSnapshot(stored.ProjectId, stored.Title, stored.CreatedAt, stored.UpdatedAt,
                stored.ConfirmedScene, stored.CurrentScene, stored.ZoomStep, null, null, stored.YawDeg);
            var presentation = CanonicalJson.Object().Set("tabletop_zoom_step", rebuilt.TabletopZoomStep);
            if (!legacy)
                presentation.Set("tabletop_yaw_deg", rebuilt.TabletopYawDeg);
            var body = CanonicalJson.Object()
                .Set("schema_version", version)
                .Set("serializer_version", (string)p["serializer_version"])
                .Set("project_id", SceneCanonicalizer.Uuid(rebuilt.ProjectId))
                .Set("title", rebuilt.Title)
                .Set("created_at", rebuilt.CreatedAt)
                .Set("updated_at", rebuilt.UpdatedAt)
                .Set("confirmed_scene", SceneCanonicalizer.ToCanonical(rebuilt.ConfirmedScene, true))
                .Set("current_scene", SceneCanonicalizer.ToCanonical(rebuilt.CurrentScene, true))
                .Set("presentation", presentation)
                .Set("attempts", CanonicalJson.Object()
                    .Set("a", stored.AttemptA.HasValue ? SceneCanonicalizer.Uuid(stored.AttemptA.Value) : null)
                    .Set("b", stored.AttemptB.HasValue ? SceneCanonicalizer.Uuid(stored.AttemptB.Value) : null))
                .Set("saved_run_ids", Ids(ids));
            if (CanonicalJson.Sha256Hex(CanonicalJson.ToBytes(body)) != hash)
                return SnapshotRead<StoredProject>.Bad("content_hash_mismatch");
            if (stored.ConfirmedScene.ProjectId != stored.ProjectId || stored.CurrentScene.ProjectId != stored.ProjectId)
                return SnapshotRead<StoredProject>.Bad("project_reference");
            return SnapshotRead<StoredProject>.Of(stored);
        }

        public static SnapshotRead<StoredRun> ReadRun(byte[] bytes)
        {
            if (!Parse(bytes, out var o, out var problem))
                return SnapshotRead<StoredRun>.Bad(problem);
            if (!Major(o, out var unsupported))
                return SnapshotRead<StoredRun>.Bad(unsupported ? "unsupported_version" : "bad_schema_version", unsupported);
            var r = new ShapeReader();
            var p = r.Object(o, "", RunKeys);
            if (p == null)
                return SnapshotRead<StoredRun>.Bad("shape");
            var serializer = r.Semver(p, "serializer_version", "");
            var runId = r.Uuid(p, "run_id", "");
            var projectId = r.Uuid(p, "project_id", "");
            var label = r.PlainText(p, "label", "", 1, 80);
            var savedAt = r.Timestamp(p, "saved_at", "");
            var scene = SceneSpecJsonReader.ReadScene(p["scene"], "scene", r);
            var result = ReadResult(p["result"], "result", r);
            var evidenceHash = r.String(p, "evidence_hash", "");
            var hash = r.String(p, "content_hash", "");
            if (r.HasErrors || scene == null || result == null)
                return SnapshotRead<StoredRun>.Bad("shape");
            if (result.RunId != runId)
                return SnapshotRead<StoredRun>.Bad("run_id_mismatch");

            var body = CanonicalJson.Object()
                .Set("schema_version", "1.0.0")
                .Set("serializer_version", serializer)
                .Set("run_id", SceneCanonicalizer.Uuid(runId))
                .Set("project_id", SceneCanonicalizer.Uuid(projectId))
                .Set("label", label)
                .Set("saved_at", savedAt)
                .Set("scene", SceneCanonicalizer.ToCanonical(scene, true))
                .Set("result", result.ToWire())
                .Set("evidence_hash", evidenceHash);
            if (CanonicalJson.Sha256Hex(CanonicalJson.ToBytes(body)) != hash)
                return SnapshotRead<StoredRun>.Bad("content_hash_mismatch");
            if (result.Evidence.EvidenceHash() != evidenceHash)
                return SnapshotRead<StoredRun>.Bad("evidence_hash_mismatch");
            if (result.Evidence.SceneHash != null && result.Evidence.SceneHash != SceneCanonicalizer.SceneHash(scene))
                return SnapshotRead<StoredRun>.Bad("scene_hash_mismatch");
            if (scene.ProjectId != projectId)
                return SnapshotRead<StoredRun>.Bad("project_reference");
            RehearsalRun run;
            try
            {
                run = new RehearsalRun(result, scene); // checks scene id and revision against the evidence
            }
            catch (ArgumentException)
            {
                return SnapshotRead<StoredRun>.Bad("scene_result_mismatch");
            }
            return SnapshotRead<StoredRun>.Of(new StoredRun(projectId, label, savedAt, run));
        }

        public static SnapshotRead<IReadOnlyList<ProjectListing>> ReadIndex(byte[] bytes)
        {
            if (!Parse(bytes, out var o, out var problem))
                return SnapshotRead<IReadOnlyList<ProjectListing>>.Bad(problem);
            if (!Major(o, out var unsupported))
                return SnapshotRead<IReadOnlyList<ProjectListing>>.Bad("bad_schema_version", unsupported);
            var r = new ShapeReader();
            var p = r.Object(o, "", "schema_version", "projects", "content_hash");
            var list = new List<ProjectListing>();
            var arr = p == null ? null : r.Array(p, "projects", "", 0, 20);
            if (arr != null)
                foreach (var item in arr)
                {
                    var e = r.Object(item, "projects[]", "project_id", "title", "updated_at", "run_count");
                    if (e == null)
                        continue;
                    list.Add(new ProjectListing(r.Uuid(e, "project_id", ""), r.PlainText(e, "title", "", 1, 80),
                        r.Timestamp(e, "updated_at", ""), r.Int(e, "run_count", "", 0, 20)));
                }
            if (p == null || r.HasErrors)
                return SnapshotRead<IReadOnlyList<ProjectListing>>.Bad("shape");
            var expected = SnapshotJson.WriteIndex(list);
            var stored = (string)p["content_hash"];
            var rebuilt = (string)JObject.Parse(new System.Text.UTF8Encoding(false).GetString(expected))["content_hash"];
            return stored == rebuilt
                ? SnapshotRead<IReadOnlyList<ProjectListing>>.Of(list)
                : SnapshotRead<IReadOnlyList<ProjectListing>>.Bad("content_hash_mismatch");
        }

        static Guid? NullableUuid(JObject o, string key, ShapeReader r)
        {
            var t = o[key];
            if (t != null && t.Type == JTokenType.Null)
                return null;
            return r.Uuid(o, key, "attempts");
        }

        /// <summary>Strict EvaluationResult reader (evaluation_result.schema.json), rebuilt through the Core constructors.</summary>
        public static EvaluationResult ReadResult(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, ResultKeys);
            if (o == null)
                return null;
            r.Exact(o, "schema_version", path, "1.0.0", ShapeCodes.UnsupportedVersion);
            var runId = r.Uuid(o, "run_id", path);
            var sceneId = r.Uuid(o, "scene_id", path);
            var revision = r.Long(o, "scene_revision", path, 0, int.MaxValue);
            string sceneHash = null;
            if (o["scene_hash"]?.Type != JTokenType.Null)
            {
                sceneHash = r.String(o, "scene_hash", path);
                if (sceneHash != null && !System.Text.RegularExpressions.Regex.IsMatch(sceneHash, "^[0-9a-f]{64}$"))
                    r.Fail(path + ".scene_hash", ShapeCodes.InvalidFormat);
            }
            var routeId = r.Slug(o, "route_id", path);
            var catalog = r.Semver(o, "catalog_version", path);
            var evaluator = r.Semver(o, "evaluator_version", path);
            var outcomeText = r.Enum(o, "outcome", path, "clear_in_model", "blocked_in_model", "invalid_input", "unsupported");
            long? length = null;
            if (o["route_length_mm"]?.Type != JTokenType.Null)
                length = r.Long(o, "route_length_mm", path, 0, 200000);
            Violation violation = null;
            if (o["first_violation"]?.Type != JTokenType.Null)
                violation = ReadViolation(o["first_violation"], path + ".first_violation", r);
            var assumptions = new List<Assumption>();
            var aa = r.Array(o, "assumptions", path, 0, 16);
            if (aa != null)
                foreach (var a in aa)
                {
                    var ao = r.Object(a, path + ".assumptions[]", "code", "text", "source", "requires_confirmation");
                    if (ao == null)
                        continue;
                    var source = r.Enum(ao, "source", path, "template", "scenario_model", "user_request", "example_default");
                    var code = r.Slug(ao, "code", path);
                    var text = r.PlainText(ao, "text", path, 1, 240);
                    var confirm = r.Bool(ao, "requires_confirmation", path);
                    if (code != null && text != null && source != null)
                        assumptions.Add(new Assumption(code, text,
                            source == "template" ? AssumptionSource.Template
                            : source == "scenario_model" ? AssumptionSource.ScenarioModel
                            : source == "user_request" ? AssumptionSource.UserRequest : AssumptionSource.ExampleDefault, confirm));
                }
            var errors = new List<ValidationError>();
            var ea = r.Array(o, "validation_errors", path, 0, 32);
            if (ea != null)
                foreach (var e in ea)
                {
                    var eo = r.Object(e, path + ".validation_errors[]", "path", "code");
                    if (eo != null)
                        errors.Add(new ValidationError(r.String(eo, "path", path) ?? "", r.Slug(eo, "code", path) ?? "invalid"));
                }
            var createdAt = r.Timestamp(o, "created_at", path);
            if (r.HasErrors || outcomeText == null || routeId == null || catalog == null || evaluator == null || createdAt == null)
                return null;
            var outcome = outcomeText == "clear_in_model" ? EvaluationOutcome.ClearInModel
                : outcomeText == "blocked_in_model" ? EvaluationOutcome.BlockedInModel
                : outcomeText == "invalid_input" ? EvaluationOutcome.InvalidInput : EvaluationOutcome.Unsupported;
            try
            {
                var evidence = new EvaluationEvidence(sceneId, revision, sceneHash, routeId, catalog, evaluator, outcome, length,
                    violation, assumptions, errors);
                return new EvaluationResult(runId, createdAt, evidence);
            }
            catch (ArgumentException)
            {
                r.Fail(path, "inconsistent_result");
                return null;
            }
        }

        static Violation ReadViolation(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "rule_code", "entity_ids", "segment_index", "entry_fraction", "contact_center",
                "model_margin_mm", "message_key");
            if (o == null)
                return null;
            var rule = r.Slug(o, "rule_code", path);
            var ids = new List<Guid>();
            var arr = r.Array(o, "entity_ids", path, 0, 24);
            if (arr != null)
                for (var i = 0; i < arr.Count; i++)
                    ids.Add(r.Uuid(new JObject { ["id"] = arr[i] }, "id", path + ".entity_ids"));
            var segment = r.Int(o, "segment_index", path, 0, 11);
            var fraction = ReadRational(o["entry_fraction"], path + ".entry_fraction", r);
            var center = r.Object(o["contact_center"], path + ".contact_center", "x_mm", "z_mm");
            var x = center == null ? Rational.Zero : ReadRational(center["x_mm"], path + ".contact_center.x_mm", r);
            var z = center == null ? Rational.Zero : ReadRational(center["z_mm"], path + ".contact_center.z_mm", r);
            var margin = r.Int(o, "model_margin_mm", path, 0, 1000);
            var key = r.Slug(o, "message_key", path);
            return rule == null || key == null ? null : new Violation(rule, ids, segment, fraction, x, z, margin, key);
        }

        static Rational ReadRational(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "numerator", "denominator");
            if (o == null)
                return Rational.Zero;
            var n = r.Long(o, "numerator", path, -4_000_000_000_000L, 4_000_000_000_000L);
            var d = r.Long(o, "denominator", path, 1, 4_000_000_000_000L);
            if (r.HasErrors)
                return Rational.Zero;
            var value = new Rational(n, d);
            if (value.Numerator != n || value.Denominator != d)
                r.Fail(path, "not_lowest_terms"); // canonical rationals are stored reduced
            return value;
        }
    }
}
