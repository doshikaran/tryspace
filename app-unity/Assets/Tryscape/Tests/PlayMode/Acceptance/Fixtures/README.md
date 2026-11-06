# Synthetic speech fixtures

`synthetic_tts_seven_by_six_north.wav` was generated on the developer Mac with macOS text-to-speech
(`say -v Samantha`, then `afconvert` to 16 kHz mono 16-bit PCM). It is **synthetic speech, not a human recording**.
It proves the on-device pipeline is wired end to end (audio → whisper.cpp → rule interpreter → proposal). It is no
evidence of recognition accuracy on human speech (AC02/AC23 still need human recordings on a Quest). Test-only:
it lives under Tests and is not included in player builds.
