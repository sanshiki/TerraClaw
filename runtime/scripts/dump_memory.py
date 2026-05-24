#!/usr/bin/env python3
"""Dump memory.db contents — POIs, regions, episodes.

Usage:
    python scripts/dump_memory.py                          # default: runtime/data/memory.db
    python scripts/dump_memory.py path/to/memory.db        # custom path
    python scripts/dump_memory.py --episodes 100           # show 100 recent episodes (default 50)
    python scripts/dump_memory.py --map                    # draw ASCII map of POIs around origin
    python scripts/dump_memory.py --type chest             # filter POIs by type
"""

import argparse
import json
import sqlite3
import sys
from pathlib import Path


def find_db() -> str:
    """Guess the default DB path relative to this script."""
    script_dir = Path(__file__).resolve().parent  # runtime/scripts/
    candidates = [
        script_dir.parent / "data" / "memory.db",
        script_dir.parent / "config" / "data" / "memory.db",
        Path("data/memory.db"),
    ]
    for p in candidates:
        if p.exists():
            return str(p)
    return str(candidates[0])


def fmt_tile(pos: dict | tuple) -> str:
    if isinstance(pos, tuple):
        return f"({pos[0]}, {pos[1]})"
    return f"({pos.get('x', '?')}, {pos.get('y', '?')})"


def dump_pois(db: sqlite3.Connection, poi_type: str | None = None):
    rows = db.execute(
        """SELECT poi_type, tile_x, tile_y, world_x, world_y, label,
                  is_consumed, discovered_tick, last_visited_tick, properties
           FROM pois
           WHERE (? IS NULL OR poi_type = ?)
           ORDER BY poi_type, tile_x, tile_y""",
        (poi_type, poi_type),
    ).fetchall()

    if not rows:
        print("  (none)")
        return

    for r in rows:
        ptype, tx, ty, wx, wy, label, consumed, dtick, ltick, props = r
        consumed_mark = " [CONSUMED]" if consumed else ""
        label_str = f" \"{label}\"" if label else ""
        print(f"  {ptype}{consumed_mark} @ tile({tx}, {ty}) world({wx:.0f}, {wy:.0f}){label_str}"
              f"  discovered=tick:{dtick} last=tick:{ltick}")
        if props:
            try:
                parsed = json.loads(props)
                # Only print non-trivial props
                filtered = {k: v for k, v in parsed.items() if k not in ("pos", "type", "label")}
                if filtered:
                    print(f"    props: {json.dumps(filtered, ensure_ascii=False)}")
            except json.JSONDecodeError:
                pass


def dump_regions(db: sqlite3.Connection):
    rows = db.execute(
        """SELECT id, region_type, bounds_x1, bounds_y1, bounds_x2, bounds_y2,
                  label, discovered_tick
           FROM regions
           ORDER BY region_type, id""",
    ).fetchall()

    if not rows:
        print("  (none)")
        return

    for r in rows:
        rid, rtype, x1, y1, x2, y2, label, dtick = r
        label_str = f" \"{label}\"" if label else ""
        print(f"  [{rid}] {rtype}{label_str}  bounds=({x1},{y1})-({x2},{y2})  discovered=tick:{dtick}")


def dump_episodes(db: sqlite3.Connection, limit: int = 50, min_importance: float = 0.0):
    rows = db.execute(
        """SELECT id, tick, episode_type, summary, importance, data_json
           FROM episodes
           WHERE importance >= ?
           ORDER BY id DESC
           LIMIT ?""",
        (min_importance, limit),
    ).fetchall()

    if not rows:
        print("  (none)")
        return

    for r in reversed(rows):
        eid, tick, etype, summary, imp, data = r
        imp_mark = " *" if imp >= 0.7 else ""
        print(f"  #{eid} tick:{tick} [{etype}] (imp={imp:.1f}){imp_mark}")
        print(f"    {summary}")
        if data:
            try:
                parsed = json.loads(data)
                # Show relevant sub-fields for non-verbose mode
                if "error" in parsed or ("result" in parsed and parsed["result"].get("error")):
                    err = parsed.get("result", parsed).get("error", "")
                    print(f"    ERROR: {err}")
            except json.JSONDecodeError:
                pass


def dump_stats(db: sqlite3.Connection):
    poi_count = db.execute("SELECT COUNT(*) FROM pois").fetchone()[0]
    poi_consumed = db.execute("SELECT COUNT(*) FROM pois WHERE is_consumed = 1").fetchone()[0]
    region_count = db.execute("SELECT COUNT(*) FROM regions").fetchone()[0]
    episode_count = db.execute("SELECT COUNT(*) FROM episodes").fetchone()[0]
    episode_types = db.execute(
        "SELECT episode_type, COUNT(*) FROM episodes GROUP BY episode_type ORDER BY COUNT(*) DESC"
    ).fetchall()

    # POI type breakdown
    poi_types = db.execute(
        "SELECT poi_type, COUNT(*) FROM pois GROUP BY poi_type ORDER BY COUNT(*) DESC"
    ).fetchall()

    # Tile count (explored_tiles table, likely 0)
    tile_count = db.execute("SELECT COUNT(*) FROM explored_tiles").fetchone()[0]

    # Tick range
    tick_range = db.execute("SELECT MIN(tick), MAX(tick) FROM episodes").fetchone()

    print("── Stats ──")
    print(f"  POIs:     {poi_count} total ({poi_consumed} consumed)")
    for pt, cnt in poi_types:
        print(f"    {pt}: {cnt}")
    print(f"  Regions:  {region_count}")
    print(f"  Episodes: {episode_count} (tick range: {tick_range[0]}–{tick_range[1]})")
    for et, cnt in episode_types:
        print(f"    {et}: {cnt}")
    print(f"  Explored tiles: {tile_count} (schema only — no writes)")
    print()


def draw_ascii_map(db: sqlite3.Connection, poi_type: str | None = None):
    """Draw a simple ASCII map of POIs around their centroid."""
    rows = db.execute(
        """SELECT poi_type, tile_x, tile_y, is_consumed
           FROM pois
           WHERE (? IS NULL OR poi_type = ?)
           ORDER BY poi_type, tile_x, tile_y""",
        (poi_type, poi_type),
    ).fetchall()

    if not rows:
        print("  (no POIs to map)")
        return

    # Determine bounds
    xs = [r[1] for r in rows]
    ys = [r[2] for r in rows]
    min_x, max_x = min(xs), max(xs)
    min_y, max_y = min(ys), max(ys)

    # Clamp to a reasonable size
    W = max_x - min_x + 1
    H = max_y - min_y + 1
    if W > 120 or H > 80:
        print(f"  Map too large ({W}×{H} tiles), skipping ASCII view.")
        print(f"  Bounds: x=[{min_x}, {max_x}]  y=[{min_y}, {max_y}]")
        return

    # Symbols per POI type
    SYMBOLS = {
        "chest": "C",
        "ore_vein": "O",
        "life_crystal": "♥",
        "bed": "B",
        "teleporter": "T",
    }

    # Build grid
    grid = {}
    for r in rows:
        ptype, tx, ty, consumed = r
        if consumed:
            sym = "·"
        else:
            sym = SYMBOLS.get(ptype, "?")
        key = (tx, ty)
        # Overlay: prefer rarer symbols
        if key not in grid or grid[key] == "?" or grid[key] == "·":
            grid[key] = sym

    print(f"  Map ({W}×{H} tiles, origin at {min_x}, {min_y}):")
    print(f"  Legend: {' | '.join(f'{s}={t}' for t, s in SYMBOLS.items())}")
    print(f"         {'.':>3} = consumed")
    print()
    for dy in range(H):
        y = min_y + dy
        line = f"  {y:4} "
        for dx in range(W):
            x = min_x + dx
            line += grid.get((x, y), ".")
        print(line)


def main():
    parser = argparse.ArgumentParser(description="Dump TerraClaw memory.db contents")
    parser.add_argument("path", nargs="?", help="Path to memory.db (default: auto-detect)")
    parser.add_argument("--episodes", type=int, default=50, help="Number of recent episodes to show (default: 50)")
    parser.add_argument("--min-importance", type=float, default=0.0, help="Minimum episode importance filter")
    parser.add_argument("--type", dest="poi_type", help="Filter POIs by type (e.g. chest, ore_vein)")
    parser.add_argument("--map", action="store_true", help="Draw ASCII map of POIs")
    parser.add_argument("--no-pois", action="store_true", help="Skip POI dump")
    parser.add_argument("--no-regions", action="store_true", help="Skip regions dump")
    parser.add_argument("--no-episodes", action="store_true", help="Skip episodes dump")

    args = parser.parse_args()

    db_path = args.path or find_db()
    db_file = Path(db_path)
    if not db_file.exists():
        print(f"ERROR: Database not found at {db_path}", file=sys.stderr)
        print(f"Try: python scripts/dump_memory.py path/to/memory.db", file=sys.stderr)
        sys.exit(1)

    db = sqlite3.connect(str(db_file))
    print(f"Memory DB: {db_file.resolve()}\n")

    dump_stats(db)

    if not args.no_pois:
        print("── POIs ──")
        dump_pois(db, args.poi_type)
        print()

    if not args.no_regions:
        print("── Regions ──")
        dump_regions(db)
        print()

    if not args.no_episodes:
        print(f"── Recent Episodes (last {args.episodes}, imp≥{args.min_importance}) ──")
        dump_episodes(db, limit=args.episodes, min_importance=args.min_importance)
        print()

    if args.map:
        print("── ASCII Map ──")
        draw_ascii_map(db, args.poi_type)

    db.close()


if __name__ == "__main__":
    main()
