#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project_dir="$script_dir/Jellyfin.Plugin.Template"
manifest_source="$script_dir/build.yaml"
configuration="Release"
framework="net9.0"
output_dir="$script_dir/dist"
version=""

usage() {
    cat <<'EOF'
Usage: ./package.sh [--version VERSION] [--output DIR] [--configuration CONFIG] [--framework TFM]

Builds the plugin, writes meta.json, and creates an installable zip archive.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version)
            version="${2:-}"
                version="$(python3 - "$manifest_source" <<'PY'
            from pathlib import Path
            import sys

            def read_scalar(prefix: str, lines: list[str]) -> str:
                for line in lines:
                    if line.startswith(prefix):
                        return line.split(':', 1)[1].strip().strip('"')
                raise SystemExit(f'Unable to determine {prefix[:-1]} from build.yaml')

            lines = Path(sys.argv[1]).read_text(encoding='utf-8').splitlines()
            print(read_scalar('version:', lines))
            PY
            )"
)"

            readarray -t manifest_values < <(python3 - "$manifest_source" <<'PY'
            from pathlib import Path
            import json
            import sys

            def parse_manifest(path: Path) -> dict[str, object]:
                lines = path.read_text(encoding='utf-8').splitlines()
                data: dict[str, object] = {}
                index = 0

                while index < len(lines):
                    line = lines[index]
                    stripped = line.strip()

                    if not stripped or stripped == '---':
                        index += 1
                        continue

                    if line.startswith('description: >') or line.startswith('changelog: >'):
                        key = line.split(':', 1)[0]
                        index += 1
                        block: list[str] = []
                        while index < len(lines):
                            next_line = lines[index]
                            if next_line.startswith('  '):
                                block.append(next_line.strip())
                                index += 1
                                continue
                            break
                        data[key] = ' '.join(block).strip()
                        continue

                    if line.startswith('artifacts:'):
                        index += 1
                        artifacts: list[str] = []
                        while index < len(lines):
                            next_line = lines[index]
                            stripped_next = next_line.strip()
                            if stripped_next.startswith('- '):
                                artifacts.append(stripped_next[2:].strip().strip('"'))
                                index += 1
                                continue
                            if not stripped_next:
                                index += 1
                                continue
                            break
                        data['artifacts'] = artifacts
                        continue

                    if ': ' in line:
                        key, value = line.split(': ', 1)
                        data[key] = value.strip().strip('"')

                    index += 1

                return data

            manifest = parse_manifest(Path(sys.argv[1]))
            values = [
                str(manifest.get('name', '')),
                str(manifest.get('guid', '')),
                str(manifest.get('targetAbi', '')),
                str(manifest.get('overview', '')),
                str(manifest.get('description', '')),
                str(manifest.get('category', '')),
                str(manifest.get('owner', '')),
                json.dumps(manifest.get('artifacts', [])),
            ]
            print('\n'.join(values))
            PY
            )

            plugin_name="${manifest_values[0]}"
            plugin_guid="${manifest_values[1]}"
            target_abi="${manifest_values[2]}"
            plugin_overview="${manifest_values[3]}"
            plugin_description="${manifest_values[4]}"
            plugin_category="${manifest_values[5]}"
            plugin_owner="${manifest_values[6]}"
            plugin_artifacts_json="${manifest_values[7]}"

            assembly_name="Jellyfin.Plugin.Template.dll"
            artifact_path="$project_dir/bin/$configuration/$framework/$assembly_name"
import re
import sys

text = Path(sys.argv[1]).read_text(encoding='utf-8')
match = re.search(r'^guid:\s*"([^"]+)"\s*$', text, re.MULTILINE)
if not match:
    raise SystemExit('Unable to determine guid from build.yaml')
print(match.group(1))
PY
)"

target_abi="$(python3 - "$manifest_source" <<'PY'
from pathlib import Path
import re
import sys

text = Path(sys.argv[1]).read_text(encoding='utf-8')
match = re.search(r'^targetAbi:\s*"([^"]+)"\s*$', text, re.MULTILINE)
if not match:
    raise SystemExit('Unable to determine targetAbi from build.yaml')
print(match.group(1))
PY
)"

assembly_name="Jellyfin.Plugin.Template.dll"
artifact_path="$project_dir/bin/$configuration/$framework/$assembly_name"

dotnet build "$project_dir/Jellyfin.Plugin.Template.csproj" \
    -c "$configuration" \
    -f "$framework" \
    /property:GenerateFullPaths=true \
    /consoleloggerparameters:NoSummary

if [[ ! -f "$artifact_path" ]]; then
    echo "Expected build artifact not found: $artifact_path" >&2
    exit 1
fi

staging_dir="$(mktemp -d)"
trap 'rm -rf "$staging_dir"' EXIT

install_name="$(echo "$plugin_name" | tr '[:upper:]' '[:lower:]' | tr ' ' '-')"
package_zip="$output_dir/$install_name-$version.zip"

mkdir -p "$output_dir"

python3 - "$manifest_source" "$version" "$plugin_name" "$plugin_guid" "$target_abi" "$plugin_overview" "$plugin_description" "$plugin_category" "$plugin_owner" "$plugin_artifacts_json" "$staging_dir/meta.json" <<'PY'
from pathlib import Path
import json
import sys

version = sys.argv[2]
name = sys.argv[3]
guid = sys.argv[4]
target_abi = sys.argv[5]
overview = sys.argv[6]
description = sys.argv[7]
category = sys.argv[8]
owner = sys.argv[9]
artifacts = json.loads(sys.argv[10])
destination = Path(sys.argv[11])

manifest = {
    'category': category,
    'changelog': 'Initial release.',
    'description': description,
    'guid': guid,
    'name': name,
    'overview': overview,
    'owner': owner,
    'targetAbi': target_abi,
    'timestamp': '',
    'version': version,
    'assemblies': artifacts,
}

destination.write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
PY

cp "$artifact_path" "$staging_dir/"
if [[ -f "${artifact_path%.dll}.pdb" ]]; then
    cp "${artifact_path%.dll}.pdb" "$staging_dir/"
fi

cp "$staging_dir/meta.json" "$output_dir/meta.json"
(cd "$staging_dir" && zip -qr "$package_zip" .)

echo "Created $package_zip"
echo "Manifest: $output_dir/meta.json"