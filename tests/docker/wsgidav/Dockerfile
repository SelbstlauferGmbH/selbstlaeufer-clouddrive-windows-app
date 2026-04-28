FROM python:3.12-slim

RUN pip install --no-cache-dir "wsgidav>=4.0,<5" cheroot pyyaml

COPY log_middleware.py /app/log_middleware.py
COPY entrypoint.py    /app/entrypoint.py
COPY wsgidav.yaml     /etc/wsgidav/wsgidav.yaml

RUN mkdir -p /data/dav

EXPOSE 8080

CMD ["python3", "/app/entrypoint.py"]
