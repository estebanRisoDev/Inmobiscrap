from rest_framework import viewsets, status, filters
from rest_framework.decorators import action
from rest_framework.response import Response
from rest_framework.permissions import IsAuthenticated, IsAuthenticatedOrReadOnly
from django_filters.rest_framework import DjangoFilterBackend
from django.db.models import Avg, Count, Q
from django.utils import timezone

from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)
from scraping.api.serializers import (
    URLToScrapeSerializer, URLToScrapeListSerializer,
    CasaSerializer, CasaListSerializer,
    DepartamentoSerializer, DepartamentoListSerializer,
    TerrenoSerializer, TerrenoListSerializer,
    CasaPrefabricadaSerializer, CasaPrefabricadaListSerializer,
    ScrapingLogSerializer, BulkURLCreateSerializer,
    ScrapingStatsSerializer
)
from scraping.tasks import scrape_url_task


class URLToScrapeViewSet(viewsets.ModelViewSet):
    """
    ViewSet para gestionar URLs a scrapear
    
    list: Listar todas las URLs
    create: Crear nueva URL
    retrieve: Ver detalle de URL
    update: Actualizar URL
    partial_update: Actualizar parcialmente URL
    destroy: Eliminar URL
    """
    queryset = URLToScrape.objects.all()
    permission_classes = [IsAuthenticatedOrReadOnly]
    filter_backends = [DjangoFilterBackend, filters.SearchFilter, filters.OrderingFilter]
    filterset_fields = ['status', 'priority', 'is_active', 'site_name']
    search_fields = ['url', 'site_name', 'description']
    ordering_fields = ['created_at', 'last_scraped_at', 'next_scrape_at', 'priority']
    
    def get_serializer_class(self):
        if self.action == 'list':
            return URLToScrapeListSerializer
        return URLToScrapeSerializer
    
    @action(detail=True, methods=['post'])
    def scrape_now(self, request, pk=None):
        """Ejecutar scraping inmediatamente para esta URL"""
        url_obj = self.get_object()
        
        if not url_obj.is_active:
            return Response(
                {'error': 'Esta URL está deshabilitada'},
                status=status.HTTP_400_BAD_REQUEST
            )
        
        # Ejecutar tarea de scraping
        task = scrape_url_task.delay(url_obj.id)
        
        return Response({
            'message': 'Scraping iniciado',
            'task_id': task.id,
            'url': url_obj.url
        }, status=status.HTTP_202_ACCEPTED)
    
    @action(detail=False, methods=['post'])
    def bulk_create(self, request):
        """Crear múltiples URLs a la vez"""
        serializer = BulkURLCreateSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        
        created_urls = serializer.save()
        
        return Response({
            'message': f'{len(created_urls)} URLs creadas exitosamente',
            'urls': URLToScrapeListSerializer(created_urls, many=True).data
        }, status=status.HTTP_201_CREATED)
    
    @action(detail=False, methods=['post'])
    def scrape_all_pending(self, request):
        """Scrapear todas las URLs pendientes"""
        pending_urls = URLToScrape.objects.filter(
            is_active=True,
            status__in=['pending', 'failed']
        )
        
        tasks = []
        for url_obj in pending_urls:
            task = scrape_url_task.delay(url_obj.id)
            tasks.append({'url_id': url_obj.id, 'task_id': task.id})
        
        return Response({
            'message': f'{len(tasks)} tareas de scraping iniciadas',
            'tasks': tasks
        }, status=status.HTTP_202_ACCEPTED)
    
    @action(detail=False, methods=['get'])
    def pending(self, request):
        """Listar URLs pendientes de scraping"""
        now = timezone.now()
        pending = URLToScrape.objects.filter(
            Q(is_active=True) &
            (Q(next_scrape_at__lte=now) | Q(next_scrape_at__isnull=True))
        )
        
        serializer = URLToScrapeListSerializer(pending, many=True)
        return Response(serializer.data)


class CasaViewSet(viewsets.ReadOnlyModelViewSet):
    """
    ViewSet para casas (solo lectura)
    
    list: Listar todas las casas
    retrieve: Ver detalle de casa
    """
    queryset = Casa.objects.filter(is_active=True)
    permission_classes = [IsAuthenticatedOrReadOnly]
    filter_backends = [DjangoFilterBackend, filters.SearchFilter, filters.OrderingFilter]
    filterset_fields = ['tipo_operacion', 'estado', 'comuna', 'ciudad', 'tipo_casa', 
                       'dormitorios', 'banos', 'estacionamientos']
    search_fields = ['titulo', 'descripcion', 'direccion', 'comuna']
    ordering_fields = ['precio', 'metros_cuadrados', 'fecha_scraping', 'dormitorios']
    
    def get_serializer_class(self):
        if self.action == 'list':
            return CasaListSerializer
        return CasaSerializer
    
    @action(detail=False, methods=['get'])
    def statistics(self, request):
        """Obtener estadísticas de casas"""
        stats = Casa.objects.filter(is_active=True).aggregate(
            total=Count('id'),
            avg_price=Avg('precio'),
            avg_m2=Avg('metros_cuadrados'),
            avg_dormitorios=Avg('dormitorios')
        )
        return Response(stats)
    
    @action(detail=False, methods=['get'])
    def price_ranges(self, request):
        """Obtener rangos de precios"""
        ranges = {
            'hasta_5000': Casa.objects.filter(precio__lt=5000, is_active=True).count(),
            '5000_10000': Casa.objects.filter(precio__gte=5000, precio__lt=10000, is_active=True).count(),
            '10000_20000': Casa.objects.filter(precio__gte=10000, precio__lt=20000, is_active=True).count(),
            'mas_20000': Casa.objects.filter(precio__gte=20000, is_active=True).count(),
        }
        return Response(ranges)


class DepartamentoViewSet(viewsets.ReadOnlyModelViewSet):
    """
    ViewSet para departamentos (solo lectura)
    """
    queryset = Departamento.objects.filter(is_active=True)
    permission_classes = [IsAuthenticatedOrReadOnly]
    filter_backends = [DjangoFilterBackend, filters.SearchFilter, filters.OrderingFilter]
    filterset_fields = ['tipo_operacion', 'estado', 'comuna', 'ciudad',
                       'dormitorios', 'banos', 'piso', 'amoblado']
    search_fields = ['titulo', 'descripcion', 'direccion', 'comuna']
    ordering_fields = ['precio', 'metros_cuadrados', 'fecha_scraping', 'piso']
    
    def get_serializer_class(self):
        if self.action == 'list':
            return DepartamentoListSerializer
        return DepartamentoSerializer
    
    @action(detail=False, methods=['get'])
    def statistics(self, request):
        """Obtener estadísticas de departamentos"""
        stats = Departamento.objects.filter(is_active=True).aggregate(
            total=Count('id'),
            avg_price=Avg('precio'),
            avg_m2=Avg('metros_cuadrados'),
            avg_piso=Avg('piso')
        )
        return Response(stats)


class TerrenoViewSet(viewsets.ReadOnlyModelViewSet):
    """
    ViewSet para terrenos (solo lectura)
    """
    queryset = Terreno.objects.filter(is_active=True)
    permission_classes = [IsAuthenticatedOrReadOnly]
    filter_backends = [DjangoFilterBackend, filters.SearchFilter, filters.OrderingFilter]
    filterset_fields = ['tipo_operacion', 'estado', 'comuna', 'ciudad',
                       'tipo_terreno', 'forma_terreno', 'tiene_agua', 'tiene_luz']
    search_fields = ['titulo', 'descripcion', 'direccion', 'comuna']
    ordering_fields = ['precio', 'metros_cuadrados', 'fecha_scraping']
    
    def get_serializer_class(self):
        if self.action == 'list':
            return TerrenoListSerializer
        return TerrenoSerializer
    
    @action(detail=False, methods=['get'])
    def statistics(self, request):
        """Obtener estadísticas de terrenos"""
        stats = Terreno.objects.filter(is_active=True).aggregate(
            total=Count('id'),
            avg_price=Avg('precio'),
            avg_m2=Avg('metros_cuadrados')
        )
        return Response(stats)


class CasaPrefabricadaViewSet(viewsets.ReadOnlyModelViewSet):
    """
    ViewSet para casas prefabricadas (solo lectura)
    """
    queryset = CasaPrefabricada.objects.filter(is_active=True)
    permission_classes = [IsAuthenticatedOrReadOnly]
    filter_backends = [DjangoFilterBackend, filters.SearchFilter, filters.OrderingFilter]
    filterset_fields = ['tipo_operacion', 'tipo_prefabricada', 'material_principal',
                       'dormitorios', 'es_transportable', 'incluye_instalacion']
    search_fields = ['titulo', 'descripcion']
    ordering_fields = ['precio', 'metros_cuadrados', 'fecha_scraping']
    
    def get_serializer_class(self):
        if self.action == 'list':
            return CasaPrefabricadaListSerializer
        return CasaPrefabricadaSerializer
    
    @action(detail=False, methods=['get'])
    def statistics(self, request):
        """Obtener estadísticas de casas prefabricadas"""
        stats = CasaPrefabricada.objects.filter(is_active=True).aggregate(
            total=Count('id'),
            avg_price=Avg('precio'),
            avg_m2=Avg('metros_cuadrados')
        )
        return Response(stats)


class ScrapingLogViewSet(viewsets.ReadOnlyModelViewSet):
    """
    ViewSet para logs de scraping (solo lectura)
    """
    queryset = ScrapingLog.objects.all()
    serializer_class = ScrapingLogSerializer
    permission_classes = [IsAuthenticatedOrReadOnly]
    filter_backends = [DjangoFilterBackend, filters.OrderingFilter]
    filterset_fields = ['status', 'url_scrape']
    ordering_fields = ['started_at', 'completed_at']
    
    @action(detail=False, methods=['get'])
    def recent(self, request):
        """Obtener los últimos 20 logs"""
        recent_logs = ScrapingLog.objects.all()[:20]
        serializer = self.get_serializer(recent_logs, many=True)
        return Response(serializer.data)


class DashboardViewSet(viewsets.ViewSet):
    """
    ViewSet para estadísticas generales del dashboard
    """
    permission_classes = [IsAuthenticatedOrReadOnly]
    
    @action(detail=False, methods=['get'])
    def stats(self, request):
        """Obtener estadísticas generales"""
        
        # Estadísticas de URLs
        total_urls = URLToScrape.objects.count()
        active_urls = URLToScrape.objects.filter(is_active=True).count()
        pending_urls = URLToScrape.objects.filter(
            is_active=True,
            status='pending'
        ).count()
        
        # Estadísticas de propiedades
        casas_count = Casa.objects.filter(is_active=True).count()
        departamentos_count = Departamento.objects.filter(is_active=True).count()
        terrenos_count = Terreno.objects.filter(is_active=True).count()
        casas_prefabricadas_count = CasaPrefabricada.objects.filter(is_active=True).count()
        
        total_properties = (casas_count + departamentos_count + 
                          terrenos_count + casas_prefabricadas_count)
        
        # Precio promedio general
        all_prices = []
        if casas_count > 0:
            all_prices.extend(Casa.objects.filter(is_active=True).values_list('precio', flat=True))
        if departamentos_count > 0:
            all_prices.extend(Departamento.objects.filter(is_active=True).values_list('precio', flat=True))
        
        average_price = sum(all_prices) / len(all_prices) if all_prices else 0
        
        # Último scraping
        last_log = ScrapingLog.objects.order_by('-started_at').first()
        last_scrape_date = last_log.started_at if last_log else None
        
        stats_data = {
            'total_urls': total_urls,
            'active_urls': active_urls,
            'pending_urls': pending_urls,
            'total_properties': total_properties,
            'casas_count': casas_count,
            'departamentos_count': departamentos_count,
            'terrenos_count': terrenos_count,
            'casas_prefabricadas_count': casas_prefabricadas_count,
            'average_price': round(average_price, 2),
            'last_scrape_date': last_scrape_date
        }
        
        serializer = ScrapingStatsSerializer(data=stats_data)
        serializer.is_valid(raise_exception=True)
        
        return Response(serializer.data)