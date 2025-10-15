import re
import logging
from decimal import Decimal, InvalidOperation
from typing import Optional, Dict, Any, List

logger = logging.getLogger(__name__)


def clean_price(price_str: str) -> Optional[Decimal]:
    """
    Limpia y convierte una cadena de precio a Decimal
    
    Ejemplos:
    - "$1.500.000" -> 1500000
    - "UF 3.500" -> 3500
    - "1,500,000 CLP" -> 1500000
    """
    if not price_str:
        return None
    
    try:
        # Remover símbolos de moneda y espacios
        cleaned = re.sub(r'[^\d.,]', '', str(price_str))
        
        # Manejar formato chileno (punto como separador de miles, coma como decimal)
        if ',' in cleaned and '.' in cleaned:
            # Si tiene ambos, asumir formato chileno
            cleaned = cleaned.replace('.', '').replace(',', '.')
        elif '.' in cleaned:
            # Si solo tiene puntos, verificar si es separador de miles o decimal
            parts = cleaned.split('.')
            if len(parts[-1]) == 3:  # Probablemente separador de miles
                cleaned = cleaned.replace('.', '')
            # Si len(parts[-1]) <= 2, probablemente es decimal, dejar como está
        elif ',' in cleaned:
            # Si solo tiene comas, reemplazar por punto
            cleaned = cleaned.replace(',', '.')
        
        return Decimal(cleaned)
    except (InvalidOperation, ValueError) as e:
        logger.warning(f"No se pudo convertir precio '{price_str}': {e}")
        return None


def clean_number(number_str: str) -> Optional[int]:
    """
    Limpia y convierte una cadena a número entero
    """
    if not number_str:
        return None
    
    try:
        # Remover todo excepto dígitos
        cleaned = re.sub(r'\D', '', str(number_str))
        return int(cleaned) if cleaned else None
    except ValueError as e:
        logger.warning(f"No se pudo convertir número '{number_str}': {e}")
        return None


def clean_area(area_str: str) -> Optional[Decimal]:
    """
    Limpia y convierte una cadena de área a Decimal
    
    Ejemplos:
    - "120 m²" -> 120.00
    - "150,5 m2" -> 150.50
    """
    if not area_str:
        return None
    
    try:
        # Remover unidades (m², m2, mt2, etc.)
        cleaned = re.sub(r'(m[²2t]?|metros?|cuadrados?)', '', str(area_str), flags=re.IGNORECASE)
        # Limpiar y convertir
        return clean_price(cleaned)
    except Exception as e:
        logger.warning(f"No se pudo convertir área '{area_str}': {e}")
        return None


def extract_numbers_from_text(text: str) -> List[int]:
    """
    Extrae todos los números de un texto
    
    Ejemplo: "Casa con 3 dormitorios y 2 baños" -> [3, 2]
    """
    if not text:
        return []
    
    numbers = re.findall(r'\d+', text)
    return [int(n) for n in numbers]


def clean_address(address: str) -> str:
    """
    Limpia y normaliza una dirección
    """
    if not address:
        return ""
    
    # Convertir a título (primera letra mayúscula)
    cleaned = address.strip().title()
    
    # Normalizar espacios múltiples
    cleaned = re.sub(r'\s+', ' ', cleaned)
    
    return cleaned


def extract_comuna_ciudad(location_str: str) -> Dict[str, str]:
    """
    Intenta extraer comuna y ciudad de una cadena de ubicación
    
    Ejemplo: "Las Condes, Santiago" -> {"comuna": "Las Condes", "ciudad": "Santiago"}
    """
    result = {"comuna": "", "ciudad": ""}
    
    if not location_str:
        return result
    
    # Intentar dividir por coma
    parts = [p.strip() for p in location_str.split(',')]
    
    if len(parts) >= 2:
        result["comuna"] = parts[0]
        result["ciudad"] = parts[1]
    elif len(parts) == 1:
        result["comuna"] = parts[0]
    
    return result


def normalize_tipo_operacion(operacion_str: str) -> str:
    """
    Normaliza el tipo de operación
    """
    if not operacion_str:
        return 'venta'
    
    operacion_lower = operacion_str.lower()
    
    if 'venta' in operacion_lower and 'arriendo' in operacion_lower:
        return 'venta_arriendo'
    elif 'arriendo' in operacion_lower or 'alquiler' in operacion_lower or 'renta' in operacion_lower:
        return 'arriendo'
    else:
        return 'venta'


def extract_phone_numbers(text: str) -> List[str]:
    """
    Extrae números de teléfono de un texto
    
    Soporta formatos chilenos: +56 9 1234 5678, 912345678, etc.
    """
    if not text:
        return []
    
    # Patrón para números chilenos
    patterns = [
        r'\+56\s*9\s*\d{4}\s*\d{4}',  # +56 9 1234 5678
        r'\+56\s*\d{9}',               # +56912345678
        r'9\s*\d{4}\s*\d{4}',         # 9 1234 5678
        r'\d{9}',                      # 912345678
    ]
    
    phones = []
    for pattern in patterns:
        matches = re.findall(pattern, text)
        phones.extend(matches)
    
    # Limpiar duplicados
    return list(set(phones))


def extract_email(text: str) -> Optional[str]:
    """
    Extrae dirección de email de un texto
    """
    if not text:
        return None
    
    pattern = r'\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b'
    matches = re.findall(pattern, text)
    
    return matches[0] if matches else None


def validate_url(url: str) -> bool:
    """
    Valida que una URL sea correcta
    """
    if not url:
        return False
    
    pattern = r'^https?://.+'
    return bool(re.match(pattern, url))


def extract_codigo_propiedad(text: str) -> Optional[str]:
    """
    Intenta extraer código de propiedad de un texto
    
    Busca patrones como: COD-12345, #12345, ID: 12345, etc.
    """
    if not text:
        return None
    
    patterns = [
        r'COD[:\-\s]*([A-Z0-9]+)',
        r'#\s*([A-Z0-9]+)',
        r'ID[:\-\s]*([A-Z0-9]+)',
        r'REF[:\-\s]*([A-Z0-9]+)',
    ]
    
    for pattern in patterns:
        match = re.search(pattern, text, re.IGNORECASE)
        if match:
            return match.group(1)
    
    return None


def clean_boolean_field(value: Any) -> bool:
    """
    Convierte varios tipos de valores a booleano
    """
    if isinstance(value, bool):
        return value
    
    if isinstance(value, str):
        value_lower = value.lower().strip()
        return value_lower in ['true', 'yes', 'si', 'sí', '1', 'verdadero']
    
    if isinstance(value, (int, float)):
        return bool(value)
    
    return False


def generate_property_slug(titulo: str, codigo: Optional[str] = None) -> str:
    """
    Genera un slug único para una propiedad
    """
    import re
    from django.utils.text import slugify
    
    slug = slugify(titulo)
    
    if codigo:
        slug = f"{slug}-{codigo}"
    
    return slug[:100]  # Limitar longitud


def calculate_price_per_m2(precio: Decimal, metros: Decimal) -> Optional[Decimal]:
    """
    Calcula el precio por metro cuadrado
    """
    try:
        if metros and metros > 0:
            return round(precio / metros, 2)
    except Exception:
        pass
    return None


def detect_property_features(descripcion: str) -> Dict[str, bool]:
    """
    Detecta características de la propiedad desde la descripción
    """
    if not descripcion:
        return {}
    
    desc_lower = descripcion.lower()
    
    features = {
        'tiene_piscina': any(word in desc_lower for word in ['piscina', 'alberca', 'pool']),
        'tiene_jardin': any(word in desc_lower for word in ['jardín', 'jardin', 'patio', 'garden']),
        'tiene_terraza': any(word in desc_lower for word in ['terraza', 'terrace', 'balcón', 'balcon']),
        'tiene_estacionamiento': any(word in desc_lower for word in ['estacionamiento', 'parking', 'garage', 'cochera']),
        'amoblado': any(word in desc_lower for word in ['amoblado', 'amueblado', 'furnished', 'equipado']),
        'acepta_mascotas': any(word in desc_lower for word in ['mascota', 'pet', 'perro', 'gato']),
        'tiene_bodega': any(word in desc_lower for word in ['bodega', 'storage', 'depósito']),
        'tiene_quincho': any(word in desc_lower for word in ['quincho', 'barbacoa', 'parrilla', 'bbq']),
    }
    
    return features


def format_currency(amount: Decimal, currency: str = 'CLP') -> str:
    """
    Formatea una cantidad como moneda
    """
    try:
        if currency == 'CLP':
            return f"${amount:,.0f}".replace(',', '.')
        elif currency == 'UF':
            return f"UF {amount:,.2f}".replace(',', '.')
        else:
            return f"{currency} {amount:,.2f}"
    except Exception:
        return str(amount)


def validate_property_data(data: Dict[str, Any]) -> Dict[str, List[str]]:
    """
    Valida los datos de una propiedad y retorna errores
    """
    errors = {}
    
    # Validaciones obligatorias
    if not data.get('titulo'):
        errors['titulo'] = ['El título es obligatorio']
    
    if not data.get('precio') or data['precio'] <= 0:
        errors['precio'] = ['El precio debe ser mayor a 0']
    
    if not data.get('metros_cuadrados') or data['metros_cuadrados'] <= 0:
        errors['metros_cuadrados'] = ['Los metros cuadrados deben ser mayores a 0']
    
    if not data.get('direccion'):
        errors['direccion'] = ['La dirección es obligatoria']
    
    # Validaciones de rango
    if data.get('dormitorios', 0) < 0:
        errors['dormitorios'] = ['Los dormitorios no pueden ser negativos']
    
    if data.get('banos', 0) < 0:
        errors['banos'] = ['Los baños no pueden ser negativos']
    
    return errors