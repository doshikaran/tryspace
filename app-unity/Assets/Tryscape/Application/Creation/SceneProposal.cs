using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tryscape.Core.Model;

namespace Tryscape.Application.Creation
{
    public enum ProposalStatus
    {
        Ready,
        NeedsClarification,
        Unsupported,
    }

    /// <summary>Allowlisted intent record (S10.3). Values are untrusted until the pack check accepts the proposal.</summary>
    public sealed class ProposalIntent
    {
        public ProposalIntent(string scenarioPackId, int roomWidthMm, int roomDepthMm, string payloadCatalogId,
            string obstructionState, string preferredRouteId)
        {
            ScenarioPackId = scenarioPackId;
            RoomWidthMm = roomWidthMm;
            RoomDepthMm = roomDepthMm;
            PayloadCatalogId = payloadCatalogId;
            ObstructionState = obstructionState;
            PreferredRouteId = preferredRouteId;
        }

        public string ScenarioPackId { get; }
        public int RoomWidthMm { get; }
        public int RoomDepthMm { get; }
        public string PayloadCatalogId { get; }

        /// <summary>Wire value: <c>center</c> or <c>parked_south</c>.</summary>
        public string ObstructionState { get; }

        public string PreferredRouteId { get; }
    }

    public sealed class ProposalQuestion
    {
        public ProposalQuestion(string field, string prompt, IReadOnlyList<string> choices)
        {
            Field = field;
            Prompt = prompt;
            Choices = choices;
        }

        public string Field { get; }
        public string Prompt { get; }
        public IReadOnlyList<string> Choices { get; }
    }

    /// <summary>
    /// SceneProposal v1.0.0 (contracts/v1/schemas/scene_proposal.schema.json), already shape-validated. A proposal is
    /// never authoritative: it must pass the pack check (catalog, semantic validation, consistency with its intent)
    /// and the user must confirm it with hands before anything replaces the current scene (S05.2).
    /// </summary>
    public sealed class SceneProposal
    {
        public const string SchemaVersion = "1.0.0";
        public const int MaxTranscriptChars = 2000;

        public SceneProposal(Guid proposalId, ProposalStatus status, string transcript, ProposalIntent intent, SceneSpec scene,
            IReadOnlyList<ProposalQuestion> questions, IReadOnlyList<Assumption> assumptions, string providerId,
            string providerVersion)
        {
            ProposalId = proposalId;
            Status = status;
            Transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
            Intent = intent;
            Scene = scene;
            Questions = questions ?? Array.Empty<ProposalQuestion>();
            Assumptions = assumptions ?? Array.Empty<Assumption>();
            ProviderId = providerId;
            ProviderVersion = providerVersion;
        }

        public Guid ProposalId { get; }
        public ProposalStatus Status { get; }

        /// <summary>Untrusted user text; display as plain text only (never rich text or markup).</summary>
        public string Transcript { get; }

        public ProposalIntent Intent { get; }
        public SceneSpec Scene { get; }
        public IReadOnlyList<ProposalQuestion> Questions { get; }
        public IReadOnlyList<Assumption> Assumptions { get; }
        public string ProviderId { get; }
        public string ProviderVersion { get; }
    }

    public enum ProposalFailure
    {
        None,

        /// <summary>No creation service is configured or reachable (offline, outage, release build without endpoint).</summary>
        Unavailable,

        /// <summary>The operation's overall budget (S13.2: 30 s) ran out.</summary>
        Timeout,

        /// <summary>The service refused the request (validation, consent, rate limit).</summary>
        Rejected,

        /// <summary>The response was malformed, failed the contract, or failed the pack check.</summary>
        InvalidResponse,

        /// <summary>The service accepted the job but it failed or expired.</summary>
        JobFailed,
    }

    /// <summary>Typed result of one proposal request: a proposal, or a failure code. Never a partial scene.</summary>
    public sealed class ProposalOutcome
    {
        ProposalOutcome(SceneProposal proposal, ProposalFailure failure, string detailCode, bool retryable)
        {
            Proposal = proposal;
            Failure = failure;
            DetailCode = detailCode;
            Retryable = retryable;
        }

        public SceneProposal Proposal { get; }
        public ProposalFailure Failure { get; }

        /// <summary>Safe machine code (for example <c>rate_limited</c>); never raw provider or transport text.</summary>
        public string DetailCode { get; }

        public bool Retryable { get; }
        public bool Succeeded => Proposal != null;

        public static ProposalOutcome Of(SceneProposal proposal) =>
            new ProposalOutcome(proposal ?? throw new ArgumentNullException(nameof(proposal)), ProposalFailure.None, null, false);

        public static ProposalOutcome Failed(ProposalFailure failure, string detailCode, bool retryable = false)
        {
            if (failure == ProposalFailure.None)
                throw new ArgumentException("A failed outcome needs a failure kind", nameof(failure));
            return new ProposalOutcome(null, failure, detailCode, retryable);
        }
    }

    /// <summary>
    /// Scene-proposal source (S10.2). In R0 the implementation is an HTTP adapter to the backend job endpoints.
    /// Implementations must honour cancellation and must not touch scene state.
    /// </summary>
    public interface ISceneProposalProvider
    {
        Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken cancellationToken);
    }

    /// <summary>Bounded mono PCM audio held in memory only (S10.5); never written to project files.</summary>
    public sealed class AudioClipData
    {
        public const int SampleRateHz = 16000;
        public const int MaxSeconds = 15;

        public AudioClipData(short[] samples)
        {
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
            if (samples.Length == 0 || samples.Length > SampleRateHz * MaxSeconds)
                throw new ArgumentOutOfRangeException(nameof(samples), "Audio must be 1 sample to 15 s at 16 kHz mono");
        }

        public short[] Samples { get; }
        public double DurationSeconds => Samples.Length / (double)SampleRateHz;
    }

    public sealed class TranscriptionResult
    {
        public TranscriptionResult(string text, string providerId, string locale, TimeSpan elapsed, double? confidence)
        {
            Text = text;
            ProviderId = providerId;
            Locale = locale;
            Elapsed = elapsed;
            Confidence = confidence;
        }

        public string Text { get; }
        public string ProviderId { get; }
        public string Locale { get; }
        public TimeSpan Elapsed { get; }
        public double? Confidence { get; }
    }

    /// <summary>Typed result of one transcription request: a transcript (possibly empty for silence) or a failure.</summary>
    public sealed class TranscriptionOutcome
    {
        TranscriptionOutcome(TranscriptionResult result, ProposalFailure failure, string detailCode, bool retryable)
        {
            Result = result;
            Failure = failure;
            DetailCode = detailCode;
            Retryable = retryable;
        }

        public TranscriptionResult Result { get; }

        /// <summary>Failure kinds are shared with proposals (unavailable, timeout, rejected, invalid response, job failed).</summary>
        public ProposalFailure Failure { get; }

        public string DetailCode { get; }
        public bool Retryable { get; }
        public bool Succeeded => Result != null;

        public static TranscriptionOutcome Of(TranscriptionResult result) =>
            new TranscriptionOutcome(result ?? throw new ArgumentNullException(nameof(result)), ProposalFailure.None, null, false);

        public static TranscriptionOutcome Failed(ProposalFailure failure, string detailCode, bool retryable = false)
        {
            if (failure == ProposalFailure.None)
                throw new ArgumentException("A failed outcome needs a failure kind", nameof(failure));
            return new TranscriptionOutcome(null, failure, detailCode, retryable);
        }
    }

    /// <summary>
    /// Speech-to-text source (S10.2), kept separate from proposal generation. The R0 implementation is the HTTP adapter
    /// to <c>POST /v1/transcription-jobs</c>. Implementations must honour cancellation, hold audio only in memory and
    /// never touch scene state.
    /// </summary>
    public interface ITranscriptionProvider
    {
        Task<TranscriptionOutcome> TranscribeAsync(AudioClipData audio, CancellationToken cancellationToken);
    }

    /// <summary>Pack-specific acceptance of a proposal before review (catalog, semantics, intent/scene consistency).</summary>
    public interface IProposalCheck
    {
        /// <summary>Null when acceptable; otherwise a short safe reason code.</summary>
        string Problem(SceneProposal proposal);
    }
}
