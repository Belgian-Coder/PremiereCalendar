"""Native process fixture only: no WSL, Docker, SDK, network or credentials."""
import pathlib
import json
import subprocess
import sys
import time

sys.stdout.reconfigure(newline="\n")
sys.stderr.reconfigure(newline="\n")

mode = sys.argv[1]
if mode == "duplex":
    sys.stdout.write("O" * 262144 + "\nSTDOUT_END\n")
    sys.stdout.flush()
    sys.stderr.write("E" * 262144 + "\nSTDERR_END\n")
    sys.stderr.flush()
    size = 0
    while data := sys.stdin.buffer.read(4096):
        size += len(data)
        time.sleep(0.0005)
    print(f"INPUT_BYTES={size}", flush=True)
elif mode == "hold":
    time.sleep(15)
elif mode == "inherited_pipe":
    child = subprocess.Popen(
        [sys.executable, "-I", "-B", __file__, "hold"],
        stdin=subprocess.DEVNULL,
        stdout=sys.stdout,
        stderr=sys.stderr,
    )
    pathlib.Path(sys.argv[2]).write_text(str(child.pid))
    print("PARENT_END", flush=True)
elif mode == "delay":
    time.sleep(3)
elif mode == "partial_delay":
    print("OUT_BEFORE_TIMEOUT", flush=True)
    print("ERR_BEFORE_TIMEOUT", file=sys.stderr, flush=True)
    time.sleep(3)
elif mode == "exit7":
    sys.stdin.buffer.read()
    print("EXPECTED_FAILURE", flush=True)
    raise SystemExit(7)
elif mode == "early7":
    print("EARLY_FAILURE", flush=True)
    raise SystemExit(7)
elif mode == "json_stderr":
    sys.stderr.write("E" * 262144 + "\nJSON_STDERR_END\n")
    sys.stderr.flush()
    size = len(sys.stdin.buffer.read())
    print(json.dumps({"ok": True, "argv": sys.argv[2:], "values": list(range(10000)), "inputBytes": size}), flush=True)
elif mode == "argv":
    sys.stdin.buffer.read()
    print(json.dumps(sys.argv[2:]), flush=True)
elif mode == "exit124":
    sys.stdin.buffer.read()
    raise SystemExit(124)
elif mode in ("exit125", "exit126", "exit127"):
    sys.stdin.buffer.read()
    raise SystemExit(int(mode[4:]))
elif mode in ("unicode_stdout", "unicode_stderr"):
    signal, ack = map(pathlib.Path, sys.argv[2:4])
    target = sys.stdout.buffer if mode == "unicode_stdout" else sys.stderr.buffer
    other = sys.stderr.buffer if mode == "unicode_stdout" else sys.stdout.buffer
    prefix = "A" if mode == "unicode_stdout" else "B"
    # 8191 BMP chars plus a supplementary scalar: an8192 UTF16 read ends
    # between the high/low surrogates. The writer barrier then interleaves
    # the other pipe before the next target chunk is forwarded.
    target.write((prefix * 8191 + "\U0001f600").encode("utf-8"))
    target.flush()
    deadline = time.monotonic() + 3
    while not signal.exists() and time.monotonic() < deadline:
        time.sleep(0.001)
    if not signal.exists():
        raise SystemExit(91)
    other.write(b"BETWEEN_OTHER\n")
    other.flush()
    ack.write_text("ready")
    target.write(b"TARGET_END\n")
    target.flush()
else:
    raise SystemExit("Unknown fixture mode")
