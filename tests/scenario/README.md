# Scenario runs

Gelato against a real, throwaway Jellyfin 12.1 and a fake Stremio add-on.

    sudo python3 tests/scenario/run.py              # all scenarios, about 5 minutes
    sudo python3 tests/scenario/run.py --only delete

Each scenario starts a fresh `jellyfin/jellyfin:12.1` container on 127.0.0.1:8099 with an empty
data folder, installs the Gelato build from this checkout, sets up an admin, a viewer and two
libraries, and points Gelato at the fake add-on (a small server in `run.py` that serves
`fixtures/<scenario>/`, reached on Docker's default bridge). It imports and syncs, checks the
library, then imports again, restarts Jellyfin and imports once more: nothing may change and no
new errors may be logged. The container and its data are removed afterwards (`--keep` keeps the
data folder). The run refuses to start if the port is in use.

| Scenario | Checks |
|---|---|
| `initiald` | Five stages sharing a TVDB id stay five series, two episodes each |
| `sharedtmdb` | Two films sharing a TMDB id stay two films |
| `delete` | A deleted film comes back on the next import with the viewer's watch state; the other film's is untouched |
| `streams` | A slow add-on (3 s) and a title with zero streams don't break an import |
| `onepiece` | An IMDb id change keeps one series, its episodes and watch state |
| `sharedepisodes` | Two entries claiming the same episodes don't move them back and forth |
| `comeback` | An Ended series that comes back gets its new season |
| `blackclover` | A season listed as its own entry is filed under the show |
| `splitfill` | A show with its own season 2 keeps it: the season 2 entry only fills the episode numbers it lacks, or is left out. Then the show lists a new season 2 episode and two cours of season 3 come in, the second numbered on from the first: season 2 (the show's own and a filed episode) takes the new one, and both cours are filed |
| `splitcours` | Two cours of one season listed as two entries (second first): the first is filed, the second stays its own show |
| `splitsafe` | An entry the list names as a later season of a show the library doesn't have is its own show; one it maps to season 1 ("Part 2") is its own show and the show's episodes stay where they are |
| `cinemeta` | A show imported from a catalogue without native ids, later listed by a kitsu catalogue, stays one show and gains the kitsu id |
| `imdbclaim` | An OVA with the IMDb id of a show known by that id only becomes its own show and doesn't take the show; the show's season 1 entry later does |
| `imdbshared` | The IMDb-only show and the OVA sharing its IMDb id keep their own episodes through an IMDb-only import and a tree sync |
| `reattach` | A deleted film comes back while it already holds a newer row for the same keys (set up in the stopped server's database): the newer row stays, nothing is left parked, no warning |

No scenario is expected to fail, so `KNOWN` in `run.py` is empty: a failing scenario is printed
as FAIL with its failures and the run exits with 1. A scenario that has to fail until a fix lands
can be listed in `KNOWN` with that fix; it is then reported as a known failure and doesn't fail
the run, and once it passes it is reported so its entry can go.
