using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Tryscape.Application.Creation;
using Tryscape.Content.MovingDay;
using Tryscape.Unity;
using Tryscape.Unity.Creation;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tryscape.Tests.PlayMode.Acceptance
{
    /// <summary>
    /// On-device voice (decision D12) through the real app: file microphone -> OnDeviceTranscriptionProvider (whisper.cpp
    /// native plugin, bundled base.en model) -> MovingDayLocalProposals -> review -> confirm. No server, no network.
    /// The speech is SYNTHETIC (macOS text-to-speech, see Fixtures/README.md): this proves wiring, not accuracy on human
    /// speech, and it runs the Editor's macOS plugin, not the Quest build.
    /// </summary>
    [Category("Experience")]
    public sealed class OnDeviceVoiceTests
    {
        static string Fixture(string name) =>
            Path.Combine(UnityEngine.Application.dataPath, "Tryscape", "Tests", "PlayMode", "Acceptance", "Fixtures", name);

        Rig _rig;
        OnDeviceTranscriptionProvider _speech;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Assert.IsTrue(OnDeviceTranscriptionProvider.NativeAvailable, "speech plugin missing; run scripts/build_whisper_plugin.sh");
            Assert.IsTrue(BundledModel.Verify(SpeechModel.StreamingPath), "speech model missing; run scripts/fetch_asr_model.sh");
            _rig = new Rig();
            yield return _rig.Build();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _speech?.Dispose();
            _rig.Dispose();
            yield return null;
        }

        IEnumerator Speak(string wav)
        {
            _rig.App.UseMicrophone(ScriptedMicrophone.FromWav(Fixture(wav)));
            _rig.Press("SpeakSituation");
            if (!_rig.App.AudioConsentGiven)
                _rig.Press("VoiceAllow");
            yield return null;
            _rig.Press("VoiceStop");
            yield return _rig.Until(() => !_rig.App.Creation.IsBusy, 30, "on-device transcription + interpretation");
            Debug.Log($"[Tryscape][VOICE] on-device state={_rig.App.Creation.State} asr={_rig.App.Creation.TranscriptionProviderId} " +
                      "transcript_chars=" + (_rig.App.Creation.Transcript?.Length ?? 0));
        }

        [UnityTest]
        public IEnumerator SpokenRequestIsTranscribedAndInterpretedOnTheHeadsetPath()
        {
            _speech = new OnDeviceTranscriptionProvider();
            _rig.App.UseProposalProvider(new MovingDayLocalProposals(new Tryscape.Application.Editing.RandomIdGenerator()), null, _speech);
            yield return Speak("synthetic_tts_seven_by_six_north.wav");
            Assert.That(_rig.App.Creation.State, Is.EqualTo(CreationState.Reviewing), _rig.App.Status);
            Assert.That(_rig.App.Creation.TranscriptionProviderId, Is.EqualTo(SpeechModel.ProviderId));
            Assert.That(_rig.App.Creation.Proposal.ProviderId, Is.EqualTo("deterministic_rules"));
            var p = _rig.App.ProposedParameters;
            Assert.That((p.RoomWidthMm, p.RoomDepthMm, p.Obstruction, p.RouteId),
                Is.EqualTo((7000, 6000, ObstructionState.ParkedSouth, MovingDayPack.NorthRouteId)));
            Assert.IsTrue(_speech.Loaded);
            _rig.Press("ConfirmProposal");
            yield return null;
            Assert.AreEqual(AppMode.Editing, _rig.App.Mode, _rig.App.Status);
            Assert.AreEqual(7000, _rig.App.Session.Current.Room.WidthMm);

            _rig.App.Suspend();
            Assert.IsFalse(_speech.Loaded, "suspension frees the speech model");
        }

        [UnityTest]
        public IEnumerator MissingOrDamagedModelFailsPlainlyAndHandsStillWork()
        {
            _speech = new OnDeviceTranscriptionProvider(_ => Task.FromResult<string>(null));
            _rig.App.UseProposalProvider(new MovingDayLocalProposals(new Tryscape.Application.Editing.RandomIdGenerator()), null, _speech);
            yield return Speak("synthetic_tts_seven_by_six_north.wav");
            StringAssert.Contains("Speech recognition is not available", _rig.App.VoiceText + " " + _rig.App.Status);
            Assert.IsFalse(_speech.Loaded);
            _rig.Press("VoiceHands");
            Assert.AreEqual(AppMode.CreatingWithHands, _rig.App.Mode);
        }

        [Test]
        public void ModelVerificationRejectsATruncatedCopy()
        {
            var path = Path.Combine(Path.GetTempPath(), "tryscape-model-" + System.Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(path, new byte[1024]);
            try
            {
                Assert.IsFalse(BundledModel.Verify(path));
                Assert.IsFalse(BundledModel.Verify(path + ".missing"));
            }
            finally
            {
                File.Delete(path);
            }
            Assert.AreEqual("seven by six metres", OnDeviceTranscriptionProvider.Clean(" [BLANK_AUDIO] seven  by six (music) metres "));
        }
    }
}
