"""Native process fixture only: no WSL, Docker, SDK, network or credentials."""
import pathlib
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
    time.sleep(2)
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
elif mode == "exit7":
    sys.stdin.buffer.read()
    print("EXPECTED_FAILURE", flush=True)
    raise SystemExit(7)
elif mode == "early7":
    print("EARLY_FAILURE", flush=True)
    raise SystemExit(7)
else:
    raise SystemExit("Unknown fixture mode")
