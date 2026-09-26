#!/usr/bin/env python3

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path


def parse_manifest(path: Path) -> dict[str, object]:
    lines = path.read_text(encoding="utf-8").splitlines()
    data: dict[str, object] = {}
    index = 0

    while index < len(lines):
        line = lines[index]
        stripped = line.strip()

        if not stripped or stripped == "---":
            index += 1
            continue

        if line.startswith("description: >") or line.startswith("changelog: >"):
            key = line.split(":", 1)[0]
            index += 1
            block: list[str] = []

            while index < len(lines):
                next_line = lines[index]
                if next_line.startswith("  "):
                    block.append(next_line.strip())
                    index += 1
                    continue
                break

            data[key] = " ".join(block).strip()
            continue

        if line.startswith("artifacts:"):
            index += 1
            artifacts: list[str] = []

            while index < len(lines):
                next_line = lines[index]
                stripped_next = next_line.strip()

                if stripped_next.startswith("- "):
                    artifacts.append(stripped_next[2:].strip().strip('"'))
                    index += 1
                    continue

                if not stripped_next:
                    index += 1
                    continue

                break

            data["artifacts"] = artifacts
            continue

        if ": " in line:
            key, value = line.split(": ", 1)
            data[key] = value.strip().strip('"')

        index += 1

    return data


def slugify(value: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")
    return slug or "plugin"


def calculate_md5(file_path: Path) -> str:
    if not file_path.exists():
        raise FileNotFoundError(f"Build artifact not found at {file_path}")

    hash_md5 = hashlib.md5()
    with file_path.open("rb") as file_handle:
        for chunk in iter(lambda: file_handle.read(4096), b""):
            hash_md5.update(chunk)

    return hash_md5.hexdigest()


def version_sort_key(version: str) -> list[int]:
    return [int(part) for part in version.split(".")]


def normalize_remote_url(origin_url: str) -> str:
    origin_url = origin_url.strip()

    if origin_url.startswith("git@") and ":" in origin_url:
        host, path = origin_url[4:].split(":", 1)
        origin_url = f"https://{host}/{path}"

    origin_url = origin_url.rstrip("/")
    if origin_url.endswith(".git"):
        origin_url = origin_url[:-4]

    return origin_url


def infer_source_url(script_dir: Path, package_name: str, version: str) -> str:
    origin_result = subprocess.run(
        ["git", "-C", str(script_dir), "config", "--get", "remote.origin.url"],
        check=True,
        capture_output=True,
        text=True,
    )
    origin_url = origin_result.stdout.strip()
    if not origin_url:
        raise ValueError("Unable to determine git remote origin URL")

    repo_url = normalize_remote_url(origin_url)
    tag = version if version.startswith("v") else f"v{version}"
    return f"{repo_url}/releases/download/{tag}/{package_name}.zip"


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    project_dir = script_dir / "Jellyfin.Plugin.Template"
    manifest_source = script_dir / "build.yaml"

    parser = argparse.ArgumentParser(description="Build and package the plugin.")
    parser.add_argument("--version", help="Plugin version. Defaults to build.yaml version.")
    parser.add_argument("--output", default=str(script_dir / "dist"), help="Output directory for the zip and manifest.")
    parser.add_argument("--configuration", default="Release", help="Build configuration.")
    parser.add_argument("--framework", default="net9.0", help="Target framework.")
    parser.add_argument("--source-url", help="Download URL for the packaged zip file. Required for manifest.json generation.")
    parser.add_argument("--manifest", default=str(script_dir / "build" / "manifest.json"), help="Output path for the Jellyfin repository manifest.json.")
    args = parser.parse_args()

    manifest = parse_manifest(manifest_source)

    version = args.version or str(manifest.get("version", "1.0.0.0"))
    name = str(manifest.get("name", "Plugin"))
    guid = str(manifest.get("guid", "00000000-0000-0000-0000-000000000000"))
    target_abi = str(manifest.get("targetAbi", ""))
    overview = str(manifest.get("overview", ""))
    description = str(manifest.get("description", ""))
    category = str(manifest.get("category", ""))
    owner = str(manifest.get("owner", ""))
    changelog = str(manifest.get("changelog", "Initial release."))
    artifacts = list(manifest.get("artifacts", []))

    if not artifacts:
        artifacts = ["Jellyfin.Plugin.Template.dll"]

    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)

    package_name = f"{slugify(name)}-{version}"
    source_url = args.source_url or infer_source_url(script_dir, package_name, version)
    manifest_path = Path(args.manifest)

    build_args = [
        "dotnet",
        "build",
        str(project_dir / "Jellyfin.Plugin.Template.csproj"),
        "-c",
        args.configuration,
        "-f",
        args.framework,
        "/property:GenerateFullPaths=true",
        "/consoleloggerparameters:NoSummary",
    ]
    subprocess.run(build_args, check=True)

    assembly_name = artifacts[0]
    artifact_path = project_dir / "bin" / args.configuration / args.framework / assembly_name
    if not artifact_path.exists():
        raise FileNotFoundError(f"Expected build artifact not found: {artifact_path}")

    package_zip = output_dir / f"{package_name}.zip"
    manifest_output = output_dir / "meta.json"

    with tempfile.TemporaryDirectory() as temp_dir_name:
        staging_dir = Path(temp_dir_name)

        plugin_manifest = {
            "category": category,
            "changelog": changelog,
            "description": description,
            "guid": guid,
            "name": name,
            "overview": overview,
            "owner": owner,
            "targetAbi": target_abi,
            "timestamp": dt.datetime.now(dt.timezone.utc).isoformat(),
            "version": version,
            "assemblies": artifacts,
        }

        (staging_dir / "meta.json").write_text(json.dumps(plugin_manifest, indent=2) + "\n", encoding="utf-8")
        shutil.copy2(artifact_path, staging_dir / artifact_path.name)

        pdb_path = artifact_path.with_suffix(".pdb")
        if pdb_path.exists():
            shutil.copy2(pdb_path, staging_dir / pdb_path.name)

        with zipfile.ZipFile(package_zip, mode="w", compression=zipfile.ZIP_DEFLATED) as archive:
            for file_path in staging_dir.iterdir():
                archive.write(file_path, arcname=file_path.name)

        shutil.copy2(staging_dir / "meta.json", manifest_output)

    package_checksum = calculate_md5(package_zip)
    timestamp = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    manifest_entry = {
        "guid": guid,
        "name": name,
        "description": description,
        "overview": overview,
        "owner": owner,
        "category": category,
        "versions": [
            {
                "version": version,
                "changelog": changelog,
                "targetAbi": target_abi,
                "sourceUrl": source_url,
                "checksum": package_checksum,
                "timestamp": timestamp,
            }
        ],
    }

    image_url = manifest.get("imageUrl")
    if image_url:
        manifest_entry["imageUrl"] = image_url

    manifest_json: list[dict[str, object]]
    if manifest_path.exists():
        try:
            loaded_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest_json = loaded_manifest if isinstance(loaded_manifest, list) else []
        except json.JSONDecodeError:
            manifest_json = []
    else:
        manifest_json = []

    existing_plugin = next((entry for entry in manifest_json if entry.get("guid") == guid), None)
    if existing_plugin is None:
        existing_plugin = {**manifest_entry, "versions": []}
        manifest_json.append(existing_plugin)
    else:
        for key, value in manifest_entry.items():
            if key != "versions":
                existing_plugin[key] = value

    versions = existing_plugin.setdefault("versions", [])
    existing_version_index = next((index for index, entry in enumerate(versions) if entry.get("version") == version), None)
    version_payload = {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "checksum": package_checksum,
        "timestamp": timestamp,
    }

    if existing_version_index is None:
        versions.append(version_payload)
    else:
        versions[existing_version_index] = version_payload

    versions.sort(key=lambda entry: version_sort_key(str(entry.get("version", "0"))), reverse=True)

    manifest_path.write_text(json.dumps(manifest_json, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"Created {package_zip}")
    print(f"Manifest index: {manifest_path}")
    print(f"Manifest: {manifest_output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())