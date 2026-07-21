#!/usr/bin/env python3
"""Resolve which game maps to render for the SolDocs map viewer.

Source of truth: DefaultSolMapPool in Resources/Prototypes/_Sol/Maps/Pools/default.yml

Hard exclusion: never render a Starlight station map when a Sol override exists
under Resources/Maps/_Sol/Stations/{Name}.yml (same basename). Today that skips
StarlightCork / StarlightReach / StarlightSaltern in favor of Sol*.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
POOL_PATH = REPO_ROOT / "Resources/Prototypes/_Sol/Maps/Pools/default.yml"
SOL_STATIONS = REPO_ROOT / "Resources/Maps/_Sol/Stations"
STARLIGHT_STATIONS = REPO_ROOT / "Resources/Maps/_Starlight/Stations"
PROTO_ROOTS = [
    REPO_ROOT / "Resources/Prototypes/_Sol/Maps",
    REPO_ROOT / "Resources/Prototypes/_Starlight/Maps",
]


def _sol_station_basenames() -> set[str]:
    if not SOL_STATIONS.is_dir():
        return set()
    return {p.name for p in SOL_STATIONS.glob("*.yml")}


def superseded_starlight_ids() -> set[str]:
    """Prototype IDs for Starlight maps that have a Maps/_Sol/Stations twin."""
    sol_names = _sol_station_basenames()
    if not sol_names:
        return set()

    skipped: set[str] = set()
    for proto_dir in PROTO_ROOTS:
        if not proto_dir.is_dir():
            continue
        for path in proto_dir.rglob("*.yml"):
            text = path.read_text(encoding="utf-8")
            if "type: gameMap" not in text:
                continue
            mid = re.search(r"(?m)^  id:\s*(\S+)\s*$", text)
            mpath = re.search(r"(?m)^  mapPath:\s*(\S+)\s*$", text)
            if not mid or not mpath:
                continue
            proto_id = mid.group(1)
            map_path = mpath.group(1).strip().strip('"').strip("'")
            basename = Path(map_path).name
            if basename in sol_names and "/_Starlight/" in map_path.replace("\\", "/"):
                skipped.add(proto_id)
    return skipped


def pool_map_ids() -> list[str]:
    if not POOL_PATH.is_file():
        raise FileNotFoundError(f"Missing map pool: {POOL_PATH}")
    ids: list[str] = []
    for line in POOL_PATH.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if stripped.startswith("#") or not stripped.startswith("- "):
            continue
        # Only top-level pool entries (two-space indent under maps:)
        if not line.startswith("  - "):
            continue
        value = stripped[2:].strip()
        if value and not value.startswith("#"):
            ids.append(value)
    return ids


def eligible_map_ids() -> list[str]:
    skip = superseded_starlight_ids()
    return [m for m in pool_map_ids() if m not in skip]


def prototype_id_for_map_file(map_file: Path) -> str | None:
    """Best-effort: find gameMap id whose mapPath basename matches this file."""
    name = map_file.name
    rel = str(map_file).replace("\\", "/")
    for proto_dir in PROTO_ROOTS:
        if not proto_dir.is_dir():
            continue
        for path in proto_dir.rglob("*.yml"):
            text = path.read_text(encoding="utf-8")
            if "type: gameMap" not in text:
                continue
            mid = re.search(r"(?m)^  id:\s*(\S+)\s*$", text)
            mpath = re.search(r"(?m)^  mapPath:\s*(\S+)\s*$", text)
            if not mid or not mpath:
                continue
            map_path = mpath.group(1).strip().strip('"').strip("'")
            if Path(map_path).name == name and (
                name in rel or Path(map_path).name == name
            ):
                # Prefer path segment match when possible
                if map_path.endswith(name) or map_path.endswith("/" + name):
                    # Prefer Sol over Starlight when both exist for same basename
                    proto_id = mid.group(1)
                    if "/_Sol/" in map_path.replace("\\", "/") or proto_id.startswith("Sol"):
                        return proto_id
    # Second pass: any match
    for proto_dir in PROTO_ROOTS:
        if not proto_dir.is_dir():
            continue
        for path in proto_dir.rglob("*.yml"):
            text = path.read_text(encoding="utf-8")
            if "type: gameMap" not in text:
                continue
            mid = re.search(r"(?m)^  id:\s*(\S+)\s*$", text)
            mpath = re.search(r"(?m)^  mapPath:\s*(\S+)\s*$", text)
            if not mid or not mpath:
                continue
            map_path = mpath.group(1).strip().strip('"').strip("'")
            if Path(map_path).name == name:
                return mid.group(1)
    return None


def map_ids_from_changed_paths(paths: list[str]) -> list[str]:
    """Map git-changed file paths to eligible prototype IDs."""
    eligible = set(eligible_map_ids())
    skip = superseded_starlight_ids()
    found: set[str] = set()

    for raw in paths:
        p = Path(raw)
        text_path = raw.replace("\\", "/")

        # Prototype file change
        if "/Prototypes/" in text_path and text_path.endswith((".yml", ".yaml")):
            try:
                content = (REPO_ROOT / p).read_text(encoding="utf-8")
            except OSError:
                continue
            if "type: gameMap" in content:
                mid = re.search(r"(?m)^  id:\s*(\S+)\s*$", content)
                if mid and mid.group(1) in eligible:
                    found.add(mid.group(1))
            continue

        # Map YAML under Resources/Maps
        if "/Maps/" in text_path and text_path.endswith((".yml", ".yaml")):
            # Never render superseded Starlight station files
            if "/Maps/_Starlight/" in text_path:
                basename = Path(text_path).name
                if basename in _sol_station_basenames():
                    continue
            proto = prototype_id_for_map_file(Path(text_path))
            if proto and proto in eligible and proto not in skip:
                found.add(proto)

    return sorted(found)


def rebuild_list_json(map_out: Path, existing_list: Path | None, out_path: Path) -> None:
    """Build maps/list.json from rendered map.json files, merging untouched entries.

    Drops superseded Starlight ids (Sol twin under Maps/_Sol/Stations) so merge
    uploads do not keep StarlightPacked etc. alongside SolPacked.
    """
    skip = superseded_starlight_ids()
    by_id: dict[str, dict[str, str]] = {}
    if existing_list and existing_list.is_file():
        try:
            data = json.loads(existing_list.read_text(encoding="utf-8"))
            for entry in data.get("maps", []):
                if isinstance(entry, dict) and "id" in entry and "name" in entry:
                    if entry["id"] in skip:
                        continue
                    by_id[entry["id"]] = {"id": entry["id"], "name": entry["name"]}
        except (json.JSONDecodeError, OSError):
            pass

    if map_out.is_dir():
        for child in sorted(map_out.iterdir()):
            if not child.is_dir() or child.name.startswith("_"):
                continue
            mj = child / "map.json"
            if not mj.is_file():
                continue
            try:
                data = json.loads(mj.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                continue
            mid = data.get("id") or child.name
            if mid in skip:
                continue
            name = data.get("displayName") or data.get("name") or mid
            by_id[mid] = {"id": mid, "name": name}

    # Alphabetical by display name (case-insensitive)
    ordered = sorted(
        by_id.values(),
        key=lambda e: (e.get("name") or e.get("id") or "").casefold(),
    )

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps({"maps": ordered}, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_all = sub.add_parser("all", help="Print all eligible map IDs (space-separated)")
    p_all.add_argument("--json", action="store_true", help="Emit JSON array")

    p_skip = sub.add_parser("skipped", help="Print superseded Starlight IDs")

    p_changed = sub.add_parser("from-changes", help="Map changed paths to eligible IDs")
    p_changed.add_argument("paths", nargs="*", help="Changed file paths")
    p_changed.add_argument("--json", action="store_true")

    p_list = sub.add_parser("list-json", help="Rebuild maps/list.json from a render output dir")
    p_list.add_argument("--map-out", type=Path, required=True)
    p_list.add_argument("--existing", type=Path, default=None)
    p_list.add_argument("--output", type=Path, required=True)

    args = parser.parse_args()

    if args.cmd == "all":
        ids = eligible_map_ids()
        if args.json:
            print(json.dumps(ids))
        else:
            print(" ".join(ids))
        return 0

    if args.cmd == "skipped":
        print(" ".join(sorted(superseded_starlight_ids())))
        return 0

    if args.cmd == "from-changes":
        ids = map_ids_from_changed_paths(args.paths)
        if args.json:
            print(json.dumps(ids))
        else:
            print(" ".join(ids))
        return 0

    if args.cmd == "list-json":
        rebuild_list_json(args.map_out, args.existing, args.output)
        return 0

    return 1


if __name__ == "__main__":
    sys.exit(main())
