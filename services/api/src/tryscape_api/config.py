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
    consent_versions: str = Field(
        default="r0-dev-2026-11",
        description="Comma-separated processing-notice versions the headset may affirm (X-Processing-Consent-Version).",
        max_length=512,
    )
    job_deadline_seconds: int = Field(default=30, ge=5, le=30)
    job_result_ttl_seconds: int = Field(default=24 * 3600, ge=60, le=24 * 3600)
    jobs_per_minute_per_session: int = Field(default=4, ge=1, le=60)
    worker_lease_seconds: int = Field(default=10, ge=2, le=60)
    worker_poll_seconds: float = Field(default=0.25, ge=0.05, le=5)

    audio_dir: str = Field(
        default="",
        max_length=1024,
        description="Access-restricted directory shared by API and worker for temporary job audio (random file names). "
        "Empty: a per-user directory under the system temp directory.",
    )
    audio_max_age_seconds: int = Field(default=15 * 60, ge=60, le=15 * 60)
    asr_binary: str = Field(default="", max_length=1024, description="Path to a pinned whisper.cpp whisper-cli binary.")
    asr_model: str = Field(default="", max_length=1024, description="Path to the ggml model file used by whisper-cli.")
    asr_provider_id: str = Field(default="whisper_cpp_base_en", pattern=r"^[a-z][a-z0-9_]{0,47}$")
    asr_threads: int = Field(default=4, ge=1, le=8)
    asr_timeout_seconds: float = Field(default=20, ge=1, le=30)

    @property
    def accepted_consent_versions(self) -> frozenset[str]:
        return frozenset(v.strip() for v in self.consent_versions.split(",") if v.strip())

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
