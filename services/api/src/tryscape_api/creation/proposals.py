"""SceneProposal construction (technical-spec S10.3): status, clarification questions, acknowledged defaults, scene.

Rules:
- An unsupported capability gives `unsupported` with no scene; the reason is carried as an assumption with code
  `unsupported_*`.
- Missing, invalid or conflicting required information gives `needs_clarification` with at most three questions.
  Each choice is either a phrase the extractor recognises when the client resubmits it, or `Enter dimensions`, which
  the client handles by opening the hand parameter editor. Invalid dimensions are never clamped.
- Optional information that was not stated (payload, obstruction, route) uses an illustrative template default. Each
  default is listed as an assumption with `requires_confirmation: true`, so the review card shows it.
- Provenance is always `illustrative`; a proposal cannot confirm a measurement.
- Every proposal is validated against the Pydantic mirror of scene_proposal.schema.json before it is returned.
"""

from __future__ import annotations

import uuid
from collections.abc import Callable
from typing import Any

from tryscape_api.contracts.models import Assumption, Intent, Question, SceneProposal
from tryscape_api.creation import intent as ix
from tryscape_api.creation.assembler import AssemblyIds, assemble

PROPOSAL_SCHEMA_VERSION = "1.0.0"
COMPILER_VERSION = "1.0.0"

_UNSUPPORTED_TEXT = {
    "unsupported_input_kind": "Floor plans, scans, photos and files are not supported. Describe a rectangular room "
    "5 to 8 m on each side instead.",
    "unsupported_prediction": "Predicting what people will do is not supported. Participants follow the declared "
    "route only.",
    "unsupported_threshold": "Doors, hallways, stairs, vehicles and lifting are not modelled. Only the floor footprint "
    "inside one room is checked.",
    "unsupported_object": "Only one standard sofa and one console table are available in this example.",
    "unsupported_scenario": "This build supports one situation: preparing a room for a sofa delivery.",
}

_DIMENSION_PROMPTS = {
    ix.DimensionIssue.MISSING: "No room size was given. Use the 6 m by 5 m example, or enter dimensions?",
    ix.DimensionIssue.OUT_OF_RANGE: "That room size is outside the supported 5 to 8 m range. Use the 6 m by 5 m "
    "example, or enter dimensions?",
    ix.DimensionIssue.UNSUPPORTED_UNIT: "Only metres and centimetres are supported. Use the 6 m by 5 m example, or "
    "enter dimensions?",
    ix.DimensionIssue.UNIT_MISSING: "The room size needs a unit. Use the 6 m by 5 m example, or enter dimensions?",
    ix.DimensionIssue.NOT_WHOLE_MM: "Room sizes must be whole millimetres. Use the 6 m by 5 m example, or enter "
    "dimensions?",
    ix.DimensionIssue.CONFLICTING: "The room size was unclear (more than one size heard). Use the 6 m by 5 m "
    "example, or enter dimensions?",
    ix.DimensionIssue.AREA_ONLY: "Width and depth are needed, not an area. Use the 6 m by 5 m example, or enter "
    "dimensions?",
}


def _metres(mm: int) -> str:
    return f"{mm / 1000:g} m"


def _assumption(code: str, text: str, source: str, requires_confirmation: bool) -> Assumption:
    return Assumption(code=code, text=text, source=source, requires_confirmation=requires_confirmation)  # type: ignore[arg-type]


class ProposalService:
    """Builds validated proposals from an `IntentExtractor`. Pure and synchronous; I/O stays with the job worker."""

    def __init__(self, extractor: ix.IntentExtractor, new_id: Callable[[], str] | None = None) -> None:
        self._extractor = extractor
        self._new_id = new_id or (lambda: str(uuid.uuid4()))

    @property
    def provider_metadata(self) -> dict[str, str]:
        return {"provider_id": self._extractor.provider_id, "version": self._extractor.version}

    def propose(self, transcript: str) -> SceneProposal:
        extraction = self._extractor.extract(transcript)
        proposal_id = self._new_id()

        unsupported = list(extraction.unsupported)
        if not unsupported and not extraction.scenario_cue and extraction.width_mm is None:
            unsupported.append("unsupported_scenario")
        if unsupported:
            reasons = [_assumption(code, _UNSUPPORTED_TEXT[code], "scenario_model", False) for code in unsupported]
            return self._validated(proposal_id, "unsupported", transcript, None, None, [], reasons)

        questions = self._questions(extraction)
        if questions:
            return self._validated(proposal_id, "needs_clarification", transcript, None, None, questions, [])

        assert extraction.width_mm is not None  # guaranteed by _questions
        assert extraction.depth_mm is not None
        assumptions = self._assumptions(extraction)
        intent = Intent(
            scenario_pack_id="moving_day",
            room_width_mm=extraction.width_mm,
            room_depth_mm=extraction.depth_mm,
            payload_catalog_id=extraction.payload_catalog_id or "sofa_standard",
            obstruction_state=extraction.obstruction or "center",
            preferred_route_id=extraction.route or "direct",
        )
        scene = assemble(intent, assumptions, AssemblyIds.allocate(self._new_id))
        return self._validated(
            proposal_id, "ready", transcript, intent.model_dump(), scene.model_dump(), [], assumptions
        )

    @staticmethod
    def _questions(e: ix.Extraction) -> list[Question]:
        questions: list[Question] = []
        if e.dimension_issue is ix.DimensionIssue.WIDTH_MISSING:
            questions.append(
                Question(
                    field="room_width",
                    prompt=f"Room depth {_metres(e.depth_mm or 0)} heard, but no width. Use the 6 m example width, "
                    "or enter dimensions?",
                    choices=[ix.ANSWER_EXAMPLE_WIDTH, ix.ANSWER_ENTER_DIMENSIONS],
                )
                if e.depth_mm
                else Question(
                    field="room_width",
                    prompt="No room width was given. Use the 6 m example width, or enter dimensions?",
                    choices=[ix.ANSWER_EXAMPLE_WIDTH, ix.ANSWER_ENTER_DIMENSIONS],
                )
            )
        elif e.dimension_issue is ix.DimensionIssue.DEPTH_MISSING:
            questions.append(
                Question(
                    field="room_depth",
                    prompt=f"Room width {_metres(e.width_mm or 0)} heard, but no depth. Use the 5 m example depth, "
                    "or enter dimensions?",
                    choices=[ix.ANSWER_EXAMPLE_DEPTH, ix.ANSWER_ENTER_DIMENSIONS],
                )
            )
        elif e.dimension_issue is not None:
            questions.append(
                Question(
                    field="room_dimensions",
                    prompt=_DIMENSION_PROMPTS[e.dimension_issue],
                    choices=[ix.ANSWER_EXAMPLE_ROOM, ix.ANSWER_ENTER_DIMENSIONS],
                )
            )
        if e.obstruction_issue is not None:
            prompt = {
                "conflicting": "Should the console table start in the way, or parked south?",
                "unsupported_position": "The console can start only in the way or parked south. Which one?",
                "absent_requested": "This example always includes one console table. Should it start in the way, "
                "or parked south?",
            }[e.obstruction_issue]
            questions.append(
                Question(
                    field="obstruction_state",
                    prompt=prompt,
                    choices=[ix.ANSWER_CONSOLE_CENTER, ix.ANSWER_CONSOLE_SOUTH],
                )
            )
        if e.route_issue is not None:
            questions.append(
                Question(
                    field="preferred_route",
                    prompt="Both routes were mentioned. Which route should be selected first?",
                    choices=[ix.ANSWER_ROUTE_DIRECT, ix.ANSWER_ROUTE_NORTH],
                )
            )
        return questions[:3]

    @staticmethod
    def _assumptions(e: ix.Extraction) -> list[Assumption]:
        assert e.width_mm is not None
        assert e.depth_mm is not None
        out: list[Assumption] = []
        size = f"{_metres(e.width_mm)} by {_metres(e.depth_mm)}"
        if e.width_source == "example_default" and e.depth_source == "example_default":
            out.append(
                _assumption(
                    "example_room_dimensions",
                    f"Example room size {size} chosen; not a measurement of your home.",
                    "example_default",
                    False,
                )
            )
        else:
            out.append(
                _assumption(
                    "room_dimensions_from_request",
                    f"Room size {size} taken from your request; not a confirmed measurement.",
                    "user_request",
                    True,
                )
            )
            for which, source, default in (("width", e.width_source, "6 m"), ("depth", e.depth_source, "5 m")):
                if source == "example_default":
                    out.append(
                        _assumption(
                            f"example_room_{which}",
                            f"Room {which} uses the {default} example value; not a measurement.",
                            "example_default",
                            False,
                        )
                    )
        if e.unit_inferred is not None:
            unit_word = {"m": "metres", "cm": "centimetres", "mm": "millimetres"}[e.unit_inferred]
            spoken = e.spoken_dimensions[0] if e.spoken_dimensions else "the room size"
            out.append(
                _assumption(
                    "unit_interpreted",
                    f"No unit was given; '{spoken[:80]}' was read as {unit_word}.",
                    "user_request",
                    True,
                )
            )
        if e.payload_catalog_id is None:
            out.append(
                _assumption(
                    "payload_default",
                    "No item was named; using the standard sofa, the only supported item.",
                    "template",
                    True,
                )
            )
        if e.obstruction is None:
            out.append(
                _assumption(
                    "obstruction_default",
                    "Console table position not stated; placed in the way, as in the example layout.",
                    "template",
                    True,
                )
            )
        if e.route is None:
            out.append(
                _assumption("route_default", "Route not stated; the Direct approach is selected.", "template", True)
            )
        out.append(
            _assumption(
                "upright_fixed_orientation",
                "The sofa remains upright at a fixed orientation; lifting and turning are not assessed.",
                "scenario_model",
                False,
            )
        )
        return out

    def _validated(
        self,
        proposal_id: str,
        status: str,
        transcript: str,
        intent: dict[str, Any] | None,
        scene: dict[str, Any] | None,
        questions: list[Question],
        assumptions: list[Assumption],
    ) -> SceneProposal:
        return SceneProposal.model_validate(
            {
                "schema_version": PROPOSAL_SCHEMA_VERSION,
                "proposal_id": proposal_id,
                "status": status,
                "transcript": transcript,
                "intent": intent,
                "scene": scene,
                "questions": [q.model_dump() for q in questions],
                "assumptions": [a.model_dump() for a in assumptions],
                "provider_metadata": self.provider_metadata,
            }
        )
