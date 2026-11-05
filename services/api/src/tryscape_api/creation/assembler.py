"""Deterministic, bounded SceneSpec assembly for Moving Day pack 1.0.0 (technical-spec S10.3, demo-scope D06).

Rule D06.5: the internal object and route coordinates stay fixed across the supported 5-8 m room range; only the
enclosing room changes. This is constrained template assembly, not route optimisation. The same rule is implemented
in C# by `Tryscape.Content.MovingDay.MovingDayTemplate`; tests/test_creation.py compares this assembler with the
shared fixture contracts/v1/fixtures/proposals/valid_ready.json, and the Unity EditMode suite checks the C#
template against the same fixture.

The output is validated twice: here (shape via Pydantic plus `check_scene_invariants`, which fails closed) and again
by Unity's SceneValidator before the user can confirm it. Neither the assembler nor the extractor can name an asset,
path or prefab; the scene refers only to catalog IDs from the pack.
"""

from __future__ import annotations

import itertools
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from tryscape_api.contracts.models import Assumption, Intent, SceneSpec

PACK_ID = "moving_day"
PACK_VERSION = "1.0.0"
CATALOG_VERSION = "1.0.0"
SOFA = "sofa_standard"
CONSOLE = "console_standard"
SUPPORTED_PAYLOADS = frozenset({SOFA})
SUPPORTED_ROUTES = ("direct", "north_detour")
MARGIN_MM = 25

# Catalog footprints (contracts/v1/fixtures/catalog/moving_day_1.0.0.json): width (x) and depth (z) in mm at yaw 0.
_FOOTPRINTS = {SOFA: (1800, 800), CONSOLE: (600, 1000)}
_CONSOLE_POSES = {"center": (2500, 2500), "parked_south": (2500, 800)}
_SOFA_START = (1000, 2500)
_ROUTES: dict[str, tuple[str, tuple[tuple[int, int], ...]]] = {
    "direct": ("Direct approach", ((1000, 2500), (4000, 2500), (4000, 3800))),
    "north_detour": ("Northern approach", ((1000, 2500), (1000, 4000), (4000, 4000), (4000, 3800))),
}
SPEED_MM_PER_S = 250

IdFactory = Callable[[], str]


class SceneCompilerError(Exception):
    """The assembled scene broke an invariant. This is a defect in the compiler, never a user error."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


@dataclass(frozen=True)
class AssemblyIds:
    scene_id: str
    project_id: str
    sofa_id: str
    console_id: str

    @staticmethod
    def allocate(new_id: IdFactory) -> AssemblyIds:
        # Allocation order matches MovingDayTemplate.Assemble: scene, project, sofa, console.
        return AssemblyIds(new_id(), new_id(), new_id(), new_id())


def assemble(intent: Intent, assumptions: list[Assumption], ids: AssemblyIds) -> SceneSpec:
    if intent.scenario_pack_id != PACK_ID:
        raise SceneCompilerError("unsupported_pack")
    if intent.payload_catalog_id not in SUPPORTED_PAYLOADS:
        raise SceneCompilerError("unsupported_payload")
    if intent.preferred_route_id not in SUPPORTED_ROUTES:
        raise SceneCompilerError("unsupported_route")
    cx, cz = _CONSOLE_POSES[intent.obstruction_state]
    doc: dict[str, Any] = {
        "schema_version": "1.0.0",
        "scene_id": ids.scene_id,
        "project_id": ids.project_id,
        "scenario_pack_id": PACK_ID,
        "scenario_pack_version": PACK_VERSION,
        "catalog_version": CATALOG_VERSION,
        "revision": 0,
        "room": {"width_mm": intent.room_width_mm, "depth_mm": intent.room_depth_mm},
        "entities": [
            {
                "id": ids.sofa_id,
                "catalog_id": SOFA,
                "role": "payload",
                "pose": {"x_mm": _SOFA_START[0], "z_mm": _SOFA_START[1], "yaw_deg": 0},
                "editable": False,
                "label": "Delivery sofa",
            },
            {
                "id": ids.console_id,
                "catalog_id": CONSOLE,
                "role": "blocker",
                "pose": {"x_mm": cx, "z_mm": cz, "yaw_deg": 0},
                "editable": True,
                "label": "Console table",
            },
        ],
        "routes": [
            {
                "id": route_id,
                "label": label,
                "payload_entity_id": ids.sofa_id,
                "speed_mm_per_s": SPEED_MM_PER_S,
                "waypoints": [{"x_mm": x, "z_mm": z} for x, z in points],
            }
            for route_id, (label, points) in _ROUTES.items()
        ],
        "active_route_id": intent.preferred_route_id,
        "assumptions": [a.model_dump() for a in assumptions],
        "provenance": "illustrative",  # a proposal can never confirm a measurement (S10.3)
        "measurement_sources": [],
    }
    scene = SceneSpec.model_validate(doc)
    check_scene_invariants(scene)
    return scene


def check_scene_invariants(scene: SceneSpec) -> None:
    """Fail-closed subset of the S08 semantic rules needed for an assembled R0 scene; Unity revalidates all of them.

    Checks: every footprint lies inside the room; the payload start and every waypoint keep the payload's footprint
    plus the margin inside the room; segments are axis-aligned and nonzero; routes start at the payload pose and share
    a destination; the console does not overlap the payload start footprint.
    """
    width, depth = scene.room.width_mm, scene.room.depth_mm
    by_id = {e.id: e for e in scene.entities}
    payloads = [e for e in scene.entities if e.role == "payload"]
    if len(payloads) != 1:
        raise SceneCompilerError("payload_count")
    payload = payloads[0]
    pw, pd = _FOOTPRINTS[payload.catalog_id]
    for entity in scene.entities:
        fw, fd = _FOOTPRINTS.get(entity.catalog_id, (0, 0))
        if fw == 0:
            raise SceneCompilerError("unknown_catalog_id")
        x, z = entity.pose.x_mm, entity.pose.z_mm
        if 2 * x - fw < 0 or 2 * x + fw > 2 * width or 2 * z - fd < 0 or 2 * z + fd > 2 * depth:
            raise SceneCompilerError("entity_outside_room")
        if entity.role == "blocker":
            px, pz = payload.pose.x_mm, payload.pose.z_mm
            if abs(2 * x - 2 * px) < fw + pw and abs(2 * z - 2 * pz) < fd + pd:
                raise SceneCompilerError("blocker_overlaps_payload_start")

    def inside(x: int, z: int) -> bool:
        return (
            2 * x - pw - 2 * MARGIN_MM >= 0
            and 2 * x + pw + 2 * MARGIN_MM <= 2 * width
            and 2 * z - pd - 2 * MARGIN_MM >= 0
            and 2 * z + pd + 2 * MARGIN_MM <= 2 * depth
        )

    if not inside(payload.pose.x_mm, payload.pose.z_mm):
        raise SceneCompilerError("payload_start_outside_margin")
    destinations = set()
    for route in scene.routes:
        if by_id.get(route.payload_entity_id) is not payload:
            raise SceneCompilerError("route_payload")
        points = [(w.x_mm, w.z_mm) for w in route.waypoints]
        if points[0] != (payload.pose.x_mm, payload.pose.z_mm):
            raise SceneCompilerError("route_start_not_payload_pose")
        for (ax, az), (bx, bz) in itertools.pairwise(points):
            if (ax != bx) == (az != bz):
                raise SceneCompilerError("segment_not_axis_aligned_or_zero")
        if not all(inside(x, z) for x, z in points):
            raise SceneCompilerError("waypoint_outside_margin")
        destinations.add(points[-1])
    if len(destinations) != 1:
        raise SceneCompilerError("routes_different_destinations")
    if scene.active_route_id not in {r.id for r in scene.routes}:
        raise SceneCompilerError("unknown_active_route")
