#!/usr/bin/env python3
"""Zip rendered map images and upload them directly to PlaySol (/api/v1/maps).

Uses the same deploy-secret → JWT flow as changelog/content uploads.
Does not touch the SolDocs-content git repo.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]


def _env(*names: str, default: str = "") -> str:
    for name in names:
        v = os.environ.get(name)
        if v is not None and str(v).strip() != "":
            return str(v).strip()
    return default


def load_config() -> tuple[str, str, str, str, str, str]:
    """Resolve PlaySol upload config (PLAYSOL_* preferred; SolDocs DEPLOY_* aliases accepted)."""
    api_base = _env("PLAYSOL_API_BASE", "API_BASE").rstrip("/")
    deploy_secret = _env("PLAYSOL_DEPLOY_SECRET", "DEPLOY_SECRET")
    auth_path = _env("PLAYSOL_AUTH_PATH", "DEPLOY_AUTH_PATH")
    maps_upload = _env("PLAYSOL_MAPS_UPLOAD_PATH", default="/api/v1/maps/upload")
    maps_init = _env("PLAYSOL_MAPS_INIT_PATH", default="/api/v1/maps/upload/init")
    maps_list = _env("PLAYSOL_MAPS_LIST_URL")
    return api_base, deploy_secret, auth_path, maps_upload, maps_init, maps_list


# Populated in main() so tests/imports don't freeze empty values.
API_BASE = ""
DEPLOY_SECRET = ""
AUTH_PATH = ""
MAPS_UPLOAD_PATH = "/api/v1/maps/upload"
MAPS_INIT_PATH = "/api/v1/maps/upload/init"
MAPS_LIST_URL = ""


def _norm_path(p: str) -> str:
    p = p.strip()
    if not p.startswith("/"):
        p = "/" + p
    return p


def http_json(
    method: str,
    url: str,
    data: bytes | None = None,
    headers: dict | None = None,
    timeout: int = 120,
):
    hdrs = {
        "User-Agent": "SolMapRenderer/1.0 (+https://playsol.us)",
        **(headers or {}),
    }
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = resp.read()
        if not body:
            return {}
        return json.loads(body.decode())


def http_bytes(
    method: str,
    url: str,
    data: bytes | None = None,
    headers: dict | None = None,
    timeout: int = 300,
) -> bytes:
    hdrs = {
        "User-Agent": "SolMapRenderer/1.0 (+https://playsol.us)",
        **(headers or {}),
    }
    req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def mint_token() -> str:
    if not AUTH_PATH:
        raise SystemExit("PLAYSOL_AUTH_PATH is required")
    auth = _norm_path(AUTH_PATH)
    token_resp = http_json(
        "POST",
        f"{API_BASE}{auth}",
        data=json.dumps({"scope": ["maps"]}).encode(),
        headers={
            "Authorization": f"Bearer {DEPLOY_SECRET}",
            "Content-Type": "application/json",
        },
    )
    return token_resp["token"]


def fetch_existing_list(dest: Path) -> None:
    url = MAPS_LIST_URL or f"{API_BASE}/maps/list.json"
    try:
        raw = http_bytes("GET", url, timeout=60)
        dest.write_bytes(raw)
        print(f"Fetched existing list from {url}")
    except Exception as e:
        print(f"No existing list.json ({e}); starting fresh")


def build_list_json(map_out: Path, mode: str) -> None:
    existing = map_out / "_existing_list.json"
    if mode == "merge":
        fetch_existing_list(existing)
    cmd = [
        sys.executable,
        str(REPO_ROOT / "Tools/_Sol/map_viewer_maps.py"),
        "list-json",
        "--map-out",
        str(map_out),
        "--output",
        str(map_out / "list.json"),
    ]
    if mode == "merge" and existing.is_file():
        cmd.extend(["--existing", str(existing)])
    subprocess.check_call(cmd)


def zip_maps(map_out: Path, zip_path: Path) -> None:
    # Zip contents of map-out as the maps/ root (list.json, folders, _parallax)
    subprocess.check_call(
        ["zip", "-r", "-q", str(zip_path), "."],
        cwd=map_out,
    )


def upload_zip(token: str, zip_path: Path, mode: str) -> None:
    upload = _norm_path(MAPS_UPLOAD_PATH)
    init = _norm_path(MAPS_INIT_PATH)
    zip_bytes = zip_path.read_bytes()
    size = len(zip_bytes)
    sha = hashlib.sha256(zip_bytes).hexdigest()
    print(f"size={size} sha256={sha} mode={mode}")

    api_mode = "replace" if mode == "all" else "merge"
    limit = 90 * 1024 * 1024

    if size <= limit:
        http_json(
            "POST",
            f"{API_BASE}{upload}",
            data=zip_bytes,
            headers={
                "Authorization": f"Bearer {token}",
                "Content-Type": "application/zip",
                "X-Content-SHA256": sha,
                "X-Maps-Mode": api_mode,
            },
            timeout=300,
        )
        print("Upload complete (single request).")
        return

    init_resp = http_json(
        "POST",
        f"{API_BASE}{init}",
        data=json.dumps({
            "filename": "maps.zip",
            "size": size,
            "sha256": sha,
            "mode": api_mode,
        }).encode(),
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
        },
    )
    upload_id = init_resp["uploadId"]
    chunk_size = int(init_resp["chunkSize"])
    chunk_base = upload.rstrip("/")

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        # split into parts
        subprocess.check_call(
            ["split", "-b", str(chunk_size), "-d", "-a", "4", str(zip_path), str(tmp_path / "part-")]
        )
        parts = sorted(tmp_path.glob("part-*"))
        for index, part in enumerate(parts):
            data = part.read_bytes()
            http_bytes(
                "PUT",
                f"{API_BASE}{chunk_base}/{upload_id}/chunks/{index}",
                data=data,
                headers={
                    "Authorization": f"Bearer {token}",
                    "Content-Type": "application/octet-stream",
                },
                timeout=300,
            )
            print(f"  chunk {index}/{len(parts) - 1} ({len(data)} bytes)")

    http_json(
        "POST",
        f"{API_BASE}{chunk_base}/{upload_id}/complete",
        headers={"Authorization": f"Bearer {token}"},
        timeout=300,
    )
    print("Upload complete (chunked).")


def main() -> int:
    global API_BASE, DEPLOY_SECRET, AUTH_PATH, MAPS_UPLOAD_PATH, MAPS_INIT_PATH, MAPS_LIST_URL

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map-out", type=Path, required=True)
    parser.add_argument("--mode", choices=("all", "merge"), default="merge")
    args = parser.parse_args()

    API_BASE, DEPLOY_SECRET, AUTH_PATH, MAPS_UPLOAD_PATH, MAPS_INIT_PATH, MAPS_LIST_URL = (
        load_config()
    )

    missing: list[str] = []
    if not API_BASE:
        missing.append("PLAYSOL_API_BASE")
    if not DEPLOY_SECRET:
        missing.append("PLAYSOL_DEPLOY_SECRET (or DEPLOY_SECRET)")
    if not AUTH_PATH:
        missing.append("PLAYSOL_AUTH_PATH (or DEPLOY_AUTH_PATH)")
    if missing:
        print(
            "Missing required env: "
            + ", ".join(missing)
            + "\nNote: CI must set these as GitHub Actions secrets on the workflow's "
            "`environment` (this workflow uses `prod`). Local SolDocs `.env` uses "
            "DEPLOY_SECRET / DEPLOY_AUTH_PATH — those aliases are accepted, but "
            "PLAYSOL_API_BASE must still be set (e.g. https://playsol.us).",
            file=sys.stderr,
        )
        return 1
    if not args.map_out.is_dir():
        print(f"Missing map-out directory: {args.map_out}", file=sys.stderr)
        return 1

    build_list_json(args.map_out, args.mode)

    with tempfile.TemporaryDirectory() as tmp:
        zip_path = Path(tmp) / "maps.zip"
        zip_maps(args.map_out, zip_path)
        try:
            token = mint_token()
            upload_zip(token, zip_path, args.mode)
        except urllib.error.HTTPError as e:
            print(e.read().decode(errors="replace"), file=sys.stderr)
            raise

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
