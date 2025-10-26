import logging
import json
import re
from celery import shared_task
from django.utils import timezone
from django.conf import settings
from django.db.models import Q
from datetime import timedelta
import time
from bs4 import BeautifulSoup
import requests
from typing import List, Dict, Any, Optional

from scrapegraphai.graphs import SmartScraperGraph
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)

logger = logging.getLogger(__name__)


class PropertyExtractor:
    """Clase mejorada para extracción de propiedades"""
    
    @staticmethod
    def extract_structured_data(soup: BeautifulSoup) -> List[Dict]:
        """
        Extrae datos estructurados de manera más inteligente
        buscando patrones comunes en sitios inmobiliarios
        """
        properties = []
        
        # Patrones comunes de contenedores de propiedades
        container_patterns = [
            {'tag': 'article', 'class_pattern': r'(property|listing|card|item|resultado)'},
            {'tag': 'div', 'class_pattern': r'(property|listing|card|item|resultado|anuncio|propiedad)'},
            {'tag': 'li', 'class_pattern': r'(property|listing|result|item)'},
            {'tag': 'section', 'class_pattern': r'(property|listing)'}
        ]
        
        # Buscar contenedores de propiedades
        potential_containers = []
        for pattern in container_patterns:
            elements = soup.find_all(
                pattern['tag'],
                class_=re.compile(pattern['class_pattern'], re.I)
            )
            potential_containers.extend(elements)
        
        # Si no encontramos contenedores específicos, buscar por estructura
        if not potential_containers:
            # Buscar elementos que contengan precio Y título
            potential_containers = soup.find_all(
                lambda tag: tag.name in ['div', 'article', 'li'] and
                tag.find(text=re.compile(r'\$|UF', re.I)) and
                len(tag.get_text(strip=True)) > 50
            )
        
        logger.info(f"Encontrados {len(potential_containers)} contenedores potenciales")
        
        for container in potential_containers[:50]:  # Procesar máximo 50
            prop_data = PropertyExtractor._extract_from_container(container)
            if prop_data and (prop_data.get('precio', 0) > 0 or prop_data.get('titulo')):
                properties.append(prop_data)
        
        return properties
    
    @staticmethod
    def _extract_from_container(container) -> Dict:
        """Extrae datos de un contenedor individual"""
        data = {
            'titulo': '',
            'precio': 0,
            'precio_uf': None,
            'metros_cuadrados': 0,
            'dormitorios': 0,
            'banos': 0,
            'estacionamientos': 0,
            'direccion': '',
            'comuna': '',
            'ciudad': 'Santiago',
            'region': 'Metropolitana',
            'descripcion': '',
            'tipo_operacion': 'venta',
            'url_propiedad': None,
            'imagenes_urls': []
        }
        
        text = container.get_text(' ', strip=True)
        
        # Extraer título (primer h1-h6 o primer link con texto largo)
        title_elem = container.find(['h1', 'h2', 'h3', 'h4', 'h5', 'h6'])
        if not title_elem:
            title_elem = container.find('a', string=lambda x: x and len(x) > 20)
        if title_elem:
            data['titulo'] = title_elem.get_text(strip=True)[:500]
        
        # Detectar tipo de operación
        text_lower = text.lower()
        if 'arriendo' in text_lower or 'arrendar' in text_lower or 'alquiler' in text_lower:
            data['tipo_operacion'] = 'arriendo'
        elif 'venta' in text_lower or 'vende' in text_lower:
            data['tipo_operacion'] = 'venta'
        
        # Extraer precio con mejor regex
        price_patterns = [
            (r'\$\s*([\d.,]+)(?:\s*millones?)?', 1000000),  # Millones
            (r'UF\s*([\d.,]+)', None),  # UF
            (r'([\d.,]+)\s*(?:UF)', None),  # UF alternativo
            (r'\$\s*([\d.,]+)', 1),  # Precio normal
        ]
        
        for pattern, multiplier in price_patterns:
            match = re.search(pattern, text, re.I)
            if match:
                try:
                    value_str = match.group(1).replace('.', '').replace(',', '.')
                    value = float(value_str)
                    if 'UF' in pattern:
                        data['precio_uf'] = value
                        # Convertir UF a pesos (aproximado)
                        data['precio'] = value * 37000
                    else:
                        if multiplier and 'millones' in match.group(0).lower():
                            data['precio'] = value * multiplier
                        else:
                            data['precio'] = value * (multiplier or 1)
                    break
                except:
                    pass
        
        # Extraer metros cuadrados
        m2_patterns = [
            r'(\d+(?:[.,]\d+)?)\s*(?:m[²2]|mts?2|metros?\s*cuadrados?)',
            r'(?:superficie|area|tamaño)[:\s]*(\d+(?:[.,]\d+)?)',
        ]
        for pattern in m2_patterns:
            match = re.search(pattern, text, re.I)
            if match:
                try:
                    data['metros_cuadrados'] = float(
                        match.group(1).replace(',', '.')
                    )
                    break
                except:
                    pass
        
        # Extraer dormitorios
        dorm_patterns = [
            r'(\d+)\s*(?:dorm|dormitorio|habitacion|pieza)',
            r'(?:dorm|dormitorio)[s:\s]*(\d+)',
        ]
        for pattern in dorm_patterns:
            match = re.search(pattern, text, re.I)
            if match:
                data['dormitorios'] = int(match.group(1))
                break
        
        # Extraer baños
        bath_patterns = [
            r'(\d+)\s*(?:baño|bano|bath)',
            r'(?:baño|bano)[s:\s]*(\d+)',
        ]
        for pattern in bath_patterns:
            match = re.search(pattern, text, re.I)
            if match:
                data['banos'] = int(match.group(1))
                break
        
        # Extraer estacionamientos
        parking_patterns = [
            r'(\d+)\s*(?:estacionamiento|parking|garage)',
            r'(?:estacionamiento|parking)[s:\s]*(\d+)',
        ]
        for pattern in parking_patterns:
            match = re.search(pattern, text, re.I)
            if match:
                data['estacionamientos'] = int(match.group(1))
                break
        
        # Extraer ubicación mejorada
        location_patterns = [
            r'(?:en\s+|comuna\s+|ubicado\s+en\s+)([A-Z][a-záñ]+(?:\s+[A-Z][a-záñ]+)*)',
            r'([A-Z][a-záñ]+(?:\s+[A-Z][a-záñ]+)*),\s*(?:Santiago|RM)',
            r'([A-Z][a-záñ]+(?:\s+[A-Z][a-záñ]+)*),\s*(?:Metropolitana)',
        ]
        for pattern in location_patterns:
            match = re.search(pattern, text)
            if match:
                data['comuna'] = match.group(1).strip()
                break
        
        # Lista de comunas conocidas de Santiago
        comunas_santiago = [
            'Las Condes', 'Providencia', 'Vitacura', 'Lo Barnechea', 'La Reina',
            'Ñuñoa', 'Santiago Centro', 'San Miguel', 'Macul', 'La Florida',
            'Peñalolén', 'La Cisterna', 'San Joaquín', 'La Granja', 'El Bosque',
            'Pedro Aguirre Cerda', 'Lo Espejo', 'Estación Central', 'Cerrillos',
            'Maipú', 'Quinta Normal', 'Lo Prado', 'Pudahuel', 'Cerro Navia',
            'Renca', 'Quilicura', 'Colina', 'Lampa', 'Puente Alto', 'San Bernardo'
        ]
        
        # Buscar comunas conocidas en el texto
        if not data['comuna']:
            for comuna in comunas_santiago:
                if comuna.lower() in text.lower():
                    data['comuna'] = comuna
                    break
        
        # Extraer dirección
        direccion_patterns = [
            r'(?:dirección|ubicado|calle|av\.|avenida)[:\s]*([^,\n]+)',
            r'([A-Z][a-záñ]+\s+\d+)',  # Calle con número
        ]
        for pattern in direccion_patterns:
            match = re.search(pattern, text, re.I)
            if match:
                data['direccion'] = match.group(1).strip()[:500]
                break
        
        # Extraer URL de la propiedad
        link = container.find('a', href=True)
        if link:
            href = link['href']
            if href.startswith('http'):
                data['url_propiedad'] = href
            elif href.startswith('/'):
                # Intentar construir URL completa si es relativa
                data['url_propiedad'] = href
        
        # Extraer imágenes
        images = container.find_all('img', src=True)
        for img in images[:5]:
            src = img['src']
            if src.startswith('http'):
                data['imagenes_urls'].append(src)
            elif src.startswith('//'):
                data['imagenes_urls'].append('https:' + src)
        
        # Extraer descripción si existe
        desc_elem = container.find('p')
        if desc_elem:
            data['descripcion'] = desc_elem.get_text(strip=True)[:1000]
        
        return data


def create_optimized_prompt(site_name: str = None) -> str:
    """
    Crea un prompt optimizado según el sitio web
    """
    base_prompt = """Eres un experto extractor de datos inmobiliarios.
    
TAREA: Extraer TODAS las propiedades de esta página de listado inmobiliario.

FORMATO DE SALIDA EXACTO - JSON Array:
```json
[
  {
    "titulo": "string - título descriptivo de la propiedad",
    "precio": number - solo números sin símbolos (ej: 150000000),
    "precio_uf": number o null - precio en UF si existe,
    "tipo_operacion": "venta" o "arriendo",
    "metros_cuadrados": number - superficie útil,
    "metros_terreno": number o null - superficie del terreno,
    "dormitorios": number - cantidad de dormitorios,
    "banos": number - cantidad de baños,
    "estacionamientos": number - cantidad de estacionamientos,
    "direccion": "string - dirección si está disponible",
    "comuna": "string - comuna o barrio",
    "ciudad": "string - ciudad (default: Santiago)",
    "region": "string - región (default: Metropolitana)",
    "descripcion": "string - descripción breve",
    "codigo_propiedad": "string - código único si existe",
    "url_propiedad": "string - link individual a la propiedad",
    "telefono_contacto": "string - teléfono si existe",
    "inmobiliaria": "string - nombre de la inmobiliaria"
  }
]
```

REGLAS CRÍTICAS:
1. Extrae TODAS las propiedades visibles, no solo una
2. Para precios: convierte todo a número (sin $, puntos, ni texto)
3. Si precio está en UF, guárdalo en precio_uf Y calcula precio (UF * 37000)
4. Si un campo no existe, usa null para strings, 0 para números
5. NO inventes datos - solo extrae lo que realmente está en la página
6. Detecta si es venta o arriendo por contexto
7. Si hay múltiples páginas de resultados, extrae solo la página actual
8. Para comuna, usa las comunas de Santiago si las detectas

IMPORTANTE: Tu respuesta debe ser ÚNICAMENTE el JSON array, sin explicaciones adicionales."""

    # Agregar instrucciones específicas por sitio si se conoce
    site_specific = {
        "Portal Inmobiliario": """
ESPECÍFICO para Portal Inmobiliario:
- El precio puede estar en formato "$150.000.000" o "UF 3.500"
- La comuna está usualmente después del título
- El código está en formato "Cod. XXXXX"
- Los metros cuadrados aparecen como "XX m²"
""",
        "Yapo": """
ESPECÍFICO para Yapo.cl:
- Precios pueden incluir "millones" (multiplicar por 1000000)
- La ubicación está en formato "Comuna, Región"
- Buscar clase "price" para precios
""",
        "TocToc": """
ESPECÍFICO para TocToc.com:
- Buscar clase "price" para precios
- Metros cuadrados en clase "surface"
- Comuna en clase "location"
""",
        "Mercado Libre": """
ESPECÍFICO para Mercado Libre:
- Precio en clase "price-tag-fraction"
- Ubicación en clase "ui-search-item__location"
""",
    }
    
    if site_name and site_name in site_specific:
        base_prompt += site_specific[site_name]
    
    return base_prompt


def smart_html_extraction(html_content: str, max_chars: int = 30000) -> str:
    """
    Extracción inteligente del HTML relevante
    """
    soup = BeautifulSoup(html_content, 'html.parser')
    
    # Eliminar elementos no relevantes
    for tag in soup(['script', 'style', 'nav', 'header', 'footer', 
                    'noscript', 'iframe', 'svg', 'path', 'meta', 
                    'link', 'form', 'button', 'input', 'select', 'textarea']):
        tag.decompose()
    
    # Eliminar comentarios HTML
    for comment in soup.find_all(string=lambda text: isinstance(text, str) and text.strip().startswith('<!--')):
        comment.extract()
    
    # Buscar el contenedor principal de resultados
    main_container = None
    container_candidates = [
        soup.find('main'),
        soup.find('div', id=re.compile(r'result|listing|properties|grid', re.I)),
        soup.find('div', class_=re.compile(r'result|listing|properties|grid', re.I)),
        soup.find('section', class_=re.compile(r'result|listing|properties', re.I)),
        soup.find('ul', class_=re.compile(r'result|listing|properties', re.I))
    ]
    
    for candidate in container_candidates:
        if candidate:
            main_container = candidate
            break
    
    if not main_container:
        main_container = soup.body or soup
    
    # Extraer solo el contenido relevante
    relevant_html = str(main_container)
    
    # Si aún es muy largo, extraer solo texto con estructura
    if len(relevant_html) > max_chars:
        # Convertir a texto estructurado
        properties_data = []
        property_containers = main_container.find_all(
            ['article', 'div', 'li'],
            class_=re.compile(r'property|listing|card|item|result', re.I)
        )
        
        for container in property_containers[:30]:  # Máximo 30 propiedades
            text_block = []
            
            # Extraer texto importante preservando estructura
            for elem in container.find_all(['h1', 'h2', 'h3', 'h4', 'p', 'span', 'div']):
                text = elem.get_text(strip=True)
                if text and len(text) > 3:
                    # Agregar indicador del tipo de elemento
                    if elem.name in ['h1', 'h2', 'h3', 'h4']:
                        text_block.append(f"[TITULO] {text}")
                    elif '$' in text or 'UF' in text.upper():
                        text_block.append(f"[PRECIO] {text}")
                    elif any(x in text.lower() for x in ['m2', 'mts', 'metros', 'm²']):
                        text_block.append(f"[SUPERFICIE] {text}")
                    elif any(x in text.lower() for x in ['dorm', 'hab', 'pieza']):
                        text_block.append(f"[DORMITORIOS] {text}")
                    elif any(x in text.lower() for x in ['baño', 'bano']):
                        text_block.append(f"[BANOS] {text}")
                    elif any(x in text.lower() for x in ['estacion', 'parking', 'garage']):
                        text_block.append(f"[ESTACIONAMIENTO] {text}")
                    elif len(text) > 100:  # Posible descripción
                        text_block.append(f"[DESCRIPCION] {text[:200]}")
                    else:
                        text_block.append(text)
            
            if text_block:
                properties_data.append('\n'.join(text_block))
                properties_data.append('---SEPARADOR---')
        
        relevant_html = '\n\n'.join(properties_data)
    
    return relevant_html[:max_chars]


def get_scraper_config():
    """Obtener configuración del scraper con Ollama"""
    return {
        "llm": {
            "model": "ollama/llama3.2",
            "base_url": settings.OLLAMA_BASE_URL,
            "temperature": 0.1,
            "top_p": 0.9,
            "num_ctx": 8192,
            "repeat_penalty": 1.1,
        },
        "embeddings": {
            "model": "ollama/nomic-embed-text",
            "base_url": settings.OLLAMA_BASE_URL,
        },
        "verbose": True,
        "headless": True,
    }


def extract_properties_with_llm(url: str, scraper_config: dict) -> List[Dict]:
    """
    Versión mejorada de extracción con LLM
    """
    try:
        logger.info(f"🔍 Iniciando extracción inteligente con LLM de: {url}")
        
        # Obtener el HTML
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8',
            'Accept-Language': 'es-ES,es;q=0.9,en;q=0.8',
            'Accept-Encoding': 'gzip, deflate, br',
            'Connection': 'keep-alive',
            'Upgrade-Insecure-Requests': '1'
        }
        
        response = requests.get(url, headers=headers, timeout=30)
        response.raise_for_status()
        
        # Detectar el sitio web
        site_name = None
        url_lower = url.lower()
        if 'portalinmobiliario' in url_lower:
            site_name = "Portal Inmobiliario"
        elif 'yapo' in url_lower:
            site_name = "Yapo"
        elif 'toctoc' in url_lower:
            site_name = "TocToc"
        elif 'mercadolibre' in url_lower:
            site_name = "Mercado Libre"
        
        logger.info(f"Sitio detectado: {site_name or 'Desconocido'}")
        
        # Extraer contenido relevante
        cleaned_content = smart_html_extraction(response.text)
        
        logger.info(f"Contenido limpiado: {len(cleaned_content)} caracteres")
        
        # Crear prompt optimizado
        prompt = create_optimized_prompt(site_name)
        
        # Ejecutar scraper
        smart_scraper = SmartScraperGraph(
            prompt=prompt,
            source=cleaned_content,
            config=scraper_config
        )
        
        result = smart_scraper.run()
        
        # Validar resultado
        if isinstance(result, str):
            # Intentar parsear como JSON
            try:
                # Limpiar posibles caracteres extra
                result = result.strip()
                if result.startswith('```json'):
                    result = result[7:]
                if result.endswith('```'):
                    result = result[:-3]
                result = json.loads(result.strip())
            except json.JSONDecodeError as e:
                logger.error(f"Error parseando JSON del LLM: {str(e)}")
                logger.debug(f"Resultado raw: {result[:500]}")
                result = []
        
        if not isinstance(result, list):
            result = [result] if result else []
        
        logger.info(f"✅ LLM extrajo {len(result)} propiedades")
        return result
        
    except Exception as e:
        logger.error(f"Error en extracción con LLM: {str(e)}")
        return []


def hybrid_extraction(url: str, url_obj) -> List[Dict]:
    """
    Extracción híbrida: combina LLM + BeautifulSoup
    """
    all_properties = []
    
    try:
        # Primero intentar con LLM
        scraper_config = get_scraper_config()
        llm_properties = extract_properties_with_llm(url, scraper_config)
        
        if llm_properties:
            logger.info(f"✅ LLM extrajo {len(llm_properties)} propiedades")
            all_properties.extend(llm_properties)
        else:
            logger.warning("⚠️ LLM no extrajo propiedades, intentando con BeautifulSoup")
        
        # Complementar con BeautifulSoup
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        response = requests.get(url, headers=headers, timeout=30)
        soup = BeautifulSoup(response.text, 'html.parser')
        
        extractor = PropertyExtractor()
        bs_properties = extractor.extract_structured_data(soup)
        
        if bs_properties:
            logger.info(f"✅ BeautifulSoup extrajo {len(bs_properties)} propiedades")
            
            # Merge inteligente - evitar duplicados
            existing_titles = {p.get('titulo', '').lower().strip() for p in all_properties}
            existing_prices = {p.get('precio', 0) for p in all_properties}
            
            for prop in bs_properties:
                prop_title = prop.get('titulo', '').lower().strip()
                prop_price = prop.get('precio', 0)
                
                # Evitar duplicados por título similar o mismo precio
                is_duplicate = False
                if prop_title:
                    for title in existing_titles:
                        if title and prop_title and (
                            title in prop_title or prop_title in title or
                            title[:30] == prop_title[:30]
                        ):
                            is_duplicate = True
                            break
                
                if not is_duplicate and prop_price in existing_prices and prop_price > 0:
                    is_duplicate = True
                
                if not is_duplicate:
                    all_properties.append(prop)
                    existing_titles.add(prop_title)
                    existing_prices.add(prop_price)
        
        logger.info(f"📊 Total después de merge: {len(all_properties)} propiedades únicas")
        
    except Exception as e:
        logger.error(f"Error en extracción híbrida: {str(e)}")
    
    return all_properties


def determine_property_type(data: Dict) -> str:
    """Determinar el tipo de propiedad basándose en los datos"""
    titulo = data.get('titulo', '').lower()
    descripcion = data.get('descripcion', '').lower()
    
    text = titulo + ' ' + descripcion
    
    # Lógica de detección mejorada
    if any(word in text for word in ['terreno', 'lote', 'sitio', 'parcela', 'predio']):
        return 'terreno'
    elif any(word in text for word in ['departamento', 'depto', 'apartamento', 'flat', 'pent-house', 'penthouse']):
        return 'departamento'
    elif any(word in text for word in ['prefabricada', 'modular', 'container', 'móvil', 'tiny house']):
        return 'casa_prefabricada'
    elif any(word in text for word in ['casa', 'chalet', 'villa', 'cabaña']):
        return 'casa'
    else:
        # Si no podemos determinar, usar metros cuadrados como guía
        metros = data.get('metros_cuadrados', 0)
        if metros > 500:
            return 'terreno'
        elif metros < 80:
            return 'departamento'
        else:
            return 'casa'


def clean_property_data(data: Dict) -> Dict:
    """
    Limpia y valida los datos de una propiedad individual.
    """
    cleaned = {}
    
    # Limpiar título
    titulo = data.get('titulo', '')
    if isinstance(titulo, str):
        cleaned['titulo'] = titulo.strip()[:500] or 'Sin título'
    else:
        cleaned['titulo'] = 'Sin título'
    
    # Limpiar descripción
    descripcion = data.get('descripcion', '')
    if isinstance(descripcion, str):
        cleaned['descripcion'] = descripcion.strip()[:1000]
    else:
        cleaned['descripcion'] = ''
    
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
    numeric_fields = {
        'metros_cuadrados': 0,
        'metros_terreno': 0,
        'dormitorios': 0,
        'banos': 0,
        'estacionamientos': 0
    }
    
    for field, default in numeric_fields.items():
        value = data.get(field, default)
        if isinstance(value, str):
            value = re.sub(r'[^\d]', '', value)
        try:
            cleaned[field] = int(float(value)) if value else default
        except:
            cleaned[field] = default
    
    # Campos de texto
    text_fields = [
        'direccion', 'comuna', 'ciudad', 'region',
        'codigo_propiedad', 'nombre_contacto', 'telefono_contacto',
        'email_contacto', 'inmobiliaria', 'url_propiedad'
    ]
    
    for field in text_fields:
        value = data.get(field, '')
        if isinstance(value, str):
            cleaned[field] = value.strip()[:500]
        else:
            cleaned[field] = ''
    
    # Valores por defecto para ubicación
    if not cleaned['ciudad']:
        cleaned['ciudad'] = 'Santiago'
    if not cleaned['region']:
        cleaned['region'] = 'Metropolitana'
    
    # Tipo de operación
    tipo_op = str(data.get('tipo_operacion', 'venta')).lower()
    if 'arriendo' in tipo_op or 'alquiler' in tipo_op or 'rent' in tipo_op:
        cleaned['tipo_operacion'] = 'arriendo'
    else:
        cleaned['tipo_operacion'] = 'venta'
    
    # Arrays
    cleaned['amenidades'] = data.get('amenidades', [])
    if not isinstance(cleaned['amenidades'], list):
        cleaned['amenidades'] = []
    
    cleaned['imagenes_urls'] = data.get('imagenes_urls', [])
    if not isinstance(cleaned['imagenes_urls'], list):
        cleaned['imagenes_urls'] = []
    
    # Otros campos opcionales
    cleaned['ano_construccion'] = data.get('ano_construccion')
    cleaned['piso'] = data.get('piso')
    cleaned['gastos_comunes'] = data.get('gastos_comunes')
    cleaned['contribuciones'] = data.get('contribuciones')
    
    return cleaned


def create_or_update_property(data: Dict, url_obj, property_type: str):
    """Crear o actualizar una propiedad en la base de datos"""
    
    # Limpiar datos primero
    data = clean_property_data(data)
    
    # Validación mínima
    if data['precio'] <= 0 and not data['titulo']:
        logger.warning("Propiedad sin precio ni título, omitiendo")
        return None, False
    
    # Generar código único si no existe
    if not data.get('codigo_propiedad'):
        # Crear código basado en título y precio
        import hashlib
        unique_str = f"{data['titulo']}{data['precio']}{data.get('comuna', '')}"
        data['codigo_propiedad'] = hashlib.md5(unique_str.encode()).hexdigest()[:10]
    
    # Datos comunes
    common_data = {
        'titulo': data['titulo'],
        'descripcion': data['descripcion'],
        'precio': data['precio'],
        'precio_uf': data['precio_uf'],
        'tipo_operacion': data['tipo_operacion'],
        'metros_cuadrados': data['metros_cuadrados'],
        'metros_terreno': data['metros_terreno'] or None,
        'dormitorios': data['dormitorios'],
        'banos': data['banos'],
        'estacionamientos': data['estacionamientos'],
        'direccion': data['direccion'][:500],
        'comuna': data['comuna'][:100],
        'ciudad': data['ciudad'][:100],
        'region': data['region'][:100],
        'ano_construccion': data['ano_construccion'],
        'piso': data['piso'],
        'gastos_comunes': data['gastos_comunes'],
        'contribuciones': data['contribuciones'],
        'amenidades': data['amenidades'],
        'url_fuente': data.get('url_propiedad', url_obj.url),
        'sitio_origen': url_obj.site_name,
        'codigo_propiedad': data['codigo_propiedad'],
        'imagenes_urls': data['imagenes_urls'][:10],  # Máximo 10 imágenes
        'nombre_contacto': data['nombre_contacto'],
        'telefono_contacto': data['telefono_contacto'],
        'email_contacto': data['email_contacto'],
        'inmobiliaria': data['inmobiliaria'],
        'url_scrape': url_obj,
    }
    
    try:
        # Crear o actualizar según tipo
        if property_type == 'casa':
            specific_data = {
                'tipo_casa': data.get('tipo_casa', 'independiente'),
                'tiene_patio': data.get('tiene_patio', False),
                'metros_patio': data.get('metros_patio'),
                'tiene_quincho': data.get('tiene_quincho', False),
                'tiene_piscina': data.get('tiene_piscina', False),
                'numero_pisos': data.get('numero_pisos', 1),
                'tiene_bodega': data.get('tiene_bodega', False),
            }
            obj, created = Casa.objects.update_or_create(
                codigo_propiedad=data['codigo_propiedad'],
                sitio_origen=url_obj.site_name,
                defaults={**common_data, **specific_data}
            )
            
        elif property_type == 'departamento':
            specific_data = {
                'tiene_balcon': data.get('tiene_balcon', False),
                'tiene_terraza': data.get('tiene_terraza', False),
                'metros_balcon': data.get('metros_balcon'),
                'amoblado': data.get('amoblado', False),
                'acepta_mascotas': data.get('acepta_mascotas', False),
                'total_pisos_edificio': data.get('total_pisos_edificio'),
                'tiene_porteria': data.get('tiene_porteria', False),
                'tiene_ascensor': data.get('tiene_ascensor', False),
                'tiene_bodega': data.get('tiene_bodega', False),
            }
            obj, created = Departamento.objects.update_or_create(
                codigo_propiedad=data['codigo_propiedad'],
                sitio_origen=url_obj.site_name,
                defaults={**common_data, **specific_data}
            )
            
        elif property_type == 'terreno':
            specific_data = {
                'tipo_terreno': data.get('tipo_terreno', 'urbano'),
                'forma_terreno': data.get('forma_terreno'),
                'frente_metros': data.get('frente_metros'),
                'fondo_metros': data.get('fondo_metros'),
                'tiene_agua': data.get('tiene_agua', False),
                'tiene_luz': data.get('tiene_luz', False),
                'tiene_alcantarillado': data.get('tiene_alcantarillado', False),
                'tiene_gas': data.get('tiene_gas', False),
                'es_esquina': data.get('es_esquina', False),
                'tiene_cerco': data.get('tiene_cerco', False),
                'uso_suelo': data.get('uso_suelo'),
            }
            obj, created = Terreno.objects.update_or_create(
                codigo_propiedad=data['codigo_propiedad'],
                sitio_origen=url_obj.site_name,
                defaults={**common_data, **specific_data}
            )
            
        elif property_type == 'casa_prefabricada':
            specific_data = {
                'tipo_prefabricada': data.get('tipo_prefabricada', 'modular'),
                'material_principal': data.get('material_principal', 'madera'),
                'es_transportable': data.get('es_transportable', True),
                'requiere_terreno': data.get('requiere_terreno', True),
                'tiempo_instalacion_dias': data.get('tiempo_instalacion_dias'),
                'incluye_instalacion': data.get('incluye_instalacion', False),
                'garantia_anos': data.get('garantia_anos'),
                'certificacion_energetica': data.get('certificacion_energetica'),
                'numero_modulos': data.get('numero_modulos', 1),
                'es_expandible': data.get('es_expandible', False),
            }
            obj, created = CasaPrefabricada.objects.update_or_create(
                codigo_propiedad=data['codigo_propiedad'],
                sitio_origen=url_obj.site_name,
                defaults={**common_data, **specific_data}
            )
        
        return obj, created
        
    except Exception as e:
        logger.error(f"Error creando/actualizando propiedad: {str(e)}")
        logger.debug(f"Datos problemáticos: {data}")
        return None, False


@shared_task(bind=True, max_retries=3)
def scrape_url_task(self, url_id):
    """
    Tarea mejorada de scraping con múltiples estrategias
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
    
    # Actualizar estado
    url_obj.status = 'in_progress'
    url_obj.save()
    
    try:
        logger.info(f"🚀 Iniciando scraping mejorado de: {url_obj.url}")
        logger.info(f"   Sitio: {url_obj.site_name}")
        logger.info(f"   Prioridad: {url_obj.priority}")
        
        # Usar extracción híbrida
        properties_data = hybrid_extraction(url_obj.url, url_obj)
        
        if not properties_data:
            raise Exception("No se encontraron propiedades en la página")
        
        logger.info(f"📊 Total de propiedades encontradas: {len(properties_data)}")
        
        # Procesar propiedades
        created_count = 0
        updated_count = 0
        failed_count = 0
        
        for i, prop_data in enumerate(properties_data, 1):
            try:
                # Limpiar datos
                prop_data = clean_property_data(prop_data)
                
                # Validar datos mínimos
                if not prop_data.get('titulo') and prop_data.get('precio', 0) <= 0:
                    logger.debug(f"Propiedad {i} sin datos suficientes, omitiendo")
                    failed_count += 1
                    continue
                
                # Determinar tipo de propiedad
                property_type = determine_property_type(prop_data)
                logger.debug(f"Propiedad {i} detectada como: {property_type}")
                
                # Crear o actualizar
                obj, created = create_or_update_property(prop_data, url_obj, property_type)
                
                if obj:
                    if created:
                        created_count += 1
                        logger.info(f"  ✅ [{i}/{len(properties_data)}] Nueva: {obj.titulo[:50]}")
                    else:
                        updated_count += 1
                        logger.debug(f"  🔄 [{i}/{len(properties_data)}] Actualizada: {obj.titulo[:50]}")
                else:
                    failed_count += 1
                    logger.warning(f"  ⚠️ [{i}/{len(properties_data)}] No se pudo procesar")
                    
            except Exception as e:
                failed_count += 1
                logger.error(f"  ❌ [{i}/{len(properties_data)}] Error procesando: {str(e)}")
                continue
        
        # Actualizar log
        scraping_log.status = 'completed' if failed_count == 0 else 'partial'
        scraping_log.properties_found = len(properties_data)
        scraping_log.properties_created = created_count
        scraping_log.properties_updated = updated_count
        scraping_log.properties_failed = failed_count
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.response_data = {
            'total_found': len(properties_data),
            'created': created_count,
            'updated': updated_count,
            'failed': failed_count
        }
        scraping_log.save()
        
        # Marcar URL como scrapeada
        url_obj.mark_as_scraped(success=(created_count + updated_count) > 0)
        
        logger.info(f"""
        ✅ Scraping completado para {url_obj.site_name}
        ⏱️ Tiempo: {time.time() - start_time:.2f}s
        📊 Resultados:
           - Encontradas: {len(properties_data)}
           - Creadas: {created_count}
           - Actualizadas: {updated_count}
           - Fallidas: {failed_count}
        """)
        
        return {
            'status': 'success',
            'url': url_obj.url,
            'site_name': url_obj.site_name,
            'found': len(properties_data),
            'created': created_count,
            'updated': updated_count,
            'failed': failed_count,
            'execution_time': round(time.time() - start_time, 2)
        }
        
    except Exception as e:
        logger.error(f"❌ Error crítico en scraping: {str(e)}")
        
        # Actualizar log de error
        scraping_log.status = 'failed'
        scraping_log.error_message = str(e)
        scraping_log.completed_at = timezone.now()
        scraping_log.execution_time_seconds = time.time() - start_time
        scraping_log.save()
        
        # Marcar URL como fallida
        url_obj.mark_as_scraped(success=False, error_message=str(e))
        
        # Reintentar si quedan intentos
        if self.request.retries < self.max_retries:
            logger.info(f"🔄 Reintentando en {60 * (self.request.retries + 1)} segundos...")
            raise self.retry(exc=e, countdown=60 * (self.request.retries + 1))
        
        return {
            'status': 'failed',
            'url': url_obj.url,
            'error': str(e),
            'execution_time': round(time.time() - start_time, 2)
        }


@shared_task
def scrape_pending_urls():
    """
    Tarea periódica para scrapear todas las URLs pendientes
    """
    now = timezone.now()
    
    # Buscar URLs pendientes o que necesitan actualizarse
    urls_to_scrape = URLToScrape.objects.filter(
        Q(is_active=True) &
        (Q(next_scrape_at__lte=now) | Q(next_scrape_at__isnull=True)) &
        ~Q(status='in_progress')
    )
    
    logger.info(f"📋 Encontradas {urls_to_scrape.count()} URLs pendientes de scraping")
    
    tasks = []
    for url_obj in urls_to_scrape:
        task = scrape_url_task.delay(url_obj.id)
        tasks.append({
            'url_id': url_obj.id,
            'task_id': task.id,
            'url': url_obj.url,
            'site': url_obj.site_name
        })
        logger.info(f"  ➤ Tarea iniciada para: {url_obj.site_name}")
    
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
    
    logger.info(f"🗑️ Eliminados {deleted_count} logs antiguos")
    
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
    
    count = urls_to_deactivate.update(is_active=False, status='disabled')
    
    if count > 0:
        logger.warning(f"⚠️ Desactivadas {count} URLs por múltiples fallos")
        
        # Log detalles de las URLs desactivadas
        for url in urls_to_deactivate[:10]:  # Mostrar máximo 10
            logger.info(f"   - {url.site_name}: {url.url[:50]}...")
    
    return {
        'deactivated_count': count
    }


@shared_task
def test_single_extraction(url: str, site_name: str = "Test"):
    """
    Tarea de prueba para extraer propiedades de una URL sin guardar en DB
    Útil para debugging
    """
    try:
        logger.info(f"🧪 TEST: Extrayendo propiedades de {url}")
        
        # Crear objeto URL temporal (no se guarda en DB)
        url_obj = URLToScrape(
            url=url,
            site_name=site_name,
            priority="high"
        )
        
        # Ejecutar extracción híbrida
        properties = hybrid_extraction(url, url_obj)
        
        if properties:
            logger.info(f"✅ TEST: Encontradas {len(properties)} propiedades")
            
            # Mostrar resumen de las primeras 3 propiedades
            for i, prop in enumerate(properties[:3], 1):
                logger.info(f"""
                Propiedad {i}:
                - Título: {prop.get('titulo', 'N/A')[:50]}
                - Precio: ${prop.get('precio', 0):,.0f}
                - UF: {prop.get('precio_uf', 'N/A')}
                - M²: {prop.get('metros_cuadrados', 0)}
                - Dormitorios: {prop.get('dormitorios', 0)}
                - Baños: {prop.get('banos', 0)}
                - Comuna: {prop.get('comuna', 'N/A')}
                - Tipo operación: {prop.get('tipo_operacion', 'N/A')}
                """)
            
            return {
                'status': 'success',
                'total_found': len(properties),
                'properties': properties[:5]  # Retornar solo las primeras 5
            }
        else:
            logger.warning("⚠️ TEST: No se encontraron propiedades")
            return {
                'status': 'warning',
                'message': 'No se encontraron propiedades',
                'properties': []
            }
            
    except Exception as e:
        logger.error(f"❌ TEST: Error en extracción - {str(e)}")
        return {
            'status': 'error',
            'error': str(e),
            'properties': []
        }