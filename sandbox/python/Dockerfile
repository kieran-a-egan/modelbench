FROM python:3.13-slim
COPY runner.py /runner/runner.py
USER 65534:65534
ENTRYPOINT ["python", "-I", "-B", "/runner/runner.py"]
