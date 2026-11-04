"""Service configuration. Validated once at startup; missing required values fail closed (technical-spec S21.2)."""

from __future__ import annotations

from enum import StrEnum
from functools import lru_cache

from pydantic import Field, SecretStr, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class Environment(StrEnum):
    DEVELOPMENT = "development"
    TEST = "test"
    JUDGE = "judge"
    PRODUCTION = "production"


class Settings(BaseSettings):
    """Environment-driven settings. Provider/model settings are added when a provider is selected (M0 blocker)."""

    model_config = SettingsConfigDict(env_prefix="TRYSCAPE_", extra="ignore", frozen=True)

    env: Environment
    database_url: SecretStr = Field(description="SQLAlchemy URL using the psycopg driver.")
    session_ttl_seconds: int = Field(default=3600, ge=60, le=3600)
    json_body_limit_bytes: int = Field(default=16 * 1024, ge=1024, le=1024 * 1024)

    @field_validator("database_url")
    @classmethod
    def _postgres_only(cls, value: SecretStr) -> SecretStr:
        if not value.get_secret_value().startswith("postgresql+psycopg://"):
            # R0 job/session semantics (SKIP LOCKED, timestamptz) are PostgreSQL-specific.
            raise ValueError("TRYSCAPE_DATABASE_URL must use the postgresql+psycopg:// driver")
        return value


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    return Settings()  # values come from the environment (pydantic-settings)
