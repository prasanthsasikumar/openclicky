#!/usr/bin/env bash
# Copies the repository's shared drawing stage (draw-stage/: the page, its worker, three.js, the
# samples and the prompts) into the app's Contents/Resources/draw-stage, so an install from the dmg
# has it (DrawStageWindow serves it over its own URL scheme; DrawStageLesson reads prompt/*.md).
# Run by the OpenClicky target's "Embed draw stage" build phase. The folder's README stays behind.
set -euo pipefail

project_dir="${PROJECT_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
source_directory="$project_dir/../../draw-stage"
[[ -d "$source_directory" ]] || { echo "error: the drawing stage was not found at $source_directory" >&2; exit 1; }

# Outside a build (run by hand) there is nowhere to copy to.
[[ -n "${TARGET_BUILD_DIR:-}" ]] || exit 0

destination="$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH/draw-stage"
# Rebuilt from scratch each time, so a file deleted from the repository leaves the app too.
rm -rf "$destination"
mkdir -p "$destination"
copied_file_count=0
while IFS= read -r -d '' source_file; do
  relative_path="${source_file#"$source_directory"/}"
  [[ "$relative_path" == "README.md" ]] && continue
  mkdir -p "$destination/$(dirname "$relative_path")"
  cp "$source_file" "$destination/$relative_path"
  copied_file_count=$((copied_file_count + 1))
done < <(find "$source_directory" -type f -print0)
echo "embed-draw-stage: $copied_file_count files"
