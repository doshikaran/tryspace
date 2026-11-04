"""Alembic environment. Uses a synchronous psycopg connection; URL from TRYSCAPE_DATABASE_URL or -x url=..."""

from __future__ import annotations

import os

from alembic import context
from sqlalchemy import create_engine, pool

from tryscape_api.db import Base

config = context.config
target_metadata = Base.metadata


def _url() -> str:
    url = context.get_x_argument(as_dictionary=True).get("url") or os.environ.get("TRYSCAPE_DATABASE_URL")
    if not url:
        raise RuntimeError("Set TRYSCAPE_DATABASE_URL (postgresql+psycopg://...) to run migrations")
    return url


def run_offline() -> None:
    context.configure(url=_url(), target_metadata=target_metadata, literal_binds=True, compare_type=True)
    with context.begin_transaction():
        context.run_migrations()


def run_online() -> None:
    engine = create_engine(_url(), poolclass=pool.NullPool)
    with engine.connect() as connection:
        context.configure(connection=connection, target_metadata=target_metadata, compare_type=True)
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_offline()
else:
    run_online()
