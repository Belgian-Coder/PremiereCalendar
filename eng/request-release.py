#!/usr/bin/env python3
"""Standalone committed-source SSH release requests. Python 3.10+, stdlib only."""
from __future__ import annotations

import argparse
import contextlib
import fnmatch
import glob
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import shlex
import subprocess
import sys
import tempfile
import threading
import time
import uuid

HOSTS = {
    "windows11-vm": ("WINDOWS11-VM", "windows", "x64"),
    "pc-marc": ("PC-MARC", "windows", "x64"),
    "macbook-pro": ("MacBook-Pro", "macos", "arm64"),
}
MODES = ("validate", "package", "upload")
MAX_JOBS = 16
MAX_JSON = 1024 * 1024
MAX_RESULT = 8 * 1024 * 1024
MAX_ARTIFACT = 16 * 1024**3
MARKER = {"owner": "request-release", "version": 1}
PARAM_NAME = r"[A-Za-z][A-Za-z0-9_]{0,39}"
PLACEHOLDER = re.compile(r"\{(output|target|commit|jobs|param:" + PARAM_NAME + r")\}")
LAUNCHER = "import sys; n=int(sys.stdin.buffer.readline()); s=sys.stdin.buffer.read(n); exec(compile(s,'request-release.py','exec'),{'__name__':'__remote__'})"
CONSOLE_PYTHON = {
    "windows11-vm": "C:/Users/marcp/AppData/Local/Programs/Python/Python312/python.exe",
    "macbook-pro": "/Library/Frameworks/Python.framework/Versions/3.10/bin/python3",
}


class RequestError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise RequestError(message)


def json_bytes(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")


def relative_path(value, label, pattern=False):
    require(isinstance(value, str) and value and len(value) <= 240,
            f"{label}: expected a nonempty relative path")
    require(not re.search(r"[\\:\x00-\x1f{}]", value), f"{label}: unsafe path")
    parts = value.split("/")
    require(not value.startswith("/") and all(p not in ("", ".", "..") for p in parts),
            f"{label}: traversal/absolute path forbidden")
    for part in parts:
        require(part.lower() != ".git" and not part.endswith((".", " ")),
                f"{label}: Git internals/ambiguous path forbidden")
        require(not re.match(r"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", part, re.I),
                f"{label}: reserved path")
    require(pattern or not re.search(r"[*?\[\]]", value), f"{label}: glob forbidden")
    return value


def within(root, path):
    root = Path(root).resolve()
    path = Path(path)
    # Refuse symlinks/junctions even if their resolved destination stays inside.
    absolute = Path(os.path.abspath(path))
    require(absolute.is_relative_to(root), f"Path escapes root: {path}")
    cursor = absolute
    while cursor != root:
        require(not cursor.is_symlink() and not (hasattr(cursor, "is_junction") and cursor.is_junction()),
                f"Linked path forbidden: {cursor}")
        # Python 3.10 Windows junction detection.
        if os.name == "nt" and cursor.exists():
            require(not (cursor.lstat().st_file_attributes & 0x400), f"Reparse point forbidden: {cursor}")
        cursor = cursor.parent
    require(absolute.resolve().is_relative_to(root), f"Path escapes root: {path}")
    return absolute


def load_manifest(raw):
    require(len(raw) <= MAX_JSON, "Manifest exceeds 1 MiB")
    try:
        manifest = json.loads(raw)
    except (ValueError, UnicodeError) as exc:
        raise RequestError(f"Invalid manifest JSON: {exc}") from exc
    require(isinstance(manifest, dict) and type(manifest.get("schemaVersion")) is int and manifest["schemaVersion"] == 1,
            "Expected manifest schemaVersion=1")
    fields = {"schemaVersion", "project", "stateDirectory", "worktreeDirectory", "targets"}
    require(set(manifest) == fields, "Manifest has missing or unknown fields")
    require(isinstance(manifest["project"], str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 _-]{0,79}", manifest["project"]),
            "project must be a stable safe folder name")
    relative_path(manifest["project"], "project")
    state = relative_path(manifest["stateDirectory"], "stateDirectory")
    worktrees = relative_path(manifest["worktreeDirectory"], "worktreeDirectory")
    require(not (state == worktrees or state.startswith(worktrees + "/") or worktrees.startswith(state + "/")),
            "State and worktree directories must not overlap")
    targets = manifest["targets"]
    require(isinstance(targets, dict) and targets, "targets must be a nonempty object")
    for name, target in targets.items():
        require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}", name), "Unsafe target identifier")
        target_fields = {"host", "platform", "architecture", "commands", "artifacts", "uploadPolicy", "notes"}
        require(isinstance(target, dict) and target_fields <= set(target) <= target_fields | {"parameters"},
            f"{name}: missing/unknown target fields")
        host = target["host"]
        require(isinstance(host, str) and host in HOSTS, f"{name}: unsupported host")
        require((target["platform"], target["architecture"]) == HOSTS[host][1:],
                f"{name}: host/platform/architecture mismatch")
        require(target["uploadPolicy"] in ("existing-gates", "draft-only", "unsupported"), "Invalid uploadPolicy")
        require(isinstance(target["notes"], list) and all(isinstance(x, str) for x in target["notes"]), "notes must be strings")
        parameters = target.get("parameters", {})
        require(isinstance(parameters, dict), "parameters must be an object")
        for key, definition in parameters.items():
            require(re.fullmatch(PARAM_NAME, key), "Parameter names must be safe identifiers")
            require(not re.search(r"TOKEN|PASSWORD|SECRET|PRIVATEKEY|SIGNINGKEY|CREDENTIAL", key.replace("_", "").upper()), "Secret parameters are forbidden")
            require(isinstance(definition, dict) and set(definition) <= {"default", "requiredModes", "pattern"}, "Invalid parameter definition")
            required = definition.get("requiredModes", [])
            require(isinstance(required, list) and all(p in MODES for p in required), "Invalid requiredModes")
            if "pattern" in definition:
                pattern = definition["pattern"]
                require(isinstance(pattern, str) and 0 < len(pattern) <= 256, "Parameter regex must have 1..256 characters")
                try:
                    re.compile(pattern)
                except re.error as exc:
                    raise RequestError("Invalid parameter regex") from exc
            if "default" in definition:
                validate_parameter(key, definition["default"], definition)
        commands = target["commands"]
        require(isinstance(commands, dict) and commands and set(commands) <= set(MODES), "Invalid command modes")
        require("validate" in commands, f"{name}: validate commands required")
        require(not commands.get("upload") or ("package" in commands and target["uploadPolicy"] in ("existing-gates", "draft-only")),
                f"{name}: upload requires package and an explicit upload policy")
        for mode, steps in commands.items():
            require(isinstance(steps, list) and (steps or (mode == "upload" and target["uploadPolicy"] == "unsupported")),
                    f"{name}/{mode}: expected nonempty array of argv arrays")
            for argv in steps:
                require(isinstance(argv, list) and argv and all(isinstance(a, str) and a and not re.search(r"[\x00-\x1f]", a) for a in argv),
                        f"{name}/{mode}: malformed argv")
                executable = argv[0].replace("\\", "/").split("/")[-1].lower()
                require(executable not in {"sh", "bash", "zsh", "dash", "ksh", "fish", "cmd", "cmd.exe"}, "Shell command snippets are forbidden; invoke a script explicitly")
                require(not executable.endswith((".bat", ".cmd")), "Batch files require an explicit existing PowerShell adapter")
                lowered = [a.lower() for a in argv[1:]]
                if executable in {"pwsh", "pwsh.exe", "powershell", "powershell.exe"}:
                    require("-file" in lowered and not any(a in {"-c", "-command", "-encodedcommand", "-enc", "-e"} for a in lowered),
                            "PowerShell requires explicit -File, never inline commands")
                if re.fullmatch(r"(?:python[0-9.]*|py)(?:\.exe)?", executable):
                    require("-c" not in lowered, "Python inline code is forbidden; invoke a script/module explicitly")
                if executable in {"node", "node.exe", "ruby", "perl"}:
                    require(not any(a in {"-e", "--eval", "-p", "--print"} for a in lowered), "Inline code is forbidden")
                require(not PLACEHOLDER.search(argv[0]), "Executable must be a fixed command, not a placeholder")
                for arg in argv:
                    require(not re.search(r"[{}]", PLACEHOLDER.sub("", arg)), f"Unknown/unsafe placeholder in {arg!r}")
                    for match in PLACEHOLDER.finditer(arg):
                        name = match.group(1)
                        require(not name.startswith("param:") or name[6:] in parameters, f"Undeclared parameter in {arg!r}")
        require(isinstance(target["artifacts"], list), "artifacts must be an array")
        for pattern in target["artifacts"]:
            relative_path(pattern, "artifact pattern", pattern=True)
    return manifest


def validate_parameter(name, value, definition):
    require(isinstance(value, str) and 0 < len(value) <= 1024 and not re.search(r"[\x00-\x1f\x7f]", value),
            f"Parameter {name}: expected nonsecret single-line value of 1..1024 characters")
    require(not re.search(r"-----BEGIN|://[^/ ]+:[^/ ]+@", value), f"Parameter {name}: credential material forbidden")
    require("pattern" not in definition or re.fullmatch(definition["pattern"], value), f"Parameter {name}: value does not match declared pattern")


def resolve_parameters(target, supplied, phases):
    definitions = target.get("parameters", {})
    values = {key: d["default"] for key, d in definitions.items() if "default" in d}
    seen = set()
    for item in supplied:
        key, separator, value = item.partition("=")
        require(separator and key in definitions and key not in seen, "--parameter must name a declared, unique NAME=VALUE")
        validate_parameter(key, value, definitions[key])
        values[key] = value
        seen.add(key)
    missing = []
    for key, definition in definitions.items():
        if key in values:
            validate_parameter(key, values[key], definition)
        elif set(definition.get("requiredModes", [])) & set(phases):
            missing.append(key)
    for phase in phases:
        for argv in target["commands"].get(phase, []):
            for arg in argv:
                for match in PLACEHOLDER.finditer(arg):
                    name = match.group(1)
                    if name.startswith("param:") and name[6:] not in values:
                        missing.append(name[6:])
    return values, sorted(set(missing))


def interpolate(argv, variables, parameters):
    def substitute(match):
        name = match.group(1)
        return parameters[name[6:]] if name.startswith("param:") else variables[name]
    return [PLACEHOLDER.sub(substitute, arg) for arg in argv]


def run(argv, cwd=None, check=True):
    proc = subprocess.run(argv, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    if check and proc.returncode:
        # Do not echo command stderr: Git/SSH diagnostics may contain access material.
        raise RequestError(f"{Path(str(argv[0])).name} failed with exit code {proc.returncode}")
    return proc


def git(root, *args, check=True):
    # Repository source is already explicitly selected; no persistent trust changes.
    return run(["git", "-c", f"safe.directory={Path(root).resolve().as_posix()}",
                "-c", "core.hooksPath=/dev/null", "-c", "gc.auto=0", "-c", "maintenance.auto=false",
                "-C", str(root), *args], check=check)


def ensure_ignored(root, manifest):
    for key in ("stateDirectory", "worktreeDirectory"):
        require(git(root, "check-ignore", "--no-index", "--quiet", "--", manifest[key] + "/probe", check=False).returncode == 0,
                f"{key} must be ignored in the pinned source: {manifest[key]}")


def clean_source(root):
    require(not git(root, "status", "--porcelain=v1", "--untracked-files=no").stdout,
            "Tracked/index changes present; commit or select a clean source checkout. Nothing was stashed/reset.")
    unknown = git(root, "ls-files", "--others", "--exclude-standard", "-z").stdout.split(b"\0")
    require(not any(unknown), "Untracked files present; commit relevant source or move unrelated files outside the selected checkout. Untracked source will not be transferred.")
    print("Committed source only; ignored files, caches, credentials and untracked files are omitted.", file=sys.stderr)


def sensitive_path(path):
    parts = PurePosixPath(path).parts
    return any(part.lower() in {".git", ".ssh", ".venv", "venv", "node_modules", "id_rsa", "id_ed25519", "credentials"}
               or part.lower().endswith((".pfx", ".p12", ".key", ".pem", ".jks", ".keystore"))
               or (part.lower().startswith(".env") and part.lower() not in {".env.example", ".env.sample", ".env.template"})
               for part in parts)


def check_source_history(root, commit):
    # Bundles include ancestry. Reject known credential/runtime paths in history,
    # not merely files in the selected tree. This is a guard, not a secret scanner.
    objects = git(root, "rev-list", "--objects", commit).stdout.decode("utf-8", "replace")
    for line in objects.splitlines():
        _, separator, path = line.partition(" ")
        require(not (separator and sensitive_path(path)),
                "Reachable Git history contains a prohibited credential/runtime path; no bundle transfer performed. Review history with the repository owner.")


def check_pinned_ignores(root, commit, manifest):
    # check-ignore must use pinned .gitignore files, not global excludes or the
    # remote editable checkout. An alternate work tree does not change any index.
    with tempfile.TemporaryDirectory(prefix="ignore-check-", dir=root) as scratch:
        shadow = Path(scratch)
        names = git(root, "ls-tree", "-r", "--name-only", "-z", commit).stdout.split(b"\0")
        for encoded in names:
            if not encoded:
                continue
            name = encoded.decode("utf-8")
            if PurePosixPath(name).name == ".gitignore":
                relative_path(name, "tracked ignore file")
                path = within(shadow, shadow / name)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(git(root, "show", f"{commit}:{name}").stdout)
        for key in ("stateDirectory", "worktreeDirectory"):
            proc = git(root, "-c", "core.excludesFile=/dev/null", f"--work-tree={shadow}",
                       "check-ignore", "--no-index", "--verbose", "--", manifest[key] + "/probe", check=False)
            fields = proc.stdout.split(b"\t", 1)[0].rsplit(b":", 2)
            require(proc.returncode == 0 and len(fields) == 3 and
                    fields[0].decode("utf-8").replace("\\", "/").split("/")[-1] == ".gitignore" and
                    not fields[2].startswith(b"!"), f"{key} must be ignored by pinned source .gitignore files")


def resolve_plan(root, args):
    commit_arg = args.commit or "HEAD"
    require(args.commit is None or re.fullmatch(r"[0-9a-fA-F]{40}", args.commit), "--commit requires an exact full 40-character Git SHA")
    commit = git(root, "rev-parse", "--verify", f"{commit_arg}^{{commit}}").stdout.decode().strip()
    require(re.fullmatch(r"[0-9a-f]{40}", commit), "Unsupported Git object format; expected SHA-1 repository")
    # Read capabilities from the exact source being transported, not mutable local config.
    manifest = load_manifest(git(root, "show", f"{commit}:eng/release-targets.json").stdout)
    require(args.target in manifest["targets"], f"Unsupported target: {args.target}")
    target = manifest["targets"][args.target]
    host = args.host or target["host"]
    require(HOSTS[host][1:] == (target["platform"], target["architecture"]), "Host override is incompatible with target platform/architecture")
    require(1 <= args.jobs <= MAX_JOBS, f"--jobs must be in 1..{MAX_JOBS}")
    require(1 <= args.timeout_seconds <= 86400, "--timeout-seconds must be in 1..86400")
    require(args.execution_context != "console" or 30 <= args.timeout_seconds <= 14400,
            "Console --timeout-seconds must be in 30..14400 (host bridge bound)")
    interpreter = args.remote_python or (CONSOLE_PYTHON.get(host) if args.execution_context == "console" else None)
    if interpreter:
        interpreter = validate_python_path(interpreter, target["platform"])
    require(args.mode == "plan" or args.execution_context != "console" or interpreter,
            "Console execution on this host requires an explicit --remote-python native interpreter path")
    mode = args.mode
    phases = [] if mode == "plan" else list(MODES[:MODES.index(mode) + 1])
    require(all(target["commands"].get(p) for p in phases), f"Target does not support mode {mode} and its prerequisite phases")
    source_branch = git(root, "symbolic-ref", "--quiet", "--short", "HEAD", check=False).stdout.decode().strip()
    if mode == "upload":
        policy = target["uploadPolicy"]
        require(policy in ("existing-gates", "draft-only"), "Upload is unsupported")
        require(source_branch == "main" or (policy == "draft-only" and source_branch == "feature/ci-test"),
                "Upload requires main, except explicitly draft-only feature/ci-test candidates")
        require(commit == git(root, "rev-parse", "HEAD").stdout.decode().strip(), "Upload requires current source branch HEAD")
    parameters, missing = resolve_parameters(target, args.parameter, phases if mode != "plan" else list(target["commands"]))
    require(mode == "plan" or not missing, "Missing --parameter values: " + ", ".join(missing))
    remote_root = args.remote_root or (f"D:/Projects/{manifest['project']}" if target["platform"] == "windows" else f"~/Projects/{manifest['project']}")
    validate_remote_root(remote_root, target["platform"])
    return {"schemaVersion": 1, "manifest": manifest, "commit": commit, "target": args.target,
            "host": host, "mode": mode, "phases": phases, "jobs": args.jobs,
            "remoteRoot": remote_root, "retrieveArtifacts": bool(args.output_dir),
            "commands": {p: target["commands"][p] for p in phases or target["commands"]},
            "parameters": parameters, "unresolvedParameters": missing,
            "sourceBranch": source_branch or None,
            "executionContext": args.execution_context, "remotePython": interpreter,
            "timeoutSeconds": args.timeout_seconds,
            "expectedHost": HOSTS[host][0], "sourcePolicy": "committed-bundle-only"}


def validate_remote_root(value, os_name):
    require(isinstance(value, str) and not re.search(r"[\x00-\x1f\\]", value), "Invalid remote root")
    require((os_name == "windows" and re.fullmatch(r"[A-Za-z]:/[^:]+", value)) or
            (os_name == "macos" and (value.startswith("~/") or value.startswith("/"))),
            "Remote root must be an absolute native path (forward slashes; ~/ allowed on macOS)")
    tail = value[3:] if os_name == "windows" else value[2:] if value.startswith("~/") else value[1:]
    relative_path(tail, "remote root")


def validate_python_path(value, os_name):
    require(isinstance(value, str), "Interpreter must be an absolute native path")
    value = value.replace("\\", "/") if os_name == "windows" else value
    require(re.fullmatch(r"[A-Za-z0-9 :/._()+-]+", value), "Unsafe interpreter path characters")
    if os_name == "windows":
        # Allow the drive colon only, never shell operators, variables or quotes.
        require(re.fullmatch(r"[A-Za-z]:/(?:[A-Za-z0-9 ._()+-]+/)*python[0-9.]*\.exe", value, re.I), "Expected absolute Windows Python executable")
    else:
        require(re.fullmatch(r"/(?:[A-Za-z0-9 ._()+-]+/)*python[0-9.]*", value), "Expected absolute macOS Python executable")
    relative_path(value[3:] if os_name == "windows" else value[1:], "interpreter")
    return value


def remote_command(plan, probed_python=None):
    native_os = HOSTS[plan["host"]][1]
    interpreter = plan["remotePython"]
    if plan["executionContext"] == "console":
        require(interpreter, "Console interpreter path is unresolved")
        interpreter = validate_python_path(interpreter, native_os)
        if native_os == "windows":
            # OpenSSH's verified cmd.exe default shell starts PS7 directly.
            # No PowerShell 5 wrapper/pipeline may decode the binary protocol.
            return subprocess.list2cmdline([
                "C:/Program Files/PowerShell/7/pwsh.exe", "-NoLogo", "-NoProfile", "-NonInteractive",
                "-File", "C:/ProgramData/BuildRelease/Invoke-NativeBuildJob.ps1", "-PythonExecutable", interpreter.replace("/", "\\"),
                "-TimeoutSeconds", str(plan["timeoutSeconds"])])
        return shlex.join(["sudo", "-n", CONSOLE_PYTHON["macbook-pro"],
                           "/usr/local/libexec/build-release/invoke_native_build_job.py", "--python-executable", interpreter,
                           "--timeout-seconds", str(plan["timeoutSeconds"])])
    if interpreter:
        interpreter = validate_python_path(interpreter, native_os)
        if native_os == "windows":
            return subprocess.list2cmdline([interpreter, "-c", LAUNCHER])
        return shlex.join([interpreter, "-c", LAUNCHER])
    require(probed_python in ("py -3", "python", "python3"), "Unverified Python probe result")
    return f'{probed_python} -c "{LAUNCHER}"'


def verify_host(host, actual=None):
    if actual is None:
        os_name = platform.system()
        hostname = platform.node()
        if os_name == "Darwin":
            hostname = run(["scutil", "--get", "LocalHostName"]).stdout.decode().strip()
        process_arch = platform.machine().lower()
        # Windows environment is native OS architecture; reject emulated Python too.
        native_arch = os.environ.get("PROCESSOR_ARCHITEW6432", os.environ.get("PROCESSOR_ARCHITECTURE", process_arch)).lower() if os_name == "Windows" else process_arch
        actual = {"hostname": hostname, "system": os_name, "processArchitecture": process_arch, "nativeArchitecture": native_arch}
    expected, expected_os, expected_arch = HOSTS[host]
    normal_arch = lambda a: {"amd64": "x64", "x86_64": "x64", "aarch64": "arm64"}.get(a.lower(), a.lower())
    require(actual["hostname"].lower() == expected.lower(), f"Host mismatch: expected {expected}, received {actual['hostname']}")
    require(actual["system"] == ("Windows" if expected_os == "windows" else "Darwin"), "Native OS mismatch")
    require(normal_arch(actual["processArchitecture"]) == expected_arch and normal_arch(actual["nativeArchitecture"]) == expected_arch,
            "Native/process architecture mismatch; emulation is not supported")
    return dict(actual, canonicalHostname=expected)


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


@contextlib.contextmanager
def lock(path):
    path = Path(path)
    try:
        descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError as exc:
        raise RequestError(f"Request lock exists: {path}. Inspect its owner; no stale lock is deleted automatically.") from exc
    try:
        with os.fdopen(descriptor, "w") as stream:
            json.dump({"pid": os.getpid(), "created": time.time()}, stream)
        yield
    finally:
        path.unlink()  # This request's exclusive lock only.


def ownership(path, identity, create=False):
    marker = Path(path) / ".request-release-owner.json"
    expected = dict(MARKER, **identity)
    if marker.exists():
        require(marker.is_file() and not marker.is_symlink(), f"Unsafe ownership marker: {marker}")
        require(json.loads(marker.read_text("utf-8")) == expected, f"Unrelated ownership marker: {path}")
    else:
        require(create, f"Unknown existing workspace/state: {path}; no adoption/reset performed")
        with marker.open("x", encoding="utf-8") as stream:
            json.dump(expected, stream, sort_keys=True)


def exclude_owner_marker(path):
    # Called only for newly created native roots or verified task-owned clones.
    # Never alter an existing primary repository's excludes.
    excludes = within(path, path / ".git" / "info" / "exclude")
    excludes.parent.mkdir(exist_ok=True)
    existing = excludes.read_text("utf-8") if excludes.exists() else ""
    if "/.request-release-owner.json" not in existing.splitlines():
        with excludes.open("a", encoding="utf-8") as stream:
            stream.write("\n/.request-release-owner.json\n")


def validate_origin(origin):
    require(isinstance(origin, str) and origin and not re.search(r"[\x00-\x20]", origin) and not re.search(r"://[^/]*@", origin),
            "A credential-free origin URL is required for project identity")


def validate_source_branch(branch):
    if branch is None:
        return
    require(isinstance(branch, str) and branch and branch != "HEAD" and not re.search(r"[\x00-\x20]", branch), "Invalid source branch")
    checked = run(["git", "check-ref-format", "--branch", branch], check=False)
    require(checked.returncode == 0 and checked.stdout.strip() == branch.encode("utf-8"), "Invalid named Git source branch")


def read_exact(stream, size, destination=None):
    remaining = size
    chunks = []
    while remaining:
        chunk = stream.read(min(1024 * 1024, remaining))
        require(bool(chunk), "Truncated transport frame")
        remaining -= len(chunk)
        if destination:
            destination.write(chunk)
        else:
            chunks.append(chunk)
    return b"".join(chunks)


def read_json_line(stream, limit=MAX_JSON):
    line = stream.readline(limit + 1)
    require(len(line) <= limit and line.endswith(b"\n"), "Invalid/oversized JSON transport frame")
    try:
        return json.loads(line)
    except (ValueError, UnicodeError) as exc:
        raise RequestError("Invalid JSON transport frame") from exc


def verify_bundle(root, bundle, commit, expected_hash):
    require(digest(bundle) == expected_hash, "Bundle SHA-256 mismatch")
    git(root, "bundle", "verify", str(bundle))
    heads = git(root, "bundle", "list-heads", str(bundle)).stdout.decode().splitlines()
    require(heads == [f"{commit} refs/heads/request-release-source"], "Bundle source SHA/ref mismatch")


def prepare_worktree(root, manifest, target, commit, bundle, origin=None, main_context=False):
    parent = within(root, root / manifest["worktreeDirectory"])
    parent.mkdir(parents=True, exist_ok=True)
    path = within(root, parent / ("release-" + target))
    identity = {"project": manifest["project"], "target": target, "root": str(root)}
    if path.exists():
        ownership(path, identity)
        require((path / ".git").is_dir() and not (path / ".git").is_symlink(), "Release workspace must be an independent owned clone")
        require(not git(path, "status", "--porcelain=v1", "--untracked-files=all").stdout.replace(b"?? .request-release-owner.json\n", b""),
                f"Dirty/unknown release worktree: {path}; nothing was reset or cleaned")
    else:
        path.mkdir()
        git(path, "init")
        ownership(path, identity, create=True)
    exclude_owner_marker(path)
    git(path, "fetch", "--no-tags", "--no-write-fetch-head", str(bundle), "refs/heads/request-release-source")
    require(not git(path, "ls-tree", "--name-only", commit, ".request-release-owner.json").stdout,
            "Pinned source must not track the workspace ownership marker")
    if origin:
        current_origin = git(path, "config", "--get", "remote.origin.url", check=False).stdout.decode().strip()
        require(not current_origin or current_origin == origin, "Owned clone has a different origin; preserved")
        if not current_origin:
            git(path, "remote", "add", "origin", origin)
    if main_context:
        require(origin, "Main context requires credential-free canonical origin")
        main_exists = git(path, "show-ref", "--verify", "--quiet", "refs/heads/main", check=False).returncode == 0
        if main_exists:
            require(git(path, "merge-base", "--is-ancestor", "refs/heads/main", commit, check=False).returncode == 0,
                    "Owned main cannot fast-forward to requested SHA; no reset performed")
            git(path, "checkout", "main")
            git(path, "merge", "--ff-only", commit)
        else:
            git(path, "checkout", "-b", "main", commit)
    else:
        git(path, "-c", "advice.detachedHead=false", "checkout", "--detach", commit)
    require(git(path, "rev-parse", "HEAD").stdout.decode().strip() == commit, "Worktree SHA mismatch")
    return path


def collect_artifacts(output, patterns):
    output = Path(output)
    require(not output.is_symlink() and output.resolve() == Path(os.path.abspath(output)), "Linked artifact output root forbidden")
    output = output.resolve()
    results = []
    seen = set()
    for pattern in patterns:
        for match in glob.iglob(str(output / pattern), recursive=True):
            path = within(output, Path(match))
            require(path.is_file(), f"Artifact must be an ordinary file: {path}")
            relative = path.relative_to(output).as_posix()
            relative_path(relative, "artifact")
            require(not sensitive_path(relative), "Sensitive/internal artifact path forbidden")
            require(path.stat().st_size <= MAX_ARTIFACT, "Artifact exceeds 16 GiB")
            if relative not in seen:
                require(len(results) < 10000, "Too many artifacts")
                results.append({"path": relative, "size": path.stat().st_size, "sha256": digest(path)})
                seen.add(relative)
    return sorted(results, key=lambda artifact: artifact["path"])


def execute_commands(worktree, output, request, project_root):
    results = []
    original_root = Path(project_root).resolve()
    require(Path(worktree).resolve().is_relative_to(original_root), "Execution clone is outside the validated native project root")
    temporary = within(worktree, output.parent / "tmp")
    temporary.mkdir(exist_ok=True)
    environment = dict(os.environ, TMPDIR=str(temporary), TEMP=str(temporary), TMP=str(temporary),
                       BUILD_RELEASE_PROJECT_ROOT=str(original_root))
    variables = {"output": str(output), "target": request["target"], "commit": request["commit"], "jobs": str(request["jobs"])}
    target = request["manifest"]["targets"][request["target"]]
    for phase in request["phases"]:
        for index, argv in enumerate(target["commands"][phase]):
            resolved = interpolate(argv, variables, request["parameters"])
            if resolved[0] in ("python", "python3", "python.exe", "python3.exe"):
                resolved[0] = sys.executable
            started = time.monotonic()
            log_path = output.parent / f"{phase}-{index}.log"
            # Drain continuously and retain bounded metadata; never echo logs over SSH.
            try:
                proc = subprocess.Popen(resolved, cwd=worktree, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, shell=False)
            except OSError as exc:
                raise RequestError(f"Missing/unusable prerequisite executable: {resolved[0]} ({type(exc).__name__})") from exc
            output_hash = hashlib.sha256()
            output_bytes = 0
            with proc.stdout:
                for chunk in iter(lambda: proc.stdout.read(65536), b""):
                    output_hash.update(chunk)
                    output_bytes += len(chunk)
            code = proc.wait()
            with log_path.open("xb") as saved:
                # Adapters may print credentials. Retain diagnostics metadata only;
                # secret-safe detailed diagnostics belong to the canonical adapter.
                saved.write(json_bytes({"outputBytes": output_bytes, "outputSha256": output_hash.hexdigest(),
                                        "exitCode": code, "note": "Raw stdout/stderr omitted to avoid storing credentials"}) + b"\n")
            results.append({"mode": phase, "argv": resolved, "elapsedSeconds": round(time.monotonic() - started, 3), "exitCode": code, "log": str(log_path)})
            if code:
                return results, code
    return results, 0


def remote_main():
    wire = sys.stdin.buffer
    request = read_json_line(wire)
    require(isinstance(request, dict), "Invalid request")
    require(sys.version_info >= (3, 10), "Python >=3.10 is required on the execution host")
    require(request["executionContext"] in ("current", "console"), "Invalid execution context")
    validate_origin(request["origin"])
    validate_source_branch(request["sourceBranch"])
    manifest = load_manifest(json_bytes(request["manifest"]))
    require(request["host"] in HOSTS and request["target"] in manifest["targets"], "Invalid request target")
    target = manifest["targets"][request["target"]]
    require(HOSTS[request["host"]][1:] == (target["platform"], target["architecture"]), "Request target/host mismatch")
    require(request["mode"] in MODES and request["phases"] == list(MODES[:MODES.index(request["mode"]) + 1]), "Invalid phase sequence")
    require(all(target["commands"].get(p) for p in request["phases"]), "Unsupported mode")
    if request["mode"] == "upload":
        require(target["uploadPolicy"] in ("existing-gates", "draft-only"), "Unsupported upload")
        require(request["sourceBranch"] == "main" or (target["uploadPolicy"] == "draft-only" and request["sourceBranch"] == "feature/ci-test"),
                "Invalid upload source branch")
    require(type(request["jobs"]) is int and 1 <= request["jobs"] <= MAX_JOBS, "Invalid jobs")
    values, missing = resolve_parameters(target, [f"{k}={v}" for k, v in request["parameters"].items()], request["phases"])
    require(not missing and values == request["parameters"], "Invalid/unresolved parameters")
    require(re.fullmatch(r"[0-9a-f]{40}", request["commit"]), "Invalid commit")
    require(isinstance(request["bundleSize"], int) and 0 < request["bundleSize"] <= 8 * 1024**3, "Bundle size must be in 1..8 GiB")
    actual = verify_host(request["host"])
    validate_remote_root(request["remoteRoot"], target["platform"])
    root = Path(request["remoteRoot"]).expanduser()
    require(not root.is_symlink() and root.resolve() == Path(os.path.abspath(root)), "Linked remote project root forbidden")
    root = root.resolve()
    created = not root.exists()
    if created:
        root.mkdir(parents=True)
        git(root, "init")
        ownership(root, {"project": manifest["project"]}, create=True)
    else:
        require((root / ".git").exists() and git(root, "rev-parse", "--show-toplevel").stdout.decode().strip().replace("\\", "/").lower() == root.as_posix().lower(),
                "Existing remote root is not the exact native Git project; no adoption performed")
        require(git(root, "config", "--get", "remote.origin.url", check=False).stdout.strip() == request["origin"].encode() or
                (root / ".request-release-owner.json").exists(), "Existing project origin differs; refusing unrelated directory")
        if (root / ".request-release-owner.json").exists():
            ownership(root, {"project": manifest["project"]})
        ensure_ignored(root, manifest)
    state = within(root, root / manifest["stateDirectory"])
    new_state = not state.exists()
    if new_state:
        state.mkdir(parents=True)
    ownership(state, {"project": manifest["project"]}, create=new_state)
    lane = within(root, state / (request["target"] + "-" + target["platform"] + "-" + target["architecture"]))
    lane.mkdir(exist_ok=True)
    with lock(lane / "request.lock"):
        # Retain at most 20 request directories; fail rather than delete old evidence.
        require(len(list(lane.glob("run-*"))) < 20, f"Request metadata limit reached: {lane}; archive reviewed evidence explicitly")
        run_dir = lane / ("run-" + uuid.uuid4().hex)
        run_dir.mkdir()
        bundle = run_dir / "source.bundle"
        try:
            with bundle.open("xb") as stream:
                read_exact(wire, request["bundleSize"], stream)
            verify_bundle(root, bundle, request["commit"], request["bundleSha256"])
            # Validate capabilities against transported Git source, never just wire metadata.
            git(root, "fetch", "--no-tags", "--no-write-fetch-head", str(bundle), "refs/heads/request-release-source")
            pinned = load_manifest(git(root, "show", f"{request['commit']}:eng/release-targets.json").stdout)
            require(pinned == manifest, "Manifest differs from pinned source")
            check_pinned_ignores(root, request["commit"], manifest)
            if created:
                require(not git(root, "ls-tree", "--name-only", request["commit"], ".request-release-owner.json").stdout,
                        "Pinned source must not track the workspace ownership marker")
                git(root, "remote", "add", "origin", request["origin"])
                exclude_owner_marker(root)
                if request["sourceBranch"]:
                    git(root, "checkout", "-b", request["sourceBranch"], request["commit"])
                else:
                    git(root, "checkout", "--detach", request["commit"])
            # Check ignored state against pinned source without touching primary index.
            worktree = prepare_worktree(root, manifest, request["target"], request["commit"], bundle, request["origin"],
                                        request["sourceBranch"] == "main")
            ensure_ignored(worktree, manifest)
            clone_state = within(worktree, worktree / manifest["stateDirectory"])
            new_clone_state = not clone_state.exists()
            clone_state.mkdir(parents=True, exist_ok=True)
            ownership(clone_state, {"project": manifest["project"], "target": request["target"]}, create=new_clone_state)
            clone_run = within(worktree, clone_state / run_dir.name)
            clone_run.mkdir()
            output = clone_run / "output"
            output.mkdir()
            started = time.monotonic()
            commands, code = execute_commands(worktree, output, request, root)
            within(worktree, output)
            artifacts = collect_artifacts(output, target["artifacts"]) if code == 0 else []
            require(code != 0 or request["mode"] == "validate" or artifacts,
                    "Package/upload succeeded but produced no allowlisted artifacts in the fresh output directory")
            result = {"schemaVersion": 1, "host": actual, "commit": request["commit"], "target": request["target"],
                      "mode": request["mode"], "commands": commands, "elapsedSeconds": round(time.monotonic() - started, 3),
                      "exitCode": code, "artifacts": artifacts, "output": str(output),
                      "executionContext": request["executionContext"], "pythonExecutable": sys.executable}
            (run_dir / "result.json").write_bytes(json_bytes(result) + b"\n")
            sys.stdout.buffer.write(json_bytes(result) + b"\n")
            sys.stdout.buffer.flush()
            if request["retrieveArtifacts"]:
                for artifact in artifacts:
                    path = within(output, output / artifact["path"])
                    require(path.stat().st_size == artifact["size"] and digest(path) == artifact["sha256"], "Artifact changed before retrieval")
                    with path.open("rb") as stream:
                        shutil.copyfileobj(stream, sys.stdout.buffer)
            return code
        finally:
            # Proven request-owned transport only; keep logs/results/output and warm workspace.
            if bundle.exists():
                bundle.unlink()


def create_bundle(root, commit, path):
    # A temporary bare repo creates the named bundle ref without mutating source refs/index.
    check_source_history(root, commit)
    bare = path.parent / "transport.git"
    run(["git", "init", "--bare", str(bare)])
    git(bare, "-c", "protocol.file.allow=always", "fetch", "--no-tags", "--no-write-fetch-head", str(root), commit)
    git(bare, "update-ref", "refs/heads/request-release-source", commit)
    git(bare, "bundle", "create", str(path), "refs/heads/request-release-source")
    verify_bundle(bare, path, commit, digest(path))


def ssh_args(host, config=None):
    result = ["ssh"]
    if config:
        require(Path(config).is_file(), "--ssh-config must name an existing config file")
        result += ["-F", str(Path(config).resolve())]
    return result + ["-T", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes",
                     "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", host]


def probe_python(ssh, host):
    candidates = ("py -3", "python", "python3") if HOSTS[host][1] == "windows" else ("python3",)
    code = "import sys; print('REQUEST_RELEASE_PYTHON',sys.version_info.major,sys.version_info.minor)"
    findings = []
    for candidate in candidates:
        try:
            proc = subprocess.run(ssh + [f'{candidate} -c "{code}"'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=25)
        except subprocess.TimeoutExpired:
            findings.append(candidate + ": probe timeout")
            continue
        if proc.returncode == 255:
            raise RequestError(f"{HOSTS[host][0]}: strict SSH transport failed (255); Python prerequisites were not verified")
        version = re.fullmatch(rb"REQUEST_RELEASE_PYTHON ([0-9]+) ([0-9]+)\r?\n", proc.stdout)
        if proc.returncode == 0 and version:
            major, minor = map(int, version.groups())
            if (major, minor) >= (3, 10):
                return candidate
            findings.append(f"{candidate}: Python {major}.{minor}")
        else:
            findings.append(f"{candidate}: unavailable/probe exit {proc.returncode}")
    raise RequestError(f"{HOSTS[host][0]}: native Python >=3.10 prerequisite missing ({'; '.join(findings)})")


def receive_artifact(stream, destination, artifact):
    relative_path(artifact["path"], "received artifact")
    require(not sensitive_path(artifact["path"]), "Sensitive/internal received artifact path forbidden")
    require(isinstance(artifact["size"], int) and 0 <= artifact["size"] <= MAX_ARTIFACT, "Invalid artifact size")
    require(isinstance(artifact["sha256"], str) and re.fullmatch(r"[0-9a-f]{64}", artifact["sha256"]), "Invalid artifact SHA-256")
    path = within(destination, Path(destination) / artifact["path"])
    path.parent.mkdir(parents=True, exist_ok=True)
    # Spool verified bytes in destination filesystem; exclusive link installs without overwriting.
    descriptor, name = tempfile.mkstemp(prefix=".request-release-", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as output:
            read_exact(stream, artifact["size"], output)
        require(digest(temporary) == artifact["sha256"], "Retrieved artifact SHA-256 mismatch")
        if path.exists():
            require(path.is_file() and path.stat().st_size == artifact["size"] and digest(path) == artifact["sha256"],
                    f"Existing artifact conflicts; preserved: {path}")
        else:
            try:
                os.link(temporary, path)
            except FileExistsError as exc:
                raise RequestError(f"Concurrent artifact destination conflict: {path}") from exc
    finally:
        temporary.unlink()
    return str(path)


def artifact_allowed(path, patterns, casefold=False):
    def matches(parts, pattern):
        if not pattern:
            return not parts
        if pattern[0] == "**":
            return matches(parts, pattern[1:]) or (bool(parts) and not parts[0].startswith(".") and matches(parts[1:], pattern))
        return bool(parts) and (not parts[0].startswith(".") or pattern[0].startswith(".")) and fnmatch.fnmatchcase(parts[0], pattern[0]) and matches(parts[1:], pattern[1:])
    path = path.lower() if casefold else path
    return any(matches(path.split("/"), (p.lower() if casefold else p).split("/")) for p in patterns)


def validate_result(result, plan):
    require(isinstance(result, dict) and "exitCode" in result and "artifacts" in result, "Remote rejected request or returned invalid result")
    require(result["commit"] == plan["commit"] and result["target"] == plan["target"] and result["mode"] == plan["mode"], "Remote result identity mismatch")
    require(result["executionContext"] == plan["executionContext"], "Remote execution context mismatch")
    verify_host(plan["host"], result["host"])
    require(type(result["exitCode"]) is int, "Invalid remote exit code")
    require(isinstance(result["artifacts"], list) and len(result["artifacts"]) <= 10000, "Invalid artifact manifest")
    target = plan["manifest"]["targets"][plan["target"]]
    seen = set()
    for artifact in result["artifacts"]:
        require(isinstance(artifact, dict), "Invalid artifact record")
        path = relative_path(artifact["path"], "received artifact")
        require(path not in seen and not sensitive_path(path) and artifact_allowed(path, target["artifacts"], target["platform"] == "windows"),
                "Duplicate or non-allowlisted artifact in result")
        require(type(artifact["size"]) is int and 0 <= artifact["size"] <= MAX_ARTIFACT and
                isinstance(artifact["sha256"], str) and re.fullmatch(r"[0-9a-f]{64}", artifact["sha256"]), "Invalid artifact metadata")
        seen.add(path)
    require(result["exitCode"] != 0 or plan["mode"] == "validate" or result["artifacts"], "Successful package/upload returned no artifacts")


def start_deadline(proc, seconds):
    expired = threading.Event()
    def expire():
        if proc.poll() is None:
            expired.set()
            with contextlib.suppress(OSError):
                proc.kill()
    timer = threading.Timer(seconds, expire)
    timer.daemon = True
    timer.start()
    return timer, expired


def dispatch(root, args, plan):
    clean_source(root)
    require(git(root, "rev-parse", "HEAD").stdout.decode().strip() == plan["commit"],
            "Non-plan requests require selected checkout HEAD to equal --commit; select a clean checkout at that commit")
    actual_branch = git(root, "symbolic-ref", "--quiet", "--short", "HEAD", check=False).stdout.decode().strip() or None
    require(actual_branch == plan["sourceBranch"], "Source branch changed after planning; submit a fresh request")
    origin = git(root, "config", "--get", "remote.origin.url", check=False).stdout.decode().strip()
    validate_origin(origin)
    # Copy only the credential-free identity; the owned clone's canonical uploader
    # remains responsible for authenticated upstream fetch and branch validation.
    state = within(root, root / plan["manifest"]["stateDirectory"])
    ensure_ignored(root, plan["manifest"])
    check_pinned_ignores(root, plan["commit"], plan["manifest"])
    state.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="transport-", dir=state) as scratch:
        within(state, Path(scratch))
        bundle = Path(scratch) / "source.bundle"
        create_bundle(root, plan["commit"], bundle)
        require(bundle.stat().st_size <= 8 * 1024**3, "Source bundle exceeds 8 GiB")
        request = dict(plan, bundleSize=bundle.stat().st_size, bundleSha256=digest(bundle), origin=origin)
        ssh = ssh_args(plan["host"], args.ssh_config)
        python = probe_python(ssh, plan["host"]) if plan["executionContext"] == "current" and not plan["remotePython"] else None
        command = remote_command(plan, python)
        source = Path(__file__).read_bytes()
        with tempfile.TemporaryFile(dir=scratch) as errors:
            proc = subprocess.Popen(ssh + [command], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
            timer, expired = start_deadline(proc, plan["timeoutSeconds"])
            try:
                try:
                    proc.stdin.write(str(len(source)).encode() + b"\n" + source + json_bytes(request) + b"\n")
                    with bundle.open("rb") as stream:
                        shutil.copyfileobj(stream, proc.stdin)
                    proc.stdin.close()
                except BrokenPipeError:
                    # Host preflight can refuse before consuming the source frame.
                    with contextlib.suppress(BrokenPipeError):
                        proc.stdin.close()
                result = read_json_line(proc.stdout, MAX_RESULT)
                if isinstance(result, dict) and "error" in result:
                    raise RequestError("Remote refusal: " + str(result["error"]))
                validate_result(result, plan)
                if args.output_dir:
                    destination = Path(args.output_dir).resolve()
                    destination.mkdir(parents=True, exist_ok=True)
                    for artifact in result["artifacts"]:
                        artifact["localPath"] = receive_artifact(proc.stdout, destination, artifact)
                require(proc.stdout.read(1) == b"", "Unexpected trailing artifact data")
                ssh_code = proc.wait(timeout=30)
                require(ssh_code == (result["exitCode"] & 255), f"SSH/remote exit status mismatch: {ssh_code}")
                return result
            except BaseException:
                with contextlib.suppress(OSError):
                    proc.kill()
                proc.wait()
                if expired.is_set():
                    raise RequestError(f"Request timed out after {plan['timeoutSeconds']} seconds; only the owned SSH client was killed. Remote job may still need cancellation.")
                raise
            finally:
                timer.cancel()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True)
    parser.add_argument("--mode", choices=("plan",) + MODES, default="plan")
    parser.add_argument("--host", choices=HOSTS)
    parser.add_argument("--commit")
    parser.add_argument("--jobs", type=int, default=2)
    parser.add_argument("--output-dir")
    parser.add_argument("--ssh-config")
    parser.add_argument("--execution-context", choices=("current", "console"), default="current")
    parser.add_argument("--remote-python", help="Exact absolute native interpreter path; console PC-MARC requires this explicitly")
    parser.add_argument("--timeout-seconds", type=int, default=3600, help="SSH execution deadline, 1..86400 seconds; remote job may still need cancellation")
    parser.add_argument("--parameter", action="append", default=[], metavar="NAME=VALUE", help="Declared nonsecret adapter input (repeatable)")
    parser.add_argument("--remote-root", help="Explicit existing native project root; default D:/Projects/<project> or ~/Projects/<project>")
    args = parser.parse_args(argv)
    try:
        root = Path(git(Path.cwd(), "rev-parse", "--show-toplevel").stdout.decode().strip()).resolve()
        plan = resolve_plan(root, args)
        result = plan if args.mode == "plan" else dispatch(root, args, plan)
        print(json.dumps(result, sort_keys=True, indent=2))
        return result.get("exitCode", 0)
    except (RequestError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as exc:
        print(json.dumps({"exitCode": 2, "error": str(exc)}), file=sys.stderr)
        return 2


if __name__ == "__remote__":
    try:
        sys.exit(remote_main())
    except (RequestError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as exc:
        sys.stdout.buffer.write(json_bytes({"exitCode": 2, "error": str(exc)}) + b"\n")
        sys.stdout.buffer.flush()
        sys.exit(2)
elif __name__ == "__main__":
    sys.exit(main())
