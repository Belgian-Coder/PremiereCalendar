"""Stage the five canonical NAS infrastructure documents for a release; the canonical files are only read.

Each document receives (or replaces) one managed block delimited by
<!-- premierecalendar-<version>-managed:start/end -->; older release blocks, surrounding text, the BOM and the line
endings are preserved. The blocks are rendered from a values JSON (see values.example.json) that the owner fills from
the real release evidence; pending, failed or unbound values keep readyToApply=false, so nothing claims an
acceptance the evidence does not support.

Output goes to a NEW directory under --stage-root (default: next to the values file); Apply-NasDocs.ps1 publishes
a staged manifest after verifying every hash. Nothing is written to the NAS here.
"""
import argparse
import datetime as dt
import hashlib
import json
import re
from pathlib import Path

DEFAULT_CANONICAL = r"\\NAS\Data\Backups\Infrastructure\NAS"
TARGETS = ("services/premierecalendar/README.md", "README.md", "08-applications-and-services.md",
           "04-change-management.md", "CHANGELOG.md")
GATES = {
    "tests": "Local tests on the release commit",
    "review": "Adversarial source review",
    "security": "Local vulnerability, configuration and secret scan",
    "sbom": "Local SBOM",
    "exactImage": "Exact-image runtime and hardening",
    "backupRestore": "Fresh backup and isolated restore",
    "backupCompatibility": "Exact-image preservation of the restored backup",
    "persistedSetting": "Read-only persisted-setting verification (optional)",
}
OPTIONAL = ("persistedSetting",)
SOURCE_BOUND = ("tests", "review", "exactImage", "backupCompatibility")


def pending(value):
    return value is None or not str(value).strip() or "__PENDING" in str(value) or str(value).lower() == "pending"


def text(value):
    return "pending" if pending(value) else str(value).replace("|", r"\|").replace("\r", "").replace("\n", " ")


def digest(value, prefix=""):
    return isinstance(value, str) and re.fullmatch(re.escape(prefix) + r"[a-fA-F0-9]{64}", value) is not None


def gate_pass(gate):
    return gate.get("status") == "pass" and not pending(gate.get("summary")) and not pending(gate.get("evidence"))


def candidate_ready(v):
    source = v.get("sourceCommit", "")
    if not re.fullmatch(r"[a-fA-F0-9]{40}", source) or not digest(v.get("imageConfigurationDigest"), "sha256:"):
        return False
    if not digest(v.get("archiveSha256")) or not isinstance(v.get("archiveBytes"), int) or v["archiveBytes"] <= 0:
        return False
    if any(pending(v.get(k)) for k in ("releaseDate", "publicEvidenceLocation", "releaseBehavior", "archiveFileName")):
        return False
    counts = v["testCounts"]
    if not all(isinstance(counts.get(k), int) and counts[k] >= 0 for k in ("unitPassed", "componentPassed", "integrationPassed", "skipped")):
        return False
    if sum(counts[k] for k in ("unitPassed", "componentPassed", "integrationPassed")) == 0:
        return False
    for key in GATES:
        gate = v["gates"].get(key, {"status": "not_applicable"})
        if key in OPTIONAL and gate.get("status") == "not_applicable":
            continue
        if not gate_pass(gate) or (key in SOURCE_BOUND and gate.get("sourceCommit") != source):
            return False
        if key in ("exactImage", "backupCompatibility") and gate.get("imageConfigurationDigest") != v["imageConfigurationDigest"]:
            return False
    b = v["backup"]
    return (not pending(b.get("dumpFileName")) and Path(b["dumpFileName"]).name == b["dumpFileName"] and digest(b.get("dumpSha256"))
            and isinstance(b.get("dumpBytes"), int) and b["dumpBytes"] > 0 and not pending(b.get("publicReference")))


def installed_ready(v):
    i = v["installed"]
    return (candidate_ready(v) and i.get("stackRevision") == v["stackRevision"] and isinstance(i.get("schemaVersion"), int)
            and i.get("sourceCommit") == v["sourceCommit"] and i.get("imageConfigurationDigest") == v["imageConfigurationDigest"]
            and digest(i.get("appContainerId")) and not pending(i.get("activationUtc")) and i.get("composePath") == v["composePath"]
            and all(gate_pass(i[key]) for key in ("runtime", "preservation", "browser")))


def row(label, gate):
    if gate.get("status") not in ("pending", "pass", "fail", "not_observed", "not_performed", "not_applicable"):
        raise ValueError("Unsupported gate status: " + label)
    return f"| {label} | {text(gate.get('status'))} | {text(gate.get('summary'))} | {text(gate.get('evidence'))} |"


def upsert(original, block, start, end):
    matches = list(re.finditer(re.escape(start) + r".*?" + re.escape(end), original, re.S))
    if len(matches) > 1 or original.count(start) != len(matches) or original.count(end) != len(matches):
        raise ValueError("Ambiguous or incomplete managed block; preserve and review manually")
    newline = "\r\n" if "\r\n" in original else "\n"
    managed = (start + "\n" + block.strip() + "\n" + end).replace("\n", newline)
    if matches:
        m = matches[0]
        return original[:m.start()] + managed + original[m.end():]
    if "\n" not in original:
        raise ValueError("Canonical document has no title line ending")
    offset = original.index("\n") + 1
    return original[:offset] + newline + managed + newline + original[offset:]


def render(v):
    version = v["version"]
    if v.get("status") not in ("candidate", "installed"):
        raise ValueError("status must be candidate or installed")
    installed = v["status"] == "installed"
    ready = installed_ready(v) if installed else candidate_ready(v)
    title = "installed" if installed and ready else "installed record pending verification" if installed else "candidate; not activated"
    prev, rev = v["previousVersion"], v["stackRevision"]
    baseline = (f"Previous verified deployment: v{prev}, source `{v['previousSourceCommit']}`, image `{v['previousImageDigest']}`, "
                f"stack {v['stackId']} revision {v['previousStackRevision']}, installed {text(v.get('previousInstalledUtc'))}. Its evidence is in NAS `releases/{v['previousBundleName']}`.")
    deployment = (f"Installed at {text(v['installed']['activationUtc'])}; stack {v['stackId']} revision {rev}, schema {text(v['installed'].get('schemaVersion'))}. {baseline}"
                  if installed and ready else baseline + f" Revision {rev} is planned; this draft does not establish installation.")
    behavior = text(v.get("releaseBehavior"))
    deviation = ("Review note: " + text(v["reviewNote"])) if not pending(v.get("reviewNote")) else ""
    recorded = f"Documentation recorded {text(v.get('documentationRecordedDate'))} from the closed release evidence; no NAS runtime change accompanies this record."
    identity = (f"Source `{text(v.get('sourceCommit'))}`; image `{text(v.get('imageConfigurationDigest'))}`. "
                f"Archive `releases/{v['bundleName']}/{text(v.get('archiveFileName'))}`: {text(v.get('archiveBytes'))} bytes; SHA-256 `{text(v.get('archiveSha256'))}`.")
    policy = ("Validation, tests, builds, CI, vulnerability checks, SBOM generation and packaging run locally; never use GitHub Actions, including self-hosted runners or manual dispatches. Upload accepted artifacts from the local builder. "
              f"Activation changes only the app image pin, from revision {v['previousStackRevision']} to revision {rev}. Keep pulling and pruning disabled. "
              "Preserve PostgreSQL, backups, application data, networks, hardening, saved filters, settings and credentials. "
              "Keep raw database/configuration payloads, credentials and restricted backup paths out of public documentation.")
    evidence = f"Sanitized release evidence: {text(v.get('publicEvidenceLocation'))}. Pending, failed or not-performed rows are not acceptance."
    b = v["backup"]
    backup_text = (f"Fresh dump `{text(b.get('dumpFileName'))}`: {text(b.get('dumpBytes'))} bytes; SHA-256 `{text(b.get('dumpSha256'))}`. "
                   f"Backup evidence: {text(b.get('publicReference'))}. Backup, isolated restore and exact-image compatibility are separate checks. Production is not restored by these checks.")
    c = v["testCounts"]
    count_text = "; ".join(f"{label}: {text(c.get(key))}" for key, label in (("unitPassed", "Unit passed"), ("componentPassed", "component passed"), ("integrationPassed", "integration passed"), ("skipped", "skipped"))) + ". Use the final reconciled roster; do not add repeated runs."
    gate_rows = [(label, v["gates"].get(key, {"status": "not_applicable", "summary": "not applicable", "evidence": "n/a"})) for key, label in GATES.items()]
    gate_table = "| Gate | Status | Observed result | Evidence |\n| --- | --- | --- | --- |\n" + "\n".join(row(label, gate) for label, gate in gate_rows)
    inst = v["installed"]
    installed_table = ("| Installed check | Status | Observed result | Evidence |\n| --- | --- | --- | --- |\n" + "\n".join(row(label, inst[key]) for key, label in (
        ("runtime", "Identity, health, assets, hardening and logging"), ("preservation", "Database/backup ids, start times and saved data"),
        ("browser", "Calendars, filter links, posters, image endpoint and Settings"), ("nightly", "Actual nightly execution boundary"))))
    recovery = (f"App-only rollback uses the retained v{prev} image `{v['previousImageDigest']}` with the data retained and pulling/pruning disabled" +
                (" (the schema is unchanged by this release)." if v.get("schemaChanged") is False else "; a schema change needs its own approved plan (a previous image may not read the migrated data)." if v.get("schemaChanged") else ".") +
                " Verify source/image/health after replacement. Do not recreate PostgreSQL or backup services or restore the database automatically; database recovery needs its own approved operation that preserves newer user data.")
    heading = f"## PremiereCalendar v{version} \u2014 {title} ({text(v.get('releaseDate'))})"
    short = f"{heading}\n\n{deployment}\n\n{behavior}\n\n{identity}\n\n{deviation}\n\n{evidence} See `services/premierecalendar/README.md` for gates and recovery. Earlier release sections remain historical records."
    service = (f"{heading}\n\n{deployment}\n\n{behavior}\n\n{identity}\n\n{count_text}\n\n{gate_table}\n\n{deviation}\n\n{backup_text}\n\n{policy}\n\n### Installed acceptance\n\n{installed_table}\n\n"
               f"App `{text(inst.get('appContainerId'))}`; active Compose `{text(inst.get('composePath'))}`. Browser acceptance is required for installed publication. Historical tests/jobs do not prove a new nightly boundary; retain pending/not_observed honestly.\n\n"
               f"### Activation and recovery\n\nVerify source-bound tests/reviews, scan/SBOM, exact archive identity, fresh restore and restored-backup preservation before app activation. Afterwards verify revision {rev} and direct/private health/assets, diagnostic/logging health, hardening and exact unchanged database/backup identities and start times.\n\n{recovery}\n\n{evidence}\n\n{recorded}")
    log = short + f"\n\n{count_text}\n\n{backup_text}\n\n{policy}\n\nInstalled runtime: {text(inst['runtime']['status'])}; preservation: {text(inst['preservation']['status'])}; browser: {text(inst['browser']['status'])}; nightly: {text(inst['nightly']['status'])}.\n\n{recorded}"
    blocks = (service, short, short + "\n\n" + policy, short + "\n\n" + policy + "\n\n" + recovery, log)
    return ready, dict(zip(TARGETS, blocks))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--values", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="new directory under --stage-root")
    parser.add_argument("--stage-root", type=Path, help="default: the folder of the values file")
    parser.add_argument("--canonical-root", type=Path, default=Path(DEFAULT_CANONICAL))
    args = parser.parse_args()
    stage = (args.stage_root or args.values.resolve().parent).resolve()
    output = args.output.resolve()
    output.relative_to(stage)
    if output == stage or output.exists():
        raise ValueError("Choose a new output directory under the staging root")
    values_bytes = args.values.read_bytes()
    values = json.loads(values_bytes.decode("utf-8-sig"))
    version = values["version"]
    start, end = f"<!-- premierecalendar-{version}-managed:start -->", f"<!-- premierecalendar-{version}-managed:end -->"
    ready, blocks = render(values)
    documents, staged = [], []
    for relative in TARGETS:
        canonical = args.canonical_root / relative
        before = canonical.read_bytes()
        bom = b"\xef\xbb\xbf" if before.startswith(b"\xef\xbb\xbf") else b""
        after = bom + upsert(before.decode("utf-8-sig"), blocks[relative], start, end).encode("utf-8")
        target = output / relative
        staged.append((target, after))
        documents.append({"relativePath": relative, "canonicalPath": str(canonical), "renderedPath": str(target),
                          "beforeSha256": hashlib.sha256(before).hexdigest(), "afterSha256": hashlib.sha256(after).hexdigest()})
    output.mkdir(parents=True)
    for target, after in staged:
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(after)
    manifest = {"version": version, "phase": values["status"], "readyToApply": ready, "canonicalRoot": str(args.canonical_root),
                "generatedUtc": dt.datetime.now(dt.timezone.utc).isoformat(), "valuesSha256": hashlib.sha256(values_bytes).hexdigest(),
                "documents": documents, "policy": "Canonical files were only read while rendering; applying requires unchanged preimage hashes and complete gates."}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"phase": values["status"], "readyToApply": ready, "documents": len(documents), "manifest": str(output / "manifest.json")}))


if __name__ == "__main__":
    main()
