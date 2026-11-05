using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tryscape.Application.Creation
{
    public enum CreationState
    {
        Idle,
        Proposing,
        Reviewing,
        Failed,
    }

    /// <summary>
    /// One scene-creation operation at a time (S05.1). Each submission gets a monotonic operation ID, its own
    /// cancellation, and an overall time budget (S13.2: 30 s). A completion is applied only if all of these hold:
    /// <list type="bullet">
    /// <item>it belongs to the current operation;</item>
    /// <item>the operation was not cancelled or suspended;</item>
    /// <item>no scene replacement happened since it started (<see cref="SceneReplaced"/>).</item>
    /// </list>
    /// Any other completion is counted in <see cref="DiscardedResults"/> and ignored, so an obsolete result can never
    /// reach review. Nothing here mutates the scene: a proposal only reaches <see cref="CreationState.Reviewing"/>,
    /// and the composition root replaces the scene after hand confirmation.
    /// </summary>
    public sealed class CreationCoordinator : IDisposable
    {
        public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

        readonly ISceneProposalProvider _provider;
        readonly IProposalCheck _check;
        readonly TimeSpan _budget;
        CancellationTokenSource _userCancel;
        CancellationTokenSource _timeout;
        int _operation;
        int _generation;
        bool _disposed;

        public CreationCoordinator(ISceneProposalProvider provider, IProposalCheck check, TimeSpan? budget = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _check = check ?? throw new ArgumentNullException(nameof(check));
            _budget = budget ?? DefaultBudget;
            if (_budget <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(budget));
        }

        public CreationState State { get; private set; } = CreationState.Idle;
        public int OperationId => _operation;

        /// <summary>The proposal under review (only in <see cref="CreationState.Reviewing"/>).</summary>
        public SceneProposal Proposal { get; private set; }

        /// <summary>The last failure (only in <see cref="CreationState.Failed"/>).</summary>
        public ProposalOutcome Failure { get; private set; }

        /// <summary>Transcript of the current or last submission.</summary>
        public string Transcript { get; private set; }

        /// <summary>Completions ignored because they were superseded, cancelled or stale (diagnostics and tests).</summary>
        public int DiscardedResults { get; private set; }

        public event Action Changed;

        /// <summary>
        /// Starts a new operation, superseding any in flight. The returned task completes when this operation finishes or
        /// is discarded; it never faults.
        /// </summary>
        public async Task Submit(string transcript)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CreationCoordinator));
            CancelInFlight();
            var operation = ++_operation;
            var generation = _generation;
            Transcript = transcript ?? "";
            Proposal = null;
            Failure = null;

            var trimmed = Transcript.Trim();
            if (trimmed.Length == 0 || Transcript.Length > SceneProposal.MaxTranscriptChars)
            {
                Fail(ProposalOutcome.Failed(ProposalFailure.Rejected, trimmed.Length == 0 ? "transcript_empty" : "transcript_too_long"));
                return;
            }

            var userCancel = new CancellationTokenSource();
            var timeout = new CancellationTokenSource(_budget);
            _userCancel = userCancel;
            _timeout = timeout;
            State = CreationState.Proposing;
            Raise();

            ProposalOutcome outcome = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token, timeout.Token))
            {
                // Cancellation and the budget do not depend on the provider honouring its token: whichever completes
                // first wins, and a provider that keeps running has its eventual result ignored.
                var stopped = new TaskCompletionSource<bool>();
                using (linked.Token.Register(() => stopped.TrySetResult(true)))
                {
                    Task<ProposalOutcome> work;
                    try
                    {
                        work = _provider.ProposeAsync(Transcript, linked.Token);
                    }
                    catch (Exception)
                    {
                        work = Task.FromResult(ProposalOutcome.Failed(ProposalFailure.Unavailable, "provider_exception", true));
                    }
                    var first = await Task.WhenAny(work, stopped.Task);
                    if (first == work)
                    {
                        try
                        {
                            outcome = await work;
                        }
                        catch (OperationCanceledException)
                        {
                            outcome = null;
                        }
                        catch (Exception)
                        {
                            // Infrastructure boundary: never surface raw exception text; treat as service unavailable.
                            outcome = ProposalOutcome.Failed(ProposalFailure.Unavailable, "provider_exception", true);
                        }
                    }
                    else
                        _ = work.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observe late faults
                }
            }

            var timedOut = timeout.IsCancellationRequested && !userCancel.IsCancellationRequested;
            if (operation != _operation || generation != _generation || userCancel.IsCancellationRequested || _disposed)
            {
                DiscardedResults++;
                Dispose(userCancel, timeout);
                return;
            }
            ReleaseTokens();

            if (timedOut)
                Fail(ProposalOutcome.Failed(ProposalFailure.Timeout, "deadline_exceeded", true));
            else if (outcome == null)
                Fail(ProposalOutcome.Failed(ProposalFailure.Unavailable, "cancelled_by_provider", true));
            else if (!outcome.Succeeded)
                Fail(outcome);
            else
            {
                var problem = _check.Problem(outcome.Proposal);
                if (problem != null)
                    Fail(ProposalOutcome.Failed(ProposalFailure.InvalidResponse, problem));
                else
                {
                    Proposal = outcome.Proposal;
                    State = CreationState.Reviewing;
                    Raise();
                }
            }
        }

        /// <summary>User cancel: the in-flight operation is abandoned and its late result will be discarded.</summary>
        public void Cancel()
        {
            if (State == CreationState.Idle)
                return;
            CancelInFlight();
            _operation++;
            Proposal = null;
            Failure = null;
            State = CreationState.Idle;
            Raise();
        }

        /// <summary>Focus loss or app pause (S05.3): a cancellation boundary for generation.</summary>
        public void Suspend()
        {
            if (State == CreationState.Proposing)
                Cancel();
        }

        /// <summary>
        /// The composition root replaced the scene (any creation path). Proposals started or reviewed before that point are
        /// obsolete and can no longer be confirmed.
        /// </summary>
        public void SceneReplaced()
        {
            _generation++;
            if (State != CreationState.Idle)
                Cancel();
        }

        /// <summary>The reviewed proposal, if it is still current and confirmable.</summary>
        public bool TryGetConfirmable(out SceneProposal proposal, out int generation)
        {
            proposal = State == CreationState.Reviewing ? Proposal : null;
            generation = _generation;
            return proposal != null && proposal.Status == ProposalStatus.Ready;
        }

        public bool IsCurrentGeneration(int generation) => generation == _generation;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelInFlight();
        }

        void Fail(ProposalOutcome failure)
        {
            Failure = failure;
            Proposal = null;
            State = CreationState.Failed;
            Raise();
        }

        void CancelInFlight()
        {
            if (_userCancel != null && !_userCancel.IsCancellationRequested)
                _userCancel.Cancel();
            _userCancel = null;
            _timeout = null; // disposed by the awaiting operation when it completes
        }

        void ReleaseTokens()
        {
            Dispose(_userCancel, _timeout);
            _userCancel = null;
            _timeout = null;
        }

        static void Dispose(CancellationTokenSource a, CancellationTokenSource b)
        {
            a?.Dispose();
            b?.Dispose();
        }

        void Raise() => Changed?.Invoke();
    }

    /// <summary>
    /// Clarification answers are resubmitted to the same interpreter as an explicit <c>Answer: &lt;label&gt;</c> line,
    /// so interpretation authority stays in the backend. <see cref="EnterDimensions"/> is handled locally by opening the
    /// hand parameter editor instead.
    /// </summary>
    public static class ClarificationAnswers
    {
        public const string Prefix = "Answer:";
        public const string EnterDimensions = "Enter dimensions";

        /// <summary>The transcript with the answer appended, or null if it would exceed the transcript limit.</summary>
        public static string Append(string transcript, string choice)
        {
            if (string.IsNullOrWhiteSpace(choice))
                throw new ArgumentException("A choice is required", nameof(choice));
            var next = (transcript ?? "").TrimEnd() + "\n" + Prefix + " " + choice.Trim();
            return next.Length <= SceneProposal.MaxTranscriptChars ? next : null;
        }
    }
}
