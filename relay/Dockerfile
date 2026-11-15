# Tizen Notifier video relay: ffmpeg (with Intel VA-API drivers) + a small Python server.
FROM linuxserver/ffmpeg:9.0.2-cli-ls89
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
COPY relay.py /app/relay.py
ENTRYPOINT ["python3", "/app/relay.py"]
