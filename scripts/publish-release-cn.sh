#!/usr/bin/env bash
set -euo pipefail

repo='LienChan666/GatherBuddyReborn'
if [[ "${GITHUB_REPOSITORY:?}" != "$repo" || "${GITHUB_REF:?}" != 'refs/heads/main' ]]; then
  echo 'Release is allowed only from the localization repository main branch.' >&2
  exit 1
fi
[[ "${PLUGIN_VERSION:?}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
[[ "${GITHUB_SHA:?}" =~ ^[0-9a-f]{40}$ ]]
tag="cn-$PLUGIN_VERSION"
archive="GatherBuddyReborn-$PLUGIN_VERSION.zip"
manifest='GatherBuddyReborn.json'

# A build queued before main changed must not become the new public release.
main="$(gh api "repos/$repo/git/ref/heads/main" --jq .object.sha)"
if [[ "$main" != "$GITHUB_SHA" ]]; then
  echo "Skipping superseded source commit $GITHUB_SHA (main: $main)."
  exit 0
fi

python3 - "$archive" "$manifest" "$PLUGIN_VERSION" <<'PY'
import json
import pathlib
import sys
import zipfile
archive, manifest, version = sys.argv[1:]
folder = pathlib.Path("publish")
data = (folder / manifest).read_bytes()
parsed = json.loads(data)
assert parsed["InternalName"] == "GatherBuddyReborn"
assert parsed["AssemblyVersion"] == parsed["TestingAssemblyVersion"] == version
with zipfile.ZipFile(folder / archive) as bundle:
    assert all(bundle.namelist().count(name) == 1 for name in [manifest, "GatherBuddyReborn.dll", "raphael-cli.exe"])
    assert bundle.read(manifest) == data
    assert bundle.getinfo("GatherBuddyReborn.dll").file_size > 0
    assert bundle.getinfo("raphael-cli.exe").file_size > 0
PY

releases="$(gh api --paginate "repos/$repo/releases?per_page=100" | jq -s 'add // []')"
if ! jq -e --arg tag "$tag" --arg v "$PLUGIN_VERSION" '
  ($v | split(".") | map(tonumber)) as $wanted
  | all(.[]; .draft or .prerelease or .tag_name == $tag or
      (.tag_name | test("^cn-[0-9]+(\\.[0-9]+){3}$") | not) or
      ((.tag_name | ltrimstr("cn-") | split(".") | map(tonumber)) < $wanted))
' <<< "$releases" >/dev/null; then
  echo "Refusing to publish $tag because a newer Chinese release already exists." >&2
  exit 1
fi

existing="$(jq -c --arg tag "$tag" '.[] | select(.tag_name == $tag)' <<< "$releases")"
if [[ -n "$existing" ]] && [[ "$(jq -r '.draft' <<< "$existing")" == false ]]; then
  # Do not replace a successful release. Verify it was the exact source and had intact assets.
  ref="$(gh api "repos/$repo/git/ref/tags/$tag" --jq .object.sha)"
  [[ "$ref" == "$GITHUB_SHA" ]]
  for asset in "$archive" "$manifest"; do
    key=ZIP
    [[ "$asset" == "$manifest" ]] && key=Manifest
    jq -e --arg name "$asset" --arg key "$key" --arg sha "$GITHUB_SHA" '
      .body as $body
      | ($body | contains("- Source: `" + $sha + "`")) and any(.assets[];
          .name == $name and .state == "uploaded" and .size > 0
          and (.digest | test("^sha256:[0-9a-f]{64}$"))
          and ((.digest | ltrimstr("sha256:")) as $digest
            | $body | contains("- " + $key + " SHA-256: `" + $digest + "`")))
    ' <<< "$existing" >/dev/null
  done
  echo "$tag already published from this source; no changes."
  exit 0
fi

archive_hash="$(sha256sum "publish/$archive" | cut -d ' ' -f 1)"
manifest_hash="$(sha256sum "publish/$manifest" | cut -d ' ' -f 1)"
notes="$(printf '简体中文构建：%s\n\n- Source: `%s`\n- ZIP SHA-256: `%s`\n- Manifest SHA-256: `%s`\n' "$PLUGIN_VERSION" "$GITHUB_SHA" "$archive_hash" "$manifest_hash")"
if [[ -z "$existing" ]]; then
  gh release create "$tag" --repo "$repo" --draft --target "$GITHUB_SHA" --title "GatherBuddy Reborn 简体中文 $PLUGIN_VERSION" --notes "$notes"
else
  jq -e --arg sha "$GITHUB_SHA" --arg notes "$notes" '.target_commitish == $sha and (.body | rtrimstr("\r\n") | rtrimstr("\n")) == $notes' <<< "$existing" >/dev/null
fi

# Resume a partial draft only with matching bytes; do not overwrite existing assets.
for asset in "$archive" "$manifest"; do
  expected="sha256:$(sha256sum "publish/$asset" | cut -d ' ' -f 1)"
  if [[ -z "$existing" ]] || ! jq -e --arg name "$asset" 'any(.assets[]; .name == $name)' <<< "$existing" >/dev/null; then
    gh release upload "$tag" "publish/$asset" --repo "$repo"
  else
    jq -e --arg name "$asset" --arg digest "$expected" 'any(.assets[]; .name == $name and .digest == $digest and .state == "uploaded")' <<< "$existing" >/dev/null
  fi
done

# Verify uploaded digests by reading the release fresh before making it public.
draft="$(gh api --paginate "repos/$repo/releases?per_page=100" | jq -cs --arg tag "$tag" 'add | map(select(.tag_name == $tag)) | if length == 1 then .[0] else error("Missing or duplicate release tag") end')"
[[ "$(jq -r .draft <<< "$draft")" == true ]]
for asset in "$archive" "$manifest"; do
  expected="sha256:$(sha256sum "publish/$asset" | cut -d ' ' -f 1)"
  jq -e --arg name "$asset" --arg digest "$expected" 'any(.assets[]; .name == $name and .digest == $digest and .state == "uploaded")' <<< "$draft" >/dev/null
done
main="$(gh api "repos/$repo/git/ref/heads/main" --jq .object.sha)"
if [[ "$main" != "$GITHUB_SHA" ]]; then
  echo 'Leaving unpublished draft because main changed during asset upload.'
  exit 0
fi
gh release edit "$tag" --repo "$repo" --draft=false --latest
[[ "$(gh api "repos/$repo/git/ref/tags/$tag" --jq .object.sha)" == "$GITHUB_SHA" ]]
