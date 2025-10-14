import logging
from celery import shared_task
from django.utils import timezone
from django.conf import settings
from django.db.models import Q
from datetime import timedelta
import time

from scrapegraphai.graphs import SmartScraperGraph
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)

logger = logging.getLogger(__name__)


def get_scraper_config():
    """Obtener configuración del scraper con Ollama"""
    return {
        "llm": {
            "model": "ollama/llama3.2",
            "base_url": settings.OLLAMA_BASE_URL,
            "temperature": 0,
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


def extract_property_data(url, scraper_config):
    """Extraer datos de propiedad usando ScrapeGraphAI"""
    
    prompt = """
    Extrae TODA la información disponible de esta propiedad inmobiliaria.
    Retorna un objeto JSON con los siguientes campos (usa null si no encuentras el dato):
    
    {
        "titulo": "título de la propiedad",
        "descripcion": "descripción completa",
        "precio": número (solo el valor numérico),
        "precio_uf": número o null,
        "tipo_operacion": "venta", "arriendo" o "venta_arriendo",
        "metros_cuadrados": número,
        "metros_terreno": número o null,
        "dormitorios": número,
        "banos": número,
        "estacionamientos": número,
        "direccion": "dirección completa",
        "comuna": "comuna",
        "ciudad": "ciudad",
        "region": "región",
        "ano_construccion": número o null,
        "piso": número o null,
        "gastos_comunes": número o null,
        "contribuciones": número o null,
        "amenidades": ["amenidad1", "amenidad2"],
        "codigo_propiedad": "código si existe",
        "imagenes_urls": ["url1", "url2"],
        "nombre_contacto": "nombre",
        "telefono_contacto": "teléfono",
        "email_contacto": "email",
        "inmobiliaria": "nombre inmobiliaria",
        
        // Específicos para casas
        "tipo_casa": "pareada", "independiente", "condominio" o "villa",
        "tiene_patio": true/false,
        "metros_patio": número o null,
        "tiene_quincho": true/false,
        "tiene_piscina": true/false,
        "numero_pisos": número,
        
        // Específicos para departamentos
        "tiene_balcon": true/false,
        "tiene_terraza": true/false,
        "amoblado": true/false,
        "acepta_mascotas": true/false,
        
        // Específicos para terrenos
        "tipo_terreno": "urbano", "rural", "industrial" o "comercial",
        "tiene_agua": true/false,
        "tiene_luz": true/false,
        "tiene_alcantarillado": true/false,
        "es_esquina": true/false,
        
        // Específicos para casas prefabricadas
        "tipo_prefabricada": "modular", "container", "movil" o "tiny_house",
        "material_principal": "madera", "acero", "concreto" o "mixto",
        "es_transportable": true/false,
        "tiempo_instalacion_dias": número o null
    }
    """
    
    try:
        smart_scraper_graph = SmartScraperGraph(
            prompt=prompt,
            source=url,
            config=scraper_config
        )
        
        result = smart_scraper_graph.run()
        return result
        
    except Exception as e:
        logger.error(f"Error en scraping de {url}: {str(e)}")
        return None


def create_or_update_property(data, url_obj, property_type):
    """Crear o actualizar una propiedad en la base de datos"""
    
    # Datos comunes
    common_data = {
        'titulo': data.get('titulo', ''),
        'descripcion': data.get('descripcion', ''),
        'precio': data.get('precio', 0),
        'precio_uf': data.get('precio_uf'),
        'tipo_operacion': data.get('tipo_operacion', 'venta'),
        'metros_cuadrados': data.get('metros_cuadrados', 0),
        'metros_terreno': data.get('metros_terreno'),
        'dormitorios': data.get('dormitorios', 0),
        'banos': data.get('banos', 0),
        'estacionamientos': data.get('estacionamientos', 0),
        'direccion': data.get('direccion', ''),
        'comuna': data.get('comuna', ''),
        'ciudad': data.get('ciudad', ''),
        'region': data.get('region', ''),
        'ano_construccion': data.get('ano_construccion'),
        'piso': data.get('piso'),
        'gastos_comunes': data.get('gastos_comunes'),
        'contribuciones': data.get('contribuciones'),
        'amenidades': data.get('amenidades', []),
        'url_fuente': url_obj.url,
        'sitio_origen': url_obj.site_name,
        'codigo_propiedad': data.get('codigo_propiedad'),
        'imagenes_urls': data.get('imagenes_urls', []),
        'nombre_contacto': data.get('nombre_contacto'),
        'telefono_contacto': data.get('telefono_contacto'),
        'email_contacto': data.get('email_contacto'),
        'inmobiliaria': data.get('inmobiliaria'),
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
            codigo_propiedad=data.get('codigo_propiedad'),
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
            codigo_propiedad=data.get('codigo_propiedad'),
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
            codigo_propiedad=data.get('codigo_propiedad'),
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
            codigo_propiedad=data.get('codigo_propiedad'),
            defaults={**common_data, **specific_data}
        )
    
    return obj, created


@shared_task(bind=True, max_retries=3)
def scrape_url_task(self, url_id):
    """
    Tarea para scrapear una URL específica
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
        logger.info(f"Iniciando scraping de: {url_obj.url}")
        
        # Obtener configuración
        scraper_config = get_scraper_config()
        
        # Extraer datos
        data = extract_property_data(url_obj.url, scraper_config)
        
        if not data:
            raise Exception("No se pudieron extraer datos de la URL")
        
        # Determinar tipo de propiedad
        property_type = determine_property_type(data)
        logger.info(f"Tipo de propiedad detectado: {property_type}")
        
        # Crear o actualizar propiedad
        obj, created = create_or_update_property(data, url_obj, property_type)
        
        # Actualizar log
        scraping_log.status = 'completed'
        scraping_log.properties_found = 1
        scraping_log.properties_created = 1 if created else 0
        scraping_log.properties_updated = 0 if created else 1
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.response_data = data
        scraping_log.save()
        
        # Marcar URL como scrapeada exitosamente
        url_obj.mark_as_scraped(success=True)
        
        logger.info(f"Scraping completado exitosamente para: {url_obj.url}")
        
        return {
            'status': 'success',
            'url': url_obj.url,
            'property_type': property_type,
            'created': created
        }
        
    except Exception as e:
        logger.error(f"Error en scraping de {url_obj.url}: {str(e)}")
        
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
    
    # Buscar URLs que deben ser scrapeadas
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
    """
    Tarea para limpiar logs antiguos (más de 30 días)
    """
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
    """
    Desactivar URLs que han fallado más de 5 veces consecutivas
    """
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