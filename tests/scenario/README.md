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

The last four fail today and are listed in `KNOWN` in `run.py` with the fix each waits for. A
known failure doesn't fail the run; one that starts passing is reported so its entry can go.
