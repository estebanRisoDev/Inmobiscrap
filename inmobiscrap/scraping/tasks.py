"""
TASKS.PY SIMPLIFICADO - VERSION QUE CONFIA EN EL LLM
====================================================
Esta versión elimina casi toda la lógica manual y deja que 
Llama 3.2 haga el trabajo pesado de extracción y limpieza.
"""

import logging
import time
from typing import List, Dict
from decimal import Decimal

from celery import shared_task
from django.utils import timezone
from django.conf import settings
from django.db.models import Q
from datetime import timedelta

from scrapegraphai.graphs import SmartScraperGraph
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)

logger = logging.getLogger(__name__)


# ============================================================================
# CONFIGURACIÓN OPTIMIZADA PARA LLAMA 3.2
# ============================================================================

def get_llm_config():
    """
    Configuración optimizada para Llama 3.2 con Ollama.
    Esta configuración está basada en las mejores prácticas.
    """
    return {
        "llm": {
            "model": "ollama/llama3.2",
            "base_url": settings.OLLAMA_BASE_URL,
            "temperature": 0,  # Determinístico para consistencia
            "format": "json",  # CRÍTICO: Ollama requiere esto explícitamente
            "num_ctx": 8192,   # Contexto extendido para Llama 3.2
            "repeat_penalty": 1.1,
            "top_p": 0.9,
        },
        "embeddings": {
            "model": "ollama/nomic-embed-text",
            "base_url": settings.OLLAMA_BASE_URL,
        },
        "verbose": True,
        "headless": True,
    }


# ============================================================================
# PROMPT ENGINEERING MEJORADO - FEW-SHOT + CHAIN-OF-THOUGHT
# ============================================================================

def create_advanced_prompt():
    """
    Prompt súper optimizado usando las mejores prácticas de Llama:
    1. Rol específico (experto inmobiliario)
    2. Instrucciones explícitas y detalladas
    3. Few-shot learning (ejemplos concretos)
    4. Chain-of-thought (razonamiento paso a paso)
    5. Formato JSON estricto
    6. Restricciones claras
    """
    return """Eres un EXPERTO en extracción de datos inmobiliarios de Chile. Tu tarea es analizar páginas web de listados de propiedades y extraer TODA la información de TODAS las propiedades visibles.

IMPORTANTE: Piensa paso a paso antes de responder. Primero identifica cuántas propiedades hay, luego extrae los datos de cada una.

FORMATO DE SALIDA (JSON ARRAY - OBLIGATORIO):
```json
[
  {
    "titulo": "Casa 3D 2B en Las Condes",
    "precio": 150000000,
    "precio_uf": 3500,
    "tipo_operacion": "venta",
    "metros_cuadrados": 120,
    "metros_terreno": 200,
    "dormitorios": 3,
    "banos": 2,
    "estacionamientos": 2,
    "direccion": "Avenida Apoquindo 1234",
    "comuna": "Las Condes",
    "ciudad": "Santiago",
    "region": "Metropolitana",
    "descripcion": "Hermosa casa en excelente ubicación",
    "tipo_propiedad": "casa",
    "url_propiedad": "https://ejemplo.cl/propiedad/123",
    "imagenes_urls": ["url1", "url2"],
    "codigo_propiedad": "COD-12345",
    "telefono_contacto": "+56912345678",
    "email_contacto": "contacto@ejemplo.cl",
    "inmobiliaria": "Inmobiliaria XYZ"
  }
]
```

EJEMPLOS DE CONVERSIÓN (FEW-SHOT LEARNING):

Ejemplo 1 - Formato típico chileno:
Texto: "Casa 3D 2B - $150.000.000 - 120m² - Las Condes"
→ {
  "titulo": "Casa 3D 2B en Las Condes",
  "precio": 150000000,
  "dormitorios": 3,
  "banos": 2,
  "metros_cuadrados": 120,
  "comuna": "Las Condes"
}

Ejemplo 2 - Con UF:
Texto: "Departamento UF 3.500 - 2 dorm 2 baños - Providencia"  
→ {
  "precio": 129500000,
  "precio_uf": 3500,
  "dormitorios": 2,
  "banos": 2,
  "comuna": "Providencia"
}

REGLAS CRÍTICAS:

1. CANTIDAD: Extrae TODAS las propiedades, no solo una
2. PRECIOS: 
   - Convierte "$150.000.000" → 150000000 (sin puntos ni símbolos)
   - Si dice "millones": multiplicar por 1000000
   - Si está en UF: calcular precio = UF × 37000
3. NÚMEROS: Solo dígitos (3, 2, 120), NO texto ("tres", "dos")
4. TIPO OPERACIÓN: "venta", "arriendo" o "venta_arriendo"
5. TIPO PROPIEDAD: Detectar automáticamente:
   - "casa" si menciona: casa, chalet, villa, pareada
   - "departamento" si menciona: depto, apartamento, piso, flat
   - "terreno" si menciona: terreno, sitio, lote, parcela
   - "casa_prefabricada" si menciona: prefabricada, modular, container
6. COMUNAS CHILENAS: Las Condes, Providencia, Vitacura, Santiago, Ñuñoa, etc.
7. VALORES NULL: Si un campo no existe, usa null (no inventes)
8. URLs: Si son relativas (ej: "/propiedad/123"), déjalas así

PASOS A SEGUIR (CHAIN-OF-THOUGHT):
1. Identificar: ¿Cuántas propiedades veo en la página?
2. Para cada propiedad:
   a. Extraer título/descripción principal
   b. Encontrar precio (en $ o UF)
   c. Contar dormitorios, baños, estacionamientos
   d. Medir metros cuadrados y terreno
   e. Identificar ubicación (comuna/ciudad)
   f. Determinar tipo de operación y tipo de propiedad
   g. Buscar datos de contacto si existen
3. Organizar todo en JSON array

RESTRICCIONES:
- NO uses datos de otras páginas o conocimiento externo
- NO inventes precios o características
- NO agregues explicaciones, solo JSON
- SI un campo falta, usa null, NO lo omitas

¡COMIENZA! Analiza la página paso a paso y extrae TODAS las propiedades."""


# ============================================================================
# EXTRACCIÓN SIMPLIFICADA CON LLM
# ============================================================================

@shared_task(bind=True, max_retries=3)
def scrape_url_task(self, url_id):
    """
    Tarea SIMPLIFICADA que confía casi completamente en el LLM.
    Solo hacemos validación mínima y dejamos que Llama haga el resto.
    """
    start_time = time.time()
    
    try:
        url_obj = URLToScrape.objects.get(id=url_id)
    except URLToScrape.DoesNotExist:
        logger.error(f"URL con ID {url_id} no existe")
        return {'status': 'error', 'message': 'URL no encontrada'}
    
    # Crear log
    scraping_log = ScrapingLog.objects.create(
        url_scrape=url_obj,
        status='started'
    )
    
    url_obj.status = 'in_progress'
    url_obj.save()
    
    try:
        logger.info(f"🚀 Iniciando scraping LLM-first de: {url_obj.url}")
        logger.info(f"   Sitio: {url_obj.site_name}")
        
        # ========================================
        # PASO 1: DEJAR QUE EL LLM HAGA TODO
        # ========================================
        
        scraper_config = get_llm_config()
        prompt = create_advanced_prompt()
        
        logger.info("📡 Enviando página al LLM Llama 3.2...")
        
        smart_scraper = SmartScraperGraph(
            prompt=prompt,
            source=url_obj.url,
            config=scraper_config
        )
        
        result = smart_scraper.run()
        
        # ========================================
        # PASO 2: VALIDACIÓN MÍNIMA
        # ========================================
        
        if not result:
            raise Exception("LLM no retornó resultados")
        
        # Si el resultado es string JSON, parsearlo
        if isinstance(result, str):
            import json
            result = json.loads(result)
        
        # Si no es lista, convertir a lista
        if not isinstance(result, list):
            result = [result] if result else []
        
        logger.info(f"✅ LLM extrajo {len(result)} propiedades")
        
        # ========================================
        # PASO 3: GUARDAR EN BD (confiar en el LLM)
        # ========================================
        
        created_count = 0
        updated_count = 0
        failed_count = 0
        
        for i, prop_data in enumerate(result, 1):
            try:
                # Validación súper básica
                if not prop_data.get('titulo') and not prop_data.get('precio'):
                    logger.debug(f"Propiedad {i} sin datos mínimos")
                    failed_count += 1
                    continue
                
                # El LLM ya debería haber detectado el tipo
                tipo_propiedad = prop_data.get('tipo_propiedad', 'casa')
                
                # Crear/actualizar propiedad
                obj, created = save_property_simple(prop_data, url_obj, tipo_propiedad)
                
                if obj:
                    if created:
                        created_count += 1
                        logger.info(f"  ✅ [{i}/{len(result)}] Nueva: {obj.titulo[:50]}")
                    else:
                        updated_count += 1
                        logger.debug(f"  🔄 [{i}/{len(result)}] Actualizada")
                else:
                    failed_count += 1
                    
            except Exception as e:
                failed_count += 1
                logger.error(f"  ❌ Error en propiedad {i}: {str(e)}")
                continue
        
        # ========================================
        # PASO 4: FINALIZAR
        # ========================================
        
        scraping_log.status = 'completed' if failed_count == 0 else 'partial'
        scraping_log.properties_found = len(result)
        scraping_log.properties_created = created_count
        scraping_log.properties_updated = updated_count
        scraping_log.properties_failed = failed_count
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.save()
        
        url_obj.mark_as_scraped(success=(created_count + updated_count) > 0)
        
        logger.info(f"""
        ✅ Scraping LLM completado
        ⏱️  Tiempo: {time.time() - start_time:.2f}s
        📊 Resultados:
           - Encontradas: {len(result)}
           - Creadas: {created_count}
           - Actualizadas: {updated_count}
           - Fallidas: {failed_count}
        """)
        
        return {
            'status': 'success',
            'url': url_obj.url,
            'found': len(result),
            'created': created_count,
            'updated': updated_count,
            'failed': failed_count,
            'execution_time': round(time.time() - start_time, 2)
        }
        
    except Exception as e:
        logger.error(f"❌ Error en scraping: {str(e)}")
        
        scraping_log.status = 'failed'
        scraping_log.error_message = str(e)
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.save()
        
        url_obj.mark_as_scraped(success=False, error_message=str(e))
        
        # Reintentar si quedan intentos
        if self.request.retries < self.max_retries:
            logger.info(f"🔄 Reintentando...")
            raise self.retry(exc=e, countdown=60 * (self.request.retries + 1))
        
        return {
            'status': 'failed',
            'url': url_obj.url,
            'error': str(e),
            'execution_time': round(time.time() - start_time, 2)
        }


# ============================================================================
# FUNCIÓN SIMPLIFICADA PARA GUARDAR (confía en datos del LLM)
# ============================================================================

def save_property_simple(data: Dict, url_obj, tipo: str):
    """
    Versión ultra-simplificada que confía en que el LLM ya limpió los datos.
    Solo hace conversiones de tipo básicas.
    """
    
    # Generar código único si no existe
    if not data.get('codigo_propiedad'):
        import hashlib
        unique = f"{data.get('titulo', '')}{data.get('precio', 0)}"
        data['codigo_propiedad'] = hashlib.md5(unique.encode()).hexdigest()[:10]
    
    # Función helper para convertir a número
    def to_int(val, default=0):
        try:
            return int(float(val)) if val else default
        except:
            return default
    
    def to_decimal(val, default=0):
        try:
            return Decimal(str(val)) if val else Decimal(str(default))
        except:
            return Decimal(str(default))
    
    # Datos comunes (el LLM ya los limpió)
    common = {
        'titulo': str(data.get('titulo', 'Sin título'))[:500],
        'descripcion': str(data.get('descripcion', ''))[:1000],
        'precio': to_decimal(data.get('precio', 0)),
        'precio_uf': to_decimal(data.get('precio_uf')) if data.get('precio_uf') else None,
        'tipo_operacion': data.get('tipo_operacion', 'venta'),
        'metros_cuadrados': to_decimal(data.get('metros_cuadrados', 0)),
        'metros_terreno': to_decimal(data.get('metros_terreno')) if data.get('metros_terreno') else None,
        'dormitorios': to_int(data.get('dormitorios', 0)),
        'banos': to_int(data.get('banos', 0)),
        'estacionamientos': to_int(data.get('estacionamientos', 0)),
        'direccion': str(data.get('direccion', ''))[:500],
        'comuna': str(data.get('comuna', ''))[:100],
        'ciudad': str(data.get('ciudad', 'Santiago'))[:100],
        'region': str(data.get('region', 'Metropolitana'))[:100],
        'url_fuente': data.get('url_propiedad', url_obj.url),
        'sitio_origen': url_obj.site_name,
        'codigo_propiedad': data['codigo_propiedad'],
        'imagenes_urls': data.get('imagenes_urls', [])[:10],
        'telefono_contacto': str(data.get('telefono_contacto', ''))[:50],
        'email_contacto': str(data.get('email_contacto', ''))[:254],
        'inmobiliaria': str(data.get('inmobiliaria', ''))[:200],
        'url_scrape': url_obj,
    }
    
    # Determinar modelo según tipo
    if tipo == 'casa':
        return Casa.objects.update_or_create(
            codigo_propiedad=data['codigo_propiedad'],
            sitio_origen=url_obj.site_name,
            defaults={**common}
        )
    elif tipo == 'departamento':
        return Departamento.objects.update_or_create(
            codigo_propiedad=data['codigo_propiedad'],
            sitio_origen=url_obj.site_name,
            defaults={**common}
        )
    elif tipo == 'terreno':
        return Terreno.objects.update_or_create(
            codigo_propiedad=data['codigo_propiedad'],
            sitio_origen=url_obj.site_name,
            defaults={**common}
        )
    else:  # casa_prefabricada
        return CasaPrefabricada.objects.update_or_create(
            codigo_propiedad=data['codigo_propiedad'],
            sitio_origen=url_obj.site_name,
            defaults={**common}
        )


# ============================================================================
# TAREAS PERIÓDICAS (sin cambios)
# ============================================================================

@shared_task
def scrape_pending_urls():
    """Tarea periódica para scrapear URLs pendientes"""
    now = timezone.now()
    
    urls_to_scrape = URLToScrape.objects.filter(
        Q(is_active=True) &
        (Q(next_scrape_at__lte=now) | Q(next_scrape_at__isnull=True)) &
        ~Q(status='in_progress')
    )
    
    logger.info(f"📋 {urls_to_scrape.count()} URLs pendientes")
    
    tasks = []
    for url_obj in urls_to_scrape:
        task = scrape_url_task.delay(url_obj.id)
        tasks.append({'url_id': url_obj.id, 'task_id': task.id})
    
    return {'message': f'{len(tasks)} tareas iniciadas', 'tasks': tasks}


@shared_task
def cleanup_old_logs():
    """Limpia logs antiguos (>30 días)"""
    cutoff = timezone.now() - timedelta(days=30)
    deleted = ScrapingLog.objects.filter(started_at__lt=cutoff).delete()[0]
    logger.info(f"🗑️ Eliminados {deleted} logs antiguos")
    return {'deleted_count': deleted}


@shared_task
def deactivate_failed_urls():
    """Desactiva URLs con múltiples fallos"""
    urls = URLToScrape.objects.filter(
        is_active=True,
        failed_scrapes__gte=5,
        successful_scrapes=0
    )
    count = urls.update(is_active=False, status='disabled')
    logger.warning(f"⚠️ Desactivadas {count} URLs por fallos")
    return {'deactivated_count': count}


# ============================================================================
# TAREA DE PRUEBA
# ============================================================================

@shared_task
def test_llm_extraction(url: str, site_name: str = "Test"):
    """
    Prueba rápida del LLM sin guardar en BD.
    Útil para debugging y ver qué extrae el LLM.
    """
    try:
        logger.info(f"🧪 TEST LLM: {url}")
        
        config = get_llm_config()
        prompt = create_advanced_prompt()
        
        scraper = SmartScraperGraph(
            prompt=prompt,
            source=url,
            config=config
        )
        
        result = scraper.run()
        
        if isinstance(result, str):
            import json
            result = json.loads(result)
        
        if not isinstance(result, list):
            result = [result] if result else []
        
        logger.info(f"✅ TEST: {len(result)} propiedades")
        
        # Mostrar primeras 3
        for i, prop in enumerate(result[:3], 1):
            logger.info(f"""
            Propiedad {i}:
            - Título: {prop.get('titulo', 'N/A')}
            - Precio: ${prop.get('precio', 0):,}
            - Tipo: {prop.get('tipo_propiedad', 'N/A')}
            - M²: {prop.get('metros_cuadrados', 0)}
            - Ubicación: {prop.get('comuna', 'N/A')}
            """)
        
        return {
            'status': 'success',
            'total': len(result),
            'sample': result[:3]
        }
        
    except Exception as e:
        logger.error(f"❌ TEST ERROR: {str(e)}")
        return {'status': 'error', 'error': str(e)}