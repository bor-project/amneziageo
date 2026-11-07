import argparse
import json
import os
import re
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path


BRANCHES = {"stable": "master", "beta": "beta", "test": "test"}
NUMBER = r"(?:0|[1-9][0-9]*)"
TAG = re.compile(rf"v({NUMBER}\.{NUMBER}\.{NUMBER}\.{NUMBER})(?:-(beta|test)(?:\.([1-9][0-9]*))?)?")
SHA = re.compile(r"[0-9a-f]{40}|[0-9a-f]{64}")


class Refusal(ValueError):
    pass


@dataclass(frozen=True)
class ReleasePlan:
    version: str
    channel: str
    branch: str
    release_tag: str
    source_commit: str
    tag_exists: bool


def parse_tag(tag):
    match = TAG.fullmatch(tag)
    if match is None:
        raise Refusal("Use vX.Y.Z.W, vX.Y.Z.W-beta.N or vX.Y.Z.W-test.N")
    version, channel, _ = match.groups()
    fields = [int(value) for value in version.split(".")]
    if any(value > 65535 for value in fields):
        raise Refusal("Version fields must not exceed 65535")
    channel = channel or "stable"
    if channel == "stable" and fields[3] != 0:
        raise Refusal("Stable versions require a zero fourth field")
    return version, channel


def remote_tag(refs, tag):
    name = "refs/tags/" + tag
    return refs.get(name + "^{}", refs.get(name))


def validate_tag(refs, tag, commit, required):
    parse_tag(tag)
    if SHA.fullmatch(commit) is None:
        raise Refusal("Invalid source commit")
    actual = remote_tag(refs, tag)
    if actual is None:
        if required:
            raise Refusal("The release tag no longer exists")
        return False
    if actual != commit:
        raise Refusal("The release tag points to another commit")
    return True


def validate_version(refs, tag):
    version, _ = parse_tag(tag)
    for name in refs:
        if not name.startswith("refs/tags/") or name.endswith("^{}"):
            continue
        other = name[len("refs/tags/"):]
        if other == tag or TAG.fullmatch(other) is None:
            continue
        if parse_tag(other)[0] == version:
            raise Refusal("Increment the version: this numeric version already has another release tag")


def make_plan(event, ref, event_commit, checked_out, manual_tag, refs):
    if event == "push":
        if not ref.startswith("refs/tags/"):
            raise Refusal("Branch pushes do not release packages")
        tag = ref[len("refs/tags/"):]
    elif event == "workflow_dispatch":
        if not ref.startswith("refs/heads/"):
            raise Refusal("Start a manual release from master, beta or test")
        tag = manual_tag
    else:
        raise Refusal("Only tag pushes and manual releases are allowed")
    version, channel = parse_tag(tag)
    branch = BRANCHES[channel]
    if event == "workflow_dispatch" and ref != "refs/heads/" + branch:
        raise Refusal("The selected branch does not match the release channel")
    if SHA.fullmatch(event_commit) is None or event_commit != checked_out:
        raise Refusal("The checkout does not match the triggering commit")
    if refs.get("refs/heads/" + branch) != checked_out:
        raise Refusal("The release must use the current head of " + branch)
    validate_version(refs, tag)
    exists = validate_tag(refs, tag, checked_out, event == "push")
    return ReleasePlan(version, channel, branch, tag, checked_out, exists)


def read_refs(text):
    refs = {}
    for line in text.splitlines():
        columns = line.split()
        if len(columns) != 2 or SHA.fullmatch(columns[0]) is None:
            raise Refusal("Invalid remote reference response")
        commit, name = columns
        if name in refs and refs[name] != commit:
            raise Refusal("Conflicting remote references")
        refs[name] = commit
    return refs


def git(repo, *args):
    result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    if result.returncode:
        raise Refusal("Git could not read the release source")
    return result.stdout.strip()


def refs_for(repo, tag, branch=None):
    names = ["refs/tags/" + tag, "refs/tags/" + tag + "^{}"]
    if branch:
        names.append("refs/heads/" + branch)
        names.append("refs/tags/v" + parse_tag(tag)[0] + "*")
    return read_refs(git(repo, "ls-remote", "origin", *names))


def emit(plan, output):
    values = asdict(plan)
    print(json.dumps(values, sort_keys=True))
    if output:
        with Path(output).open("a", encoding="utf-8", newline="\n") as target:
            for key, value in values.items():
                text = str(value).lower() if isinstance(value, bool) else value
                target.write(f"{key}={text}\n")


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser("prepare")
    prepare.add_argument("--event", required=True)
    prepare.add_argument("--ref", required=True)
    prepare.add_argument("--sha", required=True)
    prepare.add_argument("--tag", default="")
    prepare.add_argument("--repo", type=Path, default=Path.cwd())
    prepare.add_argument("--output", default=os.environ.get("GITHUB_OUTPUT", ""))
    verify = commands.add_parser("verify-tag")
    verify.add_argument("--tag", required=True)
    verify.add_argument("--commit", required=True)
    verify.add_argument("--required", choices=["true", "false"], default="true")
    verify.add_argument("--repo", type=Path, default=Path.cwd())
    args = parser.parse_args()
    try:
        if args.command == "verify-tag":
            parse_tag(args.tag)
            exists = validate_tag(refs_for(args.repo, args.tag), args.tag, args.commit, args.required == "true")
            print(json.dumps({"tag_exists": exists, "source_commit": args.commit}))
            return
        tag = args.ref[len("refs/tags/"):] if args.event == "push" and args.ref.startswith("refs/tags/") else args.tag
        _, channel = parse_tag(tag)
        if SHA.fullmatch(args.sha) is None:
            raise Refusal("Invalid triggering commit")
        triggering = git(args.repo, "rev-parse", args.sha + "^{commit}")
        checked_out = git(args.repo, "rev-parse", "HEAD")
        plan = make_plan(args.event, args.ref, triggering, checked_out, args.tag, refs_for(args.repo, tag, BRANCHES[channel]))
        emit(plan, args.output)
    except Refusal as error:
        parser.exit(1, str(error) + "\n")


if __name__ == "__main__":
    main()
