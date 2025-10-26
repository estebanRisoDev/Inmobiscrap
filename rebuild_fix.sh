#!/bin/bash

echo "🔧 Reparación completa de InmobiScrap"
echo "====================================="

# Colores
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

# 1. Detener todo
echo -e "${YELLOW}1. Deteniendo servicios...${NC}"
docker compose down -v

# 2. Limpiar imágenes y caché
echo -e "${YELLOW}2. Limpiando Docker...${NC}"
docker system prune -af --volumes
docker builder prune -af

# 3. Reconstruir desde cero
echo -e "${YELLOW}3. Reconstruyendo imagen (puede tomar varios minutos)...${NC}"
docker compose build --no-cache backend-django-inmobiscrap

if [ $? -ne 0 ]; then
    echo -e "${RED}Error en la construcción de la imagen${NC}"
    exit 1
fi

# 4. Iniciar servicios base
echo -e "${YELLOW}4. Iniciando PostgreSQL y Redis...${NC}"
docker compose up -d postgre-sql-inmobiscrap redis-inmobiscrap

# Esperar a PostgreSQL
echo -n "Esperando PostgreSQL..."
for i in {1..30}; do
    if docker compose exec postgre-sql-inmobiscrap pg_isready -U postgres &>/dev/null; then
        echo -e " ${GREEN}✓${NC}"
        break
    fi
    echo -n "."
    sleep 2
done

# 5. Iniciar Ollama
echo -e "${YELLOW}5. Iniciando Ollama...${NC}"
docker compose up -d ollama-inmobiscrap
sleep 10

# 6. Iniciar backend
echo -e "${YELLOW}6. Iniciando Django backend...${NC}"
docker compose up -d backend-django-inmobiscrap

# Esperar un poco
sleep 5

# 7. Verificar instalación
echo -e "${YELLOW}7. Verificando instalación de paquetes...${NC}"
docker compose exec backend-django-inmobiscrap python3.11 -c "
import sys
print('Python:', sys.version)
try:
    import django
    print('✓ Django:', django.__version__)
except:
    print('✗ Django no instalado')
try:
    import numpy
    print('✓ numpy:', numpy.__version__)
except:
    print('✗ numpy no instalado')
try:
    import faiss
    print('✓ faiss instalado')
except:
    print('✗ faiss no instalado')
try:
    import pandas
    print('✓ pandas:', pandas.__version__)
except:
    print('✗ pandas no instalado')
"

# 8. Ejecutar migraciones
echo -e "${YELLOW}8. Ejecutando migraciones...${NC}"
docker compose exec backend-django-inmobiscrap python3.11 inmobiscrap/manage.py makemigrations
docker compose exec backend-django-inmobiscrap python3.11 inmobiscrap/manage.py migrate

# 9. Crear superusuario
echo -e "${YELLOW}9. Creando superusuario...${NC}"
docker compose exec backend-django-inmobiscrap python3.11 -c "
from django.contrib.auth import get_user_model
User = get_user_model()
if not User.objects.filter(username='admin').exists():
    User.objects.create_superuser('admin', 'admin@example.com', 'admin123')
    print('✓ Superusuario creado')
else:
    print('✓ Superusuario ya existe')
"

# 10. Estado final
echo -e "${YELLOW}10. Estado de los servicios:${NC}"
docker compose ps

echo ""
echo -e "${GREEN}====================================${NC}"
echo -e "${GREEN}✓ Reparación completada!${NC}"
echo -e "${GREEN}====================================${NC}"
echo ""
echo "URLs de acceso:"
echo "  - Django Admin: http://localhost:8000/admin/"
echo "  - API: http://localhost:8000/api/v1/"
echo ""
echo "Credenciales:"
echo "  - Usuario: admin"
echo "  - Contraseña: admin123"
echo ""
echo "Ver logs: docker compose logs -f backend-django-inmobiscrap"