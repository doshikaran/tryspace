using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tryscape.Application.Creation;
using Tryscape.Infrastructure.Json;
using UnityEngine.Networking;

namespace Tryscape.Unity.Creation
{
    /// <summary>
    /// Where the creation service lives for this build. This is configuration only; the pipeline does not change when
    /// a deployment is added.
    /// <list type="bullet">
    /// <item>The Editor and development builds default to the developer machine on 127.0.0.1:8000. On a Quest this is
    /// reached with <c>adb reverse tcp:8000 tcp:8000</c>, so no LAN address is compiled in.</item>
    /// <item>Release builds need an allowlisted HTTPS origin. None is approved yet, so the service is reported
    /// unavailable and hand creation is offered (S13, S21.1).</item>
    /// </list>
    /// </summary>
    public sealed class CreationServiceConfig
    {
        public const string ConsentVersion = "r0-dev-2026-11";
        public const string DevelopmentBaseUrl = "http://127.0.0.1:8000";

        CreationServiceConfig(string baseUrl) => BaseUrl = baseUrl;

        /// <summary>Null when no service is configured for this build.</summary>
        public string BaseUrl { get; }

        public bool IsConfigured => BaseUrl != null;

        public static CreationServiceConfig ForThisBuild()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var overrideUrl = Environment.GetEnvironmentVariable("TRYSCAPE_API_URL");
            if (!string.IsNullOrEmpty(overrideUrl) && IsAllowedDevelopmentUrl(overrideUrl))
                return new CreationServiceConfig(overrideUrl.TrimEnd('/'));
            return new CreationServiceConfig(DevelopmentBaseUrl);
#else
            return new CreationServiceConfig(null);
#endif
        }

        public static CreationServiceConfig For(string baseUrl) =>
            new CreationServiceConfig(baseUrl != null && IsAllowedDevelopmentUrl(baseUrl) ? baseUrl.TrimEnd('/') : null);

        static bool IsAllowedDevelopmentUrl(string url) =>
            url.StartsWith("https://", StringComparison.Ordinal) ||
            url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) ||
            url.StartsWith("http://localhost:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Shared transport for the creation job API (S13.1). It holds the anonymous session token in memory only, and every
    /// request has a bounded timeout and a request ID. Job creation uses an idempotency key: a lost create response is
    /// retried once with the same key, so it never creates a second job. Polling runs at 1 s, then about 2 s with
    /// jitter, and a read is retried at most twice. On cancellation the in-flight request is aborted on the main thread
    /// and the job is cancelled as best effort. Only safe codes leave this class, never raw transport or server text.
    /// </summary>
    public sealed class CreationServiceClient
    {
        const int RequestTimeoutSeconds = 5;
        const int UploadTimeoutSeconds = 15;
        readonly CreationServiceConfig _config;
        readonly Random _jitter = new Random();
        string _sessionToken;

        public CreationServiceClient(CreationServiceConfig config) =>
            _config = config ?? throw new ArgumentNullException(nameof(config));

        public bool IsConfigured => _config.IsConfigured;

        public readonly struct Response
        {
            public Response(long status, string body)
            {
                Status = status;
                Body = body;
            }

            /// <summary>HTTP status, or 0 for a connection/transport failure.</summary>
            public long Status { get; }

            public string Body { get; }
        }

        /// <summary>Result of a job round trip: a terminal job view, or a failure code.</summary>
        public readonly struct JobRun
        {
            public JobRun(JobView job, ProposalFailure failure, string code, bool retryable)
            {
                Job = job;
                Failure = failure;
                Code = code;
                Retryable = retryable;
            }

            public JobView Job { get; }
            public ProposalFailure Failure { get; }
            public string Code { get; }
            public bool Retryable { get; }
        }

        /// <summary>Creates a job, polls it to a terminal state and returns the job view (or a typed failure).</summary>
        public async Task<JobRun> RunJob(string path, byte[] body, string contentType, string kind,
            IReadOnlyDictionary<string, string> extraHeaders, CancellationToken ct)
        {
            if (!_config.IsConfigured)
                return new JobRun(null, ProposalFailure.Unavailable, "service_not_configured", false);

            if (_sessionToken == null)
            {
                var session = await Send("POST", "/v1/anonymous-sessions", Encoding.UTF8.GetBytes("{}"), "application/json", null, ct, false);
                if (session.Status != 201 || !CreationJson.TryReadSessionToken(session.Body, out var token))
                    return HttpFailure(session);
                _sessionToken = token;
            }

            var headers = new Dictionary<string, string>
            {
                ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                ["X-Processing-Consent-Version"] = CreationServiceConfig.ConsentVersion,
            };
            if (extraHeaders != null)
                foreach (var pair in extraHeaders)
                    headers[pair.Key] = pair.Value;

            Response created = default;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                created = await Send("POST", path, body, contentType, headers, ct, true);
                if (created.Status != 0)
                    break; // a network failure (status 0) is retried once with the same idempotency key
            }
            if (created.Status == 401)
            {
                _sessionToken = null; // expired session: the next attempt mints a new one
                return new JobRun(null, ProposalFailure.Unavailable, "session_expired", true);
            }
            if (created.Status != 202 || !CreationJson.TryReadReceipt(created.Body, out var jobId, out var pollAfterMs))
                return HttpFailure(created);

            var delayMs = Math.Max(pollAfterMs, 1000);
            var retries = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(delayMs, ct);
                    var polled = await Send("GET", $"/v1/jobs/{jobId:D}", null, null, null, ct, true);
                    if (polled.Status == 0 && retries++ < 2)
                        continue;
                    if (polled.Status != 200)
                        return HttpFailure(polled);
                    if (!CreationJson.TryReadJob(polled.Body, kind, out var job) || job.JobId != jobId)
                        return new JobRun(null, ProposalFailure.InvalidResponse, "job_shape", false);
                    if (job.IsTerminal)
                        return new JobRun(job, ProposalFailure.None, null, false);
                    delayMs = 2000 + _jitter.Next(-250, 250);
                }
            }
            catch (OperationCanceledException)
            {
                _ = CancelJob(jobId); // best effort; bounded by the request timeout
                throw;
            }
        }

        async Task CancelJob(Guid jobId)
        {
            try
            {
                await Send("DELETE", $"/v1/jobs/{jobId:D}", null, null, null, CancellationToken.None, true);
            }
            catch (Exception)
            {
                // Best effort only: the server deadline and the coordinator's discard rules still apply.
            }
        }

        static JobRun HttpFailure(Response response)
        {
            if (response.Status == 0)
                return new JobRun(null, ProposalFailure.Unavailable, "network_unreachable", true);
            var code = CreationJson.ErrorCode(response.Body) ?? $"http_{response.Status}";
            if (response.Status == 429)
                return new JobRun(null, ProposalFailure.Rejected, "rate_limited", true);
            if (response.Status >= 500)
                return new JobRun(null, ProposalFailure.Unavailable, code, true);
            if (response.Status == 413)
                return new JobRun(null, ProposalFailure.Rejected, "payload_too_large", false);
            return new JobRun(null, ProposalFailure.Rejected, code, false);
        }

        async Task<Response> Send(string method, string path, byte[] body, string contentType,
            IReadOnlyDictionary<string, string> headers, CancellationToken ct, bool authenticated)
        {
            ct.ThrowIfCancellationRequested();
            using (var request = new UnityWebRequest(_config.BaseUrl + path, method))
            {
                request.timeout = body != null && body.Length > 64 * 1024 ? UploadTimeoutSeconds : RequestTimeoutSeconds;
                request.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(body);
                    request.SetRequestHeader("Content-Type", contentType);
                }
                request.SetRequestHeader("X-Request-ID", Guid.NewGuid().ToString("D"));
                if (authenticated && _sessionToken != null)
                    request.SetRequestHeader("Authorization", "Bearer " + _sessionToken);
                if (headers != null)
                    foreach (var pair in headers)
                        request.SetRequestHeader(pair.Key, pair.Value);

                var done = new TaskCompletionSource<bool>();
                var operation = request.SendWebRequest();
                operation.completed += _ => done.TrySetResult(true);
                // The registration may run on a timer thread; it only completes the wait. Abort() runs here, on the
                // awaiting (main-thread) continuation, because UnityWebRequest is main-thread only.
                using (ct.Register(() => done.TrySetCanceled()))
                {
                    try
                    {
                        await done.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        request.Abort();
                        throw;
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError)
                    return new Response(0, null);
                var text = request.downloadHandler.text;
                if (text != null && text.Length > CreationJson.MaxResponseChars)
                    return new Response(request.responseCode, null);
                return new Response(request.responseCode, text);
            }
        }
    }

    /// <summary>Scene-proposal adapter over <see cref="CreationServiceClient"/> (S10.2).</summary>
    public sealed class BackendSceneProposalProvider : ISceneProposalProvider
    {
        readonly CreationServiceClient _client;
        readonly string _catalogVersion;

        public BackendSceneProposalProvider(CreationServiceConfig config, string catalogVersion)
            : this(new CreationServiceClient(config), catalogVersion)
        {
        }

        public BackendSceneProposalProvider(CreationServiceClient client, string catalogVersion)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _catalogVersion = catalogVersion ?? throw new ArgumentNullException(nameof(catalogVersion));
        }

        public async Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes(CreationJson.ProposalJobRequest(transcript, _catalogVersion));
            var run = await _client.RunJob("/v1/scene-proposal-jobs", body, "application/json", CreationJson.ProposalKind, null, ct);
            if (run.Job == null)
                return ProposalOutcome.Failed(run.Failure, run.Code, run.Retryable);
            switch (run.Job.Status)
            {
                case "succeeded":
                    var parsed = CreationJson.ReadProposal(run.Job.Result);
                    return parsed.Succeeded
                        ? ProposalOutcome.Of(parsed.Proposal)
                        : ProposalOutcome.Failed(ProposalFailure.InvalidResponse, "proposal_contract");
                case "expired":
                    return ProposalOutcome.Failed(ProposalFailure.Timeout, "deadline_exceeded", true);
                case "cancelled":
                    return ProposalOutcome.Failed(ProposalFailure.JobFailed, "cancelled");
                default:
                    return ProposalOutcome.Failed(ProposalFailure.JobFailed, run.Job.ErrorCode ?? "job_failed", run.Job.ErrorRetryable);
            }
        }
    }

    /// <summary>
    /// Transcription adapter over <see cref="CreationServiceClient"/> (S10.2): it uploads the prepared 16 kHz mono WAV
    /// to <c>POST /v1/transcription-jobs</c> and reads the transcript. The encoded bytes exist only for the request.
    /// </summary>
    public sealed class BackendTranscriptionProvider : ITranscriptionProvider
    {
        static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string> { ["Content-Language"] = "en" };
        readonly CreationServiceClient _client;

        public BackendTranscriptionProvider(CreationServiceClient client) =>
            _client = client ?? throw new ArgumentNullException(nameof(client));

        public async Task<TranscriptionOutcome> TranscribeAsync(AudioClipData audio, CancellationToken ct)
        {
            if (audio == null)
                throw new ArgumentNullException(nameof(audio));
            var started = DateTime.UtcNow;
            var wav = AudioPreparation.EncodeWav(audio);
            CreationServiceClient.JobRun run;
            try
            {
                run = await _client.RunJob("/v1/transcription-jobs", wav, "audio/wav", CreationJson.TranscriptionKind, English, ct);
            }
            finally
            {
                Array.Clear(wav, 0, wav.Length);
            }
            var job = run.Job;
            if (job == null)
                return TranscriptionOutcome.Failed(run.Failure, run.Code, run.Retryable);
            switch (job.Status)
            {
                case "succeeded":
                    return CreationJson.TryReadTranscription(job.Result, out var text, out var provider)
                        ? TranscriptionOutcome.Of(new TranscriptionResult(text, provider, "en", DateTime.UtcNow - started, null))
                        : TranscriptionOutcome.Failed(ProposalFailure.InvalidResponse, "transcription_contract");
                case "expired":
                    return TranscriptionOutcome.Failed(ProposalFailure.Timeout, "deadline_exceeded", true);
                case "cancelled":
                    return TranscriptionOutcome.Failed(ProposalFailure.JobFailed, "cancelled");
                default:
                    return TranscriptionOutcome.Failed(ProposalFailure.JobFailed, job.ErrorCode ?? "job_failed", job.ErrorRetryable);
            }
        }
    }
}
