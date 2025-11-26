# 🏠 InmobiScrap Backend

<div align="center">

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-316192?style=for-the-badge&logo=postgresql&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=for-the-badge&logo=docker&logoColor=white)

**Sistema inteligente de scraping inmobiliario impulsado por LLM**

[Características](#-características) • [Instalación](#-instalación) • [Configuración](#️-configuración) • [API](#-api) • [Arquitectura](#-arquitectura)

</div>

---

## 📋 Descripción

**InmobiScrap** es una plataforma avanzada de análisis del mercado inmobiliario chileno que utiliza bots inteligentes y modelos de lenguaje (LLM) para extraer, procesar y analizar datos de propiedades en tiempo real. El sistema ayuda a consumidores a tomar decisiones informadas comparando opciones entre viviendas existentes y alternativas prefabricadas.

### 🎯 Problema que resuelve

- Automatiza la recopilación de datos inmobiliarios de múltiples fuentes
- Normaliza y estructura información heterogénea mediante IA
- Proporciona análisis comparativos del mercado inmobiliario
- Facilita la toma de decisiones de compra basada en datos

---

## ✨ Características

### 🤖 Gestión de Bots
- Sistema escalable de múltiples bots para diferentes portales inmobiliarios
- Scraping inteligente con detección de cambios
- Rate limiting y manejo de anti-bot automático
- Rotación de user agents y proxies

### 🧠 Procesamiento con LLM
- Extracción inteligente de datos no estructurados
- Normalización automática de descripciones y características
- Clasificación y categorización de propiedades
- Detección de información clave (precio, ubicación, características)

### 📊 Análisis de Datos
- Estadísticas del mercado en tiempo real
- Comparativas de precios por zona
- Tendencias históricas de propiedades
- Alertas de nuevas oportunidades

### 🔄 Arquitectura Moderna
- API RESTful con ASP.NET Core
- Base de datos PostgreSQL optimizada
- Sistema de colas para procesamiento asíncrono
- Containerización con Docker

---

## 🏗️ Arquitectura

```
┌─────────────────┐
│   API Gateway   │
│   (ASP.NET)     │
└────────┬────────┘
         │
         ├─────────────┬─────────────┬──────────────┐
         ▼             ▼             ▼              ▼
┌──────────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐
│   Scraper    │ │   LLM    │ │  Data    │ │   Alert      │
│   Service    │ │ Processor│ │ Analysis │ │   Service    │
└──────┬───────┘ └────┬─────┘ └────┬─────┘ └──────┬───────┘
       │              │            │               │
       └──────────────┴────────────┴───────────────┘
                      │
              ┌───────▼────────┐
              │   PostgreSQL   │
              │    Database    │
              └────────────────┘
```

---

## 🛠️ Stack Tecnológico

### Backend
- **.NET 8.0** - Framework principal
- **ASP.NET Core** - Web API
- **Entity Framework Core** - ORM
- **Dapper** - Queries de alto rendimiento

### Base de Datos
- **PostgreSQL 15+** - Base de datos principal
- **Redis** - Caché y colas

### Scraping & IA
- **HtmlAgilityPack** - Parsing HTML
- **Selenium/Playwright** - Scraping dinámico
- **Anthropic Claude / OpenAI** - Procesamiento LLM
- **Polly** - Resilience y retry policies

### Infraestructura
- **Docker & Docker Compose**
- **Nginx** - Reverse proxy (producción)
- **Serilog** - Logging estructurado

---

## 📦 Instalación

### Prerequisitos

- .NET 8.0 SDK
- PostgreSQL 15+
- Docker & Docker Compose (opcional)
- API Key de Anthropic/OpenAI

### 🐳 Instalación con Docker (Recomendada)

```bash
# Clonar el repositorio
git clone https://github.com/estebanRisoDev/Inmobiscrap.git
cd Inmobiscrap

# Configurar variables de entorno
cp .env.example .env
# Editar .env con tus credenciales

# Levantar servicios
docker-compose up -d

# Verificar que todo esté funcionando
curl http://localhost:5000/health
```

### 💻 Instalación Manual

```bash
# Restaurar dependencias
dotnet restore

# Aplicar migraciones
dotnet ef database update

# Ejecutar la aplicación
dotnet run --project Inmobiscrap.API
```

---

## ⚙️ Configuración

### Archivo `.env`

```env
# Database
DATABASE_CONNECTION_STRING=Host=localhost;Database=inmobiscrap;Username=postgres;Password=yourpassword

# LLM Configuration
ANTHROPIC_API_KEY=your_anthropic_key_here
LLM_MODEL=claude-sonnet-4-5-20250929
LLM_MAX_TOKENS=8192

# Scraping
SCRAPING_DELAY_MS=2000
MAX_CONCURRENT_SCRAPERS=5
USER_AGENT_ROTATION=true

# Redis (opcional)
REDIS_CONNECTION_STRING=localhost:6379

# Logging
LOG_LEVEL=Information
```

### `appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=inmobiscrap;Username=postgres;Password=yourpassword"
  },
  "LLMSettings": {
    "Provider": "Anthropic",
    "Model": "claude-sonnet-4-5-20250929",
    "MaxTokens": 8192,
    "Temperature": 0.3
  },
  "ScraperSettings": {
    "MaxConcurrentScrapers": 5,
    "DelayBetweenRequests": 2000,
    "EnableProxy": false,
    "UserAgentRotation": true
  }
}
```

---

## 🚀 API

### Endpoints Principales

#### Propiedades

```http
GET    /api/properties              # Listar propiedades
GET    /api/properties/{id}         # Obtener propiedad
GET    /api/properties/search       # Buscar propiedades
POST   /api/properties/analyze      # Analizar tendencias
```

#### Bots

```http
GET    /api/bots                    # Listar bots
POST   /api/bots/{id}/start         # Iniciar scraping
POST   /api/bots/{id}/stop          # Detener scraping
GET    /api/bots/{id}/status        # Estado del bot
```

#### Análisis

```http
GET    /api/analytics/market        # Análisis de mercado
GET    /api/analytics/trends        # Tendencias
GET    /api/analytics/compare       # Comparativas
```

### Ejemplo de Uso

```bash
# Buscar propiedades en Santiago
curl -X GET "http://localhost:5000/api/properties/search?city=Santiago&minPrice=50000000&maxPrice=100000000"

# Iniciar bot de scraping
curl -X POST "http://localhost:5000/api/bots/portalinmobiliario/start"

# Obtener análisis de mercado
curl -X GET "http://localhost:5000/api/analytics/market?region=RM"
```

---

## 📊 Base de Datos

### Schema Principal

```sql
-- Propiedades
Properties
  - Id (PK)
  - Title
  - Description
  - Price
  - Location
  - Bedrooms
  - Bathrooms
  - SquareMeters
  - PropertyType
  - SourceUrl
  - ScrapedAt
  - ProcessedAt

-- Bots
Bots
  - Id (PK)
  - Name
  - TargetUrl
  - Status
  - LastRun
  - NextRun
  - IsActive

-- Análisis
MarketAnalysis
  - Id (PK)
  - Region
  - AveragePrice
  - MedianPrice
  - TotalProperties
  - AnalyzedAt
```

---

## 🧪 Testing

```bash
# Ejecutar todos los tests
dotnet test

# Tests con cobertura
dotnet test /p:CollectCoverage=true /p:CoverageReportFormat=opencover

# Tests de integración
dotnet test --filter "Category=Integration"
```

---

## 📈 Monitoreo

### Logs

Los logs se almacenan en:
- Consola (desarrollo)
- Archivos en `/logs` (producción)
- Serilog estructura los logs en JSON

### Health Checks

```bash
# Verificar estado del sistema
curl http://localhost:5000/health

# Respuesta esperada
{
  "status": "Healthy",
  "checks": {
    "database": "Healthy",
    "redis": "Healthy",
    "llm_api": "Healthy"
  }
}
```

---

## 🤝 Contribución

Las contribuciones son bienvenidas. Por favor:

1. Fork el proyecto
2. Crea una rama para tu feature (`git checkout -b feature/AmazingFeature`)
3. Commit tus cambios (`git commit -m 'Add some AmazingFeature'`)
4. Push a la rama (`git push origin feature/AmazingFeature`)
5. Abre un Pull Request

---

## 📝 Roadmap

- [ ] Soporte para más portales inmobiliarios
- [ ] Dashboard web con React/Vue
- [ ] Sistema de alertas por email/WhatsApp
- [ ] API pública para desarrolladores
- [ ] Integración con mapas (Google Maps/Mapbox)
- [ ] Machine Learning para predicción de precios
- [ ] App móvil (React Native)

---

## 📄 Licencia

Este proyecto está bajo la Licencia MIT. Ver el archivo `LICENSE` para más detalles.

---

## 👨‍💻 Autor

**Esteban Riso**
- GitHub: [@estebanRisoDev](https://github.com/estebanRisoDev)
- Email: steveriso.2000@gmail.com

---

## 🙏 Agradecimientos

- [Anthropic](https://www.anthropic.com) por Claude API
- Comunidad de .NET
- Portales inmobiliarios chilenos

---

<div align="center">

**⭐ Si este proyecto te fue útil, considera darle una estrella ⭐**

Made with ❤️ in Chile 🇨🇱

</div>
