from rest_framework import serializers
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno, 
    CasaPrefabricada, ScrapingLog
)


class URLToScrapeSerializer(serializers.ModelSerializer):
    """Serializer para URLs a scrapear"""
    
    class Meta:
        model = URLToScrape
        fields = '__all__'
        read_only_fields = ('created_at', 'updated_at', 'last_scraped_at', 
                           'total_scrapes', 'successful_scrapes', 'failed_scrapes')
    
    def validate_url(self, value):
        """Validación personalizada para URLs"""
        if not value.startswith(('http://', 'https://')):
            raise serializers.ValidationError("La URL debe comenzar con http:// o https://")
        return value


class URLToScrapeListSerializer(serializers.ModelSerializer):
    """Serializer simplificado para listado de URLs"""
    success_rate = serializers.SerializerMethodField()
    
    class Meta:
        model = URLToScrape
        fields = ['id', 'url', 'site_name', 'status', 'priority', 
                 'is_active', 'last_scraped_at', 'next_scrape_at', 'success_rate']
    
    def get_success_rate(self, obj):
        if obj.total_scrapes == 0:
            return 0
        return round((obj.successful_scrapes / obj.total_scrapes) * 100, 2)


class BasePropiedadSerializer(serializers.ModelSerializer):
    """Serializer base para propiedades"""
    precio_por_m2 = serializers.SerializerMethodField()
    
    def get_precio_por_m2(self, obj):
        return round(obj.precio_por_m2(), 2)


class CasaSerializer(BasePropiedadSerializer):
    """Serializer para casas"""
    
    class Meta:
        model = Casa
        fields = '__all__'
        read_only_fields = ('fecha_scraping', 'ultima_actualizacion')


class CasaListSerializer(serializers.ModelSerializer):
    """Serializer simplificado para listado de casas"""
    precio_por_m2 = serializers.SerializerMethodField()
    
    class Meta:
        model = Casa
        fields = ['id', 'titulo', 'precio', 'precio_uf', 'tipo_operacion', 
                 'metros_cuadrados', 'dormitorios', 'banos', 'comuna', 
                 'ciudad', 'tipo_casa', 'precio_por_m2', 'sitio_origen']
    
    def get_precio_por_m2(self, obj):
        return round(obj.precio_por_m2(), 2)


class DepartamentoSerializer(BasePropiedadSerializer):
    """Serializer para departamentos"""
    
    class Meta:
        model = Departamento
        fields = '__all__'
        read_only_fields = ('fecha_scraping', 'ultima_actualizacion')


class DepartamentoListSerializer(serializers.ModelSerializer):
    """Serializer simplificado para listado de departamentos"""
    precio_por_m2 = serializers.SerializerMethodField()
    
    class Meta:
        model = Departamento
        fields = ['id', 'titulo', 'precio', 'precio_uf', 'tipo_operacion',
                 'metros_cuadrados', 'dormitorios', 'banos', 'piso', 
                 'comuna', 'ciudad', 'precio_por_m2', 'sitio_origen']
    
    def get_precio_por_m2(self, obj):
        return round(obj.precio_por_m2(), 2)


class TerrenoSerializer(BasePropiedadSerializer):
    """Serializer para terrenos"""
    
    class Meta:
        model = Terreno
        fields = '__all__'
        read_only_fields = ('fecha_scraping', 'ultima_actualizacion')


class TerrenoListSerializer(serializers.ModelSerializer):
    """Serializer simplificado para listado de terrenos"""
    precio_por_m2 = serializers.SerializerMethodField()
    
    class Meta:
        model = Terreno
        fields = ['id', 'titulo', 'precio', 'precio_uf', 'tipo_operacion',
                 'metros_cuadrados', 'metros_terreno', 'tipo_terreno',
                 'comuna', 'ciudad', 'precio_por_m2', 'sitio_origen']
    
    def get_precio_por_m2(self, obj):
        return round(obj.precio_por_m2(), 2)


class CasaPrefabricadaSerializer(BasePropiedadSerializer):
    """Serializer para casas prefabricadas"""
    
    class Meta:
        model = CasaPrefabricada
        fields = '__all__'
        read_only_fields = ('fecha_scraping', 'ultima_actualizacion')


class CasaPrefabricadaListSerializer(serializers.ModelSerializer):
    """Serializer simplificado para listado de casas prefabricadas"""
    precio_por_m2 = serializers.SerializerMethodField()
    
    class Meta:
        model = CasaPrefabricada
        fields = ['id', 'titulo', 'precio', 'tipo_operacion', 'metros_cuadrados',
                 'dormitorios', 'banos', 'tipo_prefabricada', 'material_principal',
                 'precio_por_m2', 'sitio_origen']
    
    def get_precio_por_m2(self, obj):
        return round(obj.precio_por_m2(), 2)


class ScrapingLogSerializer(serializers.ModelSerializer):
    """Serializer para logs de scraping"""
    url_scrape_name = serializers.CharField(source='url_scrape.site_name', read_only=True)
    
    class Meta:
        model = ScrapingLog
        fields = '__all__'
        read_only_fields = ('started_at', 'completed_at')


class BulkURLCreateSerializer(serializers.Serializer):
    """Serializer para creación masiva de URLs"""
    urls = serializers.ListField(
        child=serializers.URLField(),
        min_length=1,
        max_length=100
    )
    site_name = serializers.CharField(max_length=200)
    priority = serializers.ChoiceField(
        choices=['low', 'medium', 'high'],
        default='medium'
    )
    scrape_frequency_hours = serializers.IntegerField(default=24)
    
    def create(self, validated_data):
        urls = validated_data.pop('urls')
        created_urls = []
        
        for url in urls:
            url_obj, created = URLToScrape.objects.get_or_create(
                url=url,
                defaults={
                    'site_name': validated_data['site_name'],
                    'priority': validated_data['priority'],
                    'scrape_frequency_hours': validated_data['scrape_frequency_hours'],
                }
            )
            if created:
                created_urls.append(url_obj)
        
        return created_urls


class ScrapingStatsSerializer(serializers.Serializer):
    """Serializer para estadísticas de scraping"""
    total_urls = serializers.IntegerField()
    active_urls = serializers.IntegerField()
    pending_urls = serializers.IntegerField()
    total_properties = serializers.IntegerField()
    casas_count = serializers.IntegerField()
    departamentos_count = serializers.IntegerField()
    terrenos_count = serializers.IntegerField()
    casas_prefabricadas_count = serializers.IntegerField()
    average_price = serializers.DecimalField(max_digits=12, decimal_places=2)
    last_scrape_date = serializers.DateTimeField()