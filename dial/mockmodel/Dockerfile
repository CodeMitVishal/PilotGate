# Deterministic OpenAI-compatible mock model for the local DIAL stack and its end-to-end tests.
FROM python:3.12-slim@sha256:191083cdf6c7bda8c76158983163ead5d5cf7b77cf3033e805bb0f5296f51b4b

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PORT=7001

RUN useradd --system --uid 10002 --no-create-home --shell /usr/sbin/nologin mock
WORKDIR /app
COPY requirements.lock ./
RUN pip install --require-hashes --no-deps --root-user-action=ignore -r requirements.lock
COPY mockmodel.py ./

USER 10002
EXPOSE 7001
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 \
  CMD ["python", "-c", "import sys, urllib.request; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:7001/health', timeout=2).status == 200 else 1)"]
CMD ["python", "mockmodel.py"]
