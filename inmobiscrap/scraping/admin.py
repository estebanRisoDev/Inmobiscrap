from django.contrib import admin
from django.utils.html import format_html
from inmobiscrap.scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)


@admin.register(URLToScrape)
class URLToScrapeAdmin(admin.ModelAdmin):
    list_display = ['id', 'site_name', 'status_badge', 'priority', 'is_active', 
                    'last_scraped_at', 'next_scrape_at', 'success_rate']
    list_filter = ['status', 'priority', 'is_active', 'site_name']
    search_fields = ['url', 'site_name', 'description']
    readonly_fields = ['created_at', 'updated_at', 'last_scraped_at', 'total_scrapes', 
                      'successful_scrapes', 'failed_scrapes']
    list_per_page = 50
    
    fieldsets = (
        ('Información Básica', {
            'fields': ('url', 'site_name', 'description', 'priority')
        }),
        ('Estado y Configuración', {
            'fields': ('status', 'is_active', 'scrape_frequency_hours', 'next_scrape_at')
        }),
        ('Estadísticas', {
            'fields': ('last_scraped_at', 'total_scrapes', 'successful_scrapes', 
                      'failed_scrapes', 'last_error_message')
        }),
        ('Metadatos', {
            'fields': ('created_at', 'updated_at'),
            'classes': ('collapse',)
        }),
    )
    
    def status_badge(self, obj):
        colors = {
            'pending': 'orange',
            'in_progress': 'blue',
            'completed': 'green',
            'failed': 'red',
            'disabled': 'gray',
        }
        color = colors.get(obj.status, 'gray')
        return format_html(
            '<span style="color: {}; font-weight: bold;">●</span> {}',
            color, obj.get_status_display()
        )
    status_badge.short_description = 'Estado'
    
    def success_rate(self, obj):
        if obj.total_scrapes == 0:
            return '0%'
        rate = (obj.successful_scrapes / obj.total_scrapes) * 100
        color = 'green' if rate >= 80 else 'orange' if rate >= 50 else 'red'
        return format_html(
            '<span style="color: {}; font-weight: bold;">{:.1f}%</span>',
            color, rate
        )
    success_rate.short_description = 'Tasa de éxito'
    
    actions = ['mark_as_active', 'mark_as_inactive', 'reset_scraping_stats']
    
    def mark_as_active(self, request, queryset):
        updated = queryset.update(is_active=True)
        self.message_user(request, f'{updated} URLs activadas.')
    mark_as_active.short_description = 'Activar URLs seleccionadas'
    
    def mark_as_inactive(self, request, queryset):
        updated = queryset.update(is_active=False)
        self.message_user(request, f'{updated} URLs desactivadas.')
    mark_as_inactive.short_description = 'Desactivar URLs seleccionadas'
    
    def reset_scraping_stats(self, request, queryset):
        queryset.update(total_scrapes=0, successful_scrapes=0, failed_scrapes=0)
        self.message_user(request, 'Estadísticas reiniciadas.')
    reset_scraping_stats.short_description = 'Reiniciar estadísticas'


class BasePropiedadAdmin(admin.ModelAdmin):
    list_display = ['id', 'titulo_truncado', 'precio', 'tipo_operacion', 
                    'metros_cuadrados', 'dormitorios', 'banos', 'comuna', 'sitio_origen']
    list_filter = ['tipo_operacion', 'estado', 'ciudad', 'sitio_origen', 'is_active']
    search_fields = ['titulo', 'descripcion', 'direccion', 'comuna']
    readonly_fields = ['fecha_scraping', 'ultima_actualizacion', 'url_fuente']
    list_per_page = 50
    
    def titulo_truncado(self, obj):
        return obj.titulo[:50] + '...' if len(obj.titulo) > 50 else obj.titulo
    titulo_truncado.short_description = 'Título'


@admin.register(Casa)
class CasaAdmin(BasePropiedadAdmin):
    list_display = BasePropiedadAdmin.list_display + ['tipo_casa', 'tiene_patio', 'tiene_piscina']
    list_filter = BasePropiedadAdmin.list_filter + ['tipo_casa', 'tiene_patio', 'tiene_piscina']


@admin.register(Departamento)
class DepartamentoAdmin(BasePropiedadAdmin):
    list_display = BasePropiedadAdmin.list_display + ['piso', 'amoblado', 'acepta_mascotas']
    list_filter = BasePropiedadAdmin.list_filter + ['amoblado', 'acepta_mascotas', 'tiene_balcon']


@admin.register(Terreno)
class TerrenoAdmin(BasePropiedadAdmin):
    list_display = BasePropiedadAdmin.list_display + ['tipo_terreno', 'metros_terreno']
    list_filter = BasePropiedadAdmin.list_filter + ['tipo_terreno', 'tiene_agua', 'tiene_luz']


@admin.register(CasaPrefabricada)
class CasaPrefabricadaAdmin(BasePropiedadAdmin):
    list_display = BasePropiedadAdmin.list_display + ['tipo_prefabricada', 'material_principal']
    list_filter = BasePropiedadAdmin.list_filter + ['tipo_prefabricada', 'material_principal']


@admin.register(ScrapingLog)
class ScrapingLogAdmin(admin.ModelAdmin):
    list_display = ['id', 'url_scrape_name', 'status_badge', 'started_at', 
                    'properties_found', 'execution_time', 'error_truncated']
    list_filter = ['status', 'started_at']
    search_fields = ['url_scrape__site_name', 'error_message']
    readonly_fields = ['started_at', 'completed_at', 'execution_time_seconds', 
                      'properties_found', 'properties_created', 'properties_updated', 
                      'properties_failed', 'response_data']
    list_per_page = 100
    date_hierarchy = 'started_at'
    
    def url_scrape_name(self, obj):
        return obj.url_scrape.site_name
    url_scrape_name.short_description = 'Sitio'
    
    def status_badge(self, obj):
        colors = {
            'started': 'blue',
            'completed': 'green',
            'failed': 'red',
            'partial': 'orange',
        }
        color = colors.get(obj.status, 'gray')
        return format_html(
            '<span style="color: {}; font-weight: bold;">●</span> {}',
            color, obj.get_status_display()
        )
    status_badge.short_description = 'Estado'
    
    def execution_time(self, obj):
        if obj.execution_time_seconds:
            return f'{obj.execution_time_seconds:.2f}s'
        return '-'
    execution_time.short_description = 'Tiempo'
    
    def error_truncated(self, obj):
        if obj.error_message:
            return obj.error_message[:100] + '...' if len(obj.error_message) > 100 else obj.error_message
        return '-'
    error_truncated.short_description = 'Error'


# Personalización del sitio de administración
admin.site.site_header = 'InmobiScrap Admin'
admin.site.site_title = 'InmobiScrap'
admin.site.index_title = 'Panel de Administración'