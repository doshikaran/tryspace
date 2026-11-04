"""Migrations: fresh upgrade, full downgrade/upgrade round trip, and no drift between models and migrations."""

from __future__ import annotations

from alembic import command
from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext
from sqlalchemy import create_engine, inspect, make_url

from tests.conftest import _recreate_database, alembic_config, database_url
from tryscape_api.db import Base

R0_TABLES = {"anonymous_sessions", "jobs", "idempotency_records", "consents"}


def test_round_trip_and_no_drift() -> None:
    url = make_url(database_url()).set(database="tryscape_migration_test").render_as_string(hide_password=False)
    _recreate_database(url)
    cfg = alembic_config(url)
    engine = create_engine(url)
    try:
        command.upgrade(cfg, "head")
        tables = set(inspect(engine).get_table_names())
        assert tables >= R0_TABLES
        # R1 account/project tables must not exist in R0 (technical-spec S14.2).
        assert not tables & {"users", "projects", "project_versions", "runs"}
        with engine.connect() as conn:
            diff = compare_metadata(MigrationContext.configure(conn, opts={"compare_type": True}), Base.metadata)
        assert diff == [], diff

        command.downgrade(cfg, "base")
        assert set(inspect(engine).get_table_names()) <= {"alembic_version"}
        command.upgrade(cfg, "head")
        assert set(inspect(engine).get_table_names()) >= R0_TABLES
    finally:
        engine.dispose()
