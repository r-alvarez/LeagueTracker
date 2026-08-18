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


def pending_jobs(base_url: str) -> list[str]:
    req = urllib.request.Request(f"{base_url}/api/render/queue", headers={"Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as resp:
        body = resp.read()
    # A Cloudflare Access sign-in page (HTML, not JSON) lands here too and
    # raises - meaning DNS resolved the tracker via the internet instead of
    # the LAN's split-horizon view. The caller logs it; fix the NAS DNS.
    rows = json.loads(body)
    return [
        str(row.get("matchId") or row.get("MatchId") or "?")
        for row in rows
        if str(row.get("status") or row.get("Status") or "").lower() in WAKE_STATUSES
    ]


def send_broadcast() -> None:
    packet = bytes.fromhex("ff" * 6 + MAC * 16)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        s.sendto(packet, (BROADCAST, 9))


def send_unifi_wake() -> None:
    """Log in to the UniFi console and ask it to wake the PC. Raises on any
    failure so the caller can log the outage once instead of every poll."""
    body = json.dumps({"username": UNIFI_USER, "password": UNIFI_PASS}).encode()
    req = urllib.request.Request(
        f"{UNIFI_URL}/api/auth/login", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15, context=INSECURE) as resp:
        cookie = resp.headers.get("Set-Cookie", "").split(";")[0]
        csrf = resp.headers.get("X-CSRF-Token", "") or resp.headers.get("x-csrf-token", "")

    mac = ":".join(MAC[i:i + 2] for i in range(0, 12, 2))
    body = json.dumps({"cmd": "wake-device", "mac": mac}).encode()
    headers = {"Content-Type": "application/json", "Cookie": cookie}
    if csrf:
        headers["X-Csrf-Token"] = csrf
    req = urllib.request.Request(
        f"{UNIFI_URL}/proxy/network/api/s/{UNIFI_SITE}/cmd/stamgr", data=body, headers=headers)
    with urllib.request.urlopen(req, timeout=15, context=INSECURE) as resp:
        if resp.status != 200:
            raise RuntimeError(f"wake-device answered HTTP {resp.status}")


def main() -> None:
    if not TRACKERS:
        raise SystemExit("TRACKER_URLS is empty - nothing to watch")
    if len(MAC) != 12:
        raise SystemExit("PC_MAC is not set (or not a MAC address) - set it in the Portainer stack environment")
    unifi_on = bool(UNIFI_URL and UNIFI_USER and UNIFI_PASS)
    log(f"watching {len(TRACKERS)} tracker(s), waking {MAC} every {POLL_SECONDS}s while work waits "
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
