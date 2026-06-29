#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK_DIR="${ROOT_DIR}/target/adb-platform-tools"
ASSET_DIR="${ROOT_DIR}/assets/adb"

mkdir -p "${WORK_DIR}" "${ASSET_DIR}"

fetch_and_extract() {
  local target_id="$1"
  local url="$2"
  local adb_name="$3"
  local output_name="$4"
  local zip_path="${WORK_DIR}/${target_id}.zip"
  local extract_dir="${WORK_DIR}/${target_id}"
  local output_dir="${ASSET_DIR}/${target_id}"

  echo "Downloading ${target_id} from ${url}"
  curl --fail --location --retry 3 --output "${zip_path}" "${url}"
  rm -rf "${extract_dir}" "${output_dir}"
  mkdir -p "${extract_dir}" "${output_dir}"
  unzip -q "${zip_path}" -d "${extract_dir}"
  cp "${extract_dir}/platform-tools/${adb_name}" "${output_dir}/${output_name}"
  chmod +x "${output_dir}/${output_name}" || true
  shasum -a 256 "${output_dir}/${output_name}"
}

fetch_and_extract "linux-x86_64" "https://dl.google.com/android/repository/platform-tools-latest-linux.zip" "adb" "adb"
fetch_and_extract "macos-x86_64" "https://dl.google.com/android/repository/platform-tools-latest-darwin.zip" "adb" "adb"
fetch_and_extract "macos-arm64" "https://dl.google.com/android/repository/platform-tools-latest-darwin.zip" "adb" "adb"
fetch_and_extract "windows-x86_64" "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" "adb.exe" "adb.exe"

cat <<'NOTE'
Official Platform-Tools downloads do not cover every target declared by ADBControl.
The remaining custom-github-build targets must be supplied by the dedicated ADB build workflow/repository:
- windows-arm64
- linux-arm64
- android-x86_64
- android-arm64
NOTE
