# ML assets — local embedding model

Phase 2 semantic search embeds station descriptions locally with
**all-MiniLM-L6-v2** (384-dim sentence-transformer) via ONNX Runtime.

| File | Tracked in git? | Notes |
|---|---|---|
| `vocab.txt` | ✅ yes | BERT WordPiece vocab (~230 KB) |
| `all-MiniLM-L6-v2.onnx` | ❌ no (git-ignored) | ~90 MB model — fetch it (below) |

The `.onnx` is git-ignored to keep the repo small. Fetch it once after cloning:

```sh
curl -L -o MlAssets/all-MiniLM-L6-v2.onnx \
  https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx
```

Both files are copied to the build output (`MlAssets/`) via `<Content>` in the csproj.
If the model file is missing, semantic search degrades gracefully (the embedding provider
reports unavailable and search falls back to Pattern B); the rest of the app is unaffected.
