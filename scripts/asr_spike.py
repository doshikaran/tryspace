#!/usr/bin/env python3
"""ASR feasibility spike (Phase 3): audio -> whisper.cpp -> deterministic parser -> SceneProposal, scored against the
hand-written expectations in contracts/v1/fixtures/interpretation/moving_day_cases.json.

Two audio sources, always reported separately:
  --source tts         macOS `say` synthetic speech, written directly as 16 kHz mono 16-bit WAV. Real audio through
                       the real ASR, but SYNTHETIC voices: not evidence of accuracy on human speech.
  --source recordings  Human recordings, one WAV per case, named <case_id>.wav, in --recordings DIR. Convert with:
                       afconvert -f WAVE -d LEI16@16000 -c 1 input.m4a <case_id>.wav

Audio is generated into a temporary directory that is deleted afterwards; no audio is written to the repository.
Output: a JSON summary (no audio, no recordings). Run from the repository root:
  uv run --project services/api python scripts/asr_spike.py --source tts --out artifacts/phase-3/asr-spike-mac-tts.json
"""

from __future__ import annotations

import argparse
import json
import platform
import re
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

from tryscape_api.creation.intent import DeterministicIntentExtractor
from tryscape_api.creation.proposals import ProposalService
from tryscape_api.creation.transcription import TranscriptionFailed, WavError, WhisperCppTranscriber, validate_wav

ROOT = Path(__file__).resolve().parent.parent
CASES = ROOT / "contracts/v1/fixtures/interpretation/moving_day_cases.json"
CACHE = Path.home() / ".cache/tryscape"
DEFAULT_VOICES = ["Samantha", "Daniel", "Karen", "Rishi", "Moira", "Tessa"]


def words(text: str) -> list[str]:
    return re.findall(r"[a-z0-9.]+", text.lower().replace("-", " "))


def wer(reference: str, hypothesis: str) -> float:
    r, h = words(reference), words(hypothesis)
    d = list(range(len(h) + 1))
    for i in range(1, len(r) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(h) + 1):
            cur = min(d[j] + 1, d[j - 1] + 1, prev + (r[i - 1] != h[j - 1]))
            prev, d[j] = d[j], cur
    return d[len(h)] / max(1, len(r))


def correct(case: dict[str, Any], proposal: Any) -> bool:
    if proposal.status != case["status"]:
        return False
    if case["status"] == "ready":
        return proposal.intent.model_dump() == {"scenario_pack_id": "moving_day", **case["intent"]}
    if case["status"] == "needs_clarification":
        return [q.field for q in proposal.questions] == case["question_fields"]
    return [a.code for a in proposal.assumptions] == case["unsupported_codes"]


def peak_rss_mb(argv: list[str]) -> float:
    out = subprocess.run(["/usr/bin/time", "-l", *argv], capture_output=True, check=True)  # noqa: S603
    match = re.search(rb"(\d+)\s+maximum resident set size", out.stderr)
    return int(match.group(1)) / (1024 * 1024) if match else float("nan")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", choices=["tts", "recordings"], required=True)
    parser.add_argument("--recordings", type=Path)
    parser.add_argument("--models", nargs="+", default=["tiny.en", "base.en"])
    parser.add_argument("--voices", nargs="+", default=DEFAULT_VOICES)
    parser.add_argument("--binary", type=Path, default=CACHE / "whisper.cpp/build/bin/whisper-cli")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    cases = [c for c in json.loads(CASES.read_text())["cases"] if "\n" not in c["transcript"]]
    service = ProposalService(DeterministicIntentExtractor())
    report: dict[str, Any] = {
        "source": args.source,
        "audio_kind": "synthetic_tts_macos_say" if args.source == "tts" else "human_recordings",
        "host": {"machine": platform.machine(), "os": platform.platform(), "processor": platform.processor()},
        "binary": str(args.binary.name),
        "interpreter": "deterministic_rules 1.0.0",
        "cases": len(cases),
        "models": {},
    }
    with tempfile.TemporaryDirectory(prefix="tryscape-asr-spike-") as tmp:
        audio: dict[tuple[str, str], bytes] = {}
        if args.source == "tts":
            for voice in args.voices:
                for case in cases:
                    path = Path(tmp) / f"{voice}-{case['id']}.wav"
                    subprocess.run(  # noqa: S603 - fixed argv; transcript text is passed as an argument, not a shell string
                        ["/usr/bin/say", "-v", voice, "-o", str(path), "--data-format=LEI16@16000", "--channels=1", case["transcript"]],
                        check=True,
                    )
                    audio[(voice, case["id"])] = path.read_bytes()
        else:
            if args.recordings is None:
                parser.error("--recordings DIR is required for --source recordings")
            for case in cases:
                path = args.recordings / f"{case['id']}.wav"
                if path.is_file():
                    audio[("human", case["id"])] = path.read_bytes()
        by_id = {c["id"]: c for c in cases}

        for model_name in args.models:
            model = CACHE / f"models/ggml-{model_name}.bin"
            asr = WhisperCppTranscriber(args.binary, model, threads=4, provider_id=f"whisper_cpp_{model_name.replace('.', '_')}")
            rows = []
            for (voice, case_id), data in audio.items():
                case = by_id[case_id]
                row: dict[str, Any] = {"voice": voice, "case": case_id}
                try:
                    info = validate_wav(data)
                    row["audio_seconds"] = round(info.seconds, 2)
                    started = time.perf_counter()
                    transcript = asr.transcribe(data, timeout_seconds=30)
                    row["asr_seconds"] = round(time.perf_counter() - started, 3)
                    proposal = service.propose(transcript.text)
                    row.update(
                        transcript=transcript.text,
                        wer=round(wer(case["transcript"], transcript.text), 3),
                        status=proposal.status,
                        correct=correct(case, proposal),
                    )
                except (WavError, TranscriptionFailed) as exc:
                    row.update(error=str(exc), correct=False)
                rows.append(row)
            sample = next(iter(audio.values()))
            sample_path = Path(tmp) / "rss-sample.wav"
            sample_path.write_bytes(sample)
            ok = [r for r in rows if "asr_seconds" in r]
            latencies = sorted(r["asr_seconds"] for r in ok)
            report["models"][model_name] = {
                "model_file": model.name,
                "utterances": len(rows),
                "parameters_correct": sum(r["correct"] for r in rows),
                "accuracy": round(sum(r["correct"] for r in rows) / max(1, len(rows)), 3),
                "mean_wer": round(statistics.mean(r["wer"] for r in ok), 3) if ok else None,
                "latency_s_median": latencies[len(latencies) // 2] if latencies else None,
                "latency_s_p95": latencies[int(len(latencies) * 0.95) - 1] if latencies else None,
                "latency_note": "Cold whisper-cli process per utterance (model load included); a resident whisper-server would avoid reloads.",
                "audio_s_median": statistics.median(r["audio_seconds"] for r in ok) if ok else None,
                "peak_rss_mb_one_utterance": round(peak_rss_mb(asr.argv(sample_path)), 1),
                "failures": [r for r in rows if not r["correct"]],
            }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + "\n")
    for name, m in report["models"].items():
        print(f"{name}: correct {m['parameters_correct']}/{m['utterances']}  WER {m['mean_wer']}  "
              f"median {m['latency_s_median']}s p95 {m['latency_s_p95']}s  peak RSS {m['peak_rss_mb_one_utterance']} MB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
