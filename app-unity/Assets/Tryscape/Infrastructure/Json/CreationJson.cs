using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tryscape.Application.Creation;
using Tryscape.Core.Model;
using Tryscape.Core.Validation;

namespace Tryscape.Infrastructure.Json
{
    public sealed class ProposalParseResult
    {
        public ProposalParseResult(SceneProposal proposal, IReadOnlyList<ValidationError> errors)
        {
            Proposal = proposal;
            Errors = errors ?? Array.Empty<ValidationError>();
        }

        public SceneProposal Proposal { get; }
        public IReadOnlyList<ValidationError> Errors { get; }
        public bool Succeeded => Proposal != null;
    }

    /// <summary>Job record from <c>GET /v1/jobs/{id}</c> (api.schema.json#/$defs/job), kind already checked.</summary>
    public sealed class JobView
    {
        public JobView(Guid jobId, string status, string kind, JToken result, string errorCode, bool errorRetryable)
        {
            JobId = jobId;
            Status = status;
            Kind = kind;
            Result = result;
            ErrorCode = errorCode;
            ErrorRetryable = errorRetryable;
        }

        public Guid JobId { get; }
        public string Status { get; }
        public string Kind { get; }
        public JToken Result { get; }
        public string ErrorCode { get; }
        public bool ErrorRetryable { get; }
        public bool IsTerminal => Status == "succeeded" || Status == "failed" || Status == "cancelled" || Status == "expired";
    }

    /// <summary>
    /// Strict readers and writers for the creation API wire shapes (contracts/v1 api.schema.json and
    /// scene_proposal.schema.json). Every field is read explicitly; unknown fields, duplicate keys and wrong types fail.
    /// API output is bounded at 1 MiB (S13.1).
    /// </summary>
    public static class CreationJson
    {
        public const int MaxResponseChars = 1024 * 1024;
        public const string ProposalKind = "scene_proposal";

        static readonly string[] ProposalKeys =
            { "schema_version", "proposal_id", "status", "transcript", "intent", "scene", "questions", "assumptions", "provider_metadata" };

        static readonly string[] JobStatuses =
            { "queued", "running", "succeeded", "failed", "cancel_requested", "cancelled", "expired" };

        public static ProposalParseResult ReadProposal(string json)
        {
            if (!StrictJson.TryParse(json, MaxResponseChars, out var token, out var error))
                return new ProposalParseResult(null, new[] { error });
            return ReadProposal(token);
        }

        public static ProposalParseResult ReadProposal(JToken token)
        {
            var r = new ShapeReader();
            var proposal = ReadProposal(token, "", r);
            return r.HasErrors || proposal == null ? new ProposalParseResult(null, r.Errors) : new ProposalParseResult(proposal, Array.Empty<ValidationError>());
        }

        static SceneProposal ReadProposal(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, ProposalKeys);
            if (o == null)
                return null;
            r.Exact(o, "schema_version", path, SceneProposal.SchemaVersion, ShapeCodes.UnsupportedVersion);
            var id = r.Uuid(o, "proposal_id", path);
            var statusText = r.Enum(o, "status", path, "ready", "needs_clarification", "unsupported");
            var transcript = r.String(o, "transcript", path);
            if (transcript != null && transcript.Length > SceneProposal.MaxTranscriptChars)
                r.Fail(ShapeReader.Join(path, "transcript"), ShapeCodes.OutOfRange);

            ProposalIntent intent = null;
            var intentToken = o["intent"];
            if (intentToken != null && intentToken.Type != JTokenType.Null)
                intent = ReadIntent(intentToken, ShapeReader.Join(path, "intent"), r);

            SceneSpec scene = null;
            var sceneToken = o["scene"];
            if (sceneToken != null && sceneToken.Type != JTokenType.Null)
                scene = SceneSpecJsonReader.ReadScene(sceneToken, ShapeReader.Join(path, "scene"), r);

            var questions = new List<ProposalQuestion>();
            var qa = r.Array(o, "questions", path, 0, 3);
            if (qa != null)
                for (var i = 0; i < qa.Count; i++)
                {
                    var q = ReadQuestion(qa[i], ShapeReader.Index(ShapeReader.Join(path, "questions"), i), r);
                    if (q != null)
                        questions.Add(q);
                }

            var assumptions = new List<Assumption>();
            var aa = r.Array(o, "assumptions", path, 0, 16);
            if (aa != null)
                for (var i = 0; i < aa.Count; i++)
                {
                    var a = ReadAssumption(aa[i], ShapeReader.Index(ShapeReader.Join(path, "assumptions"), i), r);
                    if (a != null)
                        assumptions.Add(a);
                }

            var metaPath = ShapeReader.Join(path, "provider_metadata");
            var meta = r.Object(o["provider_metadata"], metaPath, "provider_id", "version");
            var providerId = meta == null ? null : r.Slug(meta, "provider_id", metaPath);
            var providerVersion = meta == null ? null : r.Semver(meta, "version", metaPath);

            if (statusText == null || transcript == null || r.HasErrors)
                return null;
            var status = statusText == "ready" ? ProposalStatus.Ready
                : statusText == "needs_clarification" ? ProposalStatus.NeedsClarification
                : ProposalStatus.Unsupported;

            // Conditional rules from the schema's allOf.
            if (status == ProposalStatus.Ready && (intent == null || scene == null || questions.Count != 0))
                r.Fail(path, "ready_requires_intent_scene_no_questions");
            if (status != ProposalStatus.Ready && scene != null)
                r.Fail(ShapeReader.Join(path, "scene"), "scene_only_when_ready");
            if (status == ProposalStatus.NeedsClarification && questions.Count == 0)
                r.Fail(ShapeReader.Join(path, "questions"), ShapeCodes.TooFewItems);
            if (r.HasErrors)
                return null;
            return new SceneProposal(id, status, transcript, intent, scene, questions, assumptions, providerId, providerVersion);
        }

        static ProposalIntent ReadIntent(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "scenario_pack_id", "room_width_mm", "room_depth_mm", "payload_catalog_id",
                "obstruction_state", "preferred_route_id");
            if (o == null)
                return null;
            var pack = r.Enum(o, "scenario_pack_id", path, "moving_day");
            var width = r.Int(o, "room_width_mm", path, RoomSpec.MinDimensionMm, RoomSpec.MaxDimensionMm);
            var depth = r.Int(o, "room_depth_mm", path, RoomSpec.MinDimensionMm, RoomSpec.MaxDimensionMm);
            var payload = r.Slug(o, "payload_catalog_id", path);
            var obstruction = r.Enum(o, "obstruction_state", path, "center", "parked_south");
            var route = r.Slug(o, "preferred_route_id", path);
            return pack == null || payload == null || obstruction == null || route == null
                ? null
                : new ProposalIntent(pack, width, depth, payload, obstruction, route);
        }

        static ProposalQuestion ReadQuestion(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "field", "prompt", "choices");
            if (o == null)
                return null;
            var field = r.Slug(o, "field", path);
            var prompt = r.PlainText(o, "prompt", path, 1, 160);
            var choices = new List<string>();
            var ca = r.Array(o, "choices", path, 2, 4);
            if (ca != null)
                for (var i = 0; i < ca.Count; i++)
                {
                    var holder = new JObject { ["c"] = ca[i] };
                    var choice = r.PlainText(holder, "c", ShapeReader.Index(ShapeReader.Join(path, "choices"), i), 1, 80);
                    if (choice != null)
                        choices.Add(choice);
                }
            return field == null || prompt == null ? null : new ProposalQuestion(field, prompt, choices);
        }

        static Assumption ReadAssumption(JToken token, string path, ShapeReader r)
        {
            var o = r.Object(token, path, "code", "text", "source", "requires_confirmation");
            if (o == null)
                return null;
            var code = r.Slug(o, "code", path);
            var text = r.PlainText(o, "text", path, 1, 240);
            var source = r.Enum(o, "source", path, "template", "scenario_model", "user_request", "example_default");
            var confirm = r.Bool(o, "requires_confirmation", path);
            if (code == null || text == null || source == null)
                return null;
            var s = source == "template" ? AssumptionSource.Template
                : source == "scenario_model" ? AssumptionSource.ScenarioModel
                : source == "user_request" ? AssumptionSource.UserRequest
                : AssumptionSource.ExampleDefault;
            return new Assumption(code, text, s, confirm);
        }

        // ---- API envelopes ----

        public static string ProposalJobRequest(string transcript, string catalogVersion)
        {
            var body = new JObject
            {
                ["schema_version"] = "1.0.0",
                ["transcript"] = transcript,
                ["locale"] = "en",
                ["scenario_pack_id"] = "moving_day",
                ["catalog_version"] = catalogVersion,
            };
            return body.ToString(Formatting.None);
        }

        public static bool TryReadSessionToken(string json, out string token)
        {
            token = null;
            if (!StrictJson.TryParse(json, 4096, out var t, out _))
                return false;
            var r = new ShapeReader();
            var o = r.Object(t, "", "session_token", "expires_at");
            if (o == null)
                return false;
            var value = r.String(o, "session_token", "");
            r.Timestamp(o, "expires_at", "");
            if (r.HasErrors || value == null || value.Length != 43)
                return false;
            foreach (var c in value)
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-'))
                    return false;
            token = value;
            return true;
        }

        public static bool TryReadReceipt(string json, out Guid jobId, out int pollAfterMs)
        {
            jobId = Guid.Empty;
            pollAfterMs = 0;
            if (!StrictJson.TryParse(json, 4096, out var t, out _))
                return false;
            var r = new ShapeReader();
            var o = r.Object(t, "", "job_id", "status", "poll_after_ms");
            if (o == null)
                return false;
            jobId = r.Uuid(o, "job_id", "");
            r.Enum(o, "status", "", JobStatuses);
            pollAfterMs = r.Int(o, "poll_after_ms", "", 100, 10000);
            return !r.HasErrors;
        }

        /// <summary>Reads a job record; rejects shapes that break the schema's status/result rules or the expected kind.</summary>
        public static bool TryReadJob(string json, string expectedKind, out JobView job)
        {
            job = null;
            if (!StrictJson.TryParse(json, MaxResponseChars, out var t, out _))
                return false;
            var r = new ShapeReader();
            var o = r.Object(t, "", "job_id", "status", "kind", "result_schema_version", "result", "error");
            if (o == null)
                return false;
            var id = r.Uuid(o, "job_id", "");
            var status = r.Enum(o, "status", "", JobStatuses);
            var kind = r.Enum(o, "kind", "", "scene_proposal", "transcription");
            var result = o["result"];
            var hasResult = result != null && result.Type != JTokenType.Null;
            string errorCode = null;
            var retryable = false;
            var errorToken = o["error"];
            if (errorToken != null && errorToken.Type != JTokenType.Null)
            {
                var e = r.Object(errorToken, "error", "code", "retryable");
                if (e != null)
                {
                    errorCode = r.Slug(e, "code", "error");
                    retryable = r.Bool(e, "retryable", "error");
                }
            }
            if (r.HasErrors || status == null || kind == null || kind != expectedKind)
                return false;
            if (status == "succeeded" ? !hasResult || errorCode != null : hasResult)
                return false;
            if (status == "succeeded" && result.Type != JTokenType.Object)
                return false;
            job = new JobView(id, status, kind, hasResult ? result : null, errorCode, retryable);
            return true;
        }

        /// <summary>Error envelope code (S13.3), or null if the body is not a valid envelope.</summary>
        public static string ErrorCode(string json)
        {
            if (string.IsNullOrEmpty(json) || !StrictJson.TryParse(json, 64 * 1024, out var t, out _))
                return null;
            return t is JObject o && o["error"] is JObject e && e["code"]?.Type == JTokenType.String ? (string)e["code"] : null;
        }
    }
}
