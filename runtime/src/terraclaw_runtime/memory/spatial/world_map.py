"""Spatial memory — grid-based world map of explored tiles and points of interest."""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from typing import Any


@dataclass
class POI:
    """Point of interest on the world map."""
    poi_id: int = 0
    poi_type: str = ""          # "chest", "ore_vein", "life_crystal", "bed", "teleporter"
    tile_x: int = 0
    tile_y: int = 0
    world_x: float = 0.0
    world_y: float = 0.0
    label: str = ""
    properties: dict[str, Any] | None = None
    is_consumed: bool = False
    discovered_tick: int = 0
    last_visited_tick: int = 0


class SpatialMemory:
    """Maintains a persistent map of the explored world."""

    def __init__(self, db_path: str, grid_resolution: int = 1):
        self._db = sqlite3.connect(db_path, check_same_thread=False)
        self._grid_resolution = grid_resolution
        self._create_tables()

    def _create_tables(self) -> None:
        self._db.executescript("""
            CREATE TABLE IF NOT EXISTS explored_tiles (
                tile_x INTEGER,
                tile_y INTEGER,
                tile_type TEXT,
                wall_type TEXT,
                liquid_type TEXT,
                liquid_amount INTEGER,
                is_interesting INTEGER DEFAULT 0,
                last_observed_tick INTEGER,
                PRIMARY KEY (tile_x, tile_y)
            );

            CREATE TABLE IF NOT EXISTS regions (
                id TEXT PRIMARY KEY,
                region_type TEXT,
                bounds_x1 INTEGER, bounds_y1 INTEGER,
                bounds_x2 INTEGER, bounds_y2 INTEGER,
                label TEXT,
                properties TEXT,
                discovered_tick INTEGER
            );

            CREATE TABLE IF NOT EXISTS pois (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                poi_type TEXT,
                tile_x INTEGER, tile_y INTEGER,
                world_x REAL, world_y REAL,
                label TEXT,
                properties TEXT,
                is_consumed INTEGER DEFAULT 0,
                discovered_tick INTEGER,
                last_visited_tick INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_explored_tiles_xy ON explored_tiles(tile_x, tile_y);
            CREATE INDEX IF NOT EXISTS idx_pois_type ON pois(poi_type);
            CREATE INDEX IF NOT EXISTS idx_pois_consumed ON pois(is_consumed);
        """)
        # Remove duplicates before creating unique index (may exist from earlier versions)
        self._db.execute("""
            DELETE FROM pois WHERE id NOT IN (
                SELECT MIN(id) FROM pois GROUP BY poi_type, tile_x, tile_y
            )
        """)
        self._db.execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS idx_pois_xy ON pois(poi_type, tile_x, tile_y)
        """)
        self._db.commit()

    def update_from_observation(self, obs_tick: int, player_tile_x: int, player_tile_y: int,
                                spatial_window: dict[str, Any]) -> None:
        """Update the spatial map from an observation's spatial window."""
        # Process interesting tiles as POIs
        interesting = spatial_window.get("tiles", {}).get("interesting", [])
        for tile in interesting:
            tx = tile.get("pos", {}).get("x", 0)
            ty = tile.get("pos", {}).get("y", 0)
            ttype = tile.get("type", "unknown")

            self._db.execute(
                """INSERT INTO pois
                   (poi_type, tile_x, tile_y, world_x, world_y, label, properties, discovered_tick, last_visited_tick)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                   ON CONFLICT(poi_type, tile_x, tile_y) DO UPDATE SET
                     last_visited_tick = excluded.last_visited_tick,
                     label = excluded.label""",
                (ttype, tx, ty, tx * 16.0, ty * 16.0,
                 tile.get("label", ""), str(tile),
                 obs_tick, obs_tick),
            )

        self._db.commit()

    def query_nearby(self, tile_x: int, tile_y: int, radius_tiles: int = 50) -> str:
        """Get a compact text summary of what's near the given tile position.

        Groups same-type POIs together with a count and bounding range to avoid
        flooding the LLM prompt with one line per tile.
        """
        pois = self._db.execute(
            """SELECT poi_type, tile_x, tile_y, label, is_consumed
               FROM pois
               WHERE tile_x BETWEEN ? AND ?
                 AND tile_y BETWEEN ? AND ?
                 AND is_consumed = 0
               ORDER BY poi_type, tile_x, tile_y""",
            (tile_x - radius_tiles, tile_x + radius_tiles,
             tile_y - radius_tiles, tile_y + radius_tiles),
        ).fetchall()

        if not pois:
            return "No known points of interest nearby."

        # Group consecutive same-type POIs
        groups: list[tuple[str, int, int, int, int, int]] = []  # type, count, min_x, max_x, min_y, max_y
        for p in pois:
            ptype = p[0]
            px, py = p[1], p[2]
            if groups and groups[-1][0] == ptype:
                prev = groups[-1]
                groups[-1] = (ptype, prev[1] + 1, min(prev[2], px), max(prev[3], px),
                              min(prev[4], py), max(prev[5], py))
            else:
                groups.append((ptype, 1, px, px, py, py))

        lines = [f"POIs within {radius_tiles} tiles of ({tile_x}, {tile_y}):"]
        for gtype, gcount, gx1, gx2, gy1, gy2 in groups[:15]:
            if gcount == 1:
                lines.append(f"  {gtype} at ({gx1}, {gy1})")
            elif gx1 == gx2 and gy1 == gy2:
                lines.append(f"  {gtype} ×{gcount} at ({gx1}, {gy1})")
            else:
                lines.append(f"  {gtype} ×{gcount} tiles ({gx1}-{gx2}, {gy1}-{gy2})")

        remaining = len(pois) - sum(g[1] for g in groups[:15])
        if remaining > 0:
            lines.append(f"  ... and {remaining} more POIs")
        return "\n".join(lines)

    def get_known_pois(self, poi_type: str | None = None, unvisited_only: bool = False) -> list[POI]:
        """Get all known points of interest, optionally filtered."""
        query = "SELECT * FROM pois WHERE 1=1"
        params: list[Any] = []
        if poi_type:
            query += " AND poi_type = ?"
            params.append(poi_type)
        if unvisited_only:
            query += " AND is_consumed = 0"

        rows = self._db.execute(query, params).fetchall()
        return [
            POI(
                poi_id=r[0], poi_type=r[1], tile_x=r[2], tile_y=r[3],
                world_x=r[4], world_y=r[5], label=r[6],
                properties=__import__('json').loads(r[7]) if r[7] else None,
                is_consumed=bool(r[8]), discovered_tick=r[9], last_visited_tick=r[10],
            )
            for r in rows
        ]

    def mark_poi_consumed(self, tile_x: int, tile_y: int) -> None:
        self._db.execute(
            "UPDATE pois SET is_consumed = 1 WHERE tile_x = ? AND tile_y = ?",
            (tile_x, tile_y),
        )
        self._db.commit()

    def add_region(self, region_id: str, region_type: str, bounds: tuple[int, int, int, int],
                   label: str = "", properties: dict | None = None) -> None:
        x1, y1, x2, y2 = bounds
        self._db.execute(
            """INSERT OR REPLACE INTO regions
               (id, region_type, bounds_x1, bounds_y1, bounds_x2, bounds_y2, label, properties, discovered_tick)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (region_id, region_type, x1, y1, x2, y2, label,
             __import__('json').dumps(properties or {}),
             int(__import__('time').time())),
        )
        self._db.commit()

    def close(self) -> None:
        self._db.close()
