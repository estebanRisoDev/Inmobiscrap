from django.core.management.base import BaseCommand
from django.db.models import Count, Avg, Sum
from scraping.models import (
    URLToScrape, Casa, Departamento, Terreno,
    CasaPrefabricada, ScrapingLog
)
from django.utils import timezone
from datetime import timedelta


class Command(BaseCommand):
    help = 'Gestionar base de datos: estadísticas, limpieza, etc.'

    def add_arguments(self, parser):
        parser.add_argument(
            '--stats',
            action='store_true',
            help='Mostrar estadísticas generales'
        )
        parser.add_argument(
            '--clean-old-logs',
            type=int,
            help='Eliminar logs más antiguos de N días'
        )
        parser.add_argument(
            '--deactivate-failed',
            action='store_true',
            help='Desactivar URLs con múltiples fallos'
        )
        parser.add_argument(
            '--reset-pending',
            action='store_true',
            help='Resetear URLs en progreso a pendiente'
        )
        parser.add_argument(
            '--export-urls',
            type=str,
            help='Exportar URLs a archivo CSV'
        )

    def handle(self, *args, **options):
        if options['stats']:
            self.show_statistics()
        
        if options['clean_old_logs']:
            days = options['clean_old_logs']
            self.clean_old_logs(days)
        
        if options['deactivate_failed']:
            self.deactivate_failed_urls()
        
        if options['reset_pending']:
            self.reset_pending_urls()
        
        if options['export_urls']:
            filename = options['export_urls']
            self.export_urls(filename)

    def show_statistics(self):
        """Mostrar estadísticas generales del sistema"""
        self.stdout.write(self.style.SUCCESS('\n' + '='*60))
        self.stdout.write(self.style.SUCCESS('  ESTADÍSTICAS DEL SISTEMA INMOBISCRAP'))
        self.stdout.write(self.style.SUCCESS('='*60 + '\n'))

        # URLs
        total_urls = URLToScrape.objects.count()
        active_urls = URLToScrape.objects.filter(is_active=True).count()
        pending_urls = URLToScrape.objects.filter(status='pending').count()
        
        self.stdout.write(self.style.WARNING('📊 URLs a Scrapear:'))
        self.stdout.write(f'  Total: {total_urls}')
        self.stdout.write(f'  Activas: {active_urls}')
        self.stdout.write(f'  Pendientes: {pending_urls}\n')

        # Propiedades
        casas = Casa.objects.filter(is_active=True).count()
        deptos = Departamento.objects.filter(is_active=True).count()
        terrenos = Terreno.objects.filter(is_active=True).count()
        prefabs = CasaPrefabricada.objects.filter(is_active=True).count()
        total_props = casas + deptos + terrenos + prefabs

        self.stdout.write(self.style.WARNING('🏠 Propiedades:'))
        self.stdout.write(f'  Total: {total_props}')
        self.stdout.write(f'  Casas: {casas}')
        self.stdout.write(f'  Departamentos: {deptos}')
        self.stdout.write(f'  Terrenos: {terrenos}')
        self.stdout.write(f'  Casas Prefabricadas: {prefabs}\n')

        # Precios promedio
        if casas > 0:
            avg_casa = Casa.objects.filter(is_active=True).aggregate(Avg('precio'))
            self.stdout.write(f'  Precio promedio casas: ${avg_casa["precio__avg"]:,.0f}')
        
        if deptos > 0:
            avg_depto = Departamento.objects.filter(is_active=True).aggregate(Avg('precio'))
            self.stdout.write(f'  Precio promedio deptos: ${avg_depto["precio__avg"]:,.0f}')

        # Logs
        total_logs = ScrapingLog.objects.count()
        completed_logs = ScrapingLog.objects.filter(status='completed').count()
        failed_logs = ScrapingLog.objects.filter(status='failed').count()

        self.stdout.write(self.style.WARNING('\n📝 Logs de Scraping:'))
        self.stdout.write(f'  Total: {total_logs}')
        self.stdout.write(f'  Completados: {completed_logs}')
        self.stdout.write(f'  Fallidos: {failed_logs}')

        # Último scraping
        last_log = ScrapingLog.objects.order_by('-started_at').first()
        if last_log:
            self.stdout.write(f'  Último scraping: {last_log.started_at.strftime("%Y-%m-%d %H:%M:%S")}')

        # Propiedades por comuna (top 5)
        self.stdout.write(self.style.WARNING('\n📍 Top 5 Comunas:'))
        top_comunas = Casa.objects.filter(is_active=True).values('comuna').annotate(
            total=Count('id')
        ).order_by('-total')[:5]
        
        for comuna in top_comunas:
            self.stdout.write(f'  {comuna["comuna"]}: {comuna["total"]} propiedades')

        self.stdout.write(self.style.SUCCESS('\n' + '='*60 + '\n'))

    def clean_old_logs(self, days):
        """Eliminar logs antiguos"""
        cutoff_date = timezone.now() - timedelta(days=days)
        deleted = ScrapingLog.objects.filter(started_at__lt=cutoff_date).delete()
        
        self.stdout.write(
            self.style.SUCCESS(
                f'✅ Eliminados {deleted[0]} logs más antiguos de {days} días'
            )
        )

    def deactivate_failed_urls(self):
        """Desactivar URLs con múltiples fallos"""
        urls = URLToScrape.objects.filter(
            is_active=True,
            failed_scrapes__gte=5,
            successful_scrapes=0
        )
        
        count = urls.count()
        if count > 0:
            urls.update(is_active=False)
            self.stdout.write(
                self.style.SUCCESS(
                    f'✅ Desactivadas {count} URLs por múltiples fallos'
                )
            )
        else:
            self.stdout.write(self.style.WARNING('No hay URLs para desactivar'))

    def reset_pending_urls(self):
        """Resetear URLs en progreso a pendiente"""
        urls = URLToScrape.objects.filter(status='in_progress')
        count = urls.update(status='pending')
        
        self.stdout.write(
            self.style.SUCCESS(
                f'✅ Reseteadas {count} URLs de "en progreso" a "pendiente"'
            )
        )

    def export_urls(self, filename):
        """Exportar URLs a CSV"""
        import csv
        
        urls = URLToScrape.objects.all()
        
        with open(filename, 'w', newline='', encoding='utf-8') as f:
            writer = csv.writer(f)
            writer.writerow([
                'ID', 'URL', 'Sitio', 'Estado', 'Prioridad', 
                'Activo', 'Total Scrapes', 'Exitosos', 'Fallidos'
            ])
            
            for url in urls:
                writer.writerow([
                    url.id,
                    url.url,
                    url.site_name,
                    url.status,
                    url.priority,
                    url.is_active,
                    url.total_scrapes,
                    url.successful_scrapes,
                    url.failed_scrapes
                ])
        
        self.stdout.write(
            self.style.SUCCESS(
                f'✅ {urls.count()} URLs exportadas a {filename}'
            )
        )