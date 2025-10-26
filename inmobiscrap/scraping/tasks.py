import logging
from celery import shared_task
from django.utils import timezone
from django.conf import settings
from django.db.models import Q
from datetime import timedelta
import time
from bs4 import BeautifulSoup
import requests
import re

from scrapegraphai.graphs import SmartScraperGraph
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)

logger = logging.getLogger(__name__)


def clean_html_content(html_content, max_tokens=6000):
    """
    Limpia el HTML y lo trunca para que no exceda el límite del contexto.
    """
    try:
        soup = BeautifulSoup(html_content, 'html.parser')
        
        # Eliminar elementos que no son útiles
        for tag in soup(['script', 'style', 'nav', 'header', 'footer', 
                        'iframe', 'noscript', 'svg', 'path', 'meta', 'link']):
            tag.decompose()
        
        # Eliminar comentarios
        for comment in soup.find_all(text=lambda text: isinstance(text, str) and text.strip().startswith('<!--')):
            comment.extract()
        
        # Obtener solo el texto visible
        text = soup.get_text(separator=' ', strip=True)
        
        # Limpiar espacios múltiples
        text = ' '.join(text.split())
        
        # Truncar por caracteres (aproximadamente)
        max_chars = max_tokens * 4
        if len(text) > max_chars:
            text = text[:max_chars]
            logger.warning(f"Contenido truncado de {len(text)} a {max_chars} caracteres")
        
        return text
        
    except Exception as e:
        logger.error(f"Error limpiando HTML: {str(e)}")
        return str(html_content)[:24000] if html_content else ""


def fetch_and_clean_url(url, max_tokens=6000):
    """
    Obtiene el contenido de una URL y lo limpia antes de enviarlo al LLM.
    """
    try:
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        response = requests.get(url, headers=headers, timeout=30)
        response.raise_for_status()
        
        # Limpiar el HTML
        cleaned_text = clean_html_content(response.text, max_tokens)
        logger.info(f"Contenido limpiado: {len(cleaned_text)} caracteres")
        
        return cleaned_text
        
    except Exception as e:
        logger.error(f"Error obteniendo URL {url}: {str(e)}")
        return None


def get_scraper_config():
    """Obtener configuración del scraper con Ollama"""
    return {
        "llm": {
            "model": "ollama/llama3.2",
            "base_url": settings.OLLAMA_BASE_URL,
            "temperature": 0,
            "num_ctx": 8192,
        },
        "embeddings": {
            "model": "ollama/nomic-embed-text",
            "base_url": settings.OLLAMA_BASE_URL,
        },
        "verbose": True,
        "headless": True,
    }


def determine_property_type(data):
    """Determinar el tipo de propiedad basándose en los datos"""
    titulo = data.get('titulo', '').lower()
    descripcion = data.get('descripcion', '').lower()
    
    text = titulo + ' ' + descripcion
    
    # Lógica de detección
    if any(word in text for word in ['terreno', 'lote', 'sitio']):
        return 'terreno'
    elif any(word in text for word in ['departamento', 'depto', 'apartamento']):
        return 'departamento'
    elif any(word in text for word in ['prefabricada', 'modular', 'container', 'móvil']):
        return 'casa_prefabricada'
    else:
        return 'casa'


def extract_properties_data(url, scraper_config):
    """
    Extraer TODAS las propiedades de una página de listado usando ScrapeGraphAI.
    
    Returns:
        Lista de diccionarios con datos de propiedades
    """
    
    # PROMPT PARA EXTRAER MÚLTIPLES PROPIEDADES
    prompt = """
    Esta es una página de listado de propiedades inmobiliarias.
    Extrae TODAS las propiedades que encuentres en la página.
    
    Retorna un array JSON con cada propiedad:
    
    [
        {
            "titulo": "título de la propiedad",
            "descripcion": "descripción breve",
            "precio": número (solo cifras, sin símbolos),
            "precio_uf": número o null,
            "tipo_operacion": "venta" o "arriendo",
            "metros_cuadrados": número,
            "metros_terreno": número o null,
            "dormitorios": número,
            "banos": número,
            "estacionamientos": número o null,
            "direccion": "dirección completa",
            "comuna": "comuna",
            "ciudad": "ciudad",
            "region": "región",
            "amenidades": ["amenidad1", "amenidad2"],
            "url_propiedad": "URL individual de esta propiedad si existe",
            "codigo_propiedad": "código o ID",
            "imagenes_urls": ["url_imagen1", "url_imagen2"],
            "telefono_contacto": "teléfono",
            "email_contacto": "email",
            "inmobiliaria": "nombre inmobiliaria"
        }
    ]
    
    IMPORTANTE:
    - Extrae TODAS las propiedades de la página, no solo una
    - Si no encuentras un campo, usa null
    - Para precios, extrae solo números (sin $, UF, puntos)
    - Sé preciso con los datos de cada propiedad
    """
    
    try:
        logger.info(f"Extrayendo propiedades de: {url}")
        
        # Obtener y limpiar el contenido
        cleaned_content = fetch_and_clean_url(url, max_tokens=6000)
        
        if not cleaned_content:
            raise Exception("No se pudo obtener contenido limpio de la URL")
        
        # Crear scraper
        smart_scraper_graph = SmartScraperGraph(
            prompt=prompt,
            source=cleaned_content,
            config=scraper_config
        )
        
        result = smart_scraper_graph.run()
        q
        # El resultado debería ser una lista de propiedades
        if result and isinstance(result, list):
            logger.info(f"✅ Encontradas {len(result)} propiedades en {url}")
            return result
        elif result and isinstance(result, dict):
            # Si devuelve un dict en lugar de lista, convertirlo a lista
            logger.warning(f"Resultado es dict, convirtiendo a lista")
            return [result]
        else:
            logger.warning(f"Resultado inesperado: {type(result)}")
            return []
        
    except Exception as e:
        logger.error(f"Error en extracción de propiedades de {url}: {str(e)}")
        
        # Fallback: intentar extracción básica
        try:
            logger.info(f"Intentando método fallback para {url}")
            return fallback_extraction_multiple(url)
        except Exception as fallback_error:
            logger.error(f"Error en método fallback: {str(fallback_error)}")
            return []


def fallback_extraction_multiple(url):
    """
    Método de extracción de emergencia para múltiples propiedades
    usando solo BeautifulSoup cuando el LLM falla.
    """
    try:
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        response = requests.get(url, headers=headers, timeout=30)
        response.raise_for_status()
        
        soup = BeautifulSoup(response.text, 'html.parser')
        properties = []
        
        # Intentar detectar contenedores de propiedades
        # Patrones comunes: div con clases que contienen 'property', 'item', 'card', 'listing'
        property_containers = []
        
        for pattern in ['property', 'item', 'card', 'listing', 'resultado', 'anuncio']:
            containers = soup.find_all(['div', 'article', 'li'], 
                                      class_=lambda x: x and pattern in x.lower())
            if containers:
                property_containers.extend(containers)
                break
        
        logger.info(f"Fallback: encontrados {len(property_containers)} contenedores potenciales")
        
        for i, container in enumerate(property_containers[:20]):  # Máximo 20 propiedades
            try:
                # Extraer datos básicos de cada contenedor
                prop_data = {
                    'titulo': '',
                    'precio': 0,
                    'metros_cuadrados': 0,
                    'dormitorios': 0,
                    'banos': 0,
                    'comuna': '',
                    'ciudad': 'Santiago',
                    'region': 'Metropolitana',
                }
                
                # Intentar extraer título
                title_elem = container.find(['h2', 'h3', 'h4', 'a'])
                if title_elem:
                    prop_data['titulo'] = title_elem.get_text(strip=True)[:500]
                
                # Intentar extraer precio
                text_content = container.get_text()
                price_match = re.search(r'\$\s*([\d.,]+)', text_content)
                if price_match:
                    try:
                        price_str = price_match.group(1).replace('.', '').replace(',', '')
                        prop_data['precio'] = float(price_str)
                    except:
                        pass
                
                # Intentar extraer m²
                m2_match = re.search(r'(\d+)\s*m[²2]', text_content, re.IGNORECASE)
                if m2_match:
                    prop_data['metros_cuadrados'] = int(m2_match.group(1))
                
                # Intentar extraer dormitorios
                dorm_match = re.search(r'(\d+)\s*dorm', text_content, re.IGNORECASE)
                if dorm_match:
                    prop_data['dormitorios'] = int(dorm_match.group(1))
                
                # Intentar extraer baños
                bath_match = re.search(r'(\d+)\s*ba[ñn]', text_content, re.IGNORECASE)
                if bath_match:
                    prop_data['banos'] = int(bath_match.group(1))
                
                # Solo agregar si tiene al menos título o precio
                if prop_data['titulo'] or prop_data['precio'] > 0:
                    properties.append(prop_data)
                    logger.info(f"Fallback: propiedad {i+1} extraída - {prop_data['titulo'][:50]}")
            
            except Exception as e:
                logger.warning(f"Error extrayendo contenedor {i}: {str(e)}")
                continue
        
        logger.info(f"Fallback: {len(properties)} propiedades extraídas exitosamente")
        return properties
        
    except Exception as e:
        logger.error(f"Error en extracción fallback múltiple: {str(e)}")
        return []


def clean_property_data(data):
    """
    Limpia y valida los datos de una propiedad individual.
    """
    cleaned = {}
    
    # Limpiar título
    cleaned['titulo'] = str(data.get('titulo', 'Sin título'))[:500].strip()
    
    # Limpiar precio - eliminar caracteres no numéricos
    precio_raw = data.get('precio', 0)
    if isinstance(precio_raw, str):
        precio_raw = re.sub(r'[^\d]', '', precio_raw)
    try:
        cleaned['precio'] = float(precio_raw) if precio_raw else 0
    except:
        cleaned['precio'] = 0
    
    # Limpiar precio UF
    precio_uf_raw = data.get('precio_uf')
    if precio_uf_raw:
        if isinstance(precio_uf_raw, str):
            precio_uf_raw = re.sub(r'[^\d.]', '', precio_uf_raw)
        try:
            cleaned['precio_uf'] = float(precio_uf_raw)
        except:
            cleaned['precio_uf'] = None
    else:
        cleaned['precio_uf'] = None
    
    # Campos numéricos
    for field in ['metros_cuadrados', 'metros_terreno', 'dormitorios', 'banos', 'estacionamientos']:
        value = data.get(field, 0)
        if isinstance(value, str):
            value = re.sub(r'[^\d]', '', value)
        try:
            cleaned[field] = int(float(value)) if value else 0
        except:
            cleaned[field] = 0
    
    # Campos de texto
    for field in ['descripcion', 'direccion', 'comuna', 'ciudad', 'region', 
                  'codigo_propiedad', 'nombre_contacto', 'telefono_contacto', 
                  'email_contacto', 'inmobiliaria']:
        cleaned[field] = str(data.get(field, ''))[:500].strip() if data.get(field) else ''
    
    # Tipo de operación
    tipo_op = str(data.get('tipo_operacion', 'venta')).lower()
    if 'arriendo' in tipo_op or 'alquiler' in tipo_op:
        cleaned['tipo_operacion'] = 'arriendo'
    else:
        cleaned['tipo_operacion'] = 'venta'
    
    # Arrays
    cleaned['amenidades'] = data.get('amenidades', []) if isinstance(data.get('amenidades'), list) else []
    cleaned['imagenes_urls'] = data.get('imagenes_urls', []) if isinstance(data.get('imagenes_urls'), list) else []
    
    # Otros campos opcionales
    cleaned['ano_construccion'] = data.get('ano_construccion')
    cleaned['piso'] = data.get('piso')
    cleaned['gastos_comunes'] = data.get('gastos_comunes')
    cleaned['contribuciones'] = data.get('contribuciones')
    
    return cleaned


def create_or_update_property(data, url_obj, property_type):
    """Crear o actualizar una propiedad en la base de datos"""
    
    # Limpiar datos primero
    data = clean_property_data(data)
    
    # Datos comunes
    common_data = {
        'titulo': data['titulo'] or 'Sin título',
        'descripcion': data['descripcion'][:1000] if data['descripcion'] else '',
        'precio': data['precio'],
        'precio_uf': data['precio_uf'],
        'tipo_operacion': data['tipo_operacion'],
        'metros_cuadrados': data['metros_cuadrados'],
        'metros_terreno': data['metros_terreno'] or None,
        'dormitorios': data['dormitorios'],
        'banos': data['banos'],
        'estacionamientos': data['estacionamientos'],
        'direccion': data['direccion'][:500],
        'comuna': data['comuna'][:100] or 'Sin comuna',
        'ciudad': data['ciudad'][:100] or 'Santiago',
        'region': data['region'][:100] or 'Metropolitana',
        'ano_construccion': data['ano_construccion'],
        'piso': data['piso'],
        'gastos_comunes': data['gastos_comunes'],
        'contribuciones': data['contribuciones'],
        'amenidades': data['amenidades'],
        'url_fuente': url_obj.url,
        'sitio_origen': url_obj.site_name,
        'codigo_propiedad': data['codigo_propiedad'],
        'imagenes_urls': data['imagenes_urls'],
        'nombre_contacto': data['nombre_contacto'],
        'telefono_contacto': data['telefono_contacto'],
        'email_contacto': data['email_contacto'],
        'inmobiliaria': data['inmobiliaria'],
        'url_scrape': url_obj,
    }
    
    # Crear o actualizar según tipo
    if property_type == 'casa':
        specific_data = {
            'tipo_casa': data.get('tipo_casa', 'independiente'),
            'tiene_patio': data.get('tiene_patio', False),
            'metros_patio': data.get('metros_patio'),
            'tiene_quincho': data.get('tiene_quincho', False),
            'tiene_piscina': data.get('tiene_piscina', False),
            'numero_pisos': data.get('numero_pisos', 1),
        }
        obj, created = Casa.objects.update_or_create(
            url_fuente=url_obj.url,
            codigo_propiedad=data['codigo_propiedad'] or f"auto_{hash(data['titulo'])}",
            defaults={**common_data, **specific_data}
        )
        
    elif property_type == 'departamento':
        specific_data = {
            'tiene_balcon': data.get('tiene_balcon', False),
            'tiene_terraza': data.get('tiene_terraza', False),
            'amoblado': data.get('amoblado', False),
            'acepta_mascotas': data.get('acepta_mascotas', False),
        }
        obj, created = Departamento.objects.update_or_create(
            url_fuente=url_obj.url,
            codigo_propiedad=data['codigo_propiedad'] or f"auto_{hash(data['titulo'])}",
            defaults={**common_data, **specific_data}
        )
        
    elif property_type == 'terreno':
        specific_data = {
            'tipo_terreno': data.get('tipo_terreno', 'urbano'),
            'tiene_agua': data.get('tiene_agua', False),
            'tiene_luz': data.get('tiene_luz', False),
            'tiene_alcantarillado': data.get('tiene_alcantarillado', False),
            'es_esquina': data.get('es_esquina', False),
        }
        obj, created = Terreno.objects.update_or_create(
            url_fuente=url_obj.url,
            codigo_propiedad=data['codigo_propiedad'] or f"auto_{hash(data['titulo'])}",
            defaults={**common_data, **specific_data}
        )
        
    elif property_type == 'casa_prefabricada':
        specific_data = {
            'tipo_prefabricada': data.get('tipo_prefabricada', 'modular'),
            'material_principal': data.get('material_principal', 'madera'),
            'es_transportable': data.get('es_transportable', True),
            'tiempo_instalacion_dias': data.get('tiempo_instalacion_dias'),
        }
        obj, created = CasaPrefabricada.objects.update_or_create(
            url_fuente=url_obj.url,
            codigo_propiedad=data['codigo_propiedad'] or f"auto_{hash(data['titulo'])}",
            defaults={**common_data, **specific_data}
        )
    
    return obj, created


@shared_task(bind=True, max_retries=3)
def scrape_url_task(self, url_id):
    """
    Tarea para scrapear TODAS las propiedades de una URL (página de listado)
    """
    start_time = time.time()
    
    try:
        url_obj = URLToScrape.objects.get(id=url_id)
    except URLToScrape.DoesNotExist:
        logger.error(f"URL con ID {url_id} no existe")
        return
    
    # Crear log
    scraping_log = ScrapingLog.objects.create(
        url_scrape=url_obj,
        status='started'
    )
    
    # Actualizar estado de URL
    url_obj.status = 'in_progress'
    url_obj.save()
    
    try:
        logger.info(f"🔍 Iniciando scraping de: {url_obj.url}")
        
        # Obtener configuración
        scraper_config = get_scraper_config()
        
        # Extraer TODAS las propiedades de la página
        properties_data = extract_properties_data(url_obj.url, scraper_config)
        
        if not properties_data or len(properties_data) == 0:
            raise Exception("No se encontraron propiedades en la URL")
        
        logger.info(f"📋 Encontradas {len(properties_data)} propiedades para procesar")
        
        # Procesar cada propiedad
        created_count = 0
        updated_count = 0
        failed_count = 0
        
        for i, prop_data in enumerate(properties_data, 1):
            try:
                # Determinar tipo de propiedad
                property_type = determine_property_type(prop_data)
                
                # Crear o actualizar propiedad
                obj, created = create_or_update_property(prop_data, url_obj, property_type)
                
                if created:
                    created_count += 1
                    logger.info(f"  ✅ [{i}/{len(properties_data)}] Creada: {obj.titulo[:50]}")
                else:
                    updated_count += 1
                    logger.info(f"  🔄 [{i}/{len(properties_data)}] Actualizada: {obj.titulo[:50]}")
                
            except Exception as e:
                failed_count += 1
                logger.error(f"  ❌ [{i}/{len(properties_data)}] Error procesando propiedad: {str(e)}")
                continue
        
        # Actualizar log
        scraping_log.status = 'completed' if failed_count == 0 else 'partial'
        scraping_log.properties_found = len(properties_data)
        scraping_log.properties_created = created_count
        scraping_log.properties_updated = updated_count
        scraping_log.properties_failed = failed_count
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.save()
        
        # Marcar URL como scrapeada exitosamente
        url_obj.mark_as_scraped(success=True)
        
        logger.info(f"✅ Scraping completado: {created_count} creadas, {updated_count} actualizadas, {failed_count} fallidas")
        
        return {
            'status': 'success',
            'url': url_obj.url,
            'properties_found': len(properties_data),
            'created': created_count,
            'updated': updated_count,
            'failed': failed_count
        }
        
    except Exception as e:
        logger.error(f"❌ Error en scraping de {url_obj.url}: {str(e)}")
        
        # Actualizar log de error
        scraping_log.status = 'failed'
        scraping_log.error_message = str(e)
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.save()
        
        # Marcar URL como fallida
        url_obj.mark_as_scraped(success=False, error_message=str(e))
        
        # Reintentar si aún hay reintentos disponibles
        if self.request.retries < self.max_retries:
            raise self.retry(exc=e, countdown=60 * (self.request.retries + 1))
        
        return {
            'status': 'failed',
            'url': url_obj.url,
            'error': str(e)
        }


@shared_task
def scrape_pending_urls():
    """
    Tarea periódica para scrapear todas las URLs pendientes
    """
    now = timezone.now()
    
    urls_to_scrape = URLToScrape.objects.filter(
        Q(is_active=True) &
        (Q(next_scrape_at__lte=now) | Q(next_scrape_at__isnull=True)) &
        ~Q(status='in_progress')
    )
    
    logger.info(f"Encontradas {urls_to_scrape.count()} URLs para scrapear")
    
    tasks = []
    for url_obj in urls_to_scrape:
        task = scrape_url_task.delay(url_obj.id)
        tasks.append({
            'url_id': url_obj.id,
            'task_id': task.id
        })
    
    return {
        'message': f'{len(tasks)} tareas de scraping iniciadas',
        'tasks': tasks
    }


@shared_task
def cleanup_old_logs():
    """Tarea para limpiar logs antiguos (más de 30 días)"""
    thirty_days_ago = timezone.now() - timedelta(days=30)
    deleted_count = ScrapingLog.objects.filter(
        started_at__lt=thirty_days_ago
    ).delete()[0]
    
    logger.info(f"Eliminados {deleted_count} logs antiguos")
    
    return {
        'deleted_count': deleted_count
    }


@shared_task
def deactivate_failed_urls():
    """Desactivar URLs que han fallado más de 5 veces consecutivas"""
    urls_to_deactivate = URLToScrape.objects.filter(
        is_active=True,
        failed_scrapes__gte=5,
        successful_scrapes=0
    )
    
    count = urls_to_deactivate.update(is_active=False)
    
    logger.info(f"Desactivadas {count} URLs por múltiples fallos")
    
    return {
        'deactivated_count': count
    }