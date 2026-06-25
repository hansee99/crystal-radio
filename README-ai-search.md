## AI-assisted station search

The radio player includes a natural-language station search: you describe what you want
to hear — "something mellow for late-night coding" or "upbeat 80s synthpop without talk
segments" — and the app finds real, playable stations that match. This feature was
deliberately designed as a learning project in AI engineering, and the architecture
reflects a set of practical decisions that are worth understanding before reading the
code.

### The problem with asking an AI for radio stations

The obvious approach — ask a language model to name some stations and play whatever it
suggests — doesn't work. Language models have no reliable, current knowledge of which
internet radio streams exist, what their URLs are, or whether those streams are live.
They will invent plausible-sounding station names and confidently produce stream URLs
that are dead, wrong, or entirely fabricated. Stream URLs in particular churn constantly,
so even a model with recent training data can't be trusted as a source of them.

This leads to the foundational rule that governs the entire feature:

> **The LLM is not the database.** All real station data — names, stream URLs, codecs —
> comes from a verified directory. The model's only job is to help bridge the gap
> between what a user describes in natural language and what that directory can actually
> look up.

### The data layer: Radio Browser

The station directory used throughout is [Radio Browser](https://api.radio-browser.info),
a free, community-maintained catalog of internet radio stations with no authentication
requirement. It covers tens of thousands of stations and, critically, actively checks
whether streams are alive — so results can be filtered to stations that are actually
reachable. Every station that ever reaches the player has been validated through this
source first.

The limitation of Radio Browser is also the reason this feature exists: its metadata is
thin. Each station carries a handful of human-applied genre tags, a country, and a
bitrate, but nothing richer — no mood, no era, no description of what the station
actually sounds like. A search for "melancholy post-rock" or "Berlin techno" will
produce either nothing or something generic. Bridging that gap is what the AI layers are
for.

### A layered design: from simple to capable

Rather than reaching for the most sophisticated approach immediately, the search is
built as a stack of layers, each adding capability and teaching a distinct AI engineering
pattern. The layers activate based on what a query needs.

---

#### Pattern A — structured output (literal queries)

For queries that map cleanly to catalog fields — "German news radio", "jazz from France",
"AAC streams above 128kbps" — the model functions as a query translator. It reads the
natural-language prompt and returns a small, strict JSON object describing what to search
for: genre tags, country, language, minimum bitrate. The app then runs that structured
query against Radio Browser directly and presents the results.

This is the simplest possible use of a language model — one call, one structured
response, no conversation — and it already handles a large class of queries well. It also
teaches the most important skill in working with LLMs: asking for structured output
rather than prose, and validating what comes back before acting on it.

---

#### Pattern B — web-search discovery (fuzzy and semantic queries)

Structured queries work when a user's intent maps to catalog fields. They fail for
soft, associative requests: "the station someone recommended for night drives", "dreamy
shoegaze", "whatever Radio Paradise-adjacent means". No set of tags captures those
descriptions because the relevant knowledge — curated lists, community recommendations,
station reputations — lives in human-written text on the web, not in a structured
catalog.

Pattern B uses the model as an **agentic search agent** equipped with two tools. The
first is a server-side web search tool that the model calls autonomously to find station
names and descriptions from blogs, forums, and "best of" lists. The second is a
client-side Radio Browser lookup tool the model uses to verify and retrieve those named
stations from the directory. The model decides how many searches to run, how to refine
its results, and when it has enough to produce a final answer.

The key safety constraint is unchanged: the model's final answer is a list of
`stationuuid` values — identifiers from Radio Browser — not URLs. The app resolves each
identifier back to a stream URL it fetched itself. A URL produced by the model in prose
or by a web page is never played directly.

This pattern teaches the agentic loop: how to register tools, how to drive a
conversation to completion across multiple model turns, and how to execute the right
kind of tool call in the right place (server-side and client-side tools have different
execution models that need to be understood separately).

---

#### Phase 1 — local enrichment (building a better data asset)

Patterns A and B are online and stateless — every search makes live API calls. They are
also ultimately limited by the same root cause: thin catalog metadata. Pattern B can
surface good stations, but it pays the cost of a web search every time.

Phase 1 takes a different approach and asks: what if the app could build its own richer
description of each station over time?

When a station appears in a search result or is played, the app fetches its homepage,
extracts readable text, and asks a language model to distill a short profile from it —
genre, mood, era, what it actually sounds like — and caches that profile locally in a
SQLite database keyed by the station's unique identifier. The next time that station is
a candidate, the re-ranking step has a real description to reason over, not three
generic tags.

Enrichment happens lazily and in the background. The player never waits for it, and
search and playback function identically without it. Over time the local catalog fills
with descriptions of stations the user has actually explored, reflecting their taste
rather than the whole Radio Browser universe.

This phase introduces the first real data engineering concern in the project: persistent
local state with a lifecycle. A description that was accurate six months ago may be
stale. A homepage that existed yesterday may be gone today. Fallbacks matter: if a
homepage is dead or JavaScript-rendered and unreadable, the pipeline falls back to the
station name, its Radio Browser tags, and optionally a web snippet — something is always
better than nothing.

---

#### Phase 2 — semantic search (embeddings and vector retrieval)

With a growing set of enriched descriptions, it becomes possible to do something Patterns
A and B cannot: find stations that *feel* like a query without sharing a single keyword
with it. This is what embeddings are for.

An embedding is a compact numerical representation of text that captures semantic
meaning — two descriptions of "melancholy music for rainy evenings" will produce vectors
that are close to each other in embedding space, even if the underlying text uses
completely different words. By embedding both the station descriptions and the user's
query using the same model, the app can rank stations by conceptual similarity rather
than keyword overlap.

The implementation deliberately avoids a purpose-built vector database. The embeddings
are stored as binary blobs in the same SQLite table as the descriptions, and similarity
search at query time is brute-force cosine similarity computed in memory. At the scale
of a personal enriched catalog this is fast enough and keeps the dependency footprint
minimal. A specialized vector store would be a later optimization for a much larger
dataset, not a starting requirement.

The embedding model runs locally via ONNX Runtime — a small sentence-transformer model
that ships with the app and works without a network connection. This keeps semantic
search fast, offline, and cost-free after the initial setup, which fits the desktop
nature of the project. The model is kept behind an interface so a hosted alternative
could be substituted, but the local default means the feature degrades gracefully to
Pattern B (and not to nothing) if offline.

One nuance worth flagging: the model used to embed station descriptions and the model
used to embed the query must be identical. Vectors from different models exist in
different spaces and cannot be meaningfully compared, so the model identifier is stored
alongside every vector in the database.

---

### How the layers combine: a self-improving loop

The full picture, once all four layers are active, looks like this:

A **literal query** bypasses all the AI machinery and goes directly to a Radio Browser
search — fast and cheap.

A **fuzzy or vibe query** first hits the local semantic index. If the enriched catalog
has enough relevant stations, it returns results immediately, with no network call beyond
playing the stream.

If the semantic results are **thin or empty** — a station the user has never encountered,
a very niche genre, a cold-start situation — the query falls through to Pattern B. The
web search discovers candidates, resolves them through Radio Browser, and returns results.

Crucially, those Pattern B results then flow through the enrichment pipeline: their
homepages are fetched, descriptions are generated, embeddings are stored. The next time
someone asks for something similar, the semantic index already has it.

This loop — **discover → enrich → embed → rely on local** — means the feature improves
with use without any explicit training step. It is a small-scale instance of the same
retrieval-augmented generation pattern used in production AI systems, built entirely from
first principles on a desktop app.
