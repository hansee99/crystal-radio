#!/usr/bin/env bash
# Downloads the embedding model (all-MiniLM-L6-v2.onnx, ~90 MB, git-ignored) into MlAssets/ and
# checks it against a known hash. Pinned to one upstream commit, so every build embeds with the same
# model: vectors from a different one aren't comparable with the catalog's (see CLAUDE.md,
# "Same model for query and documents"). Used by CI; works from Git Bash too.
#
# Does nothing when a file with the right hash is already there.
set -euo pipefail

revision=1110a243fdf4706b3f48f1d95db1a4f5529b4d41
sha256=6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452
url="https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/$revision/onnx/model.onnx"

model="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/MlAssets/all-MiniLM-L6-v2.onnx"

matches() { [ -f "$1" ] && echo "$sha256  $1" | sha256sum -c --status -; }

if matches "$model"; then
    echo "Model already present: $model"
    exit 0
fi

echo "Downloading the embedding model (revision ${revision:0:7})"
curl -fsSL --retry 3 -o "$model.part" "$url"
if ! matches "$model.part"; then
    rm -f "$model.part"
    echo "Downloaded model does not match the expected SHA-256 - not using it." >&2
    exit 1
fi
mv "$model.part" "$model"
echo "Model saved: $model"
