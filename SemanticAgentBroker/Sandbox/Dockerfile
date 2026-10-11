FROM python:3.12-slim-bookworm

# Install data science libraries
RUN pip install --no-cache-dir \
    pandas==3.0.3 \
    numpy==2.4.6 \
    scipy==1.18.0 \
    matplotlib==3.11.0 \
    seaborn==0.13.2 \
    tabulate==0.10.0 \
    openpyxl==3.1.5 \
    scikit-learn==1.9.0 \
    pypdf==6.20.0 \
    python-docx==1.2.0 \
    odfpy==1.4.1 \
    python-pptx==1.0.2 \
    xlrd==2.0.2 \
    beautifulsoup4==4.15.0 \
    lxml==6.1.1 \
    PyYAML==6.0.3 \
    Pillow==12.2.0 \
    py7zr==1.1.3 \
    markdown==3.10.2

# Create non-root user for security
RUN useradd -m -s /bin/bash sandbox

USER sandbox
WORKDIR /app/workdir

ENTRYPOINT ["python", "-c"]
