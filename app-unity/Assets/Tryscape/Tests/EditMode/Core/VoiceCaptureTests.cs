using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Tryscape.Application.Creation;
using Tryscape.Content.MovingDay;
using Tryscape.Infrastructure.Json;

namespace Tryscape.Tests.EditMode.Core
{
    /// <summary>
    /// Fake microphone with scriptable availability, permission and capture. The real Unity adapter is a manual/device
    /// check.
    /// </summary>
    public sealed class FakeMicrophone : IMicrophone
    {
        public bool Available = true;
        public MicrophonePermission CurrentPermission = MicrophonePermission.Granted;
        public bool StartSucceeds = true;
        public bool Capturing;
        public float[] Samples = Tone(48000, 2.0, 440, 0.3);
        public int Channels = 1;
        public int Rate = 48000;
        public Action<bool> PendingPermission;
        public int Starts;
        public int Discards;

        public bool IsAvailable => Available;
        public MicrophonePermission Permission => CurrentPermission;
        public void RequestPermission(Action<bool> onResult) => PendingPermission = onResult;

        public bool Start(int maxSeconds)
        {
            Starts++;
            Capturing = StartSucceeds;
            return StartSucceeds;
        }

        public bool IsCapturing => Capturing;

        public float[] StopAndRead(out int channels, out int sampleRate)
        {
            Capturing = false;
            channels = Channels;
            sampleRate = Rate;
            return (float[])Samples.Clone();
        }

        public void Discard()
        {
            Capturing = false;
            Discards++;
        }

        public static float[] Tone(int rate, double seconds, double hz, double amplitude)
        {
            var n = (int)(rate * seconds);
            var data = new float[n];
            for (var i = 0; i < n; i++)
                data[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
            return data;
        }
    }

    public sealed class AudioPreparationTests
    {
        [Test]
        public void ResamplesToSixteenKilohertzMonoPreservingDurationAndPitch()
        {
            var clip = AudioPreparation.Prepare(FakeMicrophone.Tone(48000, 2.0, 440, 0.5), 1, 48000);
            Assert.That(clip.Samples.Length, Is.EqualTo(32000));
            Assert.That(clip.DurationSeconds, Is.EqualTo(2.0).Within(1e-9));
            // Pitch check: count rising zero crossings; 440 Hz over 2 s is about 880.
            var crossings = 0;
            for (var i = 1; i < clip.Samples.Length; i++)
                if (clip.Samples[i - 1] < 0 && clip.Samples[i] >= 0)
                    crossings++;
            Assert.That(crossings, Is.InRange(875, 885));
            Assert.That(AudioPreparation.Rms(clip), Is.EqualTo(0.5 / Math.Sqrt(2)).Within(0.02));
        }

        [Test]
        public void DownmixesStereoUpsamplesAndClipsSafely()
        {
            var stereo = new float[] { 1f, -1f, 0.5f, 0.5f, 2f, 2f, -3f, -3f };
            var mono = AudioPreparation.Prepare(stereo, 2, 16000);
            Assert.That(mono.Samples, Is.EqualTo(new short[] { 0, 16384, short.MaxValue, -short.MaxValue }));
            var up = AudioPreparation.Prepare(FakeMicrophone.Tone(8000, 1.0, 200, 0.3), 1, 8000);
            Assert.That(up.Samples.Length, Is.EqualTo(16000));
        }

        [Test]
        public void InputIsBoundedToFifteenSeconds()
        {
            var clip = AudioPreparation.Prepare(new float[48000 * 20], 1, 48000);
            Assert.That(clip.DurationSeconds, Is.EqualTo(15.0).Within(1e-9));
        }

        [Test]
        public void WavHeaderMatchesTheR0UploadContract()
        {
            var wav = AudioPreparation.EncodeWav(new AudioClipData(new short[] { 1, -2, short.MaxValue }));
            Assert.That(wav.Length, Is.EqualTo(44 + 6));
            Assert.That(System.Text.Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
            Assert.That(BitConverter.ToInt32(wav, 4), Is.EqualTo(36 + 6));
            Assert.That(System.Text.Encoding.ASCII.GetString(wav, 8, 8), Is.EqualTo("WAVEfmt "));
            Assert.That(BitConverter.ToInt32(wav, 16), Is.EqualTo(16));
            Assert.That(BitConverter.ToInt16(wav, 20), Is.EqualTo(1), "PCM");
            Assert.That(BitConverter.ToInt16(wav, 22), Is.EqualTo(1), "mono");
            Assert.That(BitConverter.ToInt32(wav, 24), Is.EqualTo(16000));
            Assert.That(BitConverter.ToInt32(wav, 28), Is.EqualTo(32000));
            Assert.That(BitConverter.ToInt16(wav, 32), Is.EqualTo(2));
            Assert.That(BitConverter.ToInt16(wav, 34), Is.EqualTo(16));
            Assert.That(System.Text.Encoding.ASCII.GetString(wav, 36, 4), Is.EqualTo("data"));
            Assert.That(BitConverter.ToInt32(wav, 40), Is.EqualTo(6));
            Assert.That(new[] { BitConverter.ToInt16(wav, 44), BitConverter.ToInt16(wav, 46), BitConverter.ToInt16(wav, 48) },
                Is.EqualTo(new short[] { 1, -2, short.MaxValue }));
            // 15 s at 16 kHz fits the 512 KiB upload limit.
            Assert.That(44 + AudioClipData.SampleRateHz * AudioClipData.MaxSeconds * 2, Is.LessThanOrEqualTo(512 * 1024));
        }
    }

    public sealed class VoiceRecorderTests
    {
        static (VoiceRecorder recorder, List<AudioClipData> clips) Make(FakeMicrophone mic)
        {
            var recorder = new VoiceRecorder(mic);
            var clips = new List<AudioClipData>();
            recorder.Completed += clips.Add;
            return (recorder, clips);
        }

        [Test]
        public void RecordsShowsElapsedTimeAndDeliversPreparedAudioOnStop()
        {
            var mic = new FakeMicrophone();
            var (r, clips) = Make(mic);
            r.Begin();
            Assert.That(r.State, Is.EqualTo(RecorderState.Recording));
            r.Tick(1.25);
            r.Tick(0.5);
            Assert.That(r.ElapsedSeconds, Is.EqualTo(1.75).Within(1e-9));
            r.Stop();
            Assert.That(r.State, Is.EqualTo(RecorderState.Idle));
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Samples.Length, Is.EqualTo(32000));
        }

        [Test]
        public void StopsAutomaticallyAtFifteenSeconds()
        {
            var mic = new FakeMicrophone();
            var (r, clips) = Make(mic);
            r.Begin();
            for (var i = 0; i < 200; i++)
                r.Tick(0.1);
            Assert.That(r.StoppedAtLimit, Is.True);
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(mic.Capturing, Is.False);
        }

        [Test]
        public void UnavailableMicrophoneAndFailedStartAreReported()
        {
            var (r, _) = Make(new FakeMicrophone { Available = false });
            r.Begin();
            Assert.That(r.Failure, Is.EqualTo(RecorderFailure.Unavailable));
            var (r2, _) = Make(new FakeMicrophone { StartSucceeds = false });
            r2.Begin();
            Assert.That(r2.Failure, Is.EqualTo(RecorderFailure.Unavailable));
        }

        [Test]
        public void PermissionIsRequestedAndDenialRecordsNothing()
        {
            var mic = new FakeMicrophone { CurrentPermission = MicrophonePermission.NotDetermined };
            var (r, clips) = Make(mic);
            r.Begin();
            Assert.That(r.State, Is.EqualTo(RecorderState.RequestingPermission));
            Assert.That(mic.Starts, Is.EqualTo(0), "no capture before permission");
            mic.PendingPermission(false);
            Assert.That(r.Failure, Is.EqualTo(RecorderFailure.PermissionDenied));
            Assert.That(mic.Starts, Is.EqualTo(0));
            Assert.That(clips, Is.Empty);

            var (r2, _) = Make(new FakeMicrophone { CurrentPermission = MicrophonePermission.Denied });
            r2.Begin();
            Assert.That(r2.Failure, Is.EqualTo(RecorderFailure.PermissionDenied));
        }

        [Test]
        public void GrantedPermissionStartsCaptureButALateGrantAfterCancelDoesNot()
        {
            var mic = new FakeMicrophone { CurrentPermission = MicrophonePermission.NotDetermined };
            var (r, _) = Make(mic);
            r.Begin();
            var grant = mic.PendingPermission;
            r.Cancel();
            grant(true); // the system dialog answers after the user cancelled
            Assert.That(r.State, Is.EqualTo(RecorderState.Idle));
            Assert.That(mic.Starts, Is.EqualTo(0));
            r.Begin();
            mic.PendingPermission(true);
            Assert.That(r.State, Is.EqualTo(RecorderState.Recording));
        }

        [Test]
        public void DeviceLossOrRevocationMidRecordingDiscardsAudio()
        {
            var mic = new FakeMicrophone();
            var (r, clips) = Make(mic);
            r.Begin();
            r.Tick(1);
            mic.Capturing = false; // permission revoked or device removed
            r.Tick(0.1);
            Assert.That(r.Failure, Is.EqualTo(RecorderFailure.DeviceLost));
            Assert.That(clips, Is.Empty);
            Assert.That(mic.Discards, Is.EqualTo(1));
        }

        [Test]
        public void SilenceAndTooShortRecordingsAreNotDelivered()
        {
            var (silent, clips) = Make(new FakeMicrophone { Samples = new float[48000 * 2] });
            silent.Begin();
            silent.Stop();
            Assert.That(silent.Failure, Is.EqualTo(RecorderFailure.Silence));
            var (tap, clips2) = Make(new FakeMicrophone { Samples = FakeMicrophone.Tone(48000, 0.2, 440, 0.3) });
            tap.Begin();
            tap.Stop();
            Assert.That(tap.Failure, Is.EqualTo(RecorderFailure.TooShort));
            Assert.That(clips.Concat(clips2), Is.Empty);
        }

        [Test]
        public void CancelAndFocusLossDiscardImmediately()
        {
            var mic = new FakeMicrophone();
            var (r, clips) = Make(mic);
            r.Begin();
            r.Cancel();
            Assert.That(mic.Capturing, Is.False);
            Assert.That(r.State, Is.EqualTo(RecorderState.Idle));
            r.Begin();
            r.Suspend();
            Assert.That(r.Failure, Is.EqualTo(RecorderFailure.Interrupted));
            Assert.That(mic.Discards, Is.EqualTo(2));
            Assert.That(clips, Is.Empty);
        }
    }

    public sealed class VoiceCreationCoordinatorTests
    {
        SynchronizationContext _previous;

        [SetUp]
        public void SetUp()
        {
            _previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_previous);

        sealed class ControlledTranscriber : ITranscriptionProvider
        {
            public readonly List<(AudioClipData audio, TaskCompletionSource<TranscriptionOutcome> pending, CancellationToken token)> Calls =
                new List<(AudioClipData, TaskCompletionSource<TranscriptionOutcome>, CancellationToken)>();

            public Task<TranscriptionOutcome> TranscribeAsync(AudioClipData audio, CancellationToken token)
            {
                var pending = new TaskCompletionSource<TranscriptionOutcome>();
                Calls.Add((audio, pending, token));
                return pending.Task;
            }
        }

        sealed class RecordingProposer : ISceneProposalProvider
        {
            public readonly List<string> Transcripts = new List<string>();
            public Func<string, Task<ProposalOutcome>> Answer = t => Task.FromResult(ProposalOutcome.Of(Proposals.ValidReady()));

            public Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken token)
            {
                Transcripts.Add(transcript);
                return Answer(transcript);
            }
        }

        static AudioClipData Clip() => new AudioClipData(new short[16000]);

        static TranscriptionOutcome Heard(string text) =>
            TranscriptionOutcome.Of(new TranscriptionResult(text, "whisper_cpp_base_en", "en", TimeSpan.FromMilliseconds(300), null));

        static CreationCoordinator Make(ISceneProposalProvider p, ITranscriptionProvider t, TimeSpan? budget = null) =>
            new CreationCoordinator(p, new MovingDayProposals(Fixtures.Catalog), budget, t);

        [Test]
        public void SpokenTranscriptFlowsIntoTheProposalAndIsLabelledSpoken()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer();
            var c = Make(proposer, asr);
            _ = c.SubmitAudio(Clip());
            Assert.That(c.State, Is.EqualTo(CreationState.Transcribing));
            Assert.That(proposer.Transcripts, Is.Empty);
            asr.Calls[0].pending.SetResult(Heard("  Six by five metre room for a sofa.  "));
            Assert.That(proposer.Transcripts, Is.EqualTo(new[] { "Six by five metre room for a sofa." }));
            Assert.That(c.State, Is.EqualTo(CreationState.Reviewing));
            Assert.That(c.TranscriptFromVoice, Is.True);
            Assert.That(c.TranscriptionProviderId, Is.EqualTo("whisper_cpp_base_en"));
        }

        [Test]
        public void EmptyTranscriptStopsBeforeInterpretation()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer();
            var c = Make(proposer, asr);
            _ = c.SubmitAudio(Clip());
            asr.Calls[0].pending.SetResult(Heard("   "));
            Assert.That(c.Failure.DetailCode, Is.EqualTo("no_speech"));
            Assert.That(proposer.Transcripts, Is.Empty);
        }

        [Test]
        public void TranscriptionFailureIsTypedAndNeverInterpreted()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer();
            var c = Make(proposer, asr);
            _ = c.SubmitAudio(Clip());
            asr.Calls[0].pending.SetResult(TranscriptionOutcome.Failed(ProposalFailure.JobFailed, "provider_unavailable", true));
            Assert.That(c.State, Is.EqualTo(CreationState.Failed));
            Assert.That(c.Failure.DetailCode, Is.EqualTo("provider_unavailable"));
            Assert.That(proposer.Transcripts, Is.Empty);
        }

        [Test]
        public void CancelDuringTranscriptionDiscardsTheLateTranscript()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer();
            var c = Make(proposer, asr);
            _ = c.SubmitAudio(Clip());
            c.Cancel();
            Assert.That(asr.Calls[0].token.IsCancellationRequested, Is.True);
            asr.Calls[0].pending.SetResult(Heard("Six by five metre room for a sofa."));
            Assert.That(c.State, Is.EqualTo(CreationState.Idle));
            Assert.That(proposer.Transcripts, Is.Empty, "a cancelled transcription must not reach the interpreter");
            Assert.That(c.DiscardedResults, Is.EqualTo(1));
        }

        [Test]
        public void NewerRecordingSupersedesAnOlderOne()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer();
            var c = Make(proposer, asr);
            _ = c.SubmitAudio(Clip());
            _ = c.SubmitAudio(Clip());
            asr.Calls[1].pending.SetResult(Heard("second"));
            asr.Calls[0].pending.SetResult(Heard("first"));
            Assert.That(proposer.Transcripts, Is.EqualTo(new[] { "second" }));
        }

        [Test]
        public void OneBudgetCoversTranscriptionAndProposal()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer { Answer = _ => new TaskCompletionSource<ProposalOutcome>().Task }; // hangs
            var c = Make(proposer, asr, TimeSpan.FromMilliseconds(400));
            var started = DateTime.UtcNow;
            var task = c.SubmitAudio(Clip());
            Thread.Sleep(250); // transcription uses most of the budget
            asr.Calls[0].pending.SetResult(Heard("Six by five metre room."));
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var elapsed = DateTime.UtcNow - started;
            Assert.That(c.Failure.Failure, Is.EqualTo(ProposalFailure.Timeout));
            Assert.That(elapsed.TotalMilliseconds, Is.LessThan(600), "the proposal phase must not get a fresh 400 ms budget");
        }

        [Test]
        public void ClarificationAnswerKeepsTheSpokenOrigin()
        {
            var asr = new ControlledTranscriber();
            var proposer = new RecordingProposer { Answer = t => Task.FromResult(ProposalOutcome.Of(Proposals.Clarification(t))) };
            var c = Make(proposer, asr);
            _ = c.SubmitAudio(Clip());
            asr.Calls[0].pending.SetResult(Heard("Prepare my room for a delivery"));
            _ = c.SubmitAnswer(ClarificationAnswers.Append(c.Proposal.Transcript, "Use 6 m by 5 m example"));
            Assert.That(c.TranscriptFromVoice, Is.True);
            Assert.That(c.TranscriptionProviderId, Is.EqualTo("whisper_cpp_base_en"));
        }

        [Test]
        public void VoiceWithoutATranscriptionServiceFailsClearly()
        {
            var c = Make(new RecordingProposer(), null);
            _ = c.SubmitAudio(Clip());
            Assert.That(c.Failure.DetailCode, Is.EqualTo("transcription_not_configured"));
        }

        [Test]
        public void TranscriptionJobFixtureParsesAndBadShapesFail()
        {
            Assert.That(CreationJson.TryReadJob(Fixtures.Text("api/valid_job_succeeded_transcription.json"), "transcription", out var job), Is.True);
            Assert.That(CreationJson.TryReadTranscription(job.Result, out var text, out var provider), Is.True);
            Assert.That(text, Is.Not.Empty);
            Assert.That(provider, Is.Not.Empty);
            Assert.That(CreationJson.TryReadTranscription(JObject.Parse("{\"text\":\"a\",\"locale\":\"en\"}"), out _, out _), Is.False);
            Assert.That(CreationJson.TryReadTranscription(JObject.Parse("{\"text\":\"a\\u0000b\",\"locale\":\"en\",\"provider_id\":\"x\"}"), out _, out _), Is.False);
            Assert.That(CreationJson.TryReadTranscription(JObject.Parse("{\"text\":\"a\",\"locale\":\"fr\",\"provider_id\":\"x\"}"), out _, out _), Is.False);
            var silence = JObject.Parse("{\"text\":\"\",\"locale\":\"en\",\"provider_id\":\"whisper_cpp_base_en\"}");
            Assert.That(CreationJson.TryReadTranscription(silence, out var empty, out _), Is.True);
            Assert.That(empty, Is.Empty);
        }
    }
}
