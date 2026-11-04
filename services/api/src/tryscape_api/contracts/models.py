"""Pydantic mirrors of contracts/v1 (shape layer only). Parity with the shared JSON Schemas is enforced by
tests/test_contract_parity.py using the shared fixtures; semantic scene rules belong to Tryscape.Core and the
backend scene compiler (M3)."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, model_validator

Uuid = Annotated[str, StringConstraints(pattern=r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
Slug = Annotated[str, StringConstraints(pattern=r"^[a-z][a-z0-9_]{0,47}$")]
SemVer = Annotated[str, StringConstraints(pattern=r"^(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})$")]
_PLAIN = r"^[^\x00-\x1f\x7f]+$"
Label = Annotated[str, StringConstraints(min_length=1, max_length=80, pattern=_PLAIN)]
CoordinateMm = Annotated[int, Field(ge=0, le=8000)]
RoomMm = Annotated[int, Field(ge=5000, le=8000)]
Yaw = Literal[0, 90, 180, 270]
UtcTimestamp = Annotated[
    str, StringConstraints(max_length=40, pattern=r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$")
]


class Contract(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, frozen=True)


class Assumption(Contract):
    code: Slug
    text: Annotated[str, StringConstraints(min_length=1, max_length=240, pattern=_PLAIN)]
    source: Literal["template", "scenario_model", "user_request", "example_default"]
    requires_confirmation: bool


class Room(Contract):
    width_mm: RoomMm
    depth_mm: RoomMm


class Pose(Contract):
    x_mm: CoordinateMm
    z_mm: CoordinateMm
    yaw_deg: Yaw


class EntitySpec(Contract):
    id: Uuid
    catalog_id: Slug
    role: Literal["payload", "blocker", "decoration"]
    pose: Pose
    editable: bool
    label: Label


class Waypoint(Contract):
    x_mm: CoordinateMm
    z_mm: CoordinateMm


class RouteSpec(Contract):
    id: Slug
    label: Label
    payload_entity_id: Uuid
    speed_mm_per_s: Annotated[int, Field(ge=100, le=500)]
    waypoints: Annotated[list[Waypoint], Field(min_length=2, max_length=12)]


class MeasurementSource(Contract):
    field_path: Literal["room.width_mm", "room.depth_mm"]
    source: Literal["user_confirmed"]
    confirmed_at: UtcTimestamp


class SceneSpec(Contract):
    schema_version: Literal["1.0.0"]
    scene_id: Uuid
    project_id: Uuid
    scenario_pack_id: Literal["moving_day"]
    scenario_pack_version: SemVer
    catalog_version: SemVer
    revision: Annotated[int, Field(ge=0, le=2147483647)]
    room: Room
    entities: Annotated[list[EntitySpec], Field(min_length=1, max_length=24)]
    routes: Annotated[list[RouteSpec], Field(min_length=1, max_length=3)]
    active_route_id: Slug
    assumptions: Annotated[list[Assumption], Field(min_length=1, max_length=16)]
    provenance: Literal["illustrative", "user_measured"]
    measurement_sources: Annotated[list[MeasurementSource], Field(max_length=8)]


class Intent(Contract):
    scenario_pack_id: Literal["moving_day"]
    room_width_mm: RoomMm
    room_depth_mm: RoomMm
    payload_catalog_id: Slug
    obstruction_state: Literal["center", "parked_south"]
    preferred_route_id: Slug


class Question(Contract):
    field: Slug
    prompt: Annotated[str, StringConstraints(min_length=1, max_length=160, pattern=_PLAIN)]
    choices: Annotated[list[Label], Field(min_length=2, max_length=4)]


class ProviderMetadata(Contract):
    provider_id: Slug
    version: SemVer


class SceneProposal(Contract):
    schema_version: Literal["1.0.0"]
    proposal_id: Uuid
    status: Literal["ready", "needs_clarification", "unsupported"]
    transcript: Annotated[str, StringConstraints(max_length=2000)]
    intent: Intent | None
    scene: SceneSpec | None
    questions: Annotated[list[Question], Field(max_length=3)]
    assumptions: Annotated[list[Assumption], Field(max_length=16)]
    provider_metadata: ProviderMetadata

    @model_validator(mode="after")
    def _status_rules(self) -> SceneProposal:
        if self.status == "ready":
            if self.intent is None or self.scene is None or self.questions:
                raise ValueError("ready proposal requires intent, scene and no open questions")
        elif self.scene is not None:
            raise ValueError("non-ready proposal must not carry a scene")
        if self.status == "needs_clarification" and not self.questions:
            raise ValueError("needs_clarification requires at least one question")
        return self


class SceneProposalJobRequest(Contract):
    schema_version: Literal["1.0.0"]
    transcript: Annotated[str, StringConstraints(min_length=1, max_length=2000)]
    locale: Literal["en"]
    scenario_pack_id: Literal["moving_day"]
    catalog_version: SemVer
