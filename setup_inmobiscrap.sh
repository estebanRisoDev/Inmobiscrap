#!/bin/bash

echo "🤖 InmobiScrap Setup Script"
echo "=========================="
echo ""

# Colores para output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
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

# 1. Verificar que Docker y Docker Compose están instalados
echo -e "${YELLOW}1. Verificando requisitos...${NC}"
command -v docker >/dev/null 2>&1 || { echo -e "${RED}Docker no está instalado. Por favor instálalo primero.${NC}" >&2; exit 1; }
command -v docker-compose >/dev/null 2>&1 || { echo -e "${RED}Docker Compose no está instalado. Por favor instálalo primero.${NC}" >&2; exit 1; }
check_status "Docker y Docker Compose verificados"

# 2. Crear directorios necesarios
echo -e "${YELLOW}2. Creando directorios...${NC}"
mkdir -p media static logs reports database/init
check_status "Directorios creados"

# 3. Crear archivo de entorno si no existe
echo -e "${YELLOW}3. Configurando archivo .env...${NC}"
if [ ! -f .env ]; then
    cat > .env << EOF
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

# 4. IMPORTANTE: Renombrar archivo de tareas si existe el incorrecto
echo -e "${YELLOW}4. Verificando archivo de tareas...${NC}"
if [ -f "inmobiscrap/scraping/task.py" ]; then
    mv inmobiscrap/scraping/task.py inmobiscrap/scraping/tasks.py
    echo -e "${GREEN}✓ Archivo renombrado de task.py a tasks.py${NC}"
elif [ -f "inmobiscrap/scraping/tasks.py" ]; then
    echo -e "${GREEN}✓ Archivo tasks.py ya existe correctamente${NC}"
else
    echo -e "${RED}✗ No se encontró archivo de tareas${NC}"
fi

# 5. Construir y levantar contenedores
echo -e "${YELLOW}5. Construyendo contenedores Docker...${NC}"
docker-compose build
check_status "Construcción de contenedores"

echo -e "${YELLOW}6. Levantando servicios base...${NC}"
docker-compose up -d postgre-sql-inmobiscrap redis-inmobiscrap ollama-inmobiscrap
check_status "Servicios base iniciados"

# 7. Esperar a que los servicios estén listos
echo -e "${YELLOW}7. Esperando a que los servicios estén listos...${NC}"
sleep 10

# 8. Descargar modelos de Ollama
echo -e "${YELLOW}8. Descargando modelos de Ollama...${NC}"
echo "   Esto puede tomar varios minutos..."

# Descargar modelo LLM principal
docker exec ollama-inmobiscrap ollama pull llama3.2
check_status "Modelo llama3.2 descargado"

# Descargar modelo de embeddings
docker exec ollama-inmobiscrap ollama pull nomic-embed-text
check_status "Modelo nomic-embed-text descargado"

# 9. Levantar Django
echo -e "${YELLOW}9. Iniciando backend Django...${NC}"
docker-compose up -d backend-django-inmobiscrap
check_status "Backend Django iniciado"

# Esperar a que Django esté listo
sleep 5

# 10. Ejecutar migraciones
echo -e "${YELLOW}10. Ejecutando migraciones de base de datos...${NC}"
docker-compose exec backend-django-inmobiscrap python3.11 manage.py makemigrations
docker-compose exec backend-django-inmobiscrap python3.11 manage.py migrate
check_status "Migraciones ejecutadas"

# 11. Crear superusuario
echo -e "${YELLOW}11. Crear superusuario Django...${NC}"
echo "from django.contrib.auth import get_user_model; User = get_user_model(); User.objects.filter(username='admin').exists() or User.objects.create_superuser('admin', 'admin@inmobiscrap.com', 'admin123')" | docker-compose exec -T backend-django-inmobiscrap python3.11 manage.py shell
check_status "Superusuario creado (admin/admin123)"

# 12. Recopilar archivos estáticos
echo -e "${YELLOW}12. Recopilando archivos estáticos...${NC}"
docker-compose exec backend-django-inmobiscrap python3.11 manage.py collectstatic --noinput
check_status "Archivos estáticos recopilados"

# 13. Iniciar Celery Worker y Beat
echo -e "${YELLOW}13. Iniciando Celery...${NC}"
docker-compose exec -d backend-django-inmobiscrap celery -A inmobiscrap worker -l info
check_status "Celery Worker iniciado"

docker-compose exec -d backend-django-inmobiscrap celery -A inmobiscrap beat -l info
check_status "Celery Beat iniciado"

# 14. Mostrar estado de todos los contenedores
echo -e "${YELLOW}14. Estado de los servicios:${NC}"
docker-compose ps

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}✓ InmobiScrap instalado exitosamente!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo -e "${YELLOW}URLs de acceso:${NC}"
echo -e "  Backend API:     http://localhost:8000/api/v1/"
echo -e "  Admin Django:    http://localhost:8000/admin/"
echo -e "  API Docs:        http://localhost:8000/api/docs/"
echo -e "  Ollama:          http://localhost:11434/"
echo ""
echo -e "${YELLOW}Credenciales Admin:${NC}"
echo -e "  Usuario: admin"
echo -e "  Contraseña: admin123"
echo ""
echo -e "${YELLOW}Próximos pasos:${NC}"
echo -e "  1. Accede al frontend: cd a la carpeta del proyecto y ejecuta: npm install && npm start"
echo -e "  2. El frontend estará en: http://localhost:3000"
echo -e "  3. Agrega URLs desde el Admin o la interfaz"
echo ""
echo -e "${YELLOW}Comandos útiles:${NC}"
echo -e "  Ver logs:        docker-compose logs -f [servicio]"
echo -e "  Detener todo:    docker-compose down"
echo -e "  Reiniciar:       docker-compose restart [servicio]"
echo ""