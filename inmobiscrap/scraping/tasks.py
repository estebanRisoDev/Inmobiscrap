"""
TASKS.PY CORREGIDO - LIMPIEZA HTML EN TAREA PRINCIPAL
======================================================
Versión corregida que implementa la limpieza HTML en scrape_url_task
"""

import logging
import time
import requests
from typing import List, Dict, Optional
from decimal import Decimal

from celery import shared_task
from django.utils import timezone
from django.conf import settings
from django.db.models import Q
from datetime import timedelta

from bs4 import BeautifulSoup
from scrapegraphai.graphs import SmartScraperGraph
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)

logger = logging.getLogger(__name__)


# ============================================================================
# CONFIGURACIÓN DE LIMPIEZA HTML
# ============================================================================

HTML_CLEANING_CONFIG = {
    # Tags que se eliminan completamente (con su contenido)
    'remove_tags': [
        'script',      # JavaScript
        'style',       # CSS inline
        'noscript',    # Fallback para JS deshabilitado
        'iframe',      # Iframes (ads, widgets externos)
        'object',      # Flash y objetos embebidos
        'embed',       # Contenido embebido
        'svg',         # SVGs (suelen ser decorativos)
        'canvas',      # Canvas elements
        'video',       # Videos (no nos interesa)
        'audio',       # Audio
    ],
    
    # Tags que se eliminan por selectores CSS
    'remove_by_selector': [
        'header',              # Headers de página
        'footer',              # Footers
        'nav',                 # Menús de navegación
        '.header',             # Headers por clase
        '.footer',             # Footers por clase
        '.navbar',             # Navbars
        '.menu',               # Menús
        '.navigation',         # Navegación
        '.sidebar',            # Sidebars
        '.advertisement',      # Anuncios
        '.ad',                 # Ads
        '.cookie-banner',      # Banners de cookies
        '.modal',              # Modales
        '.popup',              # Popups
        '[role="banner"]',     # ARIA banner role
        '[role="navigation"]', # ARIA navigation
        '[role="complementary"]', # ARIA complementary
    ],
    
    # Atributos a limpiar de las imágenes
    'clean_img_attributes': True,
    
    # Comentarios HTML
    'remove_comments': True,
    
    # Espacios en blanco excesivos
    'clean_whitespace': True,
}


# ============================================================================
# FUNCIONES DE LIMPIEZA HTML
# ============================================================================

def download_html(url: str, timeout: int = 30) -> str:
    """
    Descarga el HTML de una URL con headers apropiados.
    
    Args:
        url: URL a descargar
        timeout: Timeout en segundos
        
    Returns:
        HTML como string
    """
    headers = {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8',
        'Accept-Language': 'es-CL,es;q=0.9,en;q=0.8',
        'Accept-Encoding': 'gzip, deflate, br',
        'Connection': 'keep-alive',
        'Upgrade-Insecure-Requests': '1',
    }
    
    try:
        response = requests.get(url, headers=headers, timeout=timeout)
        response.raise_for_status()
        response.encoding = response.apparent_encoding or 'utf-8'
        return response.text
    except requests.exceptions.RequestException as e:
        logger.error(f"Error descargando {url}: {str(e)}")
        raise


def clean_html(html: str, config: dict = None) -> str:
    """
    Limpia el HTML eliminando elementos inútiles para el scraping.
    
    Esta función:
    1. Elimina scripts, styles, iframes, etc.
    2. Elimina headers, footers, navs por selectores
    3. Limpia atributos innecesarios de imágenes
    4. Elimina comentarios HTML
    5. Reduce espacios en blanco
    
    Args:
        html: HTML a limpiar
        config: Configuración de limpieza (usa HTML_CLEANING_CONFIG por defecto)
        
    Returns:
        HTML limpio como string
    """
    if config is None:
        config = HTML_CLEANING_CONFIG
    
    # Parsear HTML
    soup = BeautifulSoup(html, 'html.parser')
    
    # ========================================
    # PASO 1: Eliminar tags inútiles
    # ========================================
    for tag_name in config.get('remove_tags', []):
        for tag in soup.find_all(tag_name):
            tag.decompose()
    
    # ========================================
    # PASO 2: Eliminar por selectores CSS
    # ========================================
    for selector in config.get('remove_by_selector', []):
        for element in soup.select(selector):
            element.decompose()
    
    # ========================================
    # PASO 3: Limpiar imágenes
    # ========================================
    if config.get('clean_img_attributes', True):
        for img in soup.find_all('img'):
            # Mantener solo src y alt (lo demás es ruido)
            attrs_to_keep = {}
            if img.get('src'):
                attrs_to_keep['src'] = img['src']
            if img.get('alt'):
                attrs_to_keep['alt'] = img['alt']
            
            img.attrs = attrs_to_keep
    
    # ========================================
    # PASO 4: Eliminar comentarios HTML
    # ========================================
    if config.get('remove_comments', True):
        from bs4 import Comment
        for comment in soup.find_all(string=lambda text: isinstance(text, Comment)):
            comment.extract()
    
    # ========================================
    # PASO 5: Limpiar espacios en blanco
    # ========================================
    html_clean = str(soup)
    
    if config.get('clean_whitespace', True):
        import re
        # Eliminar múltiples espacios
        html_clean = re.sub(r'\s+', ' ', html_clean)
        # Eliminar espacios alrededor de tags
        html_clean = re.sub(r'>\s+<', '><', html_clean)
    
    return html_clean


def get_html_stats(html_original: str, html_limpio: str) -> dict:
    """
    Calcula estadísticas de la limpieza HTML.
    
    Args:
        html_original: HTML original
        html_limpio: HTML después de limpieza
        
    Returns:
        Diccionario con estadísticas
    """
    size_original = len(html_original)
    size_limpio = len(html_limpio)
    reduccion = ((size_original - size_limpio) / size_original * 100) if size_original > 0 else 0
    
    # Contar tags aproximadamente
    tags_original = html_original.count('<')
    tags_limpio = html_limpio.count('<')
    
    return {
        'size_original_bytes': size_original,
        'size_limpio_bytes': size_limpio,
        'size_original_kb': round(size_original / 1024, 2),
        'size_limpio_kb': round(size_limpio / 1024, 2),
        'reduccion_porcentaje': round(reduccion, 2),
        'tags_original': tags_original,
        'tags_limpio': tags_limpio,
        'tags_eliminados': tags_original - tags_limpio,
    }


# ============================================================================
# CONFIGURACIÓN OPTIMIZADA PARA LLAMA 3.1
# ============================================================================

def get_llm_config():
    """
    Configuración CORREGIDA para Ollama con ScrapeGraphAI
    IMPORTANTE: El prefijo 'ollama/' es obligatorio
    """
    config = {
        "llm": {
            "model": "ollama/llama3.1:8b",  # ✅ CRÍTICO: prefijo 'ollama/'
            "temperature": 0.1,
            "base_url": "http://ollama-inmobiscrap:11434",
            # NO incluir api_key para Ollama - causa errores
        },
        "embeddings": {
            "model": "ollama/nomic-embed-text",  # ✅ También necesita prefijo
            "base_url": "http://ollama-inmobiscrap:11434",
        },
        "verbose": True,
        "headless": True,
    }
    
    return config


# ============================================================================
# PROMPT MEJORADO PARA LLAMA
# ============================================================================

def create_advanced_prompt():
    """Prompt mejorado y estructurado para Llama 3.1"""
    return """
    IMPORTANTE: Extrae TODOS los listados de propiedades de la página.
    
    Para CADA propiedad encuentra y extrae:
    
    DATOS OBLIGATORIOS (si faltan, intenta deducirlos del contexto):
    - titulo: título o encabezado descriptivo
    - precio: precio en pesos chilenos (número sin formato)
    - tipo_propiedad: CRÍTICO - debe ser uno de: "casa", "departamento", "terreno", o "casa_prefabricada"
    - tipo_operacion: "venta" o "arriendo"
    
    IMPORTANTE sobre tipo_propiedad:
    - Si dice "depto", "depa", "apartment", "flat" → tipo_propiedad: "departamento"
    - Si dice "house", "vivienda" → tipo_propiedad: "casa"
    - Si dice "lote", "sitio", "parcela" → tipo_propiedad: "terreno"
    - Si no está claro, usar "departamento" como default
    
    DATOS DE UBICACIÓN:
    - direccion: dirección completa si está disponible
    - comuna: nombre de la comuna
    - ciudad: ciudad (por defecto "Santiago" si no se especifica)
    - region: región (por defecto "Metropolitana" si no se especifica)
    
    CARACTERÍSTICAS:
    - metros_cuadrados: superficie útil o construida (número)
    - metros_terreno: superficie del terreno si aplica (número)
    - dormitorios: cantidad de dormitorios (número entero)
    - banos: cantidad de baños (número entero)
    - estacionamientos: cantidad de estacionamientos (número entero)
    
    ADICIONALES:
    - descripcion: descripción completa de la propiedad (máx 1000 caracteres)
    - imagenes_urls: lista de URLs de imágenes (máximo 10)
    - codigo_propiedad: código único si existe
    - url_propiedad: link al detalle de la propiedad
    - telefono_contacto: teléfono si está disponible
    - email_contacto: email si está disponible
    - inmobiliaria: nombre de la inmobiliaria o corredora
    - precio_uf: precio en UF si está disponible
    
    REGLAS CRÍTICAS:
    1. SIEMPRE retorna una LISTA JSON, incluso para una sola propiedad
    2. Cada propiedad es un objeto JSON independiente en la lista
    3. Si hay múltiples propiedades, incluye TODAS
    4. Los campos numéricos deben ser números, no strings
    5. Los precios deben ser números sin puntos ni comas
    6. Si un dato no existe, usa null
    
    Retorna ÚNICAMENTE el JSON sin explicaciones adicionales.
    """


# ============================================================================
# TAREA PRINCIPAL CORREGIDA - CON LIMPIEZA HTML
# ============================================================================

@shared_task(bind=True, max_retries=3, default_retry_delay=60)
def scrape_url_task(self, url_id: int):
    """
    Tarea principal de scraping CORREGIDA con limpieza HTML
    """
    start_time = time.time()
    scraping_log = None
    url_obj = None  # ✅ Inicializar para evitar UnboundLocalError
    
    try:
        url_obj = URLToScrape.objects.get(id=url_id)
        
        # Actualizar estado
        url_obj.status = 'in_progress'
        url_obj.save(update_fields=['status'])
        
        # Crear log con los campos correctos del modelo
        scraping_log = ScrapingLog.objects.create(
            url_scrape=url_obj,  # ✅ Campo correcto: url_scrape
            status='started'     # ✅ Status correcto: 'started' no 'in_progress'
        )
        
        logger.info(f"🔄 INICIANDO scraping: {url_obj.url}")
        logger.info(f"   📍 Sitio: {url_obj.site_name}")
        # No hay campo property_type en el modelo
        
        # ============================================
        # PASO 1: DESCARGAR HTML
        # ============================================
        logger.info("📥 Descargando HTML...")
        html_original = download_html(url_obj.url)
        logger.info(f"   ✓ HTML descargado: {len(html_original) / 1024:.2f} KB")
        
        # ============================================
        # PASO 2: LIMPIAR HTML 
        # ============================================
        logger.info("🧹 Limpiando HTML...")
        html_limpio = clean_html(html_original)
        
        # Mostrar estadísticas de limpieza
        stats = get_html_stats(html_original, html_limpio)
        logger.info(f"   ✓ HTML limpio: {stats['size_limpio_kb']} KB")
        logger.info(f"   ✓ Reducción: {stats['reduccion_porcentaje']}%")
        logger.info(f"   ✓ Tags eliminados: {stats['tags_eliminados']}")
        
        # ============================================
        # PASO 3: EXTRAER CON LLM (HTML LIMPIO)
        # ============================================
        logger.info("🤖 Extrayendo datos con LLM...")
        
        config = get_llm_config()
        prompt = create_advanced_prompt()
        
        # CAMBIO CRÍTICO: Usar HTML limpio en lugar de URL
        scraper = SmartScraperGraph(
            prompt=prompt,
            source=html_limpio,  # ← AQUÍ ESTÁ LA CORRECCIÓN
            config=config
        )
        
        result = scraper.run()
        
        # Procesar resultado
        if isinstance(result, str):
            import json
            try:
                result = json.loads(result)
            except json.JSONDecodeError as e:
                logger.error(f"❌ Error parseando JSON: {e}")
                logger.error(f"   Respuesta raw: {result[:500]}")
                result = []
        
        # Asegurar que es una lista
        if not isinstance(result, list):
            result = [result] if result else []
        
        logger.info(f"✅ {len(result)} propiedades extraídas")
        
        # ============================================
        # PASO 4: GUARDAR EN BD
        # ============================================
        created_count = 0
        updated_count = 0
        
        for item in result:
            if not isinstance(item, dict):
                continue
                
            # Obtener tipo de propiedad del item extraído (no del url_obj)
            tipo_propiedad = item.get('tipo_propiedad', 'departamento')
            
            # Guardar propiedad
            obj, created = save_property_from_data(
                item, 
                tipo_propiedad,  # Usar el tipo extraído del scraping
                url_obj
            )
            
            if created:
                created_count += 1
                logger.info(f"   ✓ Nueva: {item.get('titulo', 'Sin título')}")
            else:
                updated_count += 1
                logger.info(f"   ↻ Actualizada: {item.get('titulo', 'Sin título')}")
        
        # ============================================
        # PASO 5: ACTUALIZAR ESTADO
        # ============================================
        
        # Actualizar URL con campos correctos
        url_obj.last_scraped_at = timezone.now()
        url_obj.next_scrape_at = timezone.now() + timedelta(hours=url_obj.scrape_frequency_hours)  # ✅ Campo correcto
        url_obj.successful_scrapes += 1
        url_obj.status = 'completed'
        url_obj.save()
        
        # Completar log con campos correctos del modelo
        elapsed = time.time() - start_time
        scraping_log.status = 'completed'  # ✅ 'completed' no 'success'
        scraping_log.completed_at = timezone.now()  # ✅ 'completed_at' no 'ended_at'
        scraping_log.execution_time_seconds = elapsed  # ✅ Nombre correcto del campo
        scraping_log.properties_found = len(result)  # ✅ 'properties_found'
        scraping_log.properties_created = created_count  # ✅ 'properties_created'
        scraping_log.properties_updated = updated_count  # ✅ 'properties_updated'
        # Guardar stats de HTML en response_data ya que no hay campos específicos
        scraping_log.response_data = {
            'html_size_original_kb': stats['size_original_kb'],
            'html_size_clean_kb': stats['size_limpio_kb'],
            'html_reduction_percentage': stats['reduccion_porcentaje']
        }
        scraping_log.save()
        
        logger.info(f"✅ COMPLETADO en {elapsed:.1f}s")
        logger.info(f"   📊 Nuevas: {created_count}, Actualizadas: {updated_count}")
        
        return {
            'status': 'success',
            'url': url_obj.url,
            'total': len(result),
            'created': created_count,
            'updated': updated_count,
            'elapsed': elapsed,
            'html_reduction': f"{stats['reduccion_porcentaje']}%"
        }
        
    except Exception as e:
        logger.error(f"❌ ERROR en scraping: {str(e)}", exc_info=True)
        
        # Actualizar estado de error con campos correctos
        if url_obj:
            url_obj.status = 'failed'
            url_obj.failed_scrapes += 1
            url_obj.last_error_message = str(e)[:500]  # ✅ Campo correcto
            url_obj.save()
        
        # Actualizar log con campos correctos
        if scraping_log:
            scraping_log.status = 'failed'  # ✅ 'failed' es correcto
            scraping_log.error_message = str(e)  # ✅ Campo correcto
            scraping_log.completed_at = timezone.now()  # ✅ 'completed_at'
            scraping_log.execution_time_seconds = time.time() - start_time if start_time else None
            scraping_log.save()
        
        # Reintentar si quedan intentos
        if self.request.retries < self.max_retries:
            logger.info(f"🔄 Reintentando... ({self.request.retries + 1}/{self.max_retries})")
            raise self.retry(exc=e)
        
        return {
            'status': 'error',
            'error': str(e),
            'url': url_obj.url if url_obj else None
        }


def save_property_from_data(data: dict, tipo: str, url_obj) -> tuple:
    """
    Guarda una propiedad desde los datos extraídos.
    Retorna tupla (objeto, fue_creado)
    """
    
    # Validar y normalizar el tipo de propiedad
    tipo = str(tipo).lower().strip()
    
    # Mapear variaciones comunes a tipos válidos
    tipo_mapping = {
        'casa': 'casa',
        'casas': 'casa',
        'house': 'casa',
        'departamento': 'departamento',
        'departamentos': 'departamento',
        'depto': 'departamento',
        'apartment': 'departamento',
        'flat': 'departamento',
        'terreno': 'terreno',
        'terrenos': 'terreno',
        'land': 'terreno',
        'lote': 'terreno',
        'casa_prefabricada': 'casa_prefabricada',
        'casa prefabricada': 'casa_prefabricada',
        'prefabricada': 'casa_prefabricada',
    }
    
    tipo = tipo_mapping.get(tipo, 'departamento')  # Default a departamento
    
    if not data.get('codigo_propiedad'):
        import hashlib
        unique = f"{data.get('titulo', '')}{data.get('precio', 0)}{url_obj.site_name}"
        data['codigo_propiedad'] = hashlib.md5(unique.encode()).hexdigest()[:10]
    
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
    else:
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
# TAREA DE PRUEBA MEJORADA
# ============================================================================

@shared_task
def test_html_cleaning(url: str):
    """
    Prueba la limpieza de HTML sin guardar nada.
    Útil para ver qué tanto se reduce el HTML.
    """
    try:
        logger.info(f"🧪 TEST LIMPIEZA: {url}")
        
        # Descargar
        html_original = download_html(url)
        
        # Limpiar
        html_limpio = clean_html(html_original)
        
        # Estadísticas
        stats = get_html_stats(html_original, html_limpio)
        
        logger.info(f"""
        ✅ LIMPIEZA COMPLETADA
        📊 Estadísticas:
           - Original: {stats['size_original_kb']} KB ({stats['tags_original']} tags)
           - Limpio: {stats['size_limpio_kb']} KB ({stats['tags_limpio']} tags)
           - Reducción: {stats['reduccion_porcentaje']}%
           - Tags eliminados: {stats['tags_eliminados']}
        """)
        
        # Guardar HTMLs para inspección manual (opcional)
        with open('/tmp/html_original.html', 'w', encoding='utf-8') as f:
            f.write(html_original)
        with open('/tmp/html_limpio.html', 'w', encoding='utf-8') as f:
            f.write(html_limpio)
        
        logger.info("💾 HTMLs guardados en /tmp/ para inspección")
        
        return {
            'status': 'success',
            'stats': stats,
            'files': {
                'original': '/tmp/html_original.html',
                'limpio': '/tmp/html_limpio.html',
            }
        }
        
    except Exception as e:
        logger.error(f"❌ TEST ERROR: {str(e)}")
        return {'status': 'error', 'error': str(e)}


@shared_task
def test_llm_extraction_with_cleaning(url: str, site_name: str = "Test"):
    """
    Prueba completa: descarga → limpia → extrae con LLM.
    No guarda en BD.
    """
    try:
        logger.info(f"🧪 TEST COMPLETO: {url}")
        
        # Descargar y limpiar
        html_original = download_html(url)
        html_limpio = clean_html(html_original)
        
        stats = get_html_stats(html_original, html_limpio)
        logger.info(f"🧹 HTML reducido en {stats['reduccion_porcentaje']}%")
        
        # Extraer con LLM
        config = get_llm_config()
        prompt = create_advanced_prompt()
        
        scraper = SmartScraperGraph(
            prompt=prompt,
            source=html_limpio,  # HTML limpio
            config=config
        )
        
        result = scraper.run()
        
        if isinstance(result, str):
            import json
            result = json.loads(result)
        
        if not isinstance(result, list):
            result = [result] if result else []
        
        logger.info(f"✅ TEST: {len(result)} propiedades extraídas")
        
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
            'sample': result[:3],
            'html_stats': stats,
        }
        
    except Exception as e:
        logger.error(f"❌ TEST ERROR: {str(e)}")
        return {'status': 'error', 'error': str(e)}