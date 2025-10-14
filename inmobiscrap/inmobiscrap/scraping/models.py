from django.db import models
from django.core.validators import MinValueValidator, URLValidator
from django.utils import timezone


class URLToScrape(models.Model):
    """Modelo para gestionar las URLs a scrapear"""
    
    PRIORITY_CHOICES = [
        ('low', 'Baja'),
        ('medium', 'Media'),
        ('high', 'Alta'),
    ]
    
    STATUS_CHOICES = [
        ('pending', 'Pendiente'),
        ('in_progress', 'En Progreso'),
        ('completed', 'Completado'),
        ('failed', 'Fallido'),
        ('disabled', 'Deshabilitado'),
    ]
    
    url = models.URLField(max_length=1000, unique=True, validators=[URLValidator()])
    site_name = models.CharField(max_length=200, help_text="Nombre del sitio (ej: Portal Inmobiliario)")
    description = models.TextField(blank=True, null=True)
    priority = models.CharField(max_length=10, choices=PRIORITY_CHOICES, default='medium')
    status = models.CharField(max_length=20, choices=STATUS_CHOICES, default='pending')
    
    # Configuración de scraping
    is_active = models.BooleanField(default=True)
    scrape_frequency_hours = models.IntegerField(default=24, help_text="Frecuencia de scraping en horas")
    last_scraped_at = models.DateTimeField(null=True, blank=True)
    next_scrape_at = models.DateTimeField(null=True, blank=True)
    
    # Estadísticas
    total_scrapes = models.IntegerField(default=0)
    successful_scrapes = models.IntegerField(default=0)
    failed_scrapes = models.IntegerField(default=0)
    last_error_message = models.TextField(blank=True, null=True)
    
    # Metadatos
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    
    class Meta:
        db_table = 'urls_to_scrape'
        verbose_name = 'URL a Scrapear'
        verbose_name_plural = 'URLs a Scrapear'
        ordering = ['-priority', '-created_at']
        indexes = [
            models.Index(fields=['status', 'is_active']),
            models.Index(fields=['next_scrape_at']),
        ]
    
    def __str__(self):
        return f"{self.site_name} - {self.url[:50]}"
    
    def mark_as_scraped(self, success=True, error_message=None):
        """Marca la URL como scrapeada y actualiza estadísticas"""
        self.last_scraped_at = timezone.now()
        self.total_scrapes += 1
        
        if success:
            self.successful_scrapes += 1
            self.status = 'completed'
            self.last_error_message = None
        else:
            self.failed_scrapes += 1
            self.status = 'failed'
            self.last_error_message = error_message
        
        # Calcular próximo scrape
        self.next_scrape_at = timezone.now() + timezone.timedelta(hours=self.scrape_frequency_hours)
        self.save()


class BasePropiedad(models.Model):
    """Modelo base abstracto para todas las propiedades"""
    
    TIPO_OPERACION_CHOICES = [
        ('venta', 'Venta'),
        ('arriendo', 'Arriendo'),
        ('venta_arriendo', 'Venta y Arriendo'),
    ]
    
    ESTADO_CHOICES = [
        ('disponible', 'Disponible'),
        ('reservado', 'Reservado'),
        ('vendido', 'Vendido'),
        ('arrendado', 'Arrendado'),
    ]
    
    # Información básica
    titulo = models.CharField(max_length=500)
    descripcion = models.TextField(blank=True, null=True)
    precio = models.DecimalField(max_digits=12, decimal_places=2, validators=[MinValueValidator(0)])
    precio_uf = models.DecimalField(max_digits=10, decimal_places=2, null=True, blank=True)
    tipo_operacion = models.CharField(max_length=20, choices=TIPO_OPERACION_CHOICES, default='venta')
    estado = models.CharField(max_length=20, choices=ESTADO_CHOICES, default='disponible')
    
    # Características principales
    metros_cuadrados = models.DecimalField(max_digits=8, decimal_places=2, validators=[MinValueValidator(0)])
    metros_terreno = models.DecimalField(max_digits=8, decimal_places=2, null=True, blank=True)
    dormitorios = models.IntegerField(validators=[MinValueValidator(0)], default=0)
    banos = models.IntegerField(validators=[MinValueValidator(0)], default=0)
    estacionamientos = models.IntegerField(validators=[MinValueValidator(0)], default=0)
    
    # Ubicación
    direccion = models.CharField(max_length=500)
    comuna = models.CharField(max_length=100)
    ciudad = models.CharField(max_length=100)
    region = models.CharField(max_length=100)
    codigo_postal = models.CharField(max_length=20, blank=True, null=True)
    latitud = models.DecimalField(max_digits=10, decimal_places=7, null=True, blank=True)
    longitud = models.DecimalField(max_digits=10, decimal_places=7, null=True, blank=True)
    
    # Características adicionales
    ano_construccion = models.IntegerField(null=True, blank=True)
    piso = models.IntegerField(null=True, blank=True)
    orientacion = models.CharField(max_length=50, blank=True, null=True)
    gastos_comunes = models.DecimalField(max_digits=10, decimal_places=2, null=True, blank=True)
    contribuciones = models.DecimalField(max_digits=10, decimal_places=2, null=True, blank=True)
    
    # Amenidades (como JSON)
    amenidades = models.JSONField(default=list, blank=True)
    
    # Información del scraping
    url_fuente = models.URLField(max_length=1000)
    sitio_origen = models.CharField(max_length=200)
    codigo_propiedad = models.CharField(max_length=200, blank=True, null=True)
    imagenes_urls = models.JSONField(default=list, blank=True)
    
    # Contacto
    nombre_contacto = models.CharField(max_length=200, blank=True, null=True)
    telefono_contacto = models.CharField(max_length=50, blank=True, null=True)
    email_contacto = models.EmailField(blank=True, null=True)
    inmobiliaria = models.CharField(max_length=200, blank=True, null=True)
    
    # Metadatos
    fecha_publicacion = models.DateField(null=True, blank=True)
    fecha_scraping = models.DateTimeField(auto_now_add=True)
    ultima_actualizacion = models.DateTimeField(auto_now=True)
    is_active = models.BooleanField(default=True)
    
    # Relación con URL scrapeada
    url_scrape = models.ForeignKey(URLToScrape, on_delete=models.SET_NULL, null=True, blank=True, related_name='%(class)s_propiedades')
    
    class Meta:
        abstract = True
        ordering = ['-fecha_scraping']
        indexes = [
            models.Index(fields=['precio', 'tipo_operacion']),
            models.Index(fields=['comuna', 'ciudad']),
            models.Index(fields=['metros_cuadrados']),
        ]
    
    def __str__(self):
        return f"{self.titulo} - {self.comuna} - ${self.precio}"
    
    def precio_por_m2(self):
        """Calcula el precio por metro cuadrado"""
        if self.metros_cuadrados > 0:
            return self.precio / self.metros_cuadrados
        return 0


class Casa(BasePropiedad):
    """Modelo específico para casas"""
    
    TIPO_CASA_CHOICES = [
        ('pareada', 'Pareada'),
        ('independiente', 'Independiente'),
        ('condominio', 'Condominio'),
        ('villa', 'Villa'),
    ]
    
    tipo_casa = models.CharField(max_length=20, choices=TIPO_CASA_CHOICES, default='independiente')
    tiene_patio = models.BooleanField(default=False)
    metros_patio = models.DecimalField(max_digits=8, decimal_places=2, null=True, blank=True)
    tiene_quincho = models.BooleanField(default=False)
    tiene_piscina = models.BooleanField(default=False)
    numero_pisos = models.IntegerField(default=1)
    tiene_bodega = models.BooleanField(default=False)
    
    class Meta:
        db_table = 'casas'
        verbose_name = 'Casa'
        verbose_name_plural = 'Casas'


class Departamento(BasePropiedad):
    """Modelo específico para departamentos"""
    
    tiene_balcon = models.BooleanField(default=False)
    tiene_terraza = models.BooleanField(default=False)
    metros_balcon = models.DecimalField(max_digits=6, decimal_places=2, null=True, blank=True)
    amoblado = models.BooleanField(default=False)
    acepta_mascotas = models.BooleanField(default=False)
    total_pisos_edificio = models.IntegerField(null=True, blank=True)
    tiene_porteria = models.BooleanField(default=False)
    tiene_ascensor = models.BooleanField(default=False)
    tiene_bodega = models.BooleanField(default=False)
    
    class Meta:
        db_table = 'departamentos'
        verbose_name = 'Departamento'
        verbose_name_plural = 'Departamentos'


class Terreno(BasePropiedad):
    """Modelo específico para terrenos"""
    
    TIPO_TERRENO_CHOICES = [
        ('urbano', 'Urbano'),
        ('rural', 'Rural'),
        ('industrial', 'Industrial'),
        ('comercial', 'Comercial'),
    ]
    
    FORMA_TERRENO_CHOICES = [
        ('regular', 'Regular'),
        ('irregular', 'Irregular'),
    ]
    
    tipo_terreno = models.CharField(max_length=20, choices=TIPO_TERRENO_CHOICES, default='urbano')
    forma_terreno = models.CharField(max_length=20, choices=FORMA_TERRENO_CHOICES, null=True, blank=True)
    frente_metros = models.DecimalField(max_digits=8, decimal_places=2, null=True, blank=True)
    fondo_metros = models.DecimalField(max_digits=8, decimal_places=2, null=True, blank=True)
    tiene_agua = models.BooleanField(default=False)
    tiene_luz = models.BooleanField(default=False)
    tiene_alcantarillado = models.BooleanField(default=False)
    tiene_gas = models.BooleanField(default=False)
    es_esquina = models.BooleanField(default=False)
    tiene_cerco = models.BooleanField(default=False)
    uso_suelo = models.CharField(max_length=200, blank=True, null=True)
    
    class Meta:
        db_table = 'terrenos'
        verbose_name = 'Terreno'
        verbose_name_plural = 'Terrenos'


class CasaPrefabricada(BasePropiedad):
    """Modelo específico para casas prefabricadas"""
    
    MATERIAL_CHOICES = [
        ('madera', 'Madera'),
        ('acero', 'Acero'),
        ('concreto', 'Concreto'),
        ('mixto', 'Mixto'),
    ]
    
    TIPO_PREFABRICADA_CHOICES = [
        ('modular', 'Modular'),
        ('container', 'Container'),
        ('movil', 'Móvil'),
        ('tiny_house', 'Tiny House'),
    ]
    
    tipo_prefabricada = models.CharField(max_length=20, choices=TIPO_PREFABRICADA_CHOICES, default='modular')
    material_principal = models.CharField(max_length=20, choices=MATERIAL_CHOICES, default='madera')
    es_transportable = models.BooleanField(default=True)
    requiere_terreno = models.BooleanField(default=True)
    tiempo_instalacion_dias = models.IntegerField(null=True, blank=True)
    incluye_instalacion = models.BooleanField(default=False)
    garantia_anos = models.IntegerField(null=True, blank=True)
    certificacion_energetica = models.CharField(max_length=10, blank=True, null=True)
    numero_modulos = models.IntegerField(default=1)
    es_expandible = models.BooleanField(default=False)
    
    class Meta:
        db_table = 'casas_prefabricadas'
        verbose_name = 'Casa Prefabricada'
        verbose_name_plural = 'Casas Prefabricadas'


class ScrapingLog(models.Model):
    """Registro de ejecuciones de scraping"""
    
    STATUS_CHOICES = [
        ('started', 'Iniciado'),
        ('completed', 'Completado'),
        ('failed', 'Fallido'),
        ('partial', 'Parcial'),
    ]
    
    url_scrape = models.ForeignKey(URLToScrape, on_delete=models.CASCADE, related_name='scraping_logs')
    status = models.CharField(max_length=20, choices=STATUS_CHOICES)
    started_at = models.DateTimeField(auto_now_add=True)
    completed_at = models.DateTimeField(null=True, blank=True)
    
    # Resultados
    properties_found = models.IntegerField(default=0)
    properties_created = models.IntegerField(default=0)
    properties_updated = models.IntegerField(default=0)
    properties_failed = models.IntegerField(default=0)
    
    # Información adicional
    error_message = models.TextField(blank=True, null=True)
    execution_time_seconds = models.FloatField(null=True, blank=True)
    response_data = models.JSONField(default=dict, blank=True)
    
    class Meta:
        db_table = 'scraping_logs'
        verbose_name = 'Log de Scraping'
        verbose_name_plural = 'Logs de Scraping'
        ordering = ['-started_at']
        indexes = [
            models.Index(fields=['status', 'started_at']),
        ]
    
    def __str__(self):
        return f"Scraping {self.url_scrape.site_name} - {self.status} - {self.started_at}"