from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path
from urllib.parse import quote, urlsplit, urlunsplit

from pip._vendor.packaging.markers import default_environment
from pip._vendor.packaging.requirements import Requirement
from pip._vendor.packaging.specifiers import SpecifierSet
from pip._vendor.packaging.tags import sys_tags
from pip._vendor.packaging.utils import canonicalize_name, parse_wheel_filename
from pip._vendor.packaging.version import Version

PROJECT = Path(r"C:\Users\Sam\Documents\Codex\sam-tech")
TOOLS = PROJECT / ".tools"
CACHE = TOOLS / "pypi-json-cache"
WHEELHOUSE = TOOLS / "imagegen-wheelhouse"
TARGET = TOOLS / "imagegen-python-v3"
for directory in (CACHE, WHEELHOUSE, TARGET):
    directory.mkdir(parents=True, exist_ok=True)

tags = list(sys_tags())
tag_rank = {tag: index for index, tag in enumerate(tags)}
environment = default_environment()
environment["extra"] = ""
constraints: dict[str, list[str]] = {"openai": [""]}
expanded: dict[str, str] = {}
wheel_paths: set[Path] = set()


def fetch_json(url: str, destination: Path) -> dict:
    if not destination.exists():
        command = [
            "curl.exe", "-4", "--fail", "--location", "--silent", "--show-error",
            "--retry", "2", "--connect-timeout", "20", "--max-time", "180",
            "--output", str(destination), url,
        ]
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(f"curl failed for {url}: {result.stderr.strip()}")
    with destination.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def resolve_ipv4_via_doh(host: str) -> list[str]:
    resolvers = [
        ("cloudflare-dns.com", "1.1.1.1"),
        ("dns.google", "8.8.8.8"),
    ]
    for resolver_host, resolver_ip in resolvers:
        response_path = CACHE / f"doh-{resolver_host.replace('.', '-')}.json"
        result = subprocess.run(
            [
                "curl.exe", "-4", "--fail", "--location", "--silent", "--show-error",
                "--connect-timeout", "10", "--max-time", "20",
                "--resolve", f"{resolver_host}:443:{resolver_ip}",
                "-H", "accept: application/dns-json",
                "--output", str(response_path),
                f"https://{resolver_host}/resolve?name={quote(host)}&type=A"
                if resolver_host == "dns.google"
                else f"https://{resolver_host}/dns-query?name={quote(host)}&type=A",
            ],
            capture_output=True,
            text=True,
        )
        if result.returncode:
            continue
        try:
            payload = json.loads(response_path.read_text(encoding="utf-8"))
            addresses = [
                answer["data"]
                for answer in payload.get("Answer", [])
                if answer.get("type") == 1 and answer.get("data")
            ]
            if addresses:
                return addresses
        except (OSError, json.JSONDecodeError):
            continue
    return []


def select_wheel(project_data: dict, package: str, specifier_text: str):
    specifier = SpecifierSet(specifier_text)
    candidates = []
    for version_text, files in project_data.get("releases", {}).items():
        try:
            version = Version(version_text)
        except Exception:
            continue
        if not specifier.contains(version, prereleases=False):
            continue
        for file_info in files:
            if file_info.get("yanked") or not file_info.get("filename", "").endswith(".whl"):
                continue
            requires_python = file_info.get("requires_python")
            if requires_python and not SpecifierSet(requires_python).contains(
                f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}",
                prereleases=True,
            ):
                continue
            try:
                _, wheel_version, _, wheel_tags = parse_wheel_filename(file_info["filename"])
            except Exception:
                continue
            matches = [tag_rank[tag] for tag in wheel_tags if tag in tag_rank]
            if matches:
                candidates.append((version, min(matches), file_info))
    if not candidates:
        raise RuntimeError(
            f"No compatible Windows/Python {sys.version_info.major}.{sys.version_info.minor} wheel "
            f"found for {package}{specifier_text}"
        )
    candidates.sort(key=lambda item: (item[0], -item[1]), reverse=True)
    version, _, file_info = candidates[0]
    return version, file_info


queue = ["openai"]
while queue:
    package = canonicalize_name(queue.pop(0))
    project_json = CACHE / f"{package}.json"
    project_data = fetch_json(
        f"https://pypi.org/pypi/{quote(package)}/json", project_json
    )
    specifier_text = ",".join(part for part in constraints.get(package, [""]) if part)
    version, file_info = select_wheel(project_data, package, specifier_text)
    version_text = str(version)
    if expanded.get(package) == version_text:
        continue

    filename = file_info["filename"]
    wheel_path = WHEELHOUSE / filename
    expected = file_info.get("digests", {}).get("sha256")
    if wheel_path.exists() and expected:
        existing_digest = hashlib.sha256(wheel_path.read_bytes()).hexdigest()
        if existing_digest.lower() != expected.lower():
            wheel_path.unlink(missing_ok=True)
    if not wheel_path.exists():
        print(f"Downloading {package} {version_text}...", flush=True)
        result = subprocess.run(
            [
                "curl.exe", "-4", "--fail", "--location", "--silent", "--show-error",
                "--retry", "2", "--connect-timeout", "20", "--max-time", "300",
                "--output", str(wheel_path), file_info["url"],
            ],
            capture_output=True,
            text=True,
        )
        if result.returncode:
            last_error = result.stderr.strip()
            original_url = file_info["url"]
            parts = urlsplit(original_url)
            mirrors = [
                ("Tsinghua PyPI mirror", "pypi.tuna.tsinghua.edu.cn"),
                ("Aliyun PyPI mirror", "mirrors.aliyun.com"),
            ]
            download_succeeded = False
            for mirror_name, mirror_host in mirrors:
                mirror_url = urlunsplit(("https", mirror_host, parts.path, parts.query, ""))
                print(f"Retrying {filename} through {mirror_name}...", flush=True)
                result = subprocess.run(
                    [
                        "curl.exe", "-4", "--fail", "--location", "--silent", "--show-error",
                        "--retry", "0", "--connect-timeout", "15", "--max-time", "300",
                        "--output", str(wheel_path), mirror_url,
                    ],
                    capture_output=True,
                    text=True,
                )
                if result.returncode == 0:
                    download_succeeded = True
                    break
                last_error = result.stderr.strip()
            if not download_succeeded:
                raise RuntimeError(
                    f"Could not download {filename} from PyPI or its mirrors. {last_error}"
                )
        digest = hashlib.sha256(wheel_path.read_bytes()).hexdigest()
        if expected and digest.lower() != expected.lower():
            wheel_path.unlink(missing_ok=True)
            raise RuntimeError(f"SHA-256 check failed for {filename}")
    wheel_paths.add(wheel_path)

    release_json = CACHE / f"{package}-{version_text}.json"
    release_data = fetch_json(
        f"https://pypi.org/pypi/{quote(package)}/{quote(version_text)}/json",
        release_json,
    )
    expanded[package] = version_text
    for raw_requirement in release_data.get("info", {}).get("requires_dist") or []:
        requirement = Requirement(raw_requirement)
        if requirement.marker and not requirement.marker.evaluate(environment):
            continue
        child = canonicalize_name(requirement.name)
        constraint = str(requirement.specifier)
        constraints.setdefault(child, []).append(constraint)
        queue.append(child)

print("Installing from the downloaded wheel files (offline)...", flush=True)
install = [
    sys.executable, "-m", "pip", "install",
    "--disable-pip-version-check", "--no-index",
    "--find-links", str(WHEELHOUSE),
    "--target", str(TARGET),
    "--upgrade", "openai",
]
subprocess.run(install, check=True)
print(f"Installed OpenAI SDK and dependencies to: {TARGET}", flush=True)
