# 🏠 InmobiScrap Backend

<div align="center">

![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-316192?style=for-the-badge&logo=postgresql&logoColor=white)
![Docker](https://img.shields.io/badge/Docker-2496ED?style=for-the-badge&logo=docker&logoColor=white)

**Sistema inteligente de scraping inmobiliario impulsado por LLM**

[Características](#-características) • [Instalación](#-instalación) • [Configuración](#️-configuración) • [API](#-api) • [Arquitectura](#️-arquitectura)

</div>

---

## 📋 Descripción

**InmobiScrap** es una plataforma de análisis del mercado inmobiliario chileno. Bots configurables descargan portales inmobiliarios, un LLM (Claude vía AWS Bedrock) extrae las propiedades del contenido y el sistema guarda un historial de cada observación para detectar cambios de precio, avisos dados de baja y propiedades vendidas.

---

## ✨ Características

- **Bots de scraping** con ejecución manual o programada (CRON vía Hangfire), logs en tiempo real por SignalR y detención en caliente.
- **Extracción con LLM**: HTML + JSON embebido (Next.js, JSON-LD, `__PRELOADED_STATE__`) y fallback a Playwright para SPAs.
- **Tracking histórico**: un snapshot por cada observación, detección de cambios de precio y deduplicación por URL/fingerprint.
- **Verificación de vendidas**: job 2×/día que revisa los avisos que dejaron de aparecer.
- **Analytics y métricas**: precios por tipo, comuna, estado, precio/m², historial de precios.
- **Informes PDF** (LaTeX) de mercado y comparativos.
- **Planes y pagos**: plan Base con créditos, packs de créditos y suscripción Pro mensual con Mercado Pago.
- **Alertas de precio** por email para usuarios Pro.

---

## 🛠️ Stack

| Área | Tecnología |
|---|---|
| API | ASP.NET Core 9 (controllers), SignalR |
| Datos | PostgreSQL 16 + Entity Framework Core 9 (Npgsql) |
| Jobs | Hangfire (storage PostgreSQL) |
| Scraping | HttpClient, HtmlAgilityPack, Microsoft.Playwright (Chromium) |
| LLM | AWS Bedrock (Converse API, modelos Claude) |
| Auth | JWT + login con Google, BCrypt |
| Pagos | Mercado Pago (Checkout Pro + Preapproval) |
| PDF | pdflatex (TeX Live) |

---

## 🏗️ Arquitectura

```
                 ┌──────────────────────────────┐
  Frontend ────► │  ASP.NET Core API + SignalR  │
  (Next.js)      └──────┬───────────────┬───────┘
                        │               │
        ┌───────────────┼───────────────┼────────────────┐
        ▼               ▼               ▼                ▼
  ScraperService   ReportService   PaymentsController   Hangfire jobs
  (HTML/Playwright  (LaTeX → PDF)   (Mercado Pago)      (bots, vendidas,
   → Bedrock)                                            suscripciones, alertas)
        │
        ▼
  PropertyUpsertService ──► PostgreSQL (Properties, PropertySnapshots, Users, Bots, Payments, PriceAlerts)
```

---

## 📦 Instalación

### 🐳 Con Docker (recomendado)

```bash
cp .env.example .env      # completar credenciales (ver Configuración)
docker compose up -d --build
```

La API queda en `http://localhost:${API_PORT}` (por defecto 5000). Las migraciones se aplican solas al arrancar.

### 💻 Local (`dotnet run`)

Requisitos: .NET 9 SDK, PostgreSQL, `pdflatex` (para los PDF) y los navegadores de Playwright
(`pwsh bin/Debug/net9.0/playwright.ps1 install chromium` después del primer build).

```bash
cp .env.example .env      # definir DB_CONNECTION_STRING apuntando a tu Postgres
dotnet run                # http://localhost:5170 (Swagger en /swagger en Development)
```

---

## ⚙️ Configuración

Todas las variables están documentadas en [`.env.example`](.env.example). Resumen:

| Variable | Obligatoria | Uso |
|---|---|---|
| `JWT_SECRET` | ✅ | Firma de tokens. Mínimo 32 caracteres. |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` | ✅ (salvo mock) | Bedrock. |
| `AWS_REGION`, `BEDROCK_MODEL_ID` | | Región y modelo/perfil de Bedrock (deben ser compatibles). |
| `POSTGRES_*`, `DB_HOST`, `DB_PORT` | ✅ Docker | Base de datos en docker-compose. |
| `DB_CONNECTION_STRING` | ✅ local | Conexión al correr fuera de Docker. |
| `GOOGLE_CLIENT_ID` | para Google | Sin él, el login con Google está deshabilitado. |
| `SUPERADMIN_EMAIL` | | Emails (CSV) que pasan a admin al iniciar sesión **con Google**. |
| `MP_ACCESS_TOKEN`, `FRONTEND_URL`, `BACKEND_URL` | para pagos | Mercado Pago. `BACKEND_URL` debe ser HTTPS público para recibir webhooks. |
| `SMTP_*` | para alertas | Envío de emails (puerto 587). |
| `SCRAPER_MOCK_MODE` | | `true` para probar sin Bedrock. |

> ⚠️ El `.env` nunca debe commitearse ni copiarse a la imagen (está en `.gitignore` y `.dockerignore`).

---

## 🚀 API

| Recurso | Endpoints principales | Acceso |
|---|---|---|
| Auth | `POST /api/auth/register`, `/login`, `/google` · `GET /api/auth/me`, `/credits` | público / usuario |
| Bots | `GET/POST/PUT/DELETE /api/bots` · `POST /api/bots/{id}/run`, `/stop`, `/toggle` | admin |
| Analytics | `GET /api/analytics/locations`, `/market`, `/trends`, `/compare` | usuario (créditos) |
| Métricas | `GET /api/metrics/general`, `/properties`, `/compare` | usuario (créditos) |
| Historial | `GET /api/properties/{id}/timeline`, `/price-changes`, `/price-history`, `/tracking-stats`, `/delisted` | usuario |
| Vendidas | `GET /api/properties/sold`, `/stats`, `/job-status`, `/verification-queue` · `POST /{id}/verify` | Pro / admin |
| Informes | `GET /api/reports/market`, `/compare?ids=` | usuario (créditos) |
| Alertas | `GET/POST/DELETE /api/price-alerts/{propertyId}` | Pro |
| Pagos | `POST /api/payments/create-checkout`, `/webhook`, `/cancel-subscription` · `GET /verify/{id}`, `/verify-subscription/{id}`, `/history`, `/subscription-status` | usuario |
| Admin | `/api/admin/users`, `/api/admin/subscriptions/*` (solo Development) | admin |
| Propiedades (CRUD crudo) | `/api/properties` | admin |
| Logs en vivo | SignalR `/hubs/botlogs` (`?access_token=`) | admin |

Los usuarios Base consumen 5 créditos por consulta (5 por propiedad en comparativas); Pro y admin son ilimitados.

---

## 👨‍💻 Autor

**Esteban Riso**
- GitHub: [@estebanRisoDev](https://github.com/estebanRisoDev)
- Email: steveriso.2000@gmail.com

Made with ❤️ in Chile 🇨🇱
