# Third-party notices

This project bundles and depends on third-party components that are **not** covered by
this project's [LICENSE](LICENSE.md). Each remains under its own terms, summarized below.

## BASS audio library (native) — un4seen Developments Ltd.

Files: `native/x64/bass.dll`, `native/x64/bass_aac.dll`, and their Linux aarch64 builds
`native/linux-arm64/libbass.so`, `native/linux-arm64/libbass_aac.so` (Raspberry Pi port)

BASS and its add-ons are © un4seen Developments Ltd. They are **proprietary freeware,
free for non-commercial use only**. Commercial use requires a paid license purchased
from un4seen. These DLLs are redistributed here under that non-commercial grant and are
**not** licensed under this project's PolyForm Noncommercial license.

- Website / licensing: <https://www.un4seen.com/>

## ManagedBass / ManagedBass.Aac — MIT License

The managed .NET wrapper for BASS (referenced via NuGet) is licensed under the MIT
License. It is a separate component from the native BASS libraries above.

- Project: <https://github.com/ManagedBass/ManagedBass>

## Hanken Grotesk (UI font) — SIL Open Font License 1.1

Files: `Fonts/static/*.ttf` (the four weights the app embeds as resources — Light,
Regular, Medium, SemiBold).

Copyright 2021 The Hanken Grotesk Project Authors
(<https://github.com/marcologous/hanken-grotesk>). Licensed under the **SIL Open Font
License, Version 1.1** — full text in [`Fonts/OFL.txt`](Fonts/OFL.txt). The OFL permits
bundling and redistribution; the font is **not** covered by this project's license.

## Segoe Fluent Icons — Microsoft

A few toolbar glyphs use the **Segoe Fluent Icons** font that ships with Windows 11. The
font is not redistributed by this project; it is provided by the operating system and
remains subject to Microsoft's terms.

## all-MiniLM-L6-v2 sentence-transformer — Apache License 2.0

Files: `MlAssets/vocab.txt` (bundled WordPiece vocabulary);
`MlAssets/all-MiniLM-L6-v2.onnx` (large; fetched on setup, **not** committed — see
[`MlAssets/README.md`](MlAssets/README.md)).

The model and its tokenizer vocabulary are © their authors (Sentence-Transformers /
Hugging Face) and licensed under the **Apache License 2.0**.

## .NET package dependencies

Managed dependencies are restored from NuGet (not redistributed in this repo) and keep
their own licenses — notably **ManagedBass** / **ManagedBass.Aac** (MIT, above),
**Microsoft.Data.Sqlite**, **Microsoft.ML.OnnxRuntime**, and **Microsoft.ML.Tokenizers**
(MIT / Apache-2.0). See each package for details.
