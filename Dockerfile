# Base con Ubuntu 22.04
FROM ubuntu:22.04

LABEL maintainer="inmobiscrap@example.com"
LABEL description="Django Backend Container for InmobiScrap"

ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
# Playwright: ruta compartida para navegadores
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

# 1) Herramientas base + PPA de Python 3.11 (deadsnakes)
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates gnupg curl wget \
    software-properties-common \
  && add-apt-repository -y ppa:deadsnakes/ppa \
  && apt-get update && apt-get install -y --no-install-recommends \
    python3.11 python3.11-venv python3-pip \
    build-essential git vim htop locales \
    libpq-dev postgresql-client \
    tesseract-ocr tesseract-ocr-spa \
    libreoffice libreoffice-writer libreoffice-calc \
    default-jre \
    libopencv-dev \
    libnss3-dev libatk-bridge2.0-dev libdrm2 libxkbcommon-dev libgtk-3-dev libgbm-dev libasound2-dev \
    fonts-liberation fonts-noto fonts-ubuntu fonts-unifont \
    libgdk-pixbuf-2.0-0 \
    libjpeg-dev libx264-dev libenchant-2-dev libicu-dev libvpx-dev libwebp-dev \
  && rm -rf /var/lib/apt/lists/*

# 2) Google Chrome
RUN wget -q -O - https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /usr/share/keyrings/google-chrome-keyring.gpg \
  && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/google-chrome-keyring.gpg] http://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list \
  && apt-get update && apt-get install -y --no-install-recommends google-chrome-stable \
  && rm -rf /var/lib/apt/lists/*

# 3) Usuario no-root
RUN groupadd -r django && useradd -r -g django -d /app django

# 4) Directorio de trabajo
WORKDIR /app

# 5) Copiar requirements primero (para cacheo de capas)
COPY requirements.txt constraints.txt /app/

# Instalar con orden específico para evitar conflictos
RUN python3.11 -m pip install --upgrade pip \
 && python3.11 -m pip install --no-cache-dir numpy==1.26.2 \
 && python3.11 -m pip install --no-cache-dir faiss-cpu==1.7.4 \
 && python3.11 -m pip install --no-cache-dir -r requirements.txt -c constraints.txt \
 && python3.11 -m pip install --no-cache-dir opencv-python-headless

# 7) Playwright + navegadores (con dependencias) y cache compartido
#    Instalamos como root y luego damos permisos al usuario django
RUN python3.11 -m pip install --no-cache-dir playwright \
 && python3.11 -m playwright install chromium --with-deps \
 && mkdir -p ${PLAYWRIGHT_BROWSERS_PATH} /app/media /app/static /app/logs /app/reports /tmp/libreoffice_tmp \
 && chmod 777 /tmp/libreoffice_tmp \
 && chown -R django:django /app ${PLAYWRIGHT_BROWSERS_PATH}

# 8) Copiar código de la aplicación
COPY . /app/
RUN chown -R django:django /app

# 9) Cambiar a usuario no-root
USER django

# 10) Exponer puerto y comando simple
EXPOSE 8000
CMD ["python3.11", "inmobiscrap/manage.py", "runserver", "0.0.0.0:8000"]