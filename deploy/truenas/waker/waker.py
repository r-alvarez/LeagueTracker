"""Wake-on-LAN waker for the render PC.

Polls every tracker's render queue and wakes the PC while any job is
waiting. The render agent is pull-based, so a sleeping PC cannot discover
its own work - this loop is what summons it.

Two delivery paths, because the NAS and the PC sit on different subnets
and routers drop directed broadcasts:

- UniFi controller API (primary, needs UNIFI_USER/UNIFI_PASS): asks the
  gateway to send the magic packet, which originates INSIDE the PC's VLAN
  and always lands.
- Raw UDP broadcast (secondary, always attempted): only reaches the PC if
  the waker ever runs on the same L2 - kept because it is free and the
  UniFi path has a credential that can expire.

Magic packets are harmless no-ops on a machine that is already awake, so
no "is it awake" probe is needed (Windows firewalls routinely eat ICMP,
making ping a liar).
"""

import json
import os
import socket
import ssl
import time
import urllib.error
import urllib.request

TRACKERS = [u.strip().rstrip("/") for u in os.environ.get("TRACKER_URLS", "").split(",") if u.strip()]
MAC = os.environ.get("PC_MAC", "").replace(":", "").replace("-", "").lower()
BROADCAST = os.environ.get("WOL_BROADCAST", "255.255.255.255")
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "60"))

UNIFI_URL = os.environ.get("UNIFI_URL", "").rstrip("/")
UNIFI_USER = os.environ.get("UNIFI_USER", "")
UNIFI_PASS = os.environ.get("UNIFI_PASS", "")
UNIFI_SITE = os.environ.get("UNIFI_SITE", "default")

# The console's cert is self-signed for its LAN IP.
INSECURE = ssl.create_default_context()
INSECURE.check_hostname = False
INSECURE.verify_mode = ssl.CERT_NONE

# "rendering" means the PC holds a lease, so it is awake and working;
# "done"/"failed"/"no-events" need nothing. Only these two mean idle work.
WAKE_STATUSES = {"pending", "partial"}


def log(msg: str) -> None:
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def get_json(url: str):
    req = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        body = resp.read()
    # A Cloudflare Access sign-in page (HTML, not JSON) lands here too and
    # raises - meaning DNS resolved the tracker via the internet instead of
    # the LAN's split-horizon view. The caller logs it; fix the NAS DNS.
    return json.loads(body)


def pending_jobs(base_url: str) -> list[str]:
    # One tracker hosts every player's account; the render queue is
    # per-account and resolves the account from the URL path, so ask the
    # tracker which accounts it has and read each queue on its canonical
    # path. /api/accounts is the site's own (key-less) list - the agent
    # variant needs an enrolled key the waker doesn't have. Reading
    # /api/render/queue on the bare host would only see the default account.
    accounts = get_json(f"{base_url}/api/accounts")
    paths = [
        str(a.get("path") or a.get("Path") or "")
        for a in (accounts.get("accounts") or accounts.get("Accounts") or [])
    ]
    jobs: list[str] = []
    for path in paths:
        if not path:
            continue
        rows = get_json(f"{base_url}/api/a/{path}/render/queue")
        jobs += [
            str(row.get("matchId") or row.get("MatchId") or "?")
            for row in rows
            if str(row.get("status") or row.get("Status") or "").lower() in WAKE_STATUSES
        ]
    return jobs


def send_broadcast() -> None:
    packet = bytes.fromhex("ff" * 6 + MAC * 16)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        s.sendto(packet, (BROADCAST, 9))


def unifi_call(step: str, url: str, body: dict, headers: dict):
    # Name the step and quote the console's reply: a bare "HTTP Error 404"
    # cannot say whether the login or the wake command failed, nor why.
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json", **headers})
    try:
        return urllib.request.urlopen(req, timeout=15, context=INSECURE)
    except urllib.error.HTTPError as ex:
        reply = ex.read(300).decode("utf-8", "replace").strip()
        raise RuntimeError(f"{step} ({url}) answered HTTP {ex.code}: {reply or ex.reason}") from None


def send_unifi_wake() -> None:
    """Log in to the UniFi console and ask it to wake the PC. Raises on any
    failure so the caller can log the outage once instead of every poll."""
    with unifi_call("login", f"{UNIFI_URL}/api/auth/login", {"username": UNIFI_USER, "password": UNIFI_PASS}, {}) as resp:
        cookie = resp.headers.get("Set-Cookie", "").split(";")[0]
        csrf = resp.headers.get("X-CSRF-Token", "") or resp.headers.get("x-csrf-token", "")

    mac = ":".join(MAC[i:i + 2] for i in range(0, 12, 2))
    headers = {"Cookie": cookie}
    if csrf:
        headers["X-Csrf-Token"] = csrf
    with unifi_call("wake-device", f"{UNIFI_URL}/proxy/network/api/s/{UNIFI_SITE}/cmd/stamgr", {"cmd": "wake-device", "mac": mac}, headers) as resp:
        if resp.status != 200:
            raise RuntimeError(f"wake-device answered HTTP {resp.status}")


def main() -> None:
    if not TRACKERS:
        raise SystemExit("TRACKER_URLS is empty - nothing to watch")
    if len(MAC) != 12:
        raise SystemExit("PC_MAC is not set (or not a MAC address) - set it in the Portainer stack environment")
    unifi_on = bool(UNIFI_URL and UNIFI_USER and UNIFI_PASS)
    log(f"watching {len(TRACKERS)} tracker(s) (every account on each), waking {MAC} every {POLL_SECONDS}s while work waits "
        f"(UniFi API: {'on via ' + UNIFI_URL if unifi_on else 'OFF - set UNIFI_URL/USER/PASS; broadcast alone does not cross subnets'})")

    was_waking = False
    unreachable: set[str] = set()
    unifi_down = False
    while True:
        jobs: list[str] = []
        for url in TRACKERS:
            try:
                jobs += pending_jobs(url)
            except Exception as ex:  # noqa: BLE001 - one line per outage, not per poll
                if url not in unreachable:
                    unreachable.add(url)
                    log(f"cannot read queue at {url}: {ex} (not repeated until it recovers)")
                continue
            unreachable.discard(url)

        if jobs:
            send_broadcast()
            if unifi_on:
                try:
                    send_unifi_wake()
                    if unifi_down:
                        unifi_down = False
                        log("UniFi wake recovered")
                except Exception as ex:  # noqa: BLE001
                    if not unifi_down:
                        unifi_down = True
                        log(f"UniFi wake failed: {ex} (not repeated until it recovers)")
            if not was_waking:
                log(f"{len(jobs)} job(s) waiting ({', '.join(sorted(set(jobs))[:5])}) - sending wake packets")
            was_waking = True
        elif was_waking:
            log("queue drained - going quiet")
            was_waking = False

        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
