#!/usr/bin/env python3

from __future__ import annotations

import argparse
import datetime as dt
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


def main() -> int:
    script_dir = Path(__file__).resolve().parent
    project_dir = script_dir / "Jellyfin.Plugin.Template"
    manifest_source = script_dir / "build.yaml"

    parser = argparse.ArgumentParser(description="Build and package the plugin.")
    parser.add_argument("--version", help="Plugin version. Defaults to build.yaml version.")
    parser.add_argument("--output", default=str(script_dir / "dist"), help="Output directory for the zip and manifest.")
    parser.add_argument("--configuration", default="Release", help="Build configuration.")
    parser.add_argument("--framework", default="net9.0", help="Target framework.")
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

    package_name = f"{slugify(name)}-{version}"
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

    print(f"Created {package_zip}")
    print(f"Manifest: {manifest_output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())