#!/usr/bin/env bash
# Copies the repository's app-teaching skills (app-skills/<id>/SKILL.md) into the app's
# Contents/Resources/app-skills, so an install from the dmg has them (SkillLibraryStore reads
# them through OpenClickyConfiguration.bundledAppSkillsDirectory). Run by the OpenClicky target's
# "Embed app skills" build phase. Only SKILL.md files go in: the folder's README stays behind.
set -euo pipefail

project_dir="${PROJECT_DIR:-$(cd "$(dirname "$0")/.." && pwd)}"
source_directory="$project_dir/../../app-skills"
[[ -d "$source_directory" ]] || { echo "error: app skills not found at $source_directory" >&2; exit 1; }

# Outside a build (run by hand) there is nowhere to copy to.
[[ -n "${TARGET_BUILD_DIR:-}" ]] || exit 0

destination="$TARGET_BUILD_DIR/$UNLOCALIZED_RESOURCES_FOLDER_PATH/app-skills"
# Rebuilt from scratch each time, so a skill deleted from the repository leaves the app too.
rm -rf "$destination"
mkdir -p "$destination"
copied_skill_count=0
for skill_folder in "$source_directory"/*/; do
  skill_id="$(basename "$skill_folder")"
  # "_template" and other "_" folders are for contributors, not for the app.
  [[ "$skill_id" == _* ]] && continue
  [[ -f "$skill_folder/SKILL.md" ]] || continue
  mkdir -p "$destination/$skill_id"
  cp "$skill_folder/SKILL.md" "$destination/$skill_id/SKILL.md"
  copied_skill_count=$((copied_skill_count + 1))
done
echo "embed-app-skills: $copied_skill_count skills"
