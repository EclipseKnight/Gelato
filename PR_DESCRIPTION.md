# test: add a unit test project with a fake Stremio add-on

(Draft upstream PR description. Drop this file before opening a PR.)

Gelato has no tests yet. This adds `tests/Gelato.Tests` (xunit, net10.0), which runs without a Jellyfin server:

- `Fakes/FakeAddon.cs`: a small local HTTP server that acts as a Stremio add-on and serves fixture JSON per scenario (manifest, catalog, meta, stream). It can delay or fail chosen paths.
- `AddonScenarioTests.cs`: checks how the add-on client reads those answers: an IMDb id that changes between two imports, two stages sharing a TVDB id, films sharing a TMDB id, a season listed as its own catalogue entry, a show that comes back, two entries claiming the same episodes, and empty, slow, failed and broken stream answers.
- `.github/workflows/test.yml`: builds and runs the tests on pushes and pull requests.
- `Gelato.csproj`: keeps `tests/**` out of the plugin build.

Run locally: `dotnet test tests/Gelato.Tests`

Result: 15/15 passing on main.
