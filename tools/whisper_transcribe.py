#!/usr/bin/env python3
"""Speech-to-text for videos with no captions. Prints a JSON array of {"start": seconds, "text": "..."}.

Setup:   python3 -m venv .venv && .venv/bin/pip install faster-whisper "av<16"
         (faster-whisper 1.2.1 breaks with PyAV 16 and newer.)
Config:  Insights__WhisperCommand=".venv/bin/python tools/whisper_transcribe.py {audio}"
"""
import json
import os
import sys

from faster_whisper import WhisperModel


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit("usage: whisper_transcribe.py <audio file>")

    # "base" balances speed and accuracy on a CPU. Larger models handle tickers and names better.
    model = WhisperModel(os.environ.get("WHISPER_MODEL", "base"), device="cpu", compute_type="int8")
    segments, _ = model.transcribe(sys.argv[1], vad_filter=True)
    print(json.dumps([{"start": s.start, "text": s.text.strip()} for s in segments]))


if __name__ == "__main__":
    main()
