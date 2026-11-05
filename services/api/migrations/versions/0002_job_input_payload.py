"""job input payload

Bounded job input (the scene-proposal request body, including its transcript). Purged when the job reaches a
terminal state; result transcripts are purged with job expiry (technical-spec S13.4).

Revision ID: 0002
Revises: 0001
Create Date: 2026-11-05 23:00:00
"""

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision: str = "0002"
down_revision: str | None = "0001"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column("jobs", sa.Column("input_payload", postgresql.JSONB(astext_type=sa.Text()), nullable=True))
    op.create_check_constraint(
        op.f("ck_jobs_input_payload_bounded"), "jobs", "input_payload IS NULL OR pg_column_size(input_payload) <= 16384"
    )


def downgrade() -> None:
    op.drop_constraint(op.f("ck_jobs_input_payload_bounded"), "jobs", type_="check")
    op.drop_column("jobs", "input_payload")
