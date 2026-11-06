using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Tryscape.Application.Creation;
using Tryscape.Content.MovingDay;
using Tryscape.Core.Evaluation;
using Tryscape.Core.Geometry;
using Tryscape.Core.Model;
using Tryscape.Unity;
using Tryscape.Unity.Creation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tryscape.Tests.PlayMode.Acceptance
{
    /// <summary>Microphone stand-in for PlayMode: scripted permission/availability; samples from a tone or a WAV file.</summary>
    public sealed class ScriptedMicrophone : IMicrophone
    {
        public bool Available = true;
        public MicrophonePermission CurrentPermission = MicrophonePermission.Granted;
        public bool Capturing;
        public float[] Samples;
        public int Rate = 48000;
        public Action<bool> PendingPermission;
        public int Discards;

        public ScriptedMicrophone(float[] samples = null, int rate = 48000)
        {
            Rate = rate;
            Samples = samples ?? Tone(rate, 2.0);
        }

        public static float[] Tone(int rate, double seconds)
        {
            var data = new float[(int)(rate * seconds)];
            for (var i = 0; i < data.Length; i++)
                data[i] = 0.3f * Mathf.Sin(2 * Mathf.PI * 440 * i / rate);
            return data;
        }

        /// <summary>Reads a 16-bit PCM mono WAV (as written by afconvert or say) into float samples.</summary>
        public static ScriptedMicrophone FromWav(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var offset = 12;
            int rate = 16000, dataStart = -1, dataLength = 0;
            while (offset + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                var size = BitConverter.ToInt32(bytes, offset + 4);
                if (id == "fmt ")
                {
                    Assert.That(BitConverter.ToInt16(bytes, offset + 10), Is.EqualTo(1), "mono WAV expected");
                    rate = BitConverter.ToInt32(bytes, offset + 12);
                }
                if (id == "data")
                {
                    dataStart = offset + 8;
                    dataLength = Math.Min(size, bytes.Length - dataStart);
                    break;
                }
                offset += 8 + size + (size % 2);
            }
            Assert.That(dataStart, Is.GreaterThan(0), "no data chunk in " + path);
            var samples = new float[dataLength / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(bytes, dataStart + i * 2) / 32768f;
            return new ScriptedMicrophone(samples, rate);
        }

        public bool IsAvailable => Available;
        public MicrophonePermission Permission => CurrentPermission;
        public void RequestPermission(Action<bool> onResult) => PendingPermission = onResult;

        public bool Start(int maxSeconds)
        {
            Capturing = true;
            return true;
        }

        public bool IsCapturing => Capturing;

        public float[] StopAndRead(out int channels, out int sampleRate)
        {
            Capturing = false;
            channels = 1;
            sampleRate = Rate;
            return (float[])Samples.Clone();
        }

        public void Discard()
        {
            Capturing = false;
            Discards++;
        }
    }

    /// <summary>
    /// Voice creation through the panel and the real <c>Update</c> loop: notice, consent, record (elapsed time), stop,
    /// transcribe, interpret, review, confirm, rehearse. The microphone and transcription are scripted here, so timing
    /// and failures are controlled. Real speech through the real service is covered by <see cref="LiveBackendVoiceTests"/>.
    /// </summary>
    [Category("Experience")]
    public sealed class VoiceCreationTests
    {
        sealed class ScriptedTranscriber : ITranscriptionProvider
        {
            public readonly List<(AudioClipData audio, TaskCompletionSource<TranscriptionOutcome> pending, CancellationToken token)> Calls =
                new List<(AudioClipData, TaskCompletionSource<TranscriptionOutcome>, CancellationToken)>();

            public Task<TranscriptionOutcome> TranscribeAsync(AudioClipData audio, CancellationToken token)
            {
                var pending = new TaskCompletionSource<TranscriptionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                Calls.Add((audio, pending, token));
                return pending.Task;
            }
        }

        sealed class ScriptedProposer : ISceneProposalProvider
        {
            public readonly List<string> Transcripts = new List<string>();
            public Func<string, ProposalOutcome> Answer = _ => ProposalOutcome.Of(Rig.Fixture("proposals/valid_ready.json"));

            public Task<ProposalOutcome> ProposeAsync(string transcript, CancellationToken token)
            {
                Transcripts.Add(transcript);
                return Task.FromResult(Answer(transcript));
            }
        }

        Rig _rig;
        ScriptedMicrophone _mic;
        ScriptedTranscriber _asr;
        ScriptedProposer _proposer;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _rig = new Rig();
            yield return _rig.Build();
            _mic = new ScriptedMicrophone();
            _asr = new ScriptedTranscriber();
            _proposer = new ScriptedProposer();
            _rig.App.UseMicrophone(_mic);
            _rig.App.UseProposalProvider(_proposer, null, _asr);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
        }

        static TranscriptionOutcome Heard(string text) =>
            TranscriptionOutcome.Of(new TranscriptionResult(text, "whisper_cpp_base_en", "en", TimeSpan.FromSeconds(0.4), null));

        IEnumerator RecordAndStop(float seconds = 0.3f)
        {
            _rig.Press("SpeakSituation");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.RecordingVoice));
            if (!_rig.App.AudioConsentGiven)
            {
                StringAssert.Contains("transcribed on this headset", _rig.App.VoiceText);
                StringAssert.Contains("Nothing is sent anywhere", _rig.App.VoiceText);
                Assert.That(_rig.Panel.IsInteractable("VoiceStop"), Is.False, "no recording before consent");
                _rig.Press("VoiceAllow");
            }
            Assert.That(_rig.App.Recorder.State, Is.EqualTo(RecorderState.Recording));
            var end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
            Assert.That(_rig.App.Recorder.ElapsedSeconds, Is.GreaterThan(0), "elapsed time advances in Update");
            StringAssert.Contains("RECORDING", _rig.App.VoiceText);
            StringAssert.Contains("of 15 s", _rig.App.VoiceText);
            _rig.Press("VoiceStop");
        }

        [UnityTest]
        public IEnumerator SpokenRequestBecomesAReviewedSceneThatRehearses()
        {
            yield return RecordAndStop();
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Transcribing));
            Assert.That(_asr.Calls.Count, Is.EqualTo(1));
            Assert.That(_asr.Calls[0].audio.Samples.Length, Is.EqualTo(32000), "2 s at 48 kHz prepared as 16 kHz mono");
            Assert.That(_rig.Panel.IsInteractable("VoiceCancel"), Is.True);
            _asr.Calls[0].pending.SetResult(Heard(PanelController_Preset0));
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Reviewing, 2, "review");
            Assert.That(_proposer.Transcripts, Is.EqualTo(new[] { PanelController_Preset0 }));
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.ReviewingProposal));
            var review = _rig.App.ReviewText;
            StringAssert.Contains("Spoken input, transcribed by whisper_cpp_base_en", review);
            StringAssert.Contains("You said:", review);
            StringAssert.Contains("Room 6.0 m x 5.0 m", review);
            _rig.Press("ConfirmProposal");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing), _rig.App.Status);
            yield return null;
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120)));
        }

        static string PanelController_Preset0 => Tryscape.Unity.Experience.PanelController.PresetTranscripts[0];

        [UnityTest]
        public IEnumerator ConsentIsAskedOnceAndRemembered()
        {
            yield return RecordAndStop();
            _rig.Press("VoiceCancel");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Idle));
            _rig.Press("VoiceBack");
            _rig.Press("SpeakSituation");
            Assert.That(_rig.App.Recorder.State, Is.EqualTo(RecorderState.Recording), "second recording starts directly");
        }

        [UnityTest]
        public IEnumerator PermissionDenialOffersTypingAndHands()
        {
            _mic.CurrentPermission = MicrophonePermission.NotDetermined;
            _rig.Press("SpeakSituation");
            _rig.Press("VoiceAllow");
            Assert.That(_rig.App.Recorder.State, Is.EqualTo(RecorderState.RequestingPermission));
            _mic.PendingPermission(false);
            yield return null;
            StringAssert.Contains("permission was not granted", _rig.App.Status);
            Assert.That(_rig.Panel.IsInteractable("VoiceTypeInstead"), Is.True);
            Assert.That(_rig.Panel.IsInteractable("VoiceHands"), Is.True);
            Assert.That(_asr.Calls, Is.Empty);
            _rig.Press("VoiceTypeInstead");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.DescribingTyped));
        }

        [UnityTest]
        public IEnumerator UnavailableMicrophoneAndMidRecordingLossAreReported()
        {
            _mic.Available = false;
            _rig.Press("SpeakSituation");
            _rig.Press("VoiceAllow");
            StringAssert.Contains("No microphone is available", _rig.App.Status);
            _mic.Available = true;
            _rig.Press("VoiceRetry");
            Assert.That(_rig.App.Recorder.State, Is.EqualTo(RecorderState.Recording));
            _mic.Capturing = false; // revoked or unplugged
            yield return null;
            yield return null;
            StringAssert.Contains("microphone stopped", _rig.App.Status);
            Assert.That(_asr.Calls, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FocusLossStopsRecordingAndDiscardsAudio()
        {
            _rig.Press("SpeakSituation");
            _rig.Press("VoiceAllow");
            yield return null;
            _rig.App.Suspend(); // OnApplicationPause(true) / focus loss
            Assert.That(_mic.Capturing, Is.False);
            Assert.That(_mic.Discards, Is.EqualTo(1));
            StringAssert.Contains("lost focus", _rig.App.Status);
            Assert.That(_asr.Calls, Is.Empty, "nothing is uploaded");
        }

        [UnityTest]
        public IEnumerator FocusLossDuringTranscriptionCancelsAndIgnoresTheLateResult()
        {
            yield return RecordAndStop();
            _rig.App.Suspend();
            Assert.That(_asr.Calls[0].token.IsCancellationRequested, Is.True);
            _asr.Calls[0].pending.TrySetResult(Heard(PanelController_Preset0));
            yield return null;
            yield return null;
            Assert.That(_proposer.Transcripts, Is.Empty);
            Assert.That(_rig.App.Session, Is.Null);
        }

        [UnityTest]
        public IEnumerator CancelDuringTranscriptionPreservesTheCurrentScene()
        {
            _rig.Press("CreateWithHands");
            _rig.Press("ConfirmCreate");
            var scene = _rig.App.Session.Current;
            _rig.Press("Menu");
            yield return RecordAndStop();
            _rig.Press("VoiceCancel");
            StringAssert.Contains("Nothing was changed", _rig.App.Status);
            _asr.Calls[0].pending.TrySetResult(Heard("Seven by six metres sofa room"));
            yield return null;
            yield return null;
            Assert.That(_proposer.Transcripts, Is.Empty);
            Assert.That(_rig.App.Session.Current, Is.SameAs(scene));
        }

        [UnityTest]
        public IEnumerator SilenceAndNoSpeechOfferRetry()
        {
            _mic.Samples = new float[48000 * 2];
            yield return RecordAndStop();
            StringAssert.Contains("No sound was picked up", _rig.App.Status);
            Assert.That(_asr.Calls, Is.Empty, "silence is not uploaded");
            _mic.Samples = ScriptedMicrophone.Tone(48000, 2);
            _rig.Press("VoiceRetry");
            _rig.Press("VoiceStop");
            _asr.Calls[0].pending.SetResult(Heard(""));
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Failed, 2, "no speech");
            StringAssert.Contains("No words were recognised", _rig.App.Status);
            Assert.That(_rig.Panel.IsInteractable("VoiceRetry"), Is.True);
            Assert.That(_proposer.Transcripts, Is.Empty);
        }

        [UnityTest]
        public IEnumerator TranscriptionServiceFailureOffersRecovery()
        {
            yield return RecordAndStop();
            _asr.Calls[0].pending.SetResult(TranscriptionOutcome.Failed(ProposalFailure.Unavailable, "network_unreachable", true));
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Failed, 2, "failure");
            StringAssert.Contains("not available", _rig.App.Status);
            Assert.That(_rig.Panel.IsInteractable("VoiceHands"), Is.True);
            Assert.That(_rig.Panel.IsInteractable("VoiceTypeInstead"), Is.True);
        }

        [UnityTest]
        public IEnumerator UnsupportedOrIncompleteSpeechIsReviewedNotBuilt()
        {
            _proposer.Answer = _ => ProposalOutcome.Of(Rig.Fixture("proposals/valid_needs_clarification.json"));
            yield return RecordAndStop();
            _asr.Calls[0].pending.SetResult(Heard("Prepare my room for a delivery"));
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Reviewing, 2, "clarification");
            Assert.That(_rig.Panel.IsInteractable("ConfirmProposal"), Is.False);
            _proposer.Answer = t => ProposalOutcome.Of(Rig.ReadyFor(MovingDayParameters.Example, t));
            _rig.Press("Choice0");
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Reviewing, 2, "answered");
            Assert.That(_proposer.Transcripts[1], Is.EqualTo("Prepare my room for a delivery\nAnswer: Use 6 m by 5 m example"));
            StringAssert.Contains("Spoken input", _rig.App.ReviewText, "the answer keeps the spoken origin");
        }

        [UnityTest]
        public IEnumerator EditTranscriptCorrectsMisheardWordsByHand()
        {
            _proposer.Answer = t => t.Contains("egg")
                ? ProposalOutcome.Of(Rig.Fixture("proposals/valid_needs_clarification.json"))
                : ProposalOutcome.Of(Rig.ReadyFor(MovingDayParameters.Example.With(widthMm: 8000), t));
            yield return RecordAndStop();
            _asr.Calls[0].pending.SetResult(Heard("An egg by five metre room for a sofa"));
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Reviewing, 2, "review");
            _rig.Press("EditTranscript");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.DescribingTyped));
            Assert.That(_rig.App.TypedTranscript, Is.EqualTo("An egg by five metre room for a sofa"));
            _rig.App.SetTypedTranscript("An eight by five metre room for a sofa"); // keyboard correction
            _rig.Press("SubmitTranscript");
            yield return _rig.Until(() => _rig.App.Creation.State == CreationState.Reviewing, 2, "corrected review");
            Assert.That(_rig.App.ProposedParameters.RoomWidthMm, Is.EqualTo(8000));
        }

        [UnityTest]
        public IEnumerator HandOnlyCreationStillWorksAfterVoiceFailure()
        {
            _mic.Available = false;
            _rig.Press("SpeakSituation");
            _rig.Press("VoiceAllow");
            _rig.Press("VoiceHands");
            _rig.Press("ConfirmCreate");
            Assert.That(_rig.App.Mode, Is.EqualTo(AppMode.Editing));
            yield return null;
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.Outcome, Is.EqualTo(EvaluationOutcome.BlockedInModel));
        }
    }

    /// <summary>
    /// Real speech through the real pipeline: WAV speech → <c>BackendTranscriptionProvider</c> → local API and worker
    /// (whisper.cpp) → <c>BackendSceneProposalProvider</c> → deterministic interpreter → review → confirm → rehearse.
    /// Only the microphone is replaced, by a file source. Opt-in: <c>-testCategory BackendVoice</c> with
    /// TRYSCAPE_SPEECH_DIR pointing at reference.wav, variant.wav and unsupported.wav, and the service running with ASR
    /// configured. The audio may be synthetic (macOS say) or human; the run evidence must say which.
    /// </summary>
    [Category("BackendVoice")]
    public sealed class LiveBackendVoiceTests
    {
        Rig _rig;
        string _dir;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _dir = Environment.GetEnvironmentVariable("TRYSCAPE_SPEECH_DIR");
            Assert.That(_dir, Is.Not.Null.And.Not.Empty, "set TRYSCAPE_SPEECH_DIR");
            _rig = new Rig();
            yield return _rig.Build();
            var client = new CreationServiceClient(CreationServiceConfig.For(CreationServiceConfig.DevelopmentBaseUrl));
            _rig.App.UseProposalProvider(new BackendSceneProposalProvider(client, "1.0.0"), null, new BackendTranscriptionProvider(client));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            yield return null;
        }

        IEnumerator Speak(string file)
        {
            _rig.App.UseMicrophone(ScriptedMicrophone.FromWav(Path.Combine(_dir, file)));
            _rig.Press("SpeakSituation");
            if (!_rig.App.AudioConsentGiven)
                _rig.Press("VoiceAllow");
            yield return null;
            _rig.Press("VoiceStop");
            yield return _rig.Until(() => !_rig.App.Creation.IsBusy, 30, "transcription + proposal");
            Debug.Log($"[Tryscape][VOICE] file={file} state={_rig.App.Creation.State} " +
                      $"asr={_rig.App.Creation.TranscriptionProviderId} transcript=\"{_rig.App.Creation.Transcript}\"");
        }

        [UnityTest]
        public IEnumerator DifferentSpokenRequestsProduceTheirReviewedSceneParameters()
        {
            yield return Speak("reference.wav");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Reviewing), _rig.App.Status);
            StringAssert.StartsWith("whisper_cpp", _rig.App.Creation.TranscriptionProviderId);
            Assert.That(_rig.App.Creation.Proposal.ProviderId, Is.EqualTo("deterministic_rules"));
            var reference = _rig.App.ProposedParameters;
            Assert.That((reference.RoomWidthMm, reference.RoomDepthMm, reference.Obstruction, reference.RouteId),
                Is.EqualTo((6000, 5000, ObstructionState.Center, MovingDayPack.DirectRouteId)));
            _rig.Press("ConfirmProposal");
            yield return null;
            _rig.Press("Rehearse");
            Assert.That(_rig.App.LatestRun.Evidence.FirstViolation.EntryFraction, Is.EqualTo(new Rational(11, 120)));
            var built = _rig.App.Session.Current;

            _rig.Press("Menu");
            yield return Speak("variant.wav");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Reviewing), _rig.App.Status);
            var variant = _rig.App.ProposedParameters;
            Assert.That((variant.RoomWidthMm, variant.RoomDepthMm, variant.Obstruction, variant.RouteId),
                Is.EqualTo((7000, 6000, ObstructionState.ParkedSouth, MovingDayPack.NorthRouteId)));
            Assert.That(_rig.App.Session.Current, Is.SameAs(built), "nothing is replaced before confirmation");

            _rig.Press("ReviewMenu");
            yield return Speak("unsupported.wav");
            Assert.That(_rig.App.Creation.Proposal?.Status, Is.EqualTo(ProposalStatus.Unsupported), _rig.App.Status);
            Assert.That(_rig.App.Session.Current, Is.SameAs(built));
        }
    }

    /// <summary>
    /// Experimental loopback check of the REAL Unity microphone adapter on the developer Mac. While speech plays through
    /// the speakers (afplay), it records with <see cref="UnityMicrophone"/> and sends the capture through the real
    /// service. This exercises real capture, but with synthetic speech replayed acoustically; it is not a human-speech
    /// or headset check. Opt-in: <c>-testCategory MicrophoneLoopback</c> with TRYSCAPE_SPEECH_DIR and the service up.
    /// </summary>
    [Category("MicrophoneLoopback")]
    public sealed class MicrophoneLoopbackTests
    {
        [UnityTest]
        public IEnumerator RealMicrophoneCapturesReplayedSpeech()
        {
            var dir = Environment.GetEnvironmentVariable("TRYSCAPE_SPEECH_DIR");
            Assert.That(dir, Is.Not.Null.And.Not.Empty);
            Debug.Log($"[Tryscape][MIC] devices=[{string.Join(", ", Microphone.devices)}]");
            Assert.That(Microphone.devices.Length, Is.GreaterThan(0), "no capture device visible to Unity");
            var rig = new Rig();
            yield return rig.Build();
            try
            {
                var client = new CreationServiceClient(CreationServiceConfig.For(CreationServiceConfig.DevelopmentBaseUrl));
                rig.App.UseProposalProvider(new BackendSceneProposalProvider(client, "1.0.0"), null, new BackendTranscriptionProvider(client));
                var microphone = new UnityMicrophone();
                rig.App.UseMicrophone(microphone);
                rig.Press("SpeakSituation");
                rig.Press("VoiceAllow");
                yield return rig.Until(() => rig.App.Recorder.State != RecorderState.RequestingPermission, 10, "permission");
                Assert.That(rig.App.Recorder.State, Is.EqualTo(RecorderState.Recording), rig.App.Status);
                var player = System.Diagnostics.Process.Start("/usr/bin/afplay", "\"" + Path.Combine(dir, "reference.wav") + "\"");
                var end = Time.realtimeSinceStartup + 9f;
                while (Time.realtimeSinceStartup < end && !player.HasExited)
                    yield return null;
                yield return new WaitForSecondsRealtime(0.5f);
                rig.Press("VoiceStop");
                Debug.Log($"[Tryscape][MIC] recorder={rig.App.Recorder.State} failure={rig.App.Recorder.Failure}");
                yield return rig.Until(() => !rig.App.Creation.IsBusy, 30, "pipeline");
                Debug.Log($"[Tryscape][MIC] state={rig.App.Creation.State} transcript=\"{rig.App.Creation.Transcript}\" " +
                          $"status=\"{rig.App.Status}\"");
                Assert.That(rig.App.Creation.State, Is.EqualTo(CreationState.Reviewing), rig.App.Status);
                Assert.That(rig.App.ProposedParameters?.RoomWidthMm, Is.EqualTo(6000));
            }
            finally
            {
                rig.Dispose();
            }
        }
    }
}
