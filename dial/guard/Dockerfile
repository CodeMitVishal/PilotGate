# DIAL Guard: runtime interceptor for EPAM AI DIAL.
# Base image pinned by digest; dependencies installed from a hash-locked file; runs as a non-root user.
FROM python:3.12-slim@sha256:191083cdf6c7bda8c76158983163ead5d5cf7b77cf3033e805bb0f5296f51b4b

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONPATH=/app/src

RUN useradd --system --uid 10001 --no-create-home --shell /usr/sbin/nologin guard

WORKDIR /app
COPY requirements.lock ./
RUN pip install --require-hashes --no-deps --root-user-action=ignore -r requirements.lock

COPY src ./src
RUN python -m compileall -q /app/src && mkdir -p /var/log/dial-guard && chown guard /var/log/dial-guard

USER 10001
EXPOSE 8000
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 \
  CMD ["python", "-c", "import sys, urllib.request; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=2).status == 200 else 1)"]

# Events are JSON lines on stdout (and in DIAL_GUARD_EVENTS_FILE if set); access logs are off so stdout stays parseable.
CMD ["python", "-m", "uvicorn", "--factory", "dial_guard.app:app_from_env", "--host", "0.0.0.0", "--port", "8000", \
     "--no-access-log", "--timeout-graceful-shutdown", "20"]
