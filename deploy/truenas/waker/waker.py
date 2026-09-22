"""Wake-on-LAN waker for the render PC.

Polls every tracker's render queue and wakes the PC while any job is
waiting. The render agent is pull-based, so a sleeping PC cannot discover
its own work - this loop is what summons it.

Three delivery paths, because the NAS and the PC sit on different subnets
and routers drop directed broadcasts:

- Unicast UDP to the PC's own address (PC_ADDR, the one path proven to
  land - 2026-09-22): the gateway routes it into the PC's VLAN like any
  packet. If the gateway still has the PC's MAC cached the frame is a
  magic packet addressed to the sleeping NIC; if not, the gateway's ARP
  for the PC's IP is itself a wake pattern. Either way the NIC wakes the
  box, which is why "Wake on pattern match" has to stay enabled on it.
- UniFi controller API (needs UNIFI_USER/UNIFI_PASS): asks the gateway
  to send the magic packet inside the PC's VLAN. cmd/devmgr answers
  rc:ok to any command name, so an ok proves the console accepted the
  request, not that a packet left it - the render box slept through an
  hour of these on 2026-09-22.
- Raw UDP broadcast (always attempted): only reaches the PC if the waker
  ever runs on the same L2 - kept because it is free.

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
# The PC's IP or LAN name (rjav-agent01.lan); blank = no unicast path.
PC_ADDR = os.environ.get("PC_ADDR", "").strip()
POLL_SECONDS = int(os.environ.get("POLL_SECONDS", "60"))
# The queue count is agent-only since the tracker started authorising every
# read; the waker is enrolled and approved like any machine (docs/operate.md).
AGENT_KEY = os.environ.get("TRACKER_AGENT_KEY", "")

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


def log(msg: str) -> None:
    print(time.strftime("%H:%M:%SZ", time.gmtime()), msg, flush=True)


def get_json(url: str):
    headers = {"Accept": "application/json"}
    if AGENT_KEY:
        headers["X-Agent-Key"] = AGENT_KEY
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            body = resp.read()
    except urllib.error.HTTPError as ex:
        if ex.code == 401:
            raise RuntimeError("the tracker answered 401 - TRACKER_AGENT_KEY is unset, not yet approved or revoked; "
                               "enrol and approve the waker on the Machines page") from None
        # Quote who refused and why: the tracker refuses a pending or revoked
        # key in JSON, Cloudflare refuses a poll that left the LAN in HTML,
        # and both read as a bare "HTTP Error 403" (2026-09-22).
        reply = ex.read(200).decode("utf-8", "replace").strip()
        server = ex.headers.get("Server", "")
        raise RuntimeError(f"HTTP {ex.code}{' from ' + server if server else ''}: {reply or ex.reason}") from None
    # A Cloudflare Access sign-in page (HTML, not JSON) lands here too and
    # raises - meaning DNS resolved the tracker via the internet instead of
    # the LAN's split-horizon view. The caller logs it; fix the NAS DNS.
    return json.loads(body)


def pending_jobs(base_url: str) -> list[str]:
    # One anonymous number per tracker: how many render jobs wait across every
    # account (/api/render/pending). The per-account queues are owner/agent-
    # only now that the tracker authorizes its own API, and the waker has no
    # identity - it needs one bit, not the rows.
    count = int(get_json(f"{base_url}/api/render/pending").get("pending", 0))
    return ["job"] * count


def magic_packet() -> bytes:
    return bytes.fromhex("ff" * 6 + MAC * 16)


def send_broadcast() -> None:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        s.sendto(magic_packet(), (BROADCAST, 9))


def send_unicast() -> None:
    """Route the magic packet to the PC's own address. Resolved on every
    send: a LAN name follows the lease, an IP is just parsed. Raises so the
    caller can log a dead name once instead of every poll."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.sendto(magic_packet(), (socket.gethostbyname(PC_ADDR), 9))


def unifi_call(step: str, url: str, body: dict, headers: dict):
    # Name the step and quote the console's reply: a bare "HTTP Error 404"
    # cannot say whether the login or the wake command failed, nor why.
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json", **headers})
    try:
        return urllib.request.urlopen(req, timeout=15, context=INSECURE)
    except urllib.error.HTTPError as ex:
        reply = ex.read(300).decode("utf-8", "replace").strip()
        # A 404 is the console saying it has no such command at that path -
        # not that the PC is missing. UniFi moved the wake from cmd/stamgr to
        # cmd/devmgr in 10.6, and the reply ("api.err.NotFound") reads exactly
        # like an unknown MAC, which sends the next person after the client
        # list instead of the URL.
        hint = " - no such command at that path; UniFi has moved it before (stamgr -> devmgr in 10.6), so check the manager in the URL" if ex.code == 404 else ""
        raise RuntimeError(f"{step} ({url}) answered HTTP {ex.code}: {reply or ex.reason}{hint}") from None


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
    with unifi_call("wake-device", f"{UNIFI_URL}/proxy/network/api/s/{UNIFI_SITE}/cmd/devmgr", {"cmd": "wake-device", "mac": mac}, headers) as resp:
        if resp.status != 200:
            raise RuntimeError(f"wake-device answered HTTP {resp.status}")


def main() -> None:
    if not TRACKERS:
        raise SystemExit("TRACKER_URLS is empty - nothing to watch")
    if len(MAC) != 12:
        raise SystemExit("PC_MAC is not set (or not a MAC address) - set it in the Portainer stack environment")
    if not AGENT_KEY:
        log("TRACKER_AGENT_KEY is not set - the tracker will answer 401; enrol the waker on the Machines page")
    unifi_on = bool(UNIFI_URL and UNIFI_USER and UNIFI_PASS)
    log(f"watching {len(TRACKERS)} tracker(s) (every account on each), waking {MAC} every {POLL_SECONDS}s while work waits "
        f"(unicast: {'to ' + PC_ADDR if PC_ADDR else 'OFF - set PC_ADDR; this is the path that is known to work'}; "
        f"UniFi API: {'on via ' + UNIFI_URL if unifi_on else 'OFF - set UNIFI_URL/USER/PASS'}; broadcast to {BROADCAST} does not cross subnets)")

    was_waking = False
    unreachable: set[str] = set()
    unifi_down = False
    unicast_down = False
    while True:
        jobs: list[str] = []
        answered = 0
        for url in TRACKERS:
            try:
                jobs += pending_jobs(url)
                answered += 1
            except Exception as ex:  # noqa: BLE001 - one line per outage, not per poll
                if url not in unreachable:
                    unreachable.add(url)
                    log(f"cannot read queue at {url}: {ex} (not repeated until it recovers)")
                continue
            unreachable.discard(url)

        if jobs:
            send_broadcast()
            if PC_ADDR:
                try:
                    send_unicast()
                    if unicast_down:
                        unicast_down = False
                        log("unicast wake recovered")
                except OSError as ex:
                    if not unicast_down:
                        unicast_down = True
                        log(f"unicast wake to {PC_ADDR} failed: {ex} (not repeated until it recovers)")
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
                log(f"{len(jobs)} job(s) waiting - sending wake packets")
            was_waking = True
        elif was_waking and answered:
            # An outage is not a drained queue: keep the wake state (and the
            # log) until a tracker actually answers with zero.
            log("queue drained - going quiet")
            was_waking = False

        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    main()
