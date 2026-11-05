using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tryscape.Application.Creation;
using Tryscape.Infrastructure.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Tryscape.Unity.Creation
{
    /// <summary>
    /// Where the creation service lives for this build. Development builds and the Editor default to the developer
    /// machine on 127.0.0.1:8000; on a Quest this is reached with <c>adb reverse tcp:8000 tcp:8000</c>, so no LAN
    /// address is compiled in. Release builds need an allowlisted HTTPS origin; none is approved yet, so the service is
    /// reported unavailable and hand creation is offered (S13, S21.1).
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
    /// Client adapter for the backend scene-proposal job API (S13.1/S13.2). The session token lives only in memory.
    /// Each operation creates one job under a fresh idempotency key: a lost create response is retried once with the
    /// same key, so it never creates a second job. Polling uses 1 s, then about 2 s with jitter. On cancellation the
    /// in-flight request is aborted and the job is cancelled as best effort; the coordinator discards any late result
    /// anyway. Only safe codes leave this class, never raw transport or server text.
    /// </summary>
    public sealed class BackendSceneProposalProvider : ISceneProposalProvider
    {
        const int RequestTimeoutSeconds = 5;
        readonly CreationServiceConfig _config;
        readonly string _catalogVersion;
        readonly System.Random _jitter = new System.Random();
        string _sessionToken;

        public BackendSceneProposalProvider(CreationServiceConfig config, string catalogVersion)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _catalogVersion = catalogVersion ?? throw new ArgumentNullException(nameof(catalogVersion));
        }

        public async Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken ct)
        {
            if (!_config.IsConfigured)
                return ProposalOutcome.Failed(ProposalFailure.Unavailable, "service_not_configured");

            var session = await EnsureSession(ct);
            if (session.failure != null)
                return session.failure;

            var key = Guid.NewGuid().ToString("D");
            var body = CreationJson.ProposalJobRequest(transcript, _catalogVersion);
            Response created = default;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                created = await Send("POST", "/v1/scene-proposal-jobs", body, ct, idempotencyKey: key);
                if (created.Status != 0)
                    break; // a network failure (status 0) is retried once with the same key
            }
            if (created.Status == 401)
            {
                _sessionToken = null; // expired session: the next attempt mints a new one
                return ProposalOutcome.Failed(ProposalFailure.Unavailable, "session_expired", true);
            }
            if (created.Status != 202 || !CreationJson.TryReadReceipt(created.Body, out var jobId, out var pollAfterMs))
                return FromHttpFailure(created);

            var delayMs = Math.Max(pollAfterMs, 1000);
            var polls = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(delayMs, ct);
                    var polled = await Send("GET", $"/v1/jobs/{jobId:D}", null, ct);
                    if (polled.Status == 0 && polls++ < 2)
                        continue; // read-only poll retried at most twice
                    if (polled.Status != 200 || !CreationJson.TryReadJob(polled.Body, CreationJson.ProposalKind, out var job) ||
                        job.JobId != jobId)
                        return polled.Status == 200
                            ? ProposalOutcome.Failed(ProposalFailure.InvalidResponse, "job_shape")
                            : FromHttpFailure(polled);
                    if (job.IsTerminal)
                        return FromJob(job);
                    delayMs = 2000 + _jitter.Next(-250, 250);
                }
            }
            catch (OperationCanceledException)
            {
                _ = CancelJob(jobId); // best effort; fire and forget, bounded by the request timeout
                throw;
            }
        }

        async Task<(string token, ProposalOutcome failure)> EnsureSession(CancellationToken ct)
        {
            if (_sessionToken != null)
                return (_sessionToken, null);
            var response = await Send("POST", "/v1/anonymous-sessions", "{}", ct, authenticated: false);
            if (response.Status != 201 || !CreationJson.TryReadSessionToken(response.Body, out var token))
                return (null, FromHttpFailure(response));
            _sessionToken = token;
            return (token, null);
        }

        async Task CancelJob(Guid jobId)
        {
            try
            {
                await Send("DELETE", $"/v1/jobs/{jobId:D}", null, CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort only: the server's deadline and the coordinator's discard rules still apply.
            }
        }

        static ProposalOutcome FromJob(JobView job)
        {
            switch (job.Status)
            {
                case "succeeded":
                    var parsed = CreationJson.ReadProposal(job.Result);
                    return parsed.Succeeded
                        ? ProposalOutcome.Of(parsed.Proposal)
                        : ProposalOutcome.Failed(ProposalFailure.InvalidResponse, "proposal_contract");
                case "expired":
                    return ProposalOutcome.Failed(ProposalFailure.Timeout, "deadline_exceeded", true);
                case "cancelled":
                    return ProposalOutcome.Failed(ProposalFailure.JobFailed, "cancelled");
                default:
                    return ProposalOutcome.Failed(ProposalFailure.JobFailed, job.ErrorCode ?? "job_failed", job.ErrorRetryable);
            }
        }

        static ProposalOutcome FromHttpFailure(Response response)
        {
            if (response.Status == 0)
                return ProposalOutcome.Failed(ProposalFailure.Unavailable, "network_unreachable", true);
            var code = CreationJson.ErrorCode(response.Body) ?? $"http_{response.Status}";
            if (response.Status == 429)
                return ProposalOutcome.Failed(ProposalFailure.Rejected, "rate_limited", true);
            if (response.Status >= 500)
                return ProposalOutcome.Failed(ProposalFailure.Unavailable, code, true);
            return ProposalOutcome.Failed(ProposalFailure.Rejected, code);
        }

        readonly struct Response
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

        async Task<Response> Send(string method, string path, string jsonBody, CancellationToken ct,
            string idempotencyKey = null, bool authenticated = true)
        {
            ct.ThrowIfCancellationRequested();
            using (var request = new UnityWebRequest(_config.BaseUrl + path, method))
            {
                request.timeout = RequestTimeoutSeconds;
                request.downloadHandler = new DownloadHandlerBuffer();
                if (jsonBody != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.SetRequestHeader("X-Request-ID", Guid.NewGuid().ToString("D"));
                if (authenticated && _sessionToken != null)
                    request.SetRequestHeader("Authorization", "Bearer " + _sessionToken);
                if (idempotencyKey != null)
                {
                    request.SetRequestHeader("Idempotency-Key", idempotencyKey);
                    request.SetRequestHeader("X-Processing-Consent-Version", CreationServiceConfig.ConsentVersion);
                }

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
}
