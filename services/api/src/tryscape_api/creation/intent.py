"""Intent extraction behind a provider-neutral interface (technical-spec S10.2/S10.3).

`IntentExtractor` turns an untrusted transcript into an `Extraction`: a bounded, typed record of what was
understood. It never returns a scene, code, asset reference or file path. Proposal status, questions and scene
assembly are decided afterwards by `proposals.ProposalService`, so a future model-assisted extractor slots in
behind the same interface and is constrained by the same validation.

`DeterministicIntentExtractor` is the development implementation selected for this milestone, because no paid
model provider has been approved (B-provider-budget). It is a rule-based parser for the Moving Day 1.0.0
vocabulary. It is NOT model-assisted interpretation and does not satisfy acceptance case AC02. Its provider
metadata (`deterministic_rules`) says so on every proposal.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass, field
from decimal import Decimal, InvalidOperation
from enum import StrEnum
from typing import Literal, Protocol

MIN_ROOM_MM = 5000
MAX_ROOM_MM = 8000


class DimensionIssue(StrEnum):
    MISSING = "missing"
    WIDTH_MISSING = "width_missing"
    DEPTH_MISSING = "depth_missing"
    OUT_OF_RANGE = "out_of_range"
    UNSUPPORTED_UNIT = "unsupported_unit"
    UNIT_MISSING = "unit_missing"
    NOT_WHOLE_MM = "not_whole_mm"
    CONFLICTING = "conflicting"
    AREA_ONLY = "area_only"


ObstructionState = Literal["center", "parked_south"]
RouteId = Literal["direct", "north_detour"]
ValueSource = Literal["user_request", "example_default"]


@dataclass(frozen=True)
class Extraction:
    """What an extractor understood. `None` means "not stated"; conflicts are flagged, never resolved by guessing."""

    unsupported: tuple[str, ...] = ()
    scenario_cue: bool = False
    width_mm: int | None = None
    depth_mm: int | None = None
    width_source: ValueSource | None = None
    depth_source: ValueSource | None = None
    dimension_issue: DimensionIssue | None = None
    unit_inferred: Literal["m", "cm", "mm"] | None = None
    spoken_dimensions: tuple[str, ...] = ()
    payload_catalog_id: str | None = None
    obstruction: ObstructionState | None = None
    obstruction_issue: Literal["conflicting", "unsupported_position", "absent_requested"] | None = None
    route: RouteId | None = None
    route_issue: Literal["conflicting"] | None = None
    notes: tuple[str, ...] = field(default=())


class IntentExtractor(Protocol):
    """Provider-neutral interface. Implementations must be side-effect free and bounded in time and output."""

    @property
    def provider_id(self) -> str: ...

    @property
    def version(self) -> str: ...

    def extract(self, transcript: str) -> Extraction: ...


# ---- Deterministic development extractor ------------------------------------------------------------------------

_SMALL = {
    "zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9,
    "ten": 10, "eleven": 11, "twelve": 12, "thirteen": 13, "fourteen": 14, "fifteen": 15, "sixteen": 16,
    "seventeen": 17, "eighteen": 18, "nineteen": 19,
}  # fmt: skip
_TENS = {"twenty": 20, "thirty": 30, "forty": 40, "fifty": 50, "sixty": 60, "seventy": 70, "eighty": 80, "ninety": 90}
_DIGITS = {k: v for k, v in _SMALL.items() if v < 10} | {"oh": 0}

_UNITS: dict[str, str] = {
    **dict.fromkeys(("m", "metre", "metres", "meter", "meters", "metres", "mtr", "mtrs"), "m"),
    **dict.fromkeys(("cm", "centimetre", "centimetres", "centimeter", "centimeters", "cms"), "cm"),
    **dict.fromkeys(("mm", "millimetre", "millimetres", "millimeter", "millimeters"), "mm"),
    **dict.fromkeys(("ft", "foot", "feet", "inch", "inches", "yard", "yards", "yd"), "unsupported"),
}
_SCALE = {"m": Decimal(1000), "cm": Decimal(10), "mm": Decimal(1)}
_BY = {"by", "x", "times", "\N{MULTIPLICATION SIGN}", "*"}
_WIDTH_WORDS = {"wide", "width", "across"}
_DEPTH_WORDS = {"deep", "depth", "long", "length"}

# Clarification answers. The client appends the chosen label to the transcript and resubmits, so each choice label
# offered in a question must be recognised here and take precedence over the original wording.
ANSWER_EXAMPLE_ROOM = "Use 6 m by 5 m example"
ANSWER_EXAMPLE_DEPTH = "Use 5 m example depth"
ANSWER_EXAMPLE_WIDTH = "Use 6 m example width"
ANSWER_CONSOLE_CENTER = "Console in the way"
ANSWER_CONSOLE_SOUTH = "Console parked south"
ANSWER_ROUTE_DIRECT = "Direct approach"
ANSWER_ROUTE_NORTH = "Northern approach"
ANSWER_ENTER_DIMENSIONS = "Enter dimensions"  # handled by the client (hand parameter editor), never sent back

_UNSUPPORTED_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    (
        "unsupported_input_kind",
        re.compile(
            r"\b(floor ?plans?|blue ?prints?|scans?|scanned|scanning|photos?|photographs?|pictures?|images?|pdfs?|"
            r"meshe?s?|lidar|videos?|3d models?|cad|uploads?|files?|attachments?)\b"
        ),
    ),
    (
        "unsupported_prediction",
        re.compile(r"\b(predict\w*|forecast\w*|behaviou?rs?|where (?:the )?(?:people|movers|they) (?:will|would))\b"),
    ),
    (
        "unsupported_threshold",
        re.compile(
            r"\b(doors?|doorways?|hallways?|corridors?|stairs?|staircases?|stairwells?|upstairs|downstairs|"
            r"elevators?|lifts?|lifting|tilt\w*|pivot\w*|truck|van|building)\b"
        ),
    ),
    (
        "unsupported_object",
        re.compile(
            r"\b(pianos?|fridges?|refrigerators?|wardrobes?|beds?|mattress(?:es)?|dressers?|bookcases?|bookshel\w+|"
            r"desks?|cabinets?|washing machines?|dishwashers?|armchairs?|chairs?|dining tables?|ovens?|tvs?|"
            r"televisions?|boxes|crates?)\b"
        ),
    ),
)
_SCENARIO_CUE = re.compile(
    r"\b(sofas?|couch(?:es)?|settees?|deliver\w*|move|moving|moved|move in|living ?rooms?|lounge|rooms?|console)\b"
)
_PAYLOAD = re.compile(r"\b(sofas?|couch(?:es)?|settees?)\b")
_CONSOLE = re.compile(r"\b(console|side table|table)\b")
_CENTER_CUE = re.compile(
    r"\b(in the way|in the middle|in the cent(?:re|er)|blocking|blocks|obstruct\w*|on the route|on the path)\b"
)
_SOUTH_CUE = re.compile(r"\b(parked south|south|southern|out of the way|aside|moved away|pushed back)\b")
_ABSENT_CUE = re.compile(
    r"\b(no (?:console|table|obstacle|obstruction)s?|without (?:a |the |any )?(?:console|table|obstacles?)|"
    r"empty room|nothing in the way)\b"
)
_NORTH_ROUTE_CUE = re.compile(
    r"\b(northern (?:approach|route|path|way)|north (?:route|detour|approach|path|side route)|detour|"
    r"via the north|around the north|go(?:es|ing)? north|the long way)\b"
)
_DIRECT_ROUTE_CUE = re.compile(r"\b(direct\w*|straight (?:in|through|across|route|path)|shortest)\b")
_CONSOLE_NORTH = re.compile(r"\b(?:console|table)\b[^.;]*?\bnorth(?:ern)?\b(?! (?:approach|route|detour|path))")

_TOKEN = re.compile(r"\d+(?:\.\d+)?|[a-z]+|[\N{MULTIPLICATION SIGN}*]")


def normalize(transcript: str) -> str:
    """NFKC, lower case, typographic dashes to spaces, thousands separators removed, whitespace collapsed."""
    text = unicodedata.normalize("NFKC", transcript).lower()
    text = re.sub(r"(?<=\d),(?=\d{3}\b)", "", text)
    text = re.sub(r"[\N{HYPHEN}-\N{HORIZONTAL BAR}\-_/]", " ", text)
    text = re.sub(r"(?<=\d)(?=[a-z])|(?<=[a-z])(?=\d)", " ", text)  # 6m -> 6 m, 6x5 -> 6 x 5
    return re.sub(r"\s+", " ", text).strip()


@dataclass(frozen=True)
class _Quantity:
    value: Decimal
    start: int
    end: int  # exclusive token index


def _parse_quantity(tokens: list[str], i: int) -> _Quantity | None:
    tok = tokens[i]
    if tok[0].isdigit():
        try:
            value = Decimal(tok)
        except InvalidOperation:
            return None
        # ASR often writes "6 and a half"; accept it only after a whole number.
        if "." not in tok and tokens[i + 1 : i + 4] == ["and", "a", "half"]:
            return _Quantity(value + Decimal("0.5"), i, i + 4)
        return _Quantity(value, i, i + 1)
    if tok not in _SMALL and tok not in _TENS:
        return None
    total = Decimal(0)
    current: Decimal = Decimal(0)
    j = i
    while j < len(tokens):
        t = tokens[j]
        if t in _SMALL:
            current += _SMALL[t]
        elif t in _TENS:
            current += _TENS[t]
        elif t == "hundred" and j > i:
            current = (current or Decimal(1)) * 100
        elif t == "thousand" and j > i:
            total += (current or Decimal(1)) * 1000
            current = Decimal(0)
        elif t == "and" and j + 1 < len(tokens) and (tokens[j + 1] in _SMALL or tokens[j + 1] in _TENS):
            pass
        else:
            break
        j += 1
    value = total + current
    # Fractions: "six and a half", "six point two five".
    if j + 2 < len(tokens) and tokens[j] == "and" and tokens[j + 1] == "a" and tokens[j + 2] == "half":
        return _Quantity(value + Decimal("0.5"), i, j + 3)
    if j + 1 < len(tokens) and tokens[j] == "point" and tokens[j + 1] in _DIGITS:
        k = j + 1
        digits = ""
        while k < len(tokens) and tokens[k] in _DIGITS:
            digits += str(_DIGITS[tokens[k]])
            k += 1
        return _Quantity(value + Decimal("0." + digits), i, k)
    return _Quantity(value, i, j)


@dataclass(frozen=True)
class _Measure:
    value: Decimal
    unit: str | None  # "m", "cm", "mm", "unsupported" or None (no unit stated)
    area: bool
    start: int
    end: int
    spoken: str


def _measures(tokens: list[str]) -> list[_Measure]:
    found: list[_Measure] = []
    i = 0
    while i < len(tokens):
        q = _parse_quantity(tokens, i)
        if q is None:
            i += 1
            continue
        end = q.end
        unit = None
        area = False
        if end < len(tokens) and tokens[end] == "square":
            area = True
            end += 1
        if end < len(tokens) and tokens[end] in _UNITS:
            unit = _UNITS[tokens[end]]
            end += 1
            if end < len(tokens) and tokens[end] in ("square", "squared", "sq"):
                area = True
                end += 1
        found.append(_Measure(q.value, unit, area, q.start, end, " ".join(tokens[q.start : end])))
        i = end
    return found


def _to_mm(value: Decimal, unit: str) -> Decimal:
    return value * _SCALE[unit]


def _infer_unit(value: Decimal) -> str | None:
    """Unitless numbers are interpreted only when exactly one supported unit gives an in-range room size."""
    candidates = [u for u in ("m", "cm", "mm") if MIN_ROOM_MM <= _to_mm(value, u) <= MAX_ROOM_MM]
    return candidates[0] if len(candidates) == 1 else None


@dataclass
class _Dimensions:
    width: Decimal | None = None
    depth: Decimal | None = None
    width_unit: str | None = None
    depth_unit: str | None = None
    issue: DimensionIssue | None = None
    spoken: list[str] = field(default_factory=list)

    def assign(self, which: str, value: Decimal, unit: str | None) -> None:
        current = self.width if which == "width" else self.depth
        current_unit = self.width_unit if which == "width" else self.depth_unit
        if current is not None and (current != value or current_unit != unit):
            self.issue = DimensionIssue.CONFLICTING
            return
        if which == "width":
            self.width, self.width_unit = value, unit
        else:
            self.depth, self.depth_unit = value, unit


def _dimensions(tokens: list[str]) -> _Dimensions:
    dims = _Dimensions()
    measures = _measures(tokens)
    used: set[int] = set()
    for idx, m in enumerate(measures):
        if idx in used:
            continue
        nxt = measures[idx + 1] if idx + 1 < len(measures) else None
        # "<a> [unit] by <b> [unit]"
        if nxt is not None and m.end < len(tokens) and tokens[m.end] in _BY and nxt.start == m.end + 1:
            unit_a = m.unit or nxt.unit
            unit_b = nxt.unit or m.unit
            dims.assign("width", m.value, unit_a)
            dims.assign("depth", nxt.value, unit_b)
            dims.spoken.append(f"{m.spoken} {tokens[m.end]} {nxt.spoken}")
            if m.area or nxt.area:
                dims.issue = DimensionIssue.AREA_ONLY
            used.update((idx, idx + 1))
            continue
        before = tokens[m.start - 1] if m.start > 0 else ""
        before2 = tokens[m.start - 2] if m.start > 1 else ""
        after = tokens[m.end] if m.end < len(tokens) else ""
        label = None
        if after in _WIDTH_WORDS or before in _WIDTH_WORDS or (before in ("is", "of") and before2 in _WIDTH_WORDS):
            label = "width"
        elif after in _DEPTH_WORDS or before in _DEPTH_WORDS or (before in ("is", "of") and before2 in _DEPTH_WORDS):
            label = "depth"
        if m.area:
            dims.issue = DimensionIssue.AREA_ONLY
            dims.spoken.append(m.spoken)
            used.add(idx)
        elif label is not None:
            dims.assign(label, m.value, m.unit)
            dims.spoken.append(f"{m.spoken} {label}")
            used.add(idx)
        elif m.unit is not None:
            # A unit-bearing length that is neither a pair nor labelled: which side it measures is unknown.
            if dims.issue is None:
                dims.issue = DimensionIssue.CONFLICTING if dims.width is not None else DimensionIssue.MISSING
            dims.spoken.append(m.spoken)
            used.add(idx)
    return dims


ANSWER_PREFIX = "Answer:"
_ANSWER_LINE = re.compile(r"^\s*answer\s*:\s*(.*?)\s*$", re.IGNORECASE)
KNOWN_ANSWERS = (
    ANSWER_EXAMPLE_ROOM,
    ANSWER_EXAMPLE_DEPTH,
    ANSWER_EXAMPLE_WIDTH,
    ANSWER_CONSOLE_CENTER,
    ANSWER_CONSOLE_SOUTH,
    ANSWER_ROUTE_DIRECT,
    ANSWER_ROUTE_NORTH,
)


def split_answers(transcript: str) -> tuple[str, set[str]]:
    """Separate clarification answers from the description.

    The client appends each chosen label as its own line, `Answer: <label>`, and resubmits the transcript. Only such
    lines whose label exactly matches a known choice (case-insensitive) count as answers. Ordinary wording such as
    "northern approach" inside the description stays a cue and never overrides a conflict. Unknown answer lines are
    ignored.
    """
    description: list[str] = []
    answers: set[str] = set()
    by_key = {a.casefold(): a for a in KNOWN_ANSWERS}
    for line in transcript.splitlines():
        match = _ANSWER_LINE.match(line)
        if match is None:
            description.append(line)
        elif match.group(1).casefold() in by_key:
            answers.add(by_key[match.group(1).casefold()])
    return "\n".join(description), answers


class DeterministicIntentExtractor:
    """Rule-based Moving Day 1.0.0 parser (development implementation; not a model)."""

    @property
    def provider_id(self) -> str:
        return "deterministic_rules"

    @property
    def version(self) -> str:
        return "1.0.0"

    def extract(self, transcript: str) -> Extraction:
        description, answers = split_answers(transcript)
        text = normalize(description)
        unsupported = tuple(code for code, pattern in _UNSUPPORTED_PATTERNS if pattern.search(text))
        scenario_cue = bool(_SCENARIO_CUE.search(text)) or bool(answers)

        tokens = _TOKEN.findall(text)
        dims = _dimensions(tokens)
        width_mm, depth_mm, width_source, depth_source, issue, inferred = self._resolve_dimensions(dims, answers)

        payload = "sofa_standard" if _PAYLOAD.search(text) else None

        obstruction: ObstructionState | None = None
        obstruction_issue: Literal["conflicting", "unsupported_position", "absent_requested"] | None = None
        if ANSWER_CONSOLE_CENTER in answers and ANSWER_CONSOLE_SOUTH in answers:
            obstruction_issue = "conflicting"
        elif ANSWER_CONSOLE_CENTER in answers:
            obstruction = "center"
        elif ANSWER_CONSOLE_SOUTH in answers:
            obstruction = "parked_south"
        else:
            route_free = _NORTH_ROUTE_CUE.sub(" ", text)
            center = bool(_CENTER_CUE.search(route_free))
            south = bool(_SOUTH_CUE.search(route_free)) and bool(_CONSOLE.search(route_free) or "parked" in route_free)
            if _ABSENT_CUE.search(text):
                obstruction_issue = "absent_requested"
            elif _CONSOLE_NORTH.search(route_free):
                obstruction_issue = "unsupported_position"
            elif center and south:
                obstruction_issue = "conflicting"
            elif center:
                obstruction = "center"
            elif south:
                obstruction = "parked_south"

        route: RouteId | None = None
        route_issue: Literal["conflicting"] | None = None
        north = ANSWER_ROUTE_NORTH in answers
        direct = ANSWER_ROUTE_DIRECT in answers
        if not (north or direct):
            north = bool(_NORTH_ROUTE_CUE.search(text))
            direct = bool(_DIRECT_ROUTE_CUE.search(text))
        if north and direct:
            route_issue = "conflicting"
        elif north:
            route = "north_detour"
        elif direct:
            route = "direct"

        return Extraction(
            unsupported=unsupported,
            scenario_cue=scenario_cue,
            width_mm=width_mm,
            depth_mm=depth_mm,
            width_source=width_source,
            depth_source=depth_source,
            dimension_issue=issue,
            unit_inferred=inferred,  # type: ignore[arg-type]
            spoken_dimensions=tuple(dims.spoken),
            payload_catalog_id=payload,
            obstruction=obstruction,
            obstruction_issue=obstruction_issue,
            route=route,
            route_issue=route_issue,
        )

    @staticmethod
    def _resolve_dimensions(
        dims: _Dimensions, answers: set[str]
    ) -> tuple[int | None, int | None, ValueSource | None, ValueSource | None, DimensionIssue | None, str | None]:
        if ANSWER_EXAMPLE_ROOM in answers:
            return 6000, 5000, "example_default", "example_default", None, None

        issue = dims.issue
        inferred: str | None = None
        result: dict[str, int | None] = {"width": None, "depth": None}
        for which, value, unit in (("width", dims.width, dims.width_unit), ("depth", dims.depth, dims.depth_unit)):
            if value is None or issue is not None:
                continue
            if unit == "unsupported":
                issue = DimensionIssue.UNSUPPORTED_UNIT
                continue
            if unit is None:
                unit = _infer_unit(value)
                if unit is None:
                    issue = DimensionIssue.UNIT_MISSING
                    continue
                inferred = unit
            mm = _to_mm(value, unit)
            if mm != mm.to_integral_value():
                issue = DimensionIssue.NOT_WHOLE_MM
                continue
            if not MIN_ROOM_MM <= mm <= MAX_ROOM_MM:
                issue = DimensionIssue.OUT_OF_RANGE
                continue
            result[which] = int(mm)

        width_source: ValueSource | None = "user_request" if result["width"] is not None else None
        depth_source: ValueSource | None = "user_request" if result["depth"] is not None else None
        if issue is None:
            if ANSWER_EXAMPLE_DEPTH in answers and result["depth"] is None:
                result["depth"], depth_source = 5000, "example_default"
            if ANSWER_EXAMPLE_WIDTH in answers and result["width"] is None:
                result["width"], width_source = 6000, "example_default"
            if result["width"] is None and result["depth"] is None:
                issue = DimensionIssue.MISSING
            elif result["width"] is None:
                issue = DimensionIssue.WIDTH_MISSING
            elif result["depth"] is None:
                issue = DimensionIssue.DEPTH_MISSING
        if issue in (DimensionIssue.WIDTH_MISSING, DimensionIssue.DEPTH_MISSING):
            return result["width"], result["depth"], width_source, depth_source, issue, inferred
        if issue is not None:
            return None, None, None, None, issue, None
        return result["width"], result["depth"], width_source, depth_source, None, inferred
