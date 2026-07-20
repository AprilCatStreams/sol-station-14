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

API_BASE = os.environ.get("PLAYSOL_API_BASE", "").rstrip("/")
DEPLOY_SECRET = os.environ.get("PLAYSOL_DEPLOY_SECRET", "")
AUTH_PATH = os.environ.get("PLAYSOL_AUTH_PATH", "").strip()
MAPS_UPLOAD_PATH = os.environ.get("PLAYSOL_MAPS_UPLOAD_PATH", "/api/v1/maps/upload")
MAPS_INIT_PATH = os.environ.get("PLAYSOL_MAPS_INIT_PATH", "/api/v1/maps/upload/init")
# Public URL to fetch existing list.json when merging (optional)
MAPS_LIST_URL = os.environ.get("PLAYSOL_MAPS_LIST_URL", "").strip()

REPO_ROOT = Path(__file__).resolve().parents[2]


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
    req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
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
    req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map-out", type=Path, required=True)
    parser.add_argument("--mode", choices=("all", "merge"), default="merge")
    args = parser.parse_args()

    if not API_BASE or not DEPLOY_SECRET:
        print("PLAYSOL_API_BASE and PLAYSOL_DEPLOY_SECRET are required", file=sys.stderr)
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
