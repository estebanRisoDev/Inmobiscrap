#!/bin/bash

echo "🤖 InmobiScrap Setup Script - Version Corregida"
echo "==============================================="
echo ""

# Colores para output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Función para verificar el éxito de comandos
check_status() {
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✓ $1${NC}"
    else
        echo -e "${RED}✗ $1 falló${NC}"
        exit 1
    fi
}

# Función para esperar a que un servicio esté listo
wait_for_service() {
    local service=$1
    local port=$2
    local max_attempts=30
    local attempt=1
    
    echo -e "${BLUE}Esperando a que $service esté listo...${NC}"
    
    while [ $attempt -le $max_attempts ]; do
        if nc -z localhost $port 2>/dev/null; then
            echo -e "${GREEN}✓ $service está listo${NC}"
            return 0
        fi
        echo -n "."
        sleep 2
        attempt=$((attempt + 1))
    done
    
    echo -e "${RED}✗ $service no respondió después de $max_attempts intentos${NC}"
    return 1
}

# 1. Verificar requisitos
echo -e "${YELLOW}1. Verificando requisitos...${NC}"
command -v docker >/dev/null 2>&1 || { echo -e "${RED}Docker no está instalado. Por favor instálalo primero.${NC}" >&2; exit 1; }
command -v docker compose version >/dev/null 2>&1 || command -v docker-compose >/dev/null 2>&1 || { echo -e "${RED}Docker Compose no está instalado.${NC}" >&2; exit 1; }

# Determinar comando de docker-compose
if command -v docker compose version >/dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
else
    COMPOSE_CMD="docker-compose"
fi
echo -e "${GREEN}✓ Usando: $COMPOSE_CMD${NC}"

# 2. Limpiar instalaciones previas si existe
echo -e "${YELLOW}2. Limpiando instalación previa si existe...${NC}"
$COMPOSE_CMD down -v 2>/dev/null
docker volume prune -f 2>/dev/null
echo -e "${GREEN}✓ Limpieza completada${NC}"

# 3. Crear directorios necesarios
echo -e "${YELLOW}3. Creando directorios...${NC}"
mkdir -p media static logs reports database/init
mkdir -p inmobiscrap/logs  # Para los logs de Django
check_status "Directorios creados"

# 4. Crear archivo de entorno si no existe
echo -e "${YELLOW}4. Configurando archivo .env...${NC}"
if [ ! -f .env ]; then
    cat > .env << 'EOF'
# Django Settings
DJANGO_SECRET_KEY=change-this-to-a-secure-random-string-in-production
DJANGO_DEBUG=True
DJANGO_ALLOWED_HOSTS=localhost,127.0.0.1,backend-django-inmobiscrap

# Database Configuration
DB_NAME=inmobiscrap_db
DB_USER=postgres
DB_PASSWORD=postgres123
DB_HOST=postgre-sql-inmobiscrap
DB_PORT=5432
DATABASE_URL=postgresql://postgres:postgres123@postgre-sql-inmobiscrap:5432/inmobiscrap_db

# Ollama Configuration
OLLAMA_BASE_URL=http://ollama-inmobiscrap:11434
OLLAMA_MODEL=llama3.2
EMBEDDINGS_MODEL=nomic-embed-text

# Redis/Celery Configuration
REDIS_URL=redis://redis-inmobiscrap:6379/0
CELERY_BROKER_URL=redis://redis-inmobiscrap:6379/0
CELERY_RESULT_BACKEND=redis://redis-inmobiscrap:6379/0

# Scraping Configuration
SCRAPEGRAPH_RATE_LIMIT=15
SCRAPEGRAPH_MAX_REQUESTS_PER_HOUR=20
SCRAPEGRAPH_RESPECT_ROBOTS_TXT=True

# LibreOffice Path
LIBREOFFICE_PATH=/usr/bin/libreoffice

# Logging
LOG_LEVEL=INFO

# Timezone
TZ=America/Santiago
EOF
    echo -e "${GREEN}✓ Archivo .env creado${NC}"
else
    echo -e "${GREEN}✓ Archivo .env ya existe${NC}"
fi

# 5. Verificar estructura del proyecto
echo -e "${YELLOW}5. Verificando estructura del proyecto...${NC}"
if [ ! -f "inmobiscrap/manage.py" ]; then
    echo -e "${RED}✗ No se encuentra inmobiscrap/manage.py${NC}"
    echo -e "${RED}  Asegúrate de estar en el directorio raíz del proyecto${NC}"
    exit 1
fi
check_status "Estructura del proyecto verificada"

# 6. Construir imagen del backend
echo -e "${YELLOW}6. Construyendo imagen Docker del backend...${NC}"
$COMPOSE_CMD build backend-django-inmobiscrap
check_status "Imagen del backend construida"

# 7. Iniciar servicios base (PostgreSQL y Redis primero)
echo -e "${YELLOW}7. Iniciando PostgreSQL y Redis...${NC}"
$COMPOSE_CMD up -d postgre-sql-inmobiscrap redis-inmobiscrap
check_status "PostgreSQL y Redis iniciados"

# Esperar a que estén listos
wait_for_service "PostgreSQL" 5432
wait_for_service "Redis" 6379

# 8. Iniciar Ollama
echo -e "${YELLOW}8. Iniciando Ollama...${NC}"
$COMPOSE_CMD up -d ollama-inmobiscrap
check_status "Ollama iniciado"

# Esperar más tiempo para Ollama y descargar modelos
echo -e "${YELLOW}9. Esperando a que Ollama esté listo y descargando modelos...${NC}"
echo -e "${BLUE}Esto puede tomar varios minutos en la primera ejecución...${NC}"
sleep 20

# Verificar si Ollama responde
max_attempts=30
attempt=1
while [ $attempt -le $max_attempts ]; do
    if curl -s http://localhost:11434/ >/dev/null 2>&1; then
        echo -e "${GREEN}✓ Ollama está respondiendo${NC}"
        break
    fi
    echo -n "."
    sleep 5
    attempt=$((attempt + 1))
done

# Intentar descargar los modelos si Ollama está listo
if curl -s http://localhost:11434/ >/dev/null 2>&1; then
    echo -e "${YELLOW}Descargando modelo llama3.2...${NC}"
    docker exec ollama-inmobiscrap ollama pull llama3.2 || echo -e "${YELLOW}⚠ No se pudo descargar llama3.2, se intentará más tarde${NC}"
    
    echo -e "${YELLOW}Descargando modelo nomic-embed-text...${NC}"
    docker exec ollama-inmobiscrap ollama pull nomic-embed-text || echo -e "${YELLOW}⚠ No se pudo descargar nomic-embed-text, se intentará más tarde${NC}"
fi

# 10. Iniciar Django Backend
echo -e "${YELLOW}10. Iniciando backend Django...${NC}"
$COMPOSE_CMD up -d backend-django-inmobiscrap
check_status "Backend Django iniciado"

# Esperar a que Django esté listo
sleep 10
wait_for_service "Django" 8000

# 11. Ejecutar migraciones
echo -e "${YELLOW}11. Ejecutando migraciones...${NC}"
$COMPOSE_CMD exec backend-django-inmobiscrap python3.11 inmobiscrap/manage.py makemigrations
$COMPOSE_CMD exec backend-django-inmobiscrap python3.11 inmobiscrap/manage.py migrate
check_status "Migraciones ejecutadas"

# 12. Crear superusuario
echo -e "${YELLOW}12. Creando superusuario...${NC}"
echo "from django.contrib.auth import get_user_model; User = get_user_model(); User.objects.filter(username='admin').exists() or User.objects.create_superuser('admin', 'admin@inmobiscrap.com', 'admin123')" | $COMPOSE_CMD exec -T backend-django-inmobiscrap python3.11 inmobiscrap/manage.py shell
check_status "Superusuario creado (admin/admin123)"

# 13. Recopilar archivos estáticos
echo -e "${YELLOW}13. Recopilando archivos estáticos...${NC}"
$COMPOSE_CMD exec backend-django-inmobiscrap python3.11 inmobiscrap/manage.py collectstatic --noinput
check_status "Archivos estáticos recopilados"

# 14. Verificar estado de servicios
echo -e "${YELLOW}14. Verificando estado de los servicios...${NC}"
$COMPOSE_CMD ps

# 15. Verificar conectividad
echo -e "${YELLOW}15. Verificando conectividad...${NC}"
echo -n "  PostgreSQL: "
$COMPOSE_CMD exec postgre-sql-inmobiscrap pg_isready -U postgres && echo -e "${GREEN}✓${NC}" || echo -e "${RED}✗${NC}"

echo -n "  Redis: "
$COMPOSE_CMD exec redis-inmobiscrap redis-cli ping | grep -q PONG && echo -e "${GREEN}✓${NC}" || echo -e "${RED}✗${NC}"

echo -n "  Ollama: "
curl -s http://localhost:11434/ >/dev/null 2>&1 && echo -e "${GREEN}✓${NC}" || echo -e "${YELLOW}⚠ (puede estar descargando modelos)${NC}"

echo -n "  Django: "
curl -s http://localhost:8000/admin/ >/dev/null 2>&1 && echo -e "${GREEN}✓${NC}" || echo -e "${RED}✗${NC}"

# 16. Mensaje final
echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}✓ InmobiScrap instalado!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo -e "${YELLOW}URLs de acceso:${NC}"
echo -e "  Backend API:     http://localhost:8000/api/v1/"
echo -e "  Admin Django:    http://localhost:8000/admin/"
echo -e "  API Docs:        http://localhost:8000/api/docs/"
echo ""
echo -e "${YELLOW}Credenciales:${NC}"
echo -e "  Usuario: admin"
echo -e "  Contraseña: admin123"
echo ""
echo -e "${YELLOW}Comandos útiles:${NC}"
echo -e "  Ver logs:        $COMPOSE_CMD logs -f [servicio]"
echo -e "  Reiniciar todo:  $COMPOSE_CMD restart"
echo -e "  Detener todo:    $COMPOSE_CMD down"
echo -e "  Logs Django:     $COMPOSE_CMD logs -f backend-django-inmobiscrap"
echo -e "  Logs Ollama:     $COMPOSE_CMD logs -f ollama-inmobiscrap"
echo ""

# Verificación final de Ollama
if ! curl -s http://localhost:11434/ >/dev/null 2>&1; then
    echo -e "${YELLOW}⚠ NOTA: Ollama puede estar aún descargando modelos.${NC}"
    echo -e "${YELLOW}  Puedes verificar el progreso con: $COMPOSE_CMD logs -f ollama-inmobiscrap${NC}"
    echo -e "${YELLOW}  Una vez listo, reinicia el backend: $COMPOSE_CMD restart backend-django-inmobiscrap${NC}"
fi