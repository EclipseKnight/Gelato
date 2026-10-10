# Fake add-on fixtures

Each folder is one scenario. The fake add-on (`Fakes/FakeAddon.cs`) answers a request with the
file at the same path, so `/meta/series/kitsu:12.json` is `<scenario>/meta/series/kitsu_12.json` (`:` is written `_`).
`import1` / `import2` are the same add-on on two imports.

| Folder | Scenario |
|---|---|
| `onepiece` | A show whose IMDb id changes between imports; its native id stays |
| `initiald` | Five stages sharing one TVDB id, each with its own native id (must stay separate) |
| `sharedtmdb` | Two different films sharing one TMDB id (must never merge) |
| `blackclover` | A season listed as its own entry (`mal:61967`), same TVDB id as the show |
| `comeback` | An Ended show that comes back with a new season |
| `sharedepisodes` | Two entries claiming the same episodes |
| `streams` | Streams found, zero streams, broken JSON; delays and failures are set per test |
