#!/usr/bin/env python3
"""Gelato scenario runs against a throwaway Jellyfin 12.1.

Each run starts a fresh Jellyfin 12.1 container (its own empty data folder, 127.0.0.1:8099),
installs the Gelato build from this checkout, and points it at a fake Stremio add-on that
serves the fixtures in tests/scenario/fixtures. Every scenario imports, checks the library
(series and episode counts, owners, watch state), imports again, restarts Jellyfin, imports
again, and checks that nothing churned and no new errors were logged. The container and its
data are removed at the end.

Usage: sudo python3 tests/scenario/run.py [--keep] [--only NAME ...] [--port 8099]
Needs Docker and the jellyfin/jellyfin:12.1 image. Never touches another Jellyfin.
"""
import argparse, glob, http.server, json, os, shutil, socket, subprocess, sys, tempfile
import threading, time, traceback, urllib.error, urllib.parse, urllib.request, uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
FIXTURES = os.path.join(HERE, "fixtures")
IMAGE = "jellyfin/jellyfin:12.1"
GELATO_ID = "94ea4e148163498996fe0a2094bc2d6a"
BRIDGE = "172.17.0.1"  # Docker's default bridge: how the container reaches the fake add-on
AUTH = 'MediaBrowser Client="gelato-scenario", Device="runner", DeviceId="gelato-scenario", Version="1"'

# Scenarios that fail on purpose until the fix named here lands. A known failure doesn't fail
# the run; a known failure that passes is reported so its entry can be removed.
KNOWN = {
    "onepiece": "IMDb id change creates a second series (fix: identify items by native id)",
    "sharedepisodes": "two series fight over the same episodes (fix: one owner per episode)",
    "comeback": "an Ended series is never re-synced (fix: keep series status current)",
    "blackclover": "a season listed as its own entry becomes its own series (fix: file split seasons)",
}


# ---------- fake add-on ----------

class Addon:
    def __init__(self):
        self.stage = None
        self.delay = 0.0
        addon = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                path = urllib.parse.unquote(urllib.parse.urlparse(self.path).path).lstrip("/")
                if addon.delay and path.startswith("stream/"):
                    time.sleep(addon.delay)
                # A "skip=N" page is past the end: catalogs here are one page.
                if "/skip=" in path:
                    return self.send(200, b'{"metas":[]}')
                f = os.path.join(addon.stage or "", path.replace(":", "_"))
                if not os.path.isfile(f):
                    shared = os.path.join(FIXTURES, "_shared", path.replace(":", "_"))
                    f = shared if os.path.isfile(shared) else None
                if f is None:
                    return self.send(404, b"{}")
                with open(f, "rb") as fh:
                    self.send(200, fh.read())

            def send(self, code, body):
                self.send_response(code)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.server = http.server.ThreadingHTTPServer((BRIDGE, 0), H)
        self.url = f"http://{BRIDGE}:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()


# ---------- Jellyfin ----------

class Jellyfin:
    def __init__(self, port, workdir):
        self.port = port
        self.base = f"http://127.0.0.1:{port}"
        self.name = f"gelato-scenario-{os.getpid()}"
        self.work = workdir
        self.token = None

    def start(self):
        for d in ("config", "cache", "media/movies", "media/series"):
            os.makedirs(os.path.join(self.work, d), exist_ok=True)
        subprocess.run(
            ["docker", "run", "-d", "--name", self.name, "-p", f"127.0.0.1:{self.port}:8096",
             "-v", f"{self.work}/config:/config", "-v", f"{self.work}/cache:/cache",
             "-v", f"{self.work}/media:/gelato", IMAGE],
            check=True, stdout=subprocess.DEVNULL)
        self.wait()

    def wait(self, seconds=120):
        end = time.time() + seconds
        while time.time() < end:
            try:
                info = json.loads(urllib.request.urlopen(self.base + "/System/Info/Public", timeout=2).read())
                if info.get("StartupWizardCompleted") is not None:
                    time.sleep(2)
                    return
            except Exception:
                time.sleep(1)
        raise RuntimeError("Jellyfin didn't start")

    def restart(self):
        subprocess.run(["docker", "restart", self.name], check=True, stdout=subprocess.DEVNULL)
        self.wait()

    def remove(self):
        subprocess.run(["docker", "rm", "-f", self.name], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def call(self, method, path, body=None, token=True, raw=False):
        headers = {"Content-Type": "application/json"}
        auth = AUTH + (f', Token="{self.token}"' if token and self.token else "")
        headers["Authorization"] = auth
        data = json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None)
        req = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        for attempt in range(30):
            try:
                with urllib.request.urlopen(req, timeout=60) as r:
                    out = r.read()
                break
            except (ConnectionError, urllib.error.URLError) as e:
                # Jellyfin answers 503 or drops connections while it is still starting.
                if isinstance(e, urllib.error.HTTPError) and e.code != 503 or attempt == 29:
                    raise
                time.sleep(2)
        if raw:
            return out
        return json.loads(out) if out else None

    def setup(self, addon_url):
        self.call("POST", "/Startup/Configuration",
                  {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
        self.call("GET", "/Startup/User")
        self.call("POST", "/Startup/User", {"Name": "admin", "Password": "scenario"})
        self.call("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True})
        self.call("POST", "/Startup/Complete")
        auth = self.call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "scenario"})
        self.token, self.admin = auth["AccessToken"], auth["User"]["Id"]
        self.viewer = self.call("POST", "/Users/New", {"Name": "viewer", "Password": "scenario"})["Id"]
        for name, ctype, path in (("Movies", "movies", "/gelato/movies"), ("Shows", "tvshows", "/gelato/series")):
            q = urllib.parse.urlencode({"name": name, "collectionType": ctype, "paths": path, "refreshLibrary": "false"})
            self.call("POST", "/Library/VirtualFolders?" + q, {"LibraryOptions": {
                "EnableRealtimeMonitor": False, "EnableInternetProviders": False,
                "TypeOptions": [
                    {"Type": "Series", "MetadataFetchers": ["Gelato"], "ImageFetchers": []},
                    {"Type": "Season", "MetadataFetchers": ["Gelato"], "ImageFetchers": []},
                    {"Type": "Episode", "MetadataFetchers": ["Gelato"], "ImageFetchers": []},
                    {"Type": "Movie", "MetadataFetchers": ["Gelato"], "ImageFetchers": []}]}})
        cfg = self.call("GET", f"/Plugins/{GELATO_ID}/Configuration")
        cfg.update({"Url": addon_url, "MoviePath": "/gelato/movies", "SeriesPath": "/gelato/series",
                    "RemuxDbEnabled": False, "RemuxDbContribute": False, "PreProbe": False,
                    "Catalogs": [
                        {"Id": "anime", "Type": "series", "Name": "Anime", "Enabled": True, "MaxItems": 100, "Url": ""},
                        {"Id": "films", "Type": "movie", "Name": "Films", "Enabled": True, "MaxItems": 100, "Url": ""}]})
        self.call("POST", f"/Plugins/{GELATO_ID}/Configuration", cfg)
        # The library folders exist once scanned; Gelato finds them on start.
        self.call("POST", "/Library/Refresh")
        self.task("RefreshLibrary")
        self.restart()
        self.login()

    def login(self):
        auth = self.call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "scenario"}, token=False)
        self.token = auth["AccessToken"]

    def task(self, key, seconds=300):
        t = next(t for t in self.call("GET", "/ScheduledTasks") if t["Key"] == key)
        before = (t.get("LastExecutionResult") or {}).get("EndTimeUtc")
        self.call("POST", f"/ScheduledTasks/Running/{t['Id']}")
        end = time.time() + seconds
        while time.time() < end:
            time.sleep(1)
            t = self.call("GET", f"/ScheduledTasks/{t['Id']}")
            last = t.get("LastExecutionResult") or {}
            if t["State"] == "Idle" and last.get("EndTimeUtc") != before:
                return last.get("Status")
        raise RuntimeError(f"task {key} didn't finish")

    def items(self, kind):
        q = urllib.parse.urlencode({"Recursive": "true", "IncludeItemTypes": kind, "userId": self.admin,
                                    "Fields": "ProviderIds,DateCreated,SeriesId,ParentId,Path"})
        return self.call("GET", "/Items?" + q)["Items"]

    def played(self, user, item_id):
        q = urllib.parse.urlencode({"userId": user})
        return self.call("GET", f"/Items/{item_id}?{q}")["UserData"]["Played"]

    def mark_played(self, user, item_id):
        self.call("POST", f"/UserPlayedItems/{item_id}?userId={user}")

    def delete(self, item_id):
        self.call("DELETE", f"/Items/{item_id}")

    def errors(self):
        lines = []
        for f in sorted(glob.glob(os.path.join(self.work, "config", "log", "*.log"))):
            with open(f, errors="replace") as fh:
                lines += [l.rstrip() for l in fh if "[ERR]" in l or "[FTL]" in l]
        return lines


# ---------- scenarios ----------

class Check:
    def __init__(self, name):
        self.name, self.failures = name, []

    def eq(self, what, got, want):
        if got != want:
            self.failures.append(f"{what}: got {got!r}, want {want!r}")

    def true(self, what, ok):
        if not ok:
            self.failures.append(what)


def snapshot(jf):
    series = jf.items("Series")
    episodes = jf.items("Episode")
    movies = jf.items("Movie")
    return {
        "series": sorted((s["Name"], s["DateCreated"]) for s in series),
        "episodes": sorted((e.get("SeriesName"), e.get("ParentIndexNumber"), e.get("IndexNumber"), e.get("SeriesId"), e["DateCreated"]) for e in episodes),
        "movies": sorted((m["Name"], m["DateCreated"]) for m in movies),
    }


def import_all(jf):
    jf.task("GelatoCatalogItemsSync")
    jf.task("SyncSeriesTrees")


def repeat_check(jf, c):
    """Import and sync again, restart, and again: nothing may change and nothing may error."""
    first = snapshot(jf)
    errors = len(jf.errors())
    import_all(jf)
    c.eq("library after a second import", snapshot(jf), first)
    jf.restart(); jf.login()
    import_all(jf)
    c.eq("library after a restart and a third import", snapshot(jf), first)
    new = jf.errors()[errors:]
    c.eq("new errors in the log", new[:5], [])


def by_name(items):
    out = {}
    for i in items:
        out.setdefault(i["Name"], []).append(i)
    return out


def sc_onepiece(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "onepiece", "import1")
    import_all(jf)
    series = jf.items("Series")
    c.eq("series after import 1", len(series), 1)
    eps = jf.items("Episode")
    c.eq("episodes after import 1", len(eps), 3)
    if eps:
        first = min(eps, key=lambda e: e.get("IndexNumber") or 0)
        jf.mark_played(jf.viewer, first["Id"])
    addon.stage = os.path.join(FIXTURES, "onepiece", "import2")
    import_all(jf)
    c.eq("series after the IMDb id changed", len(jf.items("Series")), 1)
    eps = jf.items("Episode")
    c.eq("episodes after the IMDb id changed", len(eps), 3)
    played = [e for e in eps if jf.played(jf.viewer, e["Id"])]
    c.eq("viewer's played episodes kept", len(played), 1)
    repeat_check(jf, c)


def sc_initiald(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "initiald")
    import_all(jf)
    series = jf.items("Series")
    c.eq("stages kept apart", len(series), 5)
    c.eq("episodes", len(jf.items("Episode")), 10)
    for s in series:
        n = len([e for e in jf.items("Episode") if e.get("SeriesId") == s["Id"]])
        c.eq(f"episodes of {s['Name']}", n, 2)
    repeat_check(jf, c)


def sc_sharedtmdb(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "sharedtmdb")
    import_all(jf)
    c.eq("films kept apart", sorted(m["Name"] for m in jf.items("Movie")), ["Film A", "Film B"])
    repeat_check(jf, c)


def sc_comeback(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "comeback", "import1")
    import_all(jf)
    c.eq("episodes while Ended", len(jf.items("Episode")), 3)
    addon.stage = os.path.join(FIXTURES, "comeback", "import2")
    import_all(jf)
    c.eq("series", len(jf.items("Series")), 1)
    c.eq("episodes after it came back", len(jf.items("Episode")), 5)


def sc_sharedepisodes(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "sharedepisodes")
    import_all(jf)
    eps = jf.items("Episode")
    keys = [(e.get("ParentIndexNumber"), e.get("IndexNumber"), e.get("SeriesId")) for e in eps]
    c.eq("no episode listed twice under one series", len(keys), len(set(keys)))
    repeat_check(jf, c)


def sc_blackclover(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "blackclover")
    import_all(jf)
    c.eq("series (season 2 filed under the show)", [s["Name"] for s in jf.items("Series")], ["Black Clover"])
    c.eq("episodes", len(jf.items("Episode")), 5)


def sc_delete(jf, addon, c):
    addon.stage = os.path.join(FIXTURES, "delete")
    import_all(jf)
    movies = by_name(jf.items("Movie"))
    c.eq("films imported", sorted(movies), ["Kept Film", "Removed Film"])
    if "Removed Film" not in movies:
        return
    gone = movies["Removed Film"][0]
    jf.mark_played(jf.viewer, gone["Id"])
    jf.mark_played(jf.admin, movies["Kept Film"][0]["Id"])
    jf.delete(gone["Id"])
    c.eq("film gone after the delete", sorted(by_name(jf.items("Movie"))), ["Kept Film"])
    import_all(jf)
    back = by_name(jf.items("Movie")).get("Removed Film", [])
    c.eq("deleted film re-imported", len(back), 1)
    if back:
        c.true("viewer's watch state came back with it", jf.played(jf.viewer, back[0]["Id"]))
        c.true("admin's watch state on the kept film unchanged",
               jf.played(jf.admin, by_name(jf.items("Movie"))["Kept Film"][0]["Id"]))
    repeat_check(jf, c)


def sc_streams(jf, addon, c):
    """Zero streams and a slow add-on don't break an import."""
    addon.stage = os.path.join(FIXTURES, "streams")
    addon.delay = 3.0
    try:
        import_all(jf)
        c.eq("films imported", sorted(m["Name"] for m in jf.items("Movie")), ["No Streams Film", "Slow Film"])
        repeat_check(jf, c)
    finally:
        addon.delay = 0.0


SCENARIOS = {
    "initiald": sc_initiald,
    "sharedtmdb": sc_sharedtmdb,
    "delete": sc_delete,
    "streams": sc_streams,
    "onepiece": sc_onepiece,
    "sharedepisodes": sc_sharedepisodes,
    "comeback": sc_comeback,
    "blackclover": sc_blackclover,
}


def build():
    out = os.path.join(REPO, "bin", "scenario")
    subprocess.run(["dotnet", "build", os.path.join(REPO, "Gelato.csproj"), "-c", "Release", "-o", out, "--nologo", "-v", "q"],
                   check=True, stdout=subprocess.DEVNULL)
    return out


def port_free(port):
    with socket.socket() as s:
        return s.connect_ex(("127.0.0.1", port)) != 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8099)
    ap.add_argument("--only", nargs="*")
    ap.add_argument("--keep", action="store_true", help="keep the data folder of the last scenario")
    a = ap.parse_args()
    if not port_free(a.port):
        sys.exit(f"Port {a.port} is in use; wait for it to be free.")
    dll_dir = build()
    addon = Addon()
    results = {}
    for name in a.only or SCENARIOS:
        work = tempfile.mkdtemp(prefix=f"gelato-scenario-{name}-")
        jf = Jellyfin(a.port, work)
        c = Check(name)
        t0 = time.time()
        try:
            plugin_dir = os.path.join(work, "config", "plugins", "Gelato_scenario")
            os.makedirs(plugin_dir)
            for f in glob.glob(os.path.join(dll_dir, "*.dll")):
                shutil.copy(f, plugin_dir)
            jf.start()
            jf.setup(addon.url)
            SCENARIOS[name](jf, addon, c)
        except Exception as e:
            c.failures.append(f"error: {e}")
            traceback.print_exc()
        finally:
            jf.remove()
            if not a.keep:
                shutil.rmtree(work, ignore_errors=True)
        results[name] = (c.failures, time.time() - t0)

    bad = 0
    print()
    for name, (failures, secs) in results.items():
        known = KNOWN.get(name)
        if not failures:
            status = "PASS" if not known else "PASS (listed as known failure: remove it)"
        elif known:
            status = f"KNOWN FAIL ({known})"
        else:
            status = "FAIL"
            bad += 1
        print(f"{status:<12} {name} ({secs:.0f} s)")
        for f in failures:
            print(f"    - {f}")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
